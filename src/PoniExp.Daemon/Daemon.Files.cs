using System.Collections.Concurrent;
using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Files (see docs/plan.md, Phase 6). A file is uploaded once, shared with exactly the machines its messages
/// go to. On a receiving machine the daemon fetches a blob once, however many local agents received it,
/// keeps one verified copy under <c>&lt;home&gt;/files/&lt;blob&gt;/</c>, sends a signed file receipt for every
/// message that carried it, and deletes the copy when every one of those agents has released it.
/// </summary>
public sealed partial class Daemon
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _fetching = new();

    internal string FilesDirectory => Path.Combine(_context.Home, "files");

    /// <summary>Uploads each file once, shared with <paramref name="machines"/> (ids).</summary>
    internal async Task<IReadOnlyList<Attachment>> UploadAsync(IReadOnlyList<string> files, IReadOnlyCollection<string> machines, CancellationToken cancellationToken)
    {
        if (files.Count > Limits.MaxAttachments)
        {
            throw new ArgumentException($"at most {Limits.MaxAttachments} files per message");
        }

        var attachments = new List<Attachment>();
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file);
            try
            {
                attachments.Add(await _relay.UploadAsync(path, machines, ContentTypes.Of(path), cancellationToken: cancellationToken));
            }
            catch (Microsoft.AspNetCore.SignalR.HubException e)
            {
                throw new InvalidOperationException($"can't upload {Path.GetFileName(path)}: {RelayClient.ReasonOf(e)}");
            }
        }

        return attachments;
    }

    /// <summary>
    /// Fetches a received message's files (or one of them) to this machine, verified, and returns where they are.
    /// A blob another agent already fetched isn't fetched again. Acknowledging doesn't fetch; fetching doesn't release.
    /// </summary>
    internal async Task<IReadOnlyList<LocalAttachment>> FetchAsync(string agent, Guid id, Guid? blobId, CancellationToken cancellationToken)
    {
        var stored = Store.FindIncoming(MachineId, agent, id) ?? throw new KeyNotFoundException($"no message {id} for agent '{agent}'");
        if (stored.Envelope is not { } envelope)
        {
            throw new InvalidOperationException($"message {id}'s envelope was dropped by retention, and with it the file keys");
        }

        var attachments = (_relay.Open(envelope).Attachments ?? []).Where(a => blobId is null || a.BlobId == blobId).ToList();
        if (attachments.Count == 0)
        {
            throw new KeyNotFoundException(blobId is null ? $"message {id} has no files" : $"message {id} has no file {blobId}");
        }

        foreach (var a in attachments)
        {
            await EnsureLocalAsync(stored.FromMachine, a, cancellationToken);
        }

        return AttachmentsOf(stored, MessageDirection.In).Where(a => blobId is null || a.BlobId == blobId).ToList();
    }

    /// <summary>
    /// An agent is done with a file it received. The local copy is deleted once every local agent that
    /// received it has released it.
    /// </summary>
    internal IReadOnlyList<LocalAttachment> Release(string agent, Guid id, Guid? blobId)
    {
        var stored = Store.FindIncoming(MachineId, agent, id) ?? throw new KeyNotFoundException($"no message {id} for agent '{agent}'");
        var rows = Store.GetAttachments(stored.FromMachine, id).Where(a => a.Direction == MessageDirection.In && (blobId is null || a.BlobId == blobId)).ToList();
        if (rows.Count == 0)
        {
            throw new KeyNotFoundException(blobId is null ? $"message {id} has no files" : $"message {id} has no file {blobId}");
        }

        var toDelete = new List<LocalBlob>();
        Store.Transact(Now, tx =>
        {
            foreach (var row in rows.Where(row => tx.ReleaseAttachment(row.FromMachine, row.MessageId, row.BlobId)))
            {
                tx.AddAttachmentEvent(row.FromMachine, row.MessageId, row.BlobId, MessageDirection.In, row.Agent, AttachmentEvents.Released);
            }

            foreach (var blob in rows.Select(r => r.BlobId).Distinct())
            {
                if (tx.GetAttachmentsOf(blob, MessageDirection.In).All(r => r.ReleasedOn is not null) && tx.GetLocalBlob(blob) is { State: "downloaded" } local)
                {
                    tx.SetLocalBlob(blob, local.FromMachine, local.Name, local.Size, "deleted");
                    toDelete.Add(local);
                }
            }
        });
        foreach (var local in toDelete)
        {
            DeleteCopy(local);
        }

        return AttachmentsOf(stored, MessageDirection.In).Where(a => blobId is null || a.BlobId == blobId).ToList();
    }

    /// <summary>The sender cancels a blob it uploaded: the router deletes it, and recipients that haven't fetched it get a refusal.</summary>
    internal async Task CancelAsync(Guid blobId, CancellationToken cancellationToken)
    {
        try
        {
            await _relay.CancelBlobAsync(blobId, cancellationToken);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException e)
        {
            throw new KeyNotFoundException(RelayClient.ReasonOf(e));
        }

        Store.Transact(Now, tx =>
        {
            foreach (var row in tx.GetAttachmentsOf(blobId, MessageDirection.Out))
            {
                tx.AddAttachmentEvent(row.FromMachine, row.MessageId, blobId, MessageDirection.Out, row.Agent, AttachmentEvents.Cancelled);
            }
        });
    }

    /// <summary>A message's files as an agent sees them.</summary>
    internal IReadOnlyList<LocalAttachment> AttachmentsOf(StoredMessage stored, MessageDirection direction) =>
        [.. Store.GetAttachments(stored.FromMachine, stored.Id).Where(a => a.Direction == direction).Select(a =>
        {
            var state = direction == MessageDirection.Out ? a.LastEvent ?? AttachmentEvents.Uploaded
                : a.ReleasedOn is not null ? AttachmentEvents.Released
                : a.LocalState switch
                {
                    "downloaded" => AttachmentEvents.Downloaded,
                    "failed" => AttachmentEvents.DownloadFailed,
                    "deleted" => "deleted",
                    _ => AttachmentEvents.Waiting,
                };
            return new LocalAttachment(a.BlobId, a.Name, a.ContentType, a.Size, state,
                state == AttachmentEvents.Downloaded ? a.LocalPath : null, a.LastDetail);
        })];

    private async Task EnsureLocalAsync(string fromMachine, Attachment a, CancellationToken cancellationToken)
    {
        var gate = _fetching.GetOrAdd(a.BlobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            switch (Store.GetLocalBlob(a.BlobId))
            {
                case { State: "downloaded", LocalPath: { } path } when File.Exists(path):
                    Store.Transact(Now, tx => FileReceipts(tx, a.BlobId, FileReceiptKind.Downloaded, null)); // for messages that arrived since
                    await FlushQuietlyAsync();
                    return;
                case { State: "failed" } failed:
                    Store.Transact(Now, tx => FileReceipts(tx, a.BlobId, FileReceiptKind.DownloadFailed, failed.Detail));
                    await FlushQuietlyAsync();
                    throw new BlobTransferException(failed.Detail ?? "the download failed", definitive: true);
                case { State: "deleted" }:
                    throw new InvalidOperationException($"{a.Name} was released and deleted on this machine");
            }

            var destination = Path.Combine(FilesDirectory, a.BlobId.ToString("N"), a.Name);
            try
            {
                await _relay.DownloadAsync(a, destination, cancellationToken);
            }
            catch (BlobTransferException e) when (e.Definitive)
            {
                Store.Transact(Now, tx =>
                {
                    tx.SetLocalBlob(a.BlobId, fromMachine, a.Name, a.Size, "failed", detail: e.Message);
                    FileReceipts(tx, a.BlobId, FileReceiptKind.DownloadFailed, e.Message);
                });
                Diagnose("warning", "file", $"{a.Name} ({a.BlobId.ToString()[..8]}): {e.Message}", fromMachine);
                await ReleaseOnRouterAsync();
                await FlushQuietlyAsync();
                throw;
            }

            Store.Transact(Now, tx =>
            {
                tx.SetLocalBlob(a.BlobId, fromMachine, a.Name, a.Size, "downloaded", destination);
                FileReceipts(tx, a.BlobId, FileReceiptKind.Downloaded, null);
            });
            await ReleaseOnRouterAsync();
            await FlushQuietlyAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A file receipt for every received message carrying the blob that hasn't had one: one per message, once.</summary>
    private void FileReceipts(StoreTransaction tx, Guid blobId, FileReceiptKind kind, string? reason)
    {
        foreach (var row in tx.GetAttachmentsOf(blobId, MessageDirection.In).Where(r => !r.ReceiptSent))
        {
            if (tx.GetEnvelope(row.FromMachine, row.MessageId) is not { } envelope)
            {
                continue;
            }

            ComposeAutomatic(tx, envelope, _relay.Open(envelope), JsonSerializer.Serialize(new FileReceiptBody(kind, blobId, reason), WireProtocol.Json),
                MessageType.FileReceipt);
            tx.MarkFileReceiptSent(row.FromMachine, row.MessageId, blobId);
            tx.AddAttachmentEvent(row.FromMachine, row.MessageId, blobId, MessageDirection.In, row.Agent,
                kind == FileReceiptKind.Downloaded ? AttachmentEvents.Verified : AttachmentEvents.DownloadFailed, reason);
        }
    }

    /// <summary>A file receipt from the machine a message with files went to: recorded on the file track.</summary>
    private void OnFileReceipt(StoreTransaction tx, Envelope envelope, MessageContent content)
    {
        if (Parse<FileReceiptBody>(content.Body) is not { } receipt || content.ReplyTo is not { } about)
        {
            tx.Diagnose("warning", "file", "a malformed file receipt", envelope.FromMachine, envelope.Id);
            return;
        }

        if (tx.FindOutgoing(MachineId, about) is not { } sent || sent.ToMachine != envelope.FromMachine
            || tx.GetAttachment(MachineId, about, receipt.BlobId) is null)
        {
            tx.Diagnose("warning", "file", "a file receipt for a file this machine didn't send to its sender", envelope.FromMachine, envelope.Id);
            return;
        }

        tx.AddAttachmentEvent(MachineId, about, receipt.BlobId, MessageDirection.Out, sent.FromAgent,
            receipt.Receipt == FileReceiptKind.Downloaded ? AttachmentEvents.Downloaded : AttachmentEvents.DownloadFailed, receipt.Reason);
    }

    /// <summary>Tells the router about blobs this machine has finished with (verified or given up on), once it's reachable.</summary>
    private async Task ReleaseOnRouterAsync()
    {
        if (!_relay.IsConnected)
        {
            return;
        }

        foreach (var blob in Store.GetLocalBlobsToRelease())
        {
            try
            {
                await _relay.ReleaseBlobAsync(blob.BlobId);
                Store.Transact(Now, tx => tx.MarkBlobReleasedOnRouter(blob.BlobId));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Diagnose("warning", "file", $"couldn't release {blob.BlobId.ToString()[..8]} on the router: {e.Message}");
                return;
            }
        }
    }

    /// <summary>Retention: local copies fetched before <paramref name="cutoff"/> go, released or not.</summary>
    private void DeleteOldCopies(DateTimeOffset cutoff)
    {
        foreach (var local in Store.GetLocalBlobsFetchedBefore(cutoff))
        {
            Store.Transact(Now, tx => tx.SetLocalBlob(local.BlobId, local.FromMachine, local.Name, local.Size, "deleted", detail: "retention"));
            DeleteCopy(local);
        }
    }

    private void DeleteCopy(LocalBlob local)
    {
        try
        {
            var directory = Path.Combine(FilesDirectory, local.BlobId.ToString("N"));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException e)
        {
            Diagnose("warning", "file", $"couldn't delete the local copy of {local.Name}: {e.Message}");
        }
    }
}

/// <summary>A guess at a file's content type from its extension (what the recipient sees; it can be wrong).</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".json"] = "application/json", [".csv"] = "text/csv",
        [".html"] = "text/html", [".xml"] = "application/xml", [".pdf"] = "application/pdf", [".png"] = "image/png",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".zip"] = "application/zip",
        [".gz"] = "application/gzip", [".tar"] = "application/x-tar", [".log"] = "text/plain", [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml", [".py"] = "text/x-python", [".cs"] = "text/x-csharp",
    };

    public static string Of(string path) => Known.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
}
