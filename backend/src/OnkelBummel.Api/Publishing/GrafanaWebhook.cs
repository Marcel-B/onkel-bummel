namespace OnkelBummel.Api.Publishing;

/// <summary>What a Grafana webhook contact point sends; only the fields used here.</summary>
public sealed class GrafanaPayload
{
    /// <summary>"firing" while at least one alert of the group fires, else "resolved".</summary>
    public string? Status { get; set; }

    public string? ExternalUrl { get; set; }

    public string? Title { get; set; }

    public string? Message { get; set; }

    public Dictionary<string, string>? GroupLabels { get; set; }

    public List<GrafanaAlert>? Alerts { get; set; }
}

public sealed class GrafanaAlert
{
    public string? Status { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public Dictionary<string, string>? Annotations { get; set; }
}

/// <summary>Turns a Grafana alert group into one message.</summary>
public static class GrafanaWebhook
{
    public const string Source = "grafana";

    public static PublishRequest ToRequest(GrafanaPayload payload, string? topic)
    {
        var alerts = payload.Alerts ?? [];
        var firing = alerts.Where(a => Is(a.Status, "firing")).ToList();
        var resolved = alerts.Where(a => Is(a.Status, "resolved")).ToList();
        var isFiring = payload.Status is null ? firing.Count > 0 : Is(payload.Status, "firing");
        var relevant = isFiring ? firing : resolved;
        if (relevant.Count == 0)
            relevant = alerts;

        var names = relevant.Select(Name).OfType<string>().Distinct().ToList();
        var name = payload.GroupLabels?.GetValueOrDefault("alertname")
            ?? names.Count switch
            {
                0 => "Grafana",
                1 => names[0],
                _ => $"{names[0]} (+{names.Count - 1})",
            };

        var lines = relevant.Select(Summary).ToList();
        // In a group that still fires, alerts that ended in the meantime are worth a line, too.
        if (isFiring)
            lines.AddRange(resolved.Select(a => $"Wieder gut: {Summary(a)}"));
        var body = lines.Count switch
        {
            0 => payload.Message ?? "",
            1 => lines[0],
            _ => string.Join('\n', lines.Select(l => $"• {l}")),
        };
        if (body.Length > MessagePublisher.MaxBodyLength)
            body = body[..(MessagePublisher.MaxBodyLength - 1)] + "…";

        return new PublishRequest
        {
            Topic = topic,
            Title = isFiring ? $"Alarm: {name}" : $"Wieder gut: {name}",
            Message = string.IsNullOrWhiteSpace(body) ? name : body,
            Priority = isFiring ? 4 : 2,
            Tags = ["grafana"],
            Click = payload.ExternalUrl,
        };
    }

    private static string? Name(GrafanaAlert alert) => alert.Labels?.GetValueOrDefault("alertname");

    private static string Summary(GrafanaAlert alert) =>
        alert.Annotations?.GetValueOrDefault("summary")
        ?? alert.Annotations?.GetValueOrDefault("description")
        ?? Name(alert)
        ?? "Alarm";

    private static bool Is(string? status, string expected) => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
