using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Messages;

namespace OnkelBummel.Api.Push;

/// <summary>
/// The JSON APNs delivers to the phone. Next to <c>aps</c>, the key <c>ob</c> carries what the app and its
/// Notification Service Extension need: the message id, the thumbnail link and the action buttons.
/// </summary>
public static class PushPayload
{
    /// <summary>APNs takes at most 4 KB for an alert.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Category the app registers with „Gelesen“ and „Öffnen“; the extension swaps it for one with the actions.</summary>
    public const string Category = "message";

    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 1500;

    // Keeps umlauts and emojis as they are instead of \u escapes, which would eat into the 4 KB.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="topicName">Display name of the topic, or its name.</param>
    /// <param name="badge">Unread messages, shown on the app icon.</param>
    /// <param name="thumbnailUrl">Signed link to the first image; null without images.</param>
    public static byte[] Create(Message message, string topicName, int badge, string? thumbnailUrl)
    {
        var emoji = Emojis.Prefix(message.Tags);
        var (title, subtitle) = message.Title is { } own
            ? (emoji + own, topicName)
            : (emoji + topicName, null);
        var body = message.Body.Length > 0 ? message.Body : FallbackBody(message);

        for (var bodyLength = MaxBodyLength; ; bodyLength /= 2)
        {
            var payload = Build(message, Truncate(title, MaxTitleLength), subtitle, Truncate(body, bodyLength), badge, thumbnailUrl);
            if (payload.Length <= MaxBytes || bodyLength < 16)
                return payload;
        }
    }

    /// <summary>1–2 wait quietly in the notification center, 4–5 break through focus modes.</summary>
    public static string InterruptionLevel(int priority) => priority switch
    {
        <= 2 => "passive",
        3 => "active",
        _ => "time-sensitive",
    };

    /// <summary>APNs priority: 10 delivers at once, 5 lets the phone pick a moment that saves power.</summary>
    public static int ApnsPriority(int priority) => priority <= 2 ? 5 : 10;

    private static byte[] Build(Message message, string title, string? subtitle, string body, int badge, string? thumbnailUrl)
    {
        var alert = new JsonObject { ["title"] = title };
        if (subtitle is not null)
            alert["subtitle"] = subtitle;
        alert["body"] = body;

        var aps = new JsonObject
        {
            ["alert"] = alert,
            ["thread-id"] = message.Topic,
            ["badge"] = badge,
            ["interruption-level"] = InterruptionLevel(message.Priority),
            ["category"] = Category,
        };
        if (message.Priority >= 3)
            aps["sound"] = "default";
        // The extension only runs with this flag: it attaches the image and sets up the action buttons.
        if (thumbnailUrl is not null || message.Actions.Count > 0)
            aps["mutable-content"] = 1;

        var ob = new JsonObject { ["id"] = message.Id, ["topic"] = message.Topic };
        if (message.ClickUrl is not null)
            ob["click"] = message.ClickUrl;
        if (thumbnailUrl is not null)
            ob["thumbnail"] = thumbnailUrl;
        if (message.Actions.Count > 0)
        {
            ob["actions"] = new JsonArray(message.Actions.OrderBy(a => a.Index)
                .Select(a => (JsonNode)new JsonObject { ["index"] = a.Index, ["label"] = a.Label })
                .ToArray());
        }

        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["aps"] = aps, ["ob"] = ob }, Json);
    }

    private static string FallbackBody(Message message) => message.Attachments switch
    {
        [] => "",
        [var only] => only.ContentType.StartsWith("image/", StringComparison.Ordinal) ? "Bild" : only.FileName,
        var all => $"{all.Count} Anhänge",
    };

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        var cut = text[..(maxLength - 1)];
        // Never split an emoji in half.
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];
        return cut.TrimEnd() + "…";
    }
}
