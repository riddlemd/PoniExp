using System.Text;

namespace PoniExp.Core.Protocol;

/// <summary>
/// What makes a message well formed. The sender checks before signing and the recipient checks again,
/// because the router can't: from phase 2 the content is encrypted.
/// </summary>
public static class MessageRules
{
    /// <summary>Null if the content is well formed, otherwise why not.</summary>
    public static string? CheckContent(MessageContent content)
    {
        if (!Limits.IsValidEndpoint(content.FromEndpoint) || !Limits.IsValidEndpoint(content.ToEndpoint))
        {
            return $"an endpoint must be {Limits.EndpointRule}";
        }

        if (!Enum.IsDefined(content.Type))
        {
            return "unknown message type";
        }

        if (content.Status is { } status && (!Enum.IsDefined(status) || content.ReplyTo is null))
        {
            return "a status is only for replies (set ReplyTo)";
        }

        if (content.ConversationId == Guid.Empty || content.OperationId == Guid.Empty || content.ReplyTo == Guid.Empty)
        {
            return "conversation, operation and reply-to ids can't be empty";
        }

        if (content.Subject is { Length: 0 or > Limits.MaxSubjectLength })
        {
            return $"a subject is 1-{Limits.MaxSubjectLength} characters (or none)";
        }

        if (!Limits.IsValidContentType(content.ContentType))
        {
            return "the content type must look like 'text/plain'";
        }

        if (content.Metadata is null || content.Metadata.Count > Limits.MaxMetadataEntries
            || content.Metadata.Any(m => !Limits.IsValidMetadataKey(m.Key) || m.Value is null || m.Value.Length > Limits.MaxMetadataValueLength))
        {
            return $"metadata is at most {Limits.MaxMetadataEntries} entries, keys [A-Za-z0-9._:-] up to {Limits.MaxMetadataKeyLength}, values up to {Limits.MaxMetadataValueLength} characters";
        }

        if (content.Body is null || Encoding.UTF8.GetByteCount(content.Body) > Limits.MaxBodyBytes)
        {
            return $"the body is at most {Limits.MaxBodyBytes} bytes";
        }

        if (content.Attachments is { } attachments)
        {
            if (content.Type != MessageType.Message || attachments.Count > Limits.MaxAttachments)
            {
                return $"only messages carry attachments, at most {Limits.MaxAttachments}";
            }

            if (attachments.Any(a => a is null || a.BlobId == Guid.Empty || !Files.BlobCrypto.IsValidFileName(a.Name) || !Limits.IsValidContentType(a.ContentType)
                                     || a.Size < 0 || a.ChunkCount != Files.BlobCrypto.ChunkCountOf(a.Size) || a.ChunksHash is not { Length: 64 }
                                     || !Files.BlobCrypto.IsValidKey(a.Key)))
            {
                return "an attachment needs a blob id, a plain file name, a content type, its size and chunk count, a 256-bit key and its chunks' hash";
            }
        }

        return null;
    }
}
