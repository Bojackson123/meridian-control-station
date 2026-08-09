using System.Net.ServerSentEvents;
using System.Threading.Channels;

using Mcs.Api.Telemetry;
using Mcs.Core;

using Microsoft.Extensions.Time.Testing;

namespace Mcs.Api.Tests;

/// <summary>
/// The heartbeat interleaver: what the SSE endpoint actually writes.
/// </summary>
/// <remarks>
/// Driven by a fake clock rather than by waiting, so "fifteen seconds of silence" costs nothing and
/// "no heartbeat while frames are flowing" is a statement rather than a hope. Against the real clock
/// the second one cannot be asserted at all -- only that none happened yet.
/// <para>
/// The source is a <see cref="Channel{T}"/> rather than a real store: what is under test is the
/// racing of a read against a timer, and a channel lets a test hold the read open indefinitely.
/// </para>
/// </remarks>
public class TelemetrySseStreamTests
{
    /// <summary>Arbitrary; every assertion below is relative to it.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A bound on the real clock, so a stream that never produces fails in a second instead of
    /// hanging the runner. Nothing here is waiting for wall time, so it only has to cover the hop
    /// from a fired timer to a resumed continuation.
    /// </summary>
    private static readonly TimeSpan Responsiveness = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenTheFleetGoesQuiet_ItSendsAHeartbeat()
    {
        // Without this a proxy drops the idle connection, the browser reconnects, and the operator
        // watches a map that blinks. Nothing in dev at 1 Hz ever reaches the timeout.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();

        using CancellationTokenSource subscription = new();
        await using IAsyncEnumerator<SseItem<VehicleFrameResponse?>> events =
            Stream(source, clock, subscription.Token);

        ValueTask<bool> pending = events.MoveNextAsync();

        //  The iterator runs synchronously as far as its first real await, so the timer is armed by
        //  the time this returns -- which is what makes advancing the clock next well-defined.
        Assert.False(pending.IsCompleted, "the stream produced something before any time passed.");

        clock.Advance(Period);

        Assert.True(await pending.AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.HeartbeatEventType, events.Current.EventType);
        Assert.Null(events.Current.Data);
    }

    [Fact]
    public async Task WhileFramesArrive_ItSendsNoHeartbeat()
    {
        // The heartbeat is for silence. One emitted alongside traffic is bytes on the wire that
        // say nothing, and a client that starts treating them as meaningful.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();

        for (int i = 0; i < 3; i++)
        {
            Assert.True(source.Writer.TryWrite(Frame(clock)));
        }

        source.Writer.Complete();

        using CancellationTokenSource subscription = new();

        List<SseItem<VehicleFrameResponse?>> events = await DrainAsync(
            source, clock, subscription.Token);

        Assert.Equal(3, events.Count);
        Assert.All(events, item =>
            Assert.Equal(TelemetryEndpoints.TelemetryEventType, item.EventType));
    }

    [Fact]
    public async Task WhenTheSourceCompletes_TheStreamEnds()
    {
        // A fleet that has gone away ends the stream rather than holding the connection open on a
        // subscription that will never produce again.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();

        source.Writer.Complete();

        using CancellationTokenSource subscription = new();

        Assert.Empty(await DrainAsync(source, clock, subscription.Token));
    }

    [Fact]
    public async Task WhenTheClientDisconnects_ItDisposesTheSubscription()
    {
        // The leak this guards is one subscription per page reload. It is also the case that spins:
        // a cancelled request completes the heartbeat delay too, so a loop that only checks which
        // task won writes heartbeats as fast as the socket takes them.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        TrackingSource tracked = new(source.Reader.ReadAllAsync());

        using CancellationTokenSource subscription = new();

        await using IAsyncEnumerator<SseItem<VehicleFrameResponse?>> events =
            TelemetrySseStream
                .WithHeartbeat(tracked, Period, clock, subscription.Token)
                .GetAsyncEnumerator(subscription.Token);

        ValueTask<bool> pending = events.MoveNextAsync();

        await subscription.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.AsTask().WaitAsync(Responsiveness));

        //  Disposal happens as the iterator unwinds, which the enumerator's own DisposeAsync above
        //  completes. Asserted after that, on the way out of the using.
        await events.DisposeAsync();

        Assert.Equal(1, tracked.Disposals);
    }

    /// <summary>Opens the interleaved stream over a channel.</summary>
    private static IAsyncEnumerator<SseItem<VehicleFrameResponse?>> Stream(
        Channel<TelemetryFrame> source, TimeProvider clock, CancellationToken cancellationToken) =>
        TelemetrySseStream
            .WithHeartbeat(source.Reader.ReadAllAsync(), Period, clock, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

    /// <summary>Reads the stream to completion, bounded by the real clock.</summary>
    private static async Task<List<SseItem<VehicleFrameResponse?>>> DrainAsync(
        Channel<TelemetryFrame> source, TimeProvider clock, CancellationToken cancellationToken)
    {
        List<SseItem<VehicleFrameResponse?>> events = [];

        await using IAsyncEnumerator<SseItem<VehicleFrameResponse?>> stream =
            Stream(source, clock, cancellationToken);

        while (await stream.MoveNextAsync().AsTask().WaitAsync(Responsiveness))
        {
            events.Add(stream.Current);
        }

        return events;
    }

    /// <summary>
    /// A frame minted the only way the domain allows -- through the ingest boundary -- off the same
    /// fake clock the stream is driven by.
    /// </summary>
    private static TelemetryFrame Frame(TimeProvider clock) =>
        new TelemetryIngest(clock)
            .BeginReceive()
            .Complete(VehicleTelemetry.Create(
                VehicleId.From("UAV-01"),
                latitudeDegrees: 51.5074,
                longitudeDegrees: -0.1278,
                Altitude.FromMeters(120, AltitudeReference.Agl),
                groundSpeedMetersPerSecond: 14.2,
                headingDegrees: 12.5,
                batteryPercent: 87.0,
                LinkStatus.Healthy));

    /// <summary>
    /// Counts how many times the wrapped sequence's enumerator was disposed. The store releases a
    /// subscriber on disposal, so this stands in for "the subscription was let go".
    /// </summary>
    private sealed class TrackingSource : IAsyncEnumerable<TelemetryFrame>
    {
        private readonly IAsyncEnumerable<TelemetryFrame> _inner;

        private int _disposals;

        public TrackingSource(IAsyncEnumerable<TelemetryFrame> inner) => _inner = inner;

        public int Disposals => Volatile.Read(ref _disposals);

        public IAsyncEnumerator<TelemetryFrame> GetAsyncEnumerator(
            CancellationToken cancellationToken) =>
            new TrackingEnumerator(_inner.GetAsyncEnumerator(cancellationToken), this);

        private void Released() => Interlocked.Increment(ref _disposals);

        private sealed class TrackingEnumerator : IAsyncEnumerator<TelemetryFrame>
        {
            private readonly IAsyncEnumerator<TelemetryFrame> _inner;
            private readonly TrackingSource _owner;

            public TrackingEnumerator(
                IAsyncEnumerator<TelemetryFrame> inner, TrackingSource owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public TelemetryFrame Current => _inner.Current;

            public ValueTask<bool> MoveNextAsync() => _inner.MoveNextAsync();

            public async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync();
                _owner.Released();
            }
        }
    }
}
