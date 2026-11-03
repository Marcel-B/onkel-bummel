using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Monitoring;

/// <summary>Counts messages, devices, tokens and storage for the metrics, once a minute.</summary>
public sealed class InventoryMetricsWorker(
    IServiceScopeFactory scopes,
    IOptions<MonitoringOptions> options,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<InventoryMetricsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.InventoryEnabled)
            return;

        using var timer = new PeriodicTimer(options.Value.InventoryInterval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await UpdateAsync(scope.ServiceProvider.GetRequiredService<OnkelBummelDbContext>(), storage.Value, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Counting for the metrics failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task UpdateAsync(OnkelBummelDbContext db, StorageOptions storage, CancellationToken cancellationToken)
    {
        OnkelBummelMetrics.Messages.WithLabels("total").Set(await db.Messages.CountAsync(cancellationToken));
        OnkelBummelMetrics.Messages.WithLabels("unread").Set(await db.Messages.CountAsync(m => m.ReadAt == null, cancellationToken));
        OnkelBummelMetrics.Devices.Set(await db.Devices.CountAsync(cancellationToken));
        OnkelBummelMetrics.PublishTokens.Set(await db.PublishTokens.CountAsync(cancellationToken));
        OnkelBummelMetrics.DatabaseSize.Set(FileSize(storage.DatabasePath) + FileSize(storage.DatabasePath + "-wal"));
        OnkelBummelMetrics.AttachmentsSize.Set(FolderSize(storage.AttachmentsPath));
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long FolderSize(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;
}
