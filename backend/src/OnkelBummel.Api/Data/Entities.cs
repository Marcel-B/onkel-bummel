namespace OnkelBummel.Api.Data;

/// <summary>Group of messages, created with its first message.</summary>
public sealed class Topic
{
    /// <summary>Lower-case name such as "garage"; see <see cref="TopicNames"/>.</summary>
    public required string Name { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Emoji or symbol name shown next to the topic.</summary>
    public string? Icon { get; set; }

    /// <summary>Hex color such as "#3b82f6".</summary>
    public string? Color { get; set; }

    /// <summary>Messages are kept, but no pushes go out.</summary>
    public bool Muted { get; set; }

    /// <summary>Messages older than this are deleted; null keeps them.</summary>
    public int? RetentionDays { get; set; }

    public List<Message> Messages { get; } = [];
}

public sealed class Message
{
    /// <summary>Increasing number, also the cursor for paging.</summary>
    public long Id { get; set; }

    public required string Topic { get; set; }

    public string? Title { get; set; }

    public required string Body { get; set; }

    /// <summary>1 (min) to 5 (urgent), 3 is normal.</summary>
    public int Priority { get; set; } = 3;

    public List<string> Tags { get; set; } = [];

    public string? ClickUrl { get; set; }

    /// <summary>Where it came from, e.g. "api" or "grafana".</summary>
    public required string Source { get; set; }

    /// <summary>Key from the sender's Idempotency-Key header; a repeat with the same key returns this message.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }

    public List<Attachment> Attachments { get; } = [];

    public List<MessageAction> Actions { get; } = [];
}

public sealed class Attachment
{
    /// <summary>Random id, also the file name on disk.</summary>
    public required string Id { get; set; }

    public long MessageId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long Size { get; set; }

    /// <summary>Hex SHA-256 of the file.</summary>
    public required string Sha256 { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Path relative to the attachments folder.</summary>
    public required string Path { get; set; }

    /// <summary>Path of the thumbnail relative to the attachments folder; null for files that are not images.</summary>
    public string? ThumbnailPath { get; set; }
}

/// <summary>Button on a message; the server makes the HTTP call when it is pressed.</summary>
public sealed class MessageAction
{
    public long MessageId { get; set; }

    /// <summary>Position on the message, 0 to 2.</summary>
    public int Index { get; set; }

    public required string Label { get; set; }

    public string Method { get; set; } = "POST";

    public required string Url { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public string? Body { get; set; }

    /// <summary>Marks the message as read once the call succeeded.</summary>
    public bool Clear { get; set; }

    /// <summary>HTTP status of the last run; 0 when the target did not answer.</summary>
    public int? LastStatus { get; set; }

    public DateTime? LastRunAt { get; set; }
}

public sealed class Device
{
    public required string Id { get; set; }

    /// <summary><c>sub</c> of the user who registered it.</summary>
    public required string OwnerSub { get; set; }

    public required string Name { get; set; }

    public required string ApnsToken { get; set; }

    /// <summary>"sandbox" for development builds, "production" for TestFlight and App Store.</summary>
    public required string Environment { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public List<Subscription> Subscriptions { get; } = [];
}

public sealed class Subscription
{
    public required string DeviceId { get; set; }

    /// <summary>Topic name, or "*" for all topics.</summary>
    public required string Topic { get; set; }
}

/// <summary>Static key for senders; only its hash is stored.</summary>
public sealed class PublishToken
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Hex SHA-256 of the token.</summary>
    public required string Hash { get; set; }

    /// <summary>Topics it may send to; "*" for all.</summary>
    public List<string> Topics { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
