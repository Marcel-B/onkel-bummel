using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;

namespace OnkelBummel.Api.Actions;

/// <param name="Status">HTTP status of the target; 0 when it did not answer.</param>
/// <param name="Ok">The target answered with 2xx.</param>
public sealed record ActionRunResult(int Index, int Status, bool Ok, DateTime RunAt, bool MarkedRead);

/// <summary>
/// Makes the HTTP call behind an action button. The server does it, not the phone: it sits in the home network and
/// always reaches the target.
/// </summary>
public sealed class ActionRunner(
    OnkelBummelDbContext db,
    IHttpClientFactory httpClients,
    MessageEvents events,
    TimeProvider time,
    ILogger<ActionRunner> logger)
{
    public const string HttpClientName = "Actions";

    /// <summary>Runs the action and stores the result; null when message or action do not exist.</summary>
    public async Task<ActionRunResult?> RunAsync(long messageId, int index, CancellationToken cancellationToken)
    {
        var action = await db.MessageActions.SingleOrDefaultAsync(a => a.MessageId == messageId && a.Index == index, cancellationToken);
        if (action is null)
            return null;

        var status = await CallAsync(action, cancellationToken);
        var ok = status is >= 200 and < 300;
        var now = time.GetUtcNow().UtcDateTime;
        action.LastStatus = status;
        action.LastRunAt = now;

        var markedRead = false;
        if (ok && action.Clear)
        {
            markedRead = await db.Messages.Where(m => m.Id == messageId && m.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), cancellationToken) > 0;
        }
        await db.SaveChangesAsync(cancellationToken);

        OnkelBummelMetrics.ActionsExecuted.WithLabels(ok ? "ok" : "failed").Inc();
        if (markedRead)
        {
            var topic = await db.Messages.Where(m => m.Id == messageId).Select(m => m.Topic).SingleAsync(cancellationToken);
            events.Publish(new LiveEvent(MessageEvents.ReadType, topic, new ReadEvent([messageId], null, null, now)));
        }
        return new ActionRunResult(index, status, ok, now, markedRead);
    }

    private async Task<int> CallAsync(MessageAction action, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(action.Method), action.Url);
        string? contentType = null;
        foreach (var (name, value) in action.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = value;
            else if (!request.Headers.TryAddWithoutValidation(name, value))
                logger.LogWarning("Action {Index} of message {MessageId}: header {Header} skipped", action.Index, action.MessageId, name);
        }
        if (action.Body is not null)
        {
            request.Content = new StringContent(action.Body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType ?? GuessType(action.Body), out var parsed)
                ? parsed
                : new MediaTypeHeaderValue("text/plain");
        }

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            logger.LogInformation("Action {Index} of message {MessageId}: {Method} {Url} answered {Status}",
                action.Index, action.MessageId, action.Method, action.Url, (int)response.StatusCode);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Action {Index} of message {MessageId}: {Method} {Url} failed: {Error}",
                action.Index, action.MessageId, action.Method, action.Url, ex.Message);
            return 0;
        }
    }

    private static string GuessType(string body) =>
        body.TrimStart() is ['{', ..] or ['[', ..] ? "application/json" : "text/plain";
}
