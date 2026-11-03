namespace OnkelBummel.Api.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// The server answers <c>/metrics</c> only on this port, so the metrics stay private even if the app port
    /// is ever forwarded as a whole. The port must be among the ones Kestrel listens on (ASPNETCORE_HTTP_PORTS).
    /// </summary>
    public int MetricsPort { get; set; } = 9464;

    /// <summary>Turns counting messages, devices and storage for the metrics off, e.g. for tests.</summary>
    public bool InventoryEnabled { get; set; } = true;

    public TimeSpan InventoryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public LokiOptions Loki { get; set; } = new();
}

public sealed class LokiOptions
{
    /// <summary>Address of Loki, such as http://monitoring:3100; without it, logs are not shipped.</summary>
    public string? Url { get; set; }

    /// <summary>Value of the <c>host</c> label, such as "proxmox"; inside a container the machine name is random.</summary>
    public string Host { get; set; } = Environment.MachineName;

    /// <summary>Lines are collected this long and then pushed together.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 500;

    /// <summary>Lines waiting while Loki is slow; beyond that, new lines are dropped instead of blocking the app.</summary>
    public int QueueLimit { get; set; } = 10_000;
}
