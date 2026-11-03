using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
using OnkelBummel.Api.Push;

namespace OnkelBummel.Api.Publishing;

/// <param name="Duplicate">A repeat with a known Idempotency-Key; <see cref="Message"/> is the original.</param>
public sealed record PublishResult(MessageDto Message, bool Duplicate);

/// <summary>Checks a message, stores it with its attachments, tells the live connections and queues the pushes.</summary>
public sealed class MessagePublisher(
    OnkelBummelDbContext db,
    AttachmentStore attachments,
    MessageEvents events,
    PushQueue push,
    IOptions<LimitsOptions> limits,
    TimeProvider time,
    ILogger<MessagePublisher> logger)
{
    public const int MaxTitleLength = 250;
    public const int MaxBodyLength = 32_000;
    public const int MaxTags = 10;
    public const int MaxTagLength = 64;
    public const int MaxActions = 3;
    public const int MaxLabelLength = 40;
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>A repeat with the same Idempotency-Key within this time returns the first message.</summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    private static readonly HashSet<string> ActionMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    // Would run script in the web page or open local files when clicked.
    private static readonly HashSet<string> BlockedClickSchemes = ["javascript", "data", "vbscript", "file"];

    /// <summary>Stores the message; throws <see cref="PublishException"/> for anything the sender has to fix.</summary>
    /// <param name="files">Uploaded files, in addition to <see cref="PublishRequest.Images"/>.</param>
    public async Task<PublishResult> PublishAsync(
        ClaimsPrincipal sender,
        string source,
        PublishRequest request,
        IReadOnlyList<IncomingFile> files,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var message = Validate(request, source);
        var incoming = files.Concat(Images(request)).ToList();
        if (incoming.Count > limits.Value.MaxFilesPerMessage)
            throw PublishException.Invalid($"Höchstens {limits.Value.MaxFilesPerMessage} Anhänge pro Nachricht.");
        if (message.Body.Length == 0 && incoming.Count == 0)
            throw PublishException.Invalid("Es fehlt der Text („message“) oder ein Anhang.");
        if (!sender.MaySendTo(message.Topic))
            throw PublishException.Forbidden(message.Topic);

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (idempotencyKey.Length > MaxIdempotencyKeyLength)
                throw PublishException.Invalid($"Der Idempotency-Key ist länger als {MaxIdempotencyKeyLength} Zeichen.");
            if (await FindRepeatAsync(idempotencyKey, cancellationToken) is { } original)
                return new PublishResult(original, Duplicate: true);
            message.IdempotencyKey = idempotencyKey;
        }

        var stored = new List<Attachment>();
        try
        {
            foreach (var file in incoming)
                stored.Add(await attachments.SaveAsync(file, cancellationToken));
            message.Attachments.AddRange(stored);
            message.CreatedAt = time.GetUtcNow().UtcDateTime;

            // A topic comes with its first message; OR IGNORE keeps two first messages at once from clashing.
            await db.Database.ExecuteSqlAsync(
                $"""INSERT OR IGNORE INTO "Topics" ("Name", "Muted") VALUES ({message.Topic}, 0)""", cancellationToken);
            db.Messages.Add(message);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && message.IdempotencyKey is not null)
        {
            // The same key arrived twice at the same moment; the other request won.
            foreach (var attachment in stored)
                attachments.Delete(attachment);
            db.ChangeTracker.Clear();
            var original = await FindRepeatAsync(message.IdempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency key clashed, but no message has it.", ex);
            return new PublishResult(original, Duplicate: true);
        }
        catch
        {
            foreach (var attachment in stored)
                attachments.Delete(attachment);
            throw;
        }

        OnkelBummelMetrics.AttachmentsStored.Inc(stored.Count);
        OnkelBummelMetrics.AttachmentBytes.Inc(stored.Sum(a => a.Size));
        var dto = MessageDto.From(message);
        events.Publish(new LiveEvent(MessageEvents.MessageType, message.Topic, dto));
        push.Enqueue(message.Id);
        logger.LogInformation("Message {Id} stored in {Topic} from {Source} with {Attachments} attachments",
            message.Id, message.Topic, source, stored.Count);
        return new PublishResult(dto, Duplicate: false);
    }

    /// <summary>The message stored under the key in the last 24 hours. An older one gives the key up for reuse.</summary>
    private async Task<MessageDto?> FindRepeatAsync(string key, CancellationToken cancellationToken)
    {
        var existing = await db.Messages.AsNoTracking()
            .Include(m => m.Attachments).Include(m => m.Actions)
            .SingleOrDefaultAsync(m => m.IdempotencyKey == key, cancellationToken);
        if (existing is null)
            return null;
        if (existing.CreatedAt >= time.GetUtcNow().UtcDateTime - IdempotencyWindow)
            return MessageDto.From(existing);

        await db.Messages.Where(m => m.Id == existing.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IdempotencyKey, (string?)null), cancellationToken);
        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 }; // SQLITE_CONSTRAINT_UNIQUE

    private static IEnumerable<IncomingFile> Images(PublishRequest request)
    {
        foreach (var image in request.Images ?? [])
        {
            var hasUrl = !string.IsNullOrWhiteSpace(image.Url);
            var hasData = !string.IsNullOrWhiteSpace(image.Data);
            if (hasUrl == hasData)
                throw PublishException.Invalid("Jedes Bild braucht genau eins von „url“ und „data“.");
            if (hasData)
            {
                yield return new InlineFile(image.Data!, image.Filename);
            }
            else
            {
                if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    throw PublishException.Invalid($"„{image.Url}“ ist keine http(s)-Adresse.");
                yield return new RemoteFile(url, image.Filename);
            }
        }
    }

    private static Message Validate(PublishRequest request, string source)
    {
        var topic = request.Topic?.Trim();
        if (!TopicNames.IsValid(topic))
            throw PublishException.Invalid($"„{request.Topic}“ ist kein gültiges Topic: a–z, 0–9, _ und -, höchstens 64 Zeichen.");

        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        if (title?.Length > MaxTitleLength)
            throw PublishException.Invalid($"Der Titel ist länger als {MaxTitleLength} Zeichen.");

        var body = request.Message?.Trim() ?? "";
        if (body.Length > MaxBodyLength)
            throw PublishException.Invalid($"Der Text ist länger als {MaxBodyLength} Zeichen.");

        var priority = request.Priority ?? 3;
        if (priority is < 1 or > 5)
            throw PublishException.Invalid("Die Priorität geht von 1 bis 5.");

        var tags = (request.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (tags.Count > MaxTags || tags.Any(t => t.Length > MaxTagLength))
            throw PublishException.Invalid($"Höchstens {MaxTags} Tags mit je höchstens {MaxTagLength} Zeichen.");

        var click = string.IsNullOrWhiteSpace(request.Click) ? null : request.Click.Trim();
        if (click is not null && (!Uri.TryCreate(click, UriKind.Absolute, out var clickUri) || BlockedClickSchemes.Contains(clickUri.Scheme)))
            throw PublishException.Invalid($"„{click}“ ist keine gültige Klick-Adresse.");

        var message = new Message
        {
            Topic = topic!,
            Title = title,
            Body = body,
            Priority = priority,
            Tags = tags,
            ClickUrl = click,
            Source = source,
        };

        var actions = request.Actions ?? [];
        if (actions.Count > MaxActions)
            throw PublishException.Invalid($"Höchstens {MaxActions} Actions pro Nachricht.");
        for (var index = 0; index < actions.Count; index++)
            message.Actions.Add(Validate(actions[index], index));

        return message;
    }

    private static MessageAction Validate(ActionRequest action, int index)
    {
        var label = action.Label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
            throw PublishException.Invalid($"Action {index + 1} braucht ein „label“ mit höchstens {MaxLabelLength} Zeichen.");
        if (!Uri.TryCreate(action.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw PublishException.Invalid($"Action {index + 1} braucht eine http(s)-Adresse in „url“.");
        var method = (action.Method ?? "POST").Trim().ToUpperInvariant();
        if (!ActionMethods.Contains(method))
            throw PublishException.Invalid($"Action {index + 1}: Methode „{action.Method}“ geht nicht, nur {string.Join(", ", ActionMethods)}.");
        if (action.Body?.Length > MaxBodyLength)
            throw PublishException.Invalid($"Action {index + 1}: „body“ ist länger als {MaxBodyLength} Zeichen.");
        var headers = action.Headers ?? [];
        if (headers.Count > 20 || headers.Keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Any(char.IsControl)) || headers.Values.Any(v => v.Any(char.IsControl)))
            throw PublishException.Invalid($"Action {index + 1}: ungültige „headers“.");

        return new MessageAction
        {
            Index = index,
            Label = label,
            Method = method,
            Url = url.AbsoluteUri,
            Headers = new Dictionary<string, string>(headers),
            Body = action.Body,
            Clear = action.Clear,
        };
    }
}
