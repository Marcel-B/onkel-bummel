using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Auth;

/// <param name="Token">The key itself; shown once, only its hash is stored.</param>
public sealed record CreatedPublishToken(string Id, string Name, List<string> Topics, DateTime CreatedAt, string Token);

public sealed record PublishTokenInfo(string Id, string Name, List<string> Topics, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Creates, lists and checks the static keys of senders.</summary>
public static class PublishTokens
{
    public const string Prefix = "ob_";

    public const int MaxNameLength = 100;

    /// <summary>Hex SHA-256; the tokens carry 256 random bits, so a plain hash is as good as a slow one.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Checks name and topics; returns the cleaned-up topics or an error in German for the user.</summary>
    public static (List<string>? Topics, string? Error) Validate(string? name, IEnumerable<string>? topics)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            return (null, $"Der Name fehlt oder ist länger als {MaxNameLength} Zeichen.");
        var cleaned = (topics ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (cleaned.Count == 0)
            return (null, "Es fehlt mindestens ein Topic (oder * für alle).");
        if (cleaned.FirstOrDefault(t => t != TopicNames.All && !TopicNames.IsValid(t)) is { } invalid)
            return (null, $"„{invalid}“ ist kein gültiger Topic-Name.");
        return (cleaned.Contains(TopicNames.All) ? [TopicNames.All] : cleaned, null);
    }

    public static async Task<CreatedPublishToken> CreateAsync(
        OnkelBummelDbContext db, string name, List<string> topics, TimeProvider time, CancellationToken cancellationToken)
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var entity = new PublishToken
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Name = name.Trim(),
            Hash = Hash(token),
            Topics = topics,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.PublishTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedPublishToken(entity.Id, entity.Name, entity.Topics, entity.CreatedAt, token);
    }

    public static Task<List<PublishTokenInfo>> ListAsync(OnkelBummelDbContext db, CancellationToken cancellationToken) =>
        db.PublishTokens
            .OrderBy(t => t.CreatedAt)
            .Select(t => new PublishTokenInfo(t.Id, t.Name, t.Topics, t.CreatedAt, t.LastUsedAt))
            .ToListAsync(cancellationToken);

    public static async Task<bool> DeleteAsync(OnkelBummelDbContext db, string id, CancellationToken cancellationToken) =>
        await db.PublishTokens.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
