using Prometheus;

namespace OnkelBummel.Api.Monitoring;

/// <summary>
/// Metrics of the server for Prometheus. Labels only take a few fixed values; <c>/metrics</c> has no login, so
/// nothing about users, topics or content goes in here.
/// </summary>
public static class OnkelBummelMetrics
{
    // The label is called "task" because Prometheus sets "job" itself, to the scrape job, and renames a "job"
    // label of the app to "exported_job".
    public static readonly string[] Jobs = ["retention", "cleanup", "backup"];

    public static readonly string[] Sources = ["api", "grafana"];

    public static readonly string[] ReceiveResults = ["stored", "invalid", "unauthorized", "forbidden", "too_large", "duplicate"];

    public static readonly string[] PushResults = ["ok", "failed", "invalid_token"];

    public static readonly Counter MessagesReceived = Metrics.CreateCounter(
        "onkelbummel_messages_received_total",
        "Messages sent to the server, by source (api, grafana) and result (stored, invalid, unauthorized, forbidden, too_large, duplicate).",
        "source", "result");

    public static readonly Counter AttachmentsStored = Metrics.CreateCounter(
        "onkelbummel_attachments_stored_total", "Attachments stored with new messages.");

    public static readonly Counter AttachmentBytes = Metrics.CreateCounter(
        "onkelbummel_attachment_bytes_total", "Bytes of attachments stored with new messages.");

    public static readonly Counter PushSent = Metrics.CreateCounter(
        "onkelbummel_push_sent_total", "Pushes handed to APNs, by result (ok, failed, invalid_token).", "result");

    public static readonly Histogram PushDuration = Metrics.CreateHistogram(
        "onkelbummel_push_duration_seconds", "Time until APNs answered a push.",
        new HistogramConfiguration { Buckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });

    public static readonly Gauge PushQueueLength = Metrics.CreateGauge(
        "onkelbummel_push_queue_length", "Pushes waiting to be sent.");

    public static readonly Counter ActionsExecuted = Metrics.CreateCounter(
        "onkelbummel_actions_executed_total", "Action buttons run by the server, by result (ok, failed).", "result");

    public static readonly Gauge SseConnections = Metrics.CreateGauge(
        "onkelbummel_sse_connections", "Open live connections (server-sent events).");

    public static readonly Gauge Messages = Metrics.CreateGauge(
        "onkelbummel_messages", "Messages in the database, by state (total, unread).", "state");

    public static readonly Gauge Devices = Metrics.CreateGauge(
        "onkelbummel_devices", "Registered devices.");

    public static readonly Gauge PublishTokens = Metrics.CreateGauge(
        "onkelbummel_publish_tokens", "Publish tokens.");

    public static readonly Gauge DatabaseSize = Metrics.CreateGauge(
        "onkelbummel_database_size_bytes", "Size of the SQLite database file including its write-ahead log.");

    public static readonly Gauge AttachmentsSize = Metrics.CreateGauge(
        "onkelbummel_attachments_size_bytes", "Size of all attachment files including thumbnails.");

    public static readonly Counter JobRuns = Metrics.CreateCounter(
        "onkelbummel_job_runs_total", "Runs of background jobs, by task and result (success, failure).", "task", "result");

    public static readonly Histogram JobDuration = Metrics.CreateHistogram(
        "onkelbummel_job_duration_seconds", "Duration of background job runs.",
        new HistogramConfiguration { LabelNames = ["task"], Buckets = [0.1, 0.5, 1, 5, 15, 60, 300, 900] });

    public static readonly Gauge JobLastSuccess = Metrics.CreateGauge(
        "onkelbummel_job_last_success_timestamp_seconds", "Unix time of the last successful run of a background job, by task.", "task");

    // Its own series because the shared alert "backup too old" looks for <app>_backup_last_success_timestamp_seconds.
    // Absent until the first backup, so a server without Nextcloud access is not alarmed.
    public static readonly Gauge BackupLastSuccess = Metrics.CreateGauge(
        "onkelbummel_backup_last_success_timestamp_seconds", "Unix time of the last successful backup.",
        new GaugeConfiguration { SuppressInitialValue = true });

    // prometheus-net calls it process_working_set_bytes; the shared overview dashboard expects this name.
    // On Linux, the working set is the resident set.
    private static readonly Gauge ResidentMemory = Metrics.CreateGauge(
        "process_resident_memory_bytes", "Resident memory size in bytes.");

    public static readonly Counter LokiLinesPushed = Metrics.CreateCounter(
        "onkelbummel_loki_lines_pushed_total", "Log lines pushed to Loki.");

    public static readonly Counter LokiLinesDropped = Metrics.CreateCounter(
        "onkelbummel_loki_lines_dropped_total", "Log lines dropped, by reason (queue_full, push_failed).", "reason");

    static OnkelBummelMetrics()
    {
        // Series that exist from the start read as 0 instead of missing, which keeps rate() and alerts simple.
        foreach (var source in Sources)
        foreach (var result in ReceiveResults)
            MessagesReceived.WithLabels(source, result);
        foreach (var result in PushResults)
            PushSent.WithLabels(result);
        ActionsExecuted.WithLabels("ok");
        ActionsExecuted.WithLabels("failed");
        foreach (var job in Jobs)
        {
            JobRuns.WithLabels(job, "success");
            JobRuns.WithLabels(job, "failure");
            JobDuration.WithLabels(job);
        }
        Messages.WithLabels("total");
        Messages.WithLabels("unread");
        LokiLinesDropped.WithLabels("queue_full");
        LokiLinesDropped.WithLabels("push_failed");

        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => ResidentMemory.Set(Environment.WorkingSet));
    }

    /// <summary>Registers all metrics, so they show up from the first scrape on and not only once they are used.</summary>
    public static void Register()
    {
        // Calling any member runs the static constructor; there is nothing else to do.
    }

    /// <summary>Runs a background job and records its result and duration; a run cut short by shutdown counts as neither.</summary>
    public static async Task<T> MeasureJobAsync<T>(string job, TimeProvider time, Func<Task<T>> run, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            var result = await run();
            JobRuns.WithLabels(job, "success").Inc();
            JobLastSuccess.WithLabels(job).Set(time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
            JobDuration.WithLabels(job).Observe(time.GetElapsedTime(started).TotalSeconds);
            return result;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            JobRuns.WithLabels(job, "failure").Inc();
            JobDuration.WithLabels(job).Observe(time.GetElapsedTime(started).TotalSeconds);
            throw;
        }
    }
}
