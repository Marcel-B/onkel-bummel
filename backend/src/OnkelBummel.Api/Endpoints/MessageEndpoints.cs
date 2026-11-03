using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Actions;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Publishing;

namespace OnkelBummel.Api.Endpoints;

/// <param name="HasMore">More messages lie beyond the last one; page on with its id as <c>before</c> (or <c>after</c>).</param>
public sealed record MessagePage(List<MessageDto> Messages, bool HasMore);

/// <param name="Topic">Only this topic; all topics when null.</param>
/// <param name="UpToId">Only messages up to this id, i.e. the ones the client has seen; all when null.</param>
public sealed record MarkReadRequest(string? Topic, long? UpToId);

public sealed record MarkReadResult(int Count);

public static class MessageEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapMessageEndpoints(this RouteGroupBuilder api)
    {
        var messages = api.MapGroup("/messages").RequireAuthorization(Policies.User);

        messages.MapGet("/", ListAsync).WithSummary("History, newest first; with after= oldest first");
        messages.MapGet("/{id:long}", GetAsync);
        messages.MapDelete("/{id:long}", DeleteAsync);
        messages.MapPost("/{id:long}/read", MarkReadAsync);
        messages.MapPost("/read", MarkAllReadAsync).WithSummary("Mark a topic or everything as read");
        messages.MapPost("/{id:long}/actions/{index:int}", RunActionAsync).WithSummary("Run an action button");

        return api;
    }

    private static async Task<IResult> ListAsync(
        OnkelBummelDbContext db,
        string? topics,
        string? q,
        string? unread,
        [FromQuery(Name = "has_image")] string? hasImage,
        long? before,
        long? after,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
            return Problems.BadRequest($"„limit“ geht von 1 bis {MaxLimit}.");

        IQueryable<Message> query = db.Messages.AsNoTracking();
        if (Topics.Parse(topics) is { } names)
            query = query.Where(m => names.Contains(m.Topic));
        if (MessageSearch.ToQuery(q) is { } match)
        {
            var hits = db.Database.SqlQuery<long>($"""SELECT rowid AS "Value" FROM "MessagesFts" WHERE "MessagesFts" MATCH {match}""");
            query = query.Where(m => hits.Contains(m.Id));
        }
        if (Flag(unread))
            query = query.Where(m => m.ReadAt == null);
        if (Flag(hasImage))
            query = query.Where(m => m.Attachments.Any(a => a.ContentType.StartsWith("image/")));
        if (before is not null)
            query = query.Where(m => m.Id < before);
        if (after is not null)
            query = query.Where(m => m.Id > after);

        // Catching up after a known id goes forward in time, so paging on with the last id misses nothing.
        query = after is not null && before is null ? query.OrderBy(m => m.Id) : query.OrderByDescending(m => m.Id);
        var page = await query
            .Include(m => m.Attachments).Include(m => m.Actions).AsSplitQuery()
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new MessagePage(page.Take(take).Select(MessageDto.From).ToList(), page.Count > take));
    }

    private static async Task<IResult> GetAsync(long id, OnkelBummelDbContext db, CancellationToken cancellationToken)
    {
        var message = await db.Messages.AsNoTracking()
            .Include(m => m.Attachments).Include(m => m.Actions).AsSplitQuery()
            .SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        return message is null ? Problems.MessageNotFound(id) : TypedResults.Ok(MessageDto.From(message));
    }

    private static async Task<IResult> DeleteAsync(
        long id, OnkelBummelDbContext db, AttachmentStore store, MessageEvents events, CancellationToken cancellationToken)
    {
        var message = await db.Messages.Include(m => m.Attachments).SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is null)
            return Problems.MessageNotFound(id);

        db.Messages.Remove(message);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var attachment in message.Attachments)
            store.Delete(attachment);
        events.Publish(new LiveEvent(MessageEvents.DeletedType, message.Topic, new DeletedEvent(id, message.Topic)));
        return TypedResults.NoContent();
    }

    private static async Task<IResult> MarkReadAsync(
        long id, OnkelBummelDbContext db, MessageEvents events, TimeProvider time, CancellationToken cancellationToken)
    {
        var topic = await db.Messages.Where(m => m.Id == id).Select(m => m.Topic).SingleOrDefaultAsync(cancellationToken);
        if (topic is null)
            return Problems.MessageNotFound(id);

        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.Messages.Where(m => m.Id == id && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), cancellationToken);
        if (changed > 0)
            events.Publish(new LiveEvent(MessageEvents.ReadType, topic, new ReadEvent([id], null, null, now)));
        return TypedResults.NoContent();
    }

    private static async Task<IResult> MarkAllReadAsync(
        MarkReadRequest? request, OnkelBummelDbContext db, MessageEvents events, TimeProvider time, CancellationToken cancellationToken)
    {
        var topic = string.IsNullOrWhiteSpace(request?.Topic) ? null : request.Topic.Trim();
        // Without a limit, only what exists now: a message arriving meanwhile stays unread.
        var upToId = request?.UpToId ?? await db.Messages.MaxAsync(m => (long?)m.Id, cancellationToken) ?? 0;
        var now = time.GetUtcNow().UtcDateTime;

        var query = db.Messages.Where(m => m.ReadAt == null && m.Id <= upToId);
        if (topic is not null)
            query = query.Where(m => m.Topic == topic);
        var count = await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), cancellationToken);

        if (count > 0)
            events.Publish(new LiveEvent(MessageEvents.ReadType, topic, new ReadEvent([], topic, upToId, now)));
        return TypedResults.Ok(new MarkReadResult(count));
    }

    private static async Task<IResult> RunActionAsync(long id, int index, ActionRunner runner, CancellationToken cancellationToken) =>
        await runner.RunAsync(id, index, cancellationToken) is { } result
            ? TypedResults.Ok(result)
            : Problems.NotFound($"Nachricht {id} hat keine Action {index}.");

    /// <summary>"1" and "true" switch a filter on.</summary>
    private static bool Flag(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

internal static class Topics
{
    /// <summary>Comma-separated topic names; null when there are none, i.e. all topics.</summary>
    public static List<string>? Parse(string? topics)
    {
        var names = (topics ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t != TopicNames.All)
            .Distinct()
            .ToList();
        return names.Count == 0 ? null : names;
    }
}

internal static class Problems
{
    public static IResult BadRequest(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Ungültige Anfrage");

    public static IResult NotFound(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Nicht gefunden");

    public static IResult MessageNotFound(long id) => NotFound($"Nachricht {id} gibt es nicht.");
}
