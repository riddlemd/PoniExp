using Microsoft.Extensions.Logging;
using PoniExp.Client;

namespace PoniExp.Daemon;

/// <summary>Encryption key rotation (see docs/plan.md, Phase 11): by hand, and every Daemon:EncryptionKeyLifetime.</summary>
public sealed partial class Daemon
{
    private readonly SemaphoreSlim _rotating = new(1, 1);

    internal async Task<LocalKeys> RotateEncryptionKeyAsync(CancellationToken cancellationToken)
    {
        await _rotating.WaitAsync(cancellationToken);
        try
        {
            await _relay.RotateEncryptionKeyAsync(_context.SaveKeys, cancellationToken);
        }
        finally
        {
            _rotating.Release();
        }

        Diagnose("info", "keys", $"encryption key rotated (issued {_context.Keys.EncryptionKeyIssuedOn:u}); the old one opens older envelopes until retention drops it");
        return Keys();
    }

    internal LocalKeys Keys() =>
        new(MachineId, _context.Keys.EncryptionKeyIssuedOn, _context.Keys.EncryptionKeyIssuedOn + _options.CurrentValue.EncryptionKeyLifetime,
            [.. _context.Keys.RetiredEncryptionKeys.Select(r => r.RetiredOn)]);

    private async Task RotateIfDueAsync(CancellationToken cancellationToken)
    {
        var keys = _context.Keys;
        var retention = _retention.CurrentValue.Envelopes;
        if (keys.RetiredEncryptionKeys.Any(r => Now - r.RetiredOn > retention) && keys.DropRetiredBefore(Now - retention) is > 0 and var dropped)
        {
            _context.SaveKeys?.Invoke(keys);
            _logger.LogInformation("Dropped {Count} retired encryption key(s) past retention", dropped);
        }

        if (_relay.IsConnected && Now - keys.EncryptionKeyIssuedOn >= _options.CurrentValue.EncryptionKeyLifetime && _rotating.CurrentCount > 0)
        {
            try
            {
                await RotateEncryptionKeyAsync(cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                Diagnose("warning", "keys", $"couldn't rotate the encryption key: {e.Message}");
            }
        }
    }
}
