using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Endpoints;

public sealed record LastMessageDto(long Id, string? Title, string Message, int Priority, DateTime CreatedAt);

public sealed record TopicDto(
    string Name, string? DisplayName, string? Icon, string? Color, bool Muted, int? RetentionDays, int Total, int Unread, LastMessageDto? LastMessage);

public static partial class TopicEndpoints
{
    public const int MaxDisplayNameLength = 64;
    public const int MaxIconLength = 32;
    public const int MaxRetentionDays = 36_500;

    // The list shows a line of the last message, not all of it.
    private const int PreviewLength = 200;

    public static RouteGroupBuilder MapTopicEndpoints(this RouteGroupBuilder api)
    {
        var topics = api.MapGroup("/topics").RequireAuthorization(Policies.User);

        topics.MapGet("/", async (OnkelBummelDbContext db, CancellationToken cancellationToken) =>
                TypedResults.Ok(await QueryAsync(db, null, cancellationToken)))
            .WithSummary("Topics with counts and their last message, most recently active first");

        topics.MapPatch("/{name}", PatchAsync)
            .WithSummary("Change display name, icon, color, muting or retention; null clears a value");

        return api;
    }

    /// <summary>Takes only the fields present in the body (JSON merge patch), so a client can change one at a time.</summary>
    private static async Task<IResult> PatchAsync(string name, JsonElement body, OnkelBummelDbContext db, CancellationToken cancellationToken)
    {
        var topic = await db.Topics.FindAsync([name], cancellationToken);
        if (topic is null)
            return Problems.NotFound($"Topic „{name}“ gibt es nicht.");
        if (body.ValueKind != JsonValueKind.Object)
            return Problems.BadRequest("Erwartet wird ein JSON-Objekt.");

        foreach (var property in body.EnumerateObject())
        {
            var value = property.Value;
            var error = property.Name.ToLowerInvariant() switch
            {
                "displayname" => SetText(value, MaxDisplayNameLength, v => topic.DisplayName = v),
                "icon" => SetText(value, MaxIconLength, v => topic.Icon = v),
                "color" => value.ValueKind == JsonValueKind.Null ? Set(() => topic.Color = null)
                    : value.ValueKind == JsonValueKind.String && Color().IsMatch(value.GetString()!) ? Set(() => topic.Color = value.GetString()!.ToLowerInvariant())
                    : "„color“ ist eine Farbe wie #3b82f6.",
                "muted" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? Set(() => topic.Muted = value.GetBoolean())
                    : "„muted“ ist true oder false.",
                "retentiondays" => value.ValueKind == JsonValueKind.Null ? Set(() => topic.RetentionDays = null)
                    : value.TryGetInt32(out var days) && days is >= 1 and <= MaxRetentionDays ? Set(() => topic.RetentionDays = days)
                    : $"„retentionDays“ ist eine Zahl von 1 bis {MaxRetentionDays} oder null.",
                _ => $"„{property.Name}“ lässt sich nicht ändern.",
            };
            if (error is not null)
                return Problems.BadRequest(error);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await QueryAsync(db, name, cancellationToken)).Single());
    }

    private static async Task<List<TopicDto>> QueryAsync(OnkelBummelDbContext db, string? name, CancellationToken cancellationToken)
    {
        var query = db.Topics.AsNoTracking();
        if (name is not null)
            query = query.Where(t => t.Name == name);
        var topics = await query
            .Select(t => new TopicDto(
                t.Name, t.DisplayName, t.Icon, t.Color, t.Muted, t.RetentionDays,
                t.Messages.Count(),
                t.Messages.Count(m => m.ReadAt == null),
                t.Messages.OrderByDescending(m => m.Id)
                    .Select(m => new LastMessageDto(m.Id, m.Title, m.Body.Substring(0, PreviewLength), m.Priority, m.CreatedAt))
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return topics.OrderByDescending(t => t.LastMessage?.Id ?? 0).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private static string? SetText(JsonElement value, int maxLength, Action<string?> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return Set(() => set(null));
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim().Length > maxLength)
            return $"Erwartet wird Text mit höchstens {maxLength} Zeichen oder null.";
        var text = value.GetString()!.Trim();
        return Set(() => set(text.Length == 0 ? null : text));
    }

    private static string? Set(Action set)
    {
        set();
        return null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Color();
}
