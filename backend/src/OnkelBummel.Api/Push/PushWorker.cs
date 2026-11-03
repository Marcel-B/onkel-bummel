using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Push;

/// <summary>
/// Sends the pushes from <see cref="PushQueue"/>: one per device whose subscriptions cover the topic, unless the
/// topic is muted. Tries again when Apple is busy and forgets devices APNs reports as gone (410).
/// </summary>
public sealed class PushWorker(
    PushQueue queue,
    ApnsClient apns,
    IServiceScopeFactory scopes,
    IOptions<ApnsOptions> options,
    TimeProvider time,
    ILogger<PushWorker> logger) : BackgroundService
{
    /// <summary>APNs keeps a push this long for a phone that is off.</summary>
    public static readonly TimeSpan Expiration = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogInformation("Push is off: Apns:KeyPath, KeyId and TeamId are missing");
            return;
        }

        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (item)
                {
                    case MessagePush message:
                        foreach (var push in await PrepareAsync(message.MessageId, stoppingToken))
                            await DeliverAsync(push, stoppingToken);
                        break;
                    case DevicePush push:
                        await DeliverAsync(push, stoppingToken);
                        break;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Push failed");
            }
        }
    }

    /// <summary>One push per device that follows the topic; none for muted topics or deleted messages.</summary>
    internal async Task<List<DevicePush>> PrepareAsync(long messageId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OnkelBummelDbContext>();
        var message = await db.Messages.AsNoTracking()
            .Include(m => m.Attachments).Include(m => m.Actions).AsSplitQuery()
            .SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
            return [];
        var topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Name == message.Topic, cancellationToken);
        if (topic.Muted)
            return [];

        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.Subscriptions.Any(s => s.Topic == message.Topic || s.Topic == TopicNames.All))
            .Select(d => new { d.Id, d.ApnsToken, d.Environment })
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
            return [];

        var unread = await db.Messages.CountAsync(m => m.ReadAt == null, cancellationToken);
        var image = message.Attachments.OrderBy(a => a.Path, StringComparer.Ordinal).FirstOrDefault(a => a.ThumbnailPath is not null);
        var thumbnailUrl = image is null ? null : scope.ServiceProvider.GetRequiredService<ThumbnailUrlSigner>().Sign(image.Id);
        var payload = PushPayload.Create(message, topic.DisplayName ?? topic.Name, unread, thumbnailUrl);
        var expiration = time.GetUtcNow() + Expiration;

        return devices
            .Select(d => new DevicePush(message.Id, d.Id,
                new ApnsNotification(d.ApnsToken, d.Environment, payload, PushPayload.ApnsPriority(message.Priority), expiration), 0))
            .ToList();
    }

    private async Task DeliverAsync(DevicePush push, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var result = await apns.SendAsync(push.Notification, cancellationToken);
        OnkelBummelMetrics.PushDuration.Observe(time.GetElapsedTime(started).TotalSeconds);
        var attempt = push.Attempt + 1;

        if (result.Ok)
        {
            OnkelBummelMetrics.PushSent.WithLabels("ok").Inc();
            logger.LogInformation("Message {MessageId} pushed to device {DeviceId}", push.MessageId, push.DeviceId);
        }
        else if (result.Unregistered)
        {
            OnkelBummelMetrics.PushSent.WithLabels("invalid_token").Inc();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OnkelBummelDbContext>();
            // Only if the token is still the same; the app may have registered a new one meanwhile.
            await db.Devices.Where(d => d.Id == push.DeviceId && d.ApnsToken == push.Notification.DeviceToken).ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation("Device {DeviceId} removed: APNs says it is unregistered", push.DeviceId);
        }
        else if (result.BadToken)
        {
            // Mostly a development build registered as production or the other way round; the app fixes that itself.
            OnkelBummelMetrics.PushSent.WithLabels("invalid_token").Inc();
            logger.LogWarning("APNs refused the token of device {DeviceId} ({Environment}): {Reason}",
                push.DeviceId, push.Notification.Environment, result.Reason);
        }
        else if (result.Transient && attempt < options.Value.MaxAttempts)
        {
            var delay = options.Value.FirstRetryDelay * Math.Pow(6, push.Attempt);
            logger.LogWarning("APNs answered {Status} {Reason} for message {MessageId}; trying again in {Delay}",
                result.Status, result.Reason, push.MessageId, delay);
            queue.Retry(push with { Attempt = attempt }, delay, cancellationToken);
        }
        else
        {
            OnkelBummelMetrics.PushSent.WithLabels("failed").Inc();
            logger.LogWarning("Push of message {MessageId} to device {DeviceId} failed after {Attempts} tries: {Status} {Reason}",
                push.MessageId, push.DeviceId, attempt, result.Status, result.Reason);
        }
    }
}
