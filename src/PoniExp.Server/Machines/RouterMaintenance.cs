using Microsoft.Extensions.Options;
using PoniExp.Server.Admin;

namespace PoniExp.Server.Machines;

/// <summary>Sweeps expired join requests (telling their waiting clients) and expired queue entries.</summary>
public sealed class RouterMaintenance(AdminService admin, MessageQueue queue, TrafficLog traffic, Blobs.BlobRegistry blobs, IOptionsMonitor<RouterOptions> options, TimeProvider time,
    ILogger<RouterMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await admin.ExpirePendingAsync();
                await queue.PruneAsync();
                traffic.Prune();
                blobs.Sweep();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Maintenance failed");
            }

            await Task.Delay(options.CurrentValue.MaintenanceInterval, time, stoppingToken);
        }
    }
}
