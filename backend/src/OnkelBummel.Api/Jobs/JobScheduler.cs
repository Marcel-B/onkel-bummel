using Microsoft.Extensions.Options;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Jobs;

/// <summary>Runs retention and cleanup daily at <c>Jobs:MaintenanceAt</c> and the backup nightly at <c>Backup:At</c>.</summary>
public sealed class JobScheduler(
    IServiceScopeFactory scopes,
    IOptions<JobsOptions> jobs,
    IOptions<BackupOptions> backup,
    TimeProvider time,
    ILogger<JobScheduler> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!jobs.Value.Enabled)
            return Task.CompletedTask;
        if (!backup.Value.IsConfigured)
            logger.LogInformation("Backup is off: Backup:NextcloudWebDavUrl, NextcloudUserName and NextcloudAppPassword are missing");

        return Task.WhenAll(
            DailyAsync(jobs.Value.MaintenanceAt, MaintenanceAsync, stoppingToken),
            DailyAsync(backup.Value.At, BackupAsync, stoppingToken));
    }

    private async Task MaintenanceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await OnkelBummelMetrics.MeasureJobAsync(RetentionJob.Name, time,
            () => services.GetRequiredService<RetentionJob>().RunAsync(cancellationToken), cancellationToken);
        await OnkelBummelMetrics.MeasureJobAsync(CleanupJob.Name, time,
            () => services.GetRequiredService<CleanupJob>().RunAsync(cancellationToken), cancellationToken);
    }

    private async Task BackupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<BackupJob>();
        // Without access nothing runs and nothing is counted, so the backup alert stays quiet.
        if (job.IsConfigured)
            await OnkelBummelMetrics.MeasureJobAsync(BackupJob.Name, time, () => job.RunAsync(cancellationToken), cancellationToken);
    }

    private async Task DailyAsync(TimeOnly at, Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNext(at, time.GetUtcNow(), time.LocalTimeZone), time, stoppingToken);
                await run(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job failed");
            }
        }
    }

    /// <summary>Time until the next <paramref name="at"/> in the given zone, which may be tomorrow.</summary>
    internal static TimeSpan UntilNext(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var next = local.Date + at.ToTimeSpan();
        if (next <= local)
            next = next.AddDays(1);
        // On the night the clocks go forward, a time in the skipped hour comes an hour later.
        if (zone.IsInvalidTime(next))
            next = next.AddHours(1);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), zone);
        var wait = nextUtc - now.UtcDateTime;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromMinutes(1);
    }
}
