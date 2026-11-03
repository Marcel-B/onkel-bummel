using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Push;

/// <summary>A new message to push to every device that follows its topic.</summary>
public sealed record MessagePush(long MessageId);

/// <summary>One push to one device, again after APNs could not take it.</summary>
/// <param name="Attempt">Tries so far.</param>
public sealed record DevicePush(long MessageId, string DeviceId, ApnsNotification Notification, int Attempt);

/// <summary>
/// Pushes waiting for <see cref="PushWorker"/>. Sending a message never waits for Apple; what is still in here on
/// shutdown is lost, the message itself is stored.
/// </summary>
public sealed class PushQueue(IOptions<ApnsOptions> options, TimeProvider time)
{
    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Does nothing while APNs is not set up.</summary>
    public void Enqueue(long messageId)
    {
        if (options.Value.IsConfigured)
            Write(new MessagePush(messageId));
    }

    /// <summary>Puts the push back after the delay.</summary>
    public void Retry(DevicePush push, TimeSpan delay, CancellationToken cancellationToken)
    {
        OnkelBummelMetrics.PushQueueLength.Inc();
        _ = Task.Delay(delay, time, cancellationToken).ContinueWith(
            _ =>
            {
                OnkelBummelMetrics.PushQueueLength.Dec();
                if (!cancellationToken.IsCancellationRequested)
                    Write(push);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async IAsyncEnumerable<object> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            OnkelBummelMetrics.PushQueueLength.Dec();
            yield return item;
        }
    }

    private void Write(object item)
    {
        OnkelBummelMetrics.PushQueueLength.Inc();
        _channel.Writer.TryWrite(item);
    }
}
