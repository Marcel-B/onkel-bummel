using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Options;
using OnkelBummel.Api.Publishing;

namespace OnkelBummel.Api.Jobs;

/// <summary>Deletes messages older than the retention of their topic; their attachment files go with the next cleanup.</summary>
public sealed class RetentionJob(OnkelBummelDbContext db, TimeProvider time, ILogger<RetentionJob> logger)
{
    public const string Name = "retention";

    /// <returns>Number of messages deleted.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var topics = await db.Topics.AsNoTracking()
            .Where(t => t.RetentionDays != null)
            .Select(t => new { t.Name, Days = t.RetentionDays!.Value })
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var topic in topics)
        {
            var cutoff = now.AddDays(-topic.Days);
            // Attachments and actions follow through the foreign keys, the search index through its trigger.
            var count = await db.Messages.Where(m => m.Topic == topic.Name && m.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            if (count > 0)
                logger.LogInformation("Deleted {Count} messages older than {Days} days from {Topic}", count, topic.Days, topic.Name);
            deleted += count;
        }
        return deleted;
    }
}

/// <summary>
/// Deletes attachment files no message refers to any more (after retention, deletions or failed sends) and frees
/// Idempotency-Keys past their 24 hours.
/// </summary>
public sealed class CleanupJob(
    OnkelBummelDbContext db,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<CleanupJob> logger)
{
    public const string Name = "cleanup";

    // A message being sent right now has its files on disk before its row is in the database.
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <returns>Number of files deleted.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var keyCutoff = now - MessagePublisher.IdempotencyWindow;
        await db.Messages.Where(m => m.IdempotencyKey != null && m.CreatedAt < keyCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IdempotencyKey, (string?)null), cancellationToken);

        var root = storage.Value.AttachmentsPath;
        if (!Directory.Exists(root))
            return 0;

        var paths = await db.Attachments.AsNoTracking().Select(a => new { a.Path, a.ThumbnailPath }).ToListAsync(cancellationToken);
        var known = paths.SelectMany(p => new[] { p.Path, p.ThumbnailPath }).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.Contains(Path.GetRelativePath(root, file)) || File.GetLastWriteTimeUtc(file) > now - MinimumAge)
                continue;
            File.Delete(file);
            deleted++;
        }

        // Month folders of messages that are all gone.
        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }

        if (deleted > 0)
            logger.LogInformation("Deleted {Count} attachment files without a message", deleted);
        return deleted;
    }
}
