using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Publishing;
using OnkelBummel.Api.Push;

namespace OnkelBummel.Api.Endpoints;

public static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        var attachments = api.MapGroup("/attachments").RequireAuthorization(Policies.User);

        attachments.MapGet("/{id}", (string id, bool? download, HttpContext http, OnkelBummelDbContext db, AttachmentStore store, CancellationToken cancellationToken) =>
                ServeAsync(id, thumbnail: false, download == true, http, db, store, cancellationToken))
            .WithSummary("The original file; ?download=true to save it");

        attachments.MapGet("/{id}/thumb", (string id, HttpContext http, OnkelBummelDbContext db, AttachmentStore store, CancellationToken cancellationToken) =>
                ServeAsync(id, thumbnail: true, download: false, http, db, store, cancellationToken))
            .WithSummary("JPEG of at most 600 px on the longest side");

        // For the Notification Service Extension, which has no login: the signed link from the push is enough.
        api.MapGet("/attachments/{id}/push-thumb", async (
                    string id, long? expires, string? signature, HttpContext http, OnkelBummelDbContext db, AttachmentStore store,
                    ThumbnailUrlSigner signer, CancellationToken cancellationToken) =>
                {
                    if (expires is null || !signer.IsValid(id, expires.Value, signature))
                        return Problems.Forbidden("Der Link ist abgelaufen oder ungültig.");
                    var result = await ServeAsync(id, thumbnail: true, download: false, http, db, store, cancellationToken);
                    http.Response.Headers.CacheControl = "private, max-age=900";
                    return result;
                })
            .AllowAnonymous()
            .WithSummary("Thumbnail by a signed link from a push, valid for 15 minutes");

        return api;
    }

    private static async Task<IResult> ServeAsync(
        string id, bool thumbnail, bool download, HttpContext http, OnkelBummelDbContext db, AttachmentStore store, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        var path = thumbnail ? attachment?.ThumbnailPath : attachment?.Path;
        if (attachment is null || path is null || !File.Exists(store.FullPath(path)))
            return Problems.NotFound(thumbnail ? $"Anhang {id} hat kein Vorschaubild." : $"Anhang {id} gibt es nicht.");

        // Files never change under their id.
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        var fileName = thumbnail ? Path.ChangeExtension(attachment.FileName, ".thumb.jpg") : attachment.FileName;
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline")
        {
            FileNameStar = fileName,
        }.ToString();

        return TypedResults.PhysicalFile(
            Path.GetFullPath(store.FullPath(path)),
            thumbnail ? "image/jpeg" : attachment.ContentType,
            enableRangeProcessing: true,
            entityTag: new EntityTagHeaderValue($"\"{attachment.Sha256[..32]}{(thumbnail ? "-t" : "")}\""));
    }
}

public static class StreamEndpoints
{
    public static RouteGroupBuilder MapStreamEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stream", (string? topics, HttpContext http, MessageEvents events, CancellationToken cancellationToken) =>
            {
                var names = Topics.Parse(topics);
                var subscription = events.Subscribe(names?.ToHashSet());
                // Also ends it when the client is gone before the first event was written.
                http.Response.RegisterForDispose(subscription);
                return TypedResults.ServerSentEvents(subscription.ReadAllAsync(cancellationToken));
            })
            .RequireAuthorization(Policies.User)
            .WithSummary("Server-sent events: message, read, deleted and a ping every 25 s");

        return api;
    }
}
