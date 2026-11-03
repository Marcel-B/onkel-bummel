using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OnkelBummel.Api.Monitoring;

namespace OnkelBummel.Api.Messages;

/// <summary>Messages with an id up to <paramref name="UpToId"/> were read: in <paramref name="Topic"/>, or in all topics when null.</summary>
/// <param name="Ids">Single messages read; empty when a whole topic (or everything) was read.</param>
public sealed record ReadEvent(long[] Ids, string? Topic, long? UpToId, DateTime ReadAt);

public sealed record DeletedEvent(long Id, string Topic);

/// <param name="Topic">Topic the event belongs to; null for events about all topics.</param>
public sealed record LiveEvent(string Type, string? Topic, object Data);

/// <summary>Hands new messages and read changes to the open live connections (server-sent events).</summary>
public sealed class MessageEvents(TimeProvider time)
{
    public const string MessageType = "message";
    public const string ReadType = "read";
    public const string DeletedType = "deleted";
    public const string PingType = "ping";

    /// <summary>Idle connections get a ping this often, so proxies and the app notice when they are gone.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(25);

    // A client that does not read for this many events loses the oldest ones instead of piling up memory.
    private const int Backlog = 1000;

    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    private readonly TimeProvider _time = time;

    public void Publish(LiveEvent liveEvent)
    {
        foreach (var subscription in _subscriptions.Keys)
        {
            if (liveEvent.Topic is null || subscription.Topics is null || subscription.Topics.Contains(liveEvent.Topic))
                subscription.Channel.Writer.TryWrite(liveEvent);
        }
    }

    /// <summary>
    /// Starts listening right away, so nothing published after this call is missed, even before the response begins.
    /// </summary>
    /// <param name="topics">Topics to follow; null for all.</param>
    public Subscription Subscribe(IReadOnlySet<string>? topics)
    {
        var subscription = new Subscription(this, topics);
        _subscriptions.TryAdd(subscription, 0);
        OnkelBummelMetrics.SseConnections.Inc();
        return subscription;
    }

    public sealed class Subscription : IDisposable
    {
        private readonly MessageEvents _owner;
        private int _disposed;

        internal Subscription(MessageEvents owner, IReadOnlySet<string>? topics)
        {
            _owner = owner;
            Topics = topics;
        }

        internal IReadOnlySet<string>? Topics { get; }

        internal Channel<LiveEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(Backlog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        /// <summary>The events as they come, starting with a ping so the client knows the connection stands.</summary>
        public async IAsyncEnumerable<SseItem<object>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                yield return Ping();
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await WaitAsync(cancellationToken))
                    {
                        yield return Ping();
                        continue;
                    }
                    while (Channel.Reader.TryRead(out var liveEvent))
                        yield return new SseItem<object>(liveEvent.Data, liveEvent.Type);
                }
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            _owner._subscriptions.TryRemove(this, out _);
            OnkelBummelMetrics.SseConnections.Dec();
        }

        /// <summary>True when events are waiting, false when it stayed quiet for <see cref="KeepAlive"/>.</summary>
        private async Task<bool> WaitAsync(CancellationToken cancellationToken)
        {
            using var quiet = new CancellationTokenSource(KeepAlive, _owner._time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);
            try
            {
                return await Channel.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        private static SseItem<object> Ping() => new(new { }, PingType);
    }
}
