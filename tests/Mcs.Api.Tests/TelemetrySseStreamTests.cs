using System.Net.ServerSentEvents;
using System.Threading.Channels;

using Mcs.Api.Telemetry;
using Mcs.Core;

using Microsoft.Extensions.Time.Testing;

namespace Mcs.Api.Tests;

/// <summary>
/// The fleet-tick interleaver: what the SSE endpoint actually writes.
/// </summary>
/// <remarks>
/// Driven by a fake clock rather than by waiting, so "a second of silence" costs nothing and "the
/// tick fires even while frames are flowing" is a statement rather than a hope. Against the real
/// clock the second one cannot be asserted at all -- only that none happened yet.
/// <para>
/// <b>The case that matters here is the one that used to be inverted.</b> While this event was a
/// heartbeat its rule was "not while traffic is flowing", because bytes that say nothing are bytes a
/// client learns to ignore. It now carries every vehicle's age, and a fleet of twelve with one that
/// has stopped is never silent -- so an idle-triggered event would fire in every case except the one
/// it exists for.
/// </para>
/// <para>
/// The source is a <see cref="Channel{T}"/> rather than a real store: what is under test is the
/// racing of a read against a timer, and a channel lets a test hold the read open indefinitely.
/// </para>
/// </remarks>
public class TelemetrySseStreamTests
{
    /// <summary>Arbitrary; every assertion below is relative to it.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A bound on the real clock, so a stream that never produces fails in a second instead of
    /// hanging the runner. Nothing here is waiting for wall time, so it only has to cover the hop
    /// from a fired timer to a resumed continuation.
    /// </summary>
    private static readonly TimeSpan Responsiveness = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenTheFleetGoesQuiet_ItTicksAnyway()
    {
        // The whole point of the event. A vehicle that has stopped reporting sends nothing, so the
        // station has to be the one that speaks -- and the payload is the fleet as of now rather
        // than an empty keep-alive, because "nothing has changed" is precisely what is untrue.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        List<TelemetryFrame> fleet = [Frame(clock)];

        using CancellationTokenSource subscription = new();
        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            Stream(source, fleet, clock, subscription.Token);

        ValueTask<bool> pending = events.MoveNextAsync();

        //  The iterator runs synchronously as far as its first real await, so the timer is armed by
        //  the time this returns -- which is what makes advancing the clock next well-defined.
        Assert.False(pending.IsCompleted, "the stream produced something before any time passed.");

        clock.Advance(Period);

        Assert.True(await pending.AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.FleetEventType, events.Current.EventType);

        VehicleFrameResponse vehicle = Assert.Single(events.Current.Data);
        Assert.Equal(VehicleState.Live, vehicle.State);
        Assert.Equal((long)Period.TotalMilliseconds, vehicle.AgeMilliseconds);
    }

    [Fact]
    public async Task WhileFramesArrive_ItStillTicksTheFleet()
    {
        // The regression this pins is the old behaviour, not a hypothetical one: the delay used to
        // be restarted by every frame. Eleven talkative vehicles would then hold the tick off
        // indefinitely, and the twelfth -- the one that had gone quiet -- would never be reported
        // stale at all. So the clock crosses one period here *through* a frame rather than around
        // it, and the tick still lands.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        List<TelemetryFrame> fleet = [];

        using CancellationTokenSource subscription = new();
        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            Stream(source, fleet, clock, subscription.Token);

        ValueTask<bool> pending = events.MoveNextAsync();
        Assert.False(pending.IsCompleted, "the stream produced something before any time passed.");

        //  Most of the way to the deadline, then traffic.
        clock.Advance(0.6 * Period);
        Assert.True(source.Writer.TryWrite(Frame(clock)));

        Assert.True(await pending.AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.TelemetryEventType, events.Current.EventType);

        //  The rest of the way. Under the old rule the frame above reset this and nothing fires.
        ValueTask<bool> ticked = events.MoveNextAsync();
        clock.Advance(0.4 * Period);

        Assert.True(await ticked.AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.FleetEventType, events.Current.EventType);
    }

    [Fact]
    public async Task WhileFramesAreBacklogged_TheTickIsNotStarved()
    {
        // The test above crosses a period through *one* frame, and a queue that is empty by the time
        // the next read is asked for lets the read wait -- which is the easy case. This is the hard
        // one: a client that is behind leaves the subscriber queue non-empty, so every read completes
        // the instant it is made and the tick has to win against a race it never actually loses.
        // Under the defect this fixes, the fleet went unstated for as long as the backlog lasted,
        // which is the connection whose ages are least trustworthy in the first place.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        List<TelemetryFrame> fleet = [Frame(clock)];

        using CancellationTokenSource subscription = new();
        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            Stream(source, fleet, clock, subscription.Token);

        //  Enough that the queue cannot drain between two reads. Written before the stream is first
        //  pulled, so there is never a moment where a read has to wait for the channel.
        for (int i = 0; i < 3; i++)
        {
            Assert.True(source.Writer.TryWrite(Frame(clock)));
        }

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.TelemetryEventType, events.Current.EventType);

        clock.Advance(Period);

        //  With two frames still queued behind it, the next event is the tick or the defect is back.
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.FleetEventType, events.Current.EventType);
        Assert.Equal((long)Period.TotalMilliseconds, Assert.Single(events.Current.Data).AgeMilliseconds);

        //  And the read the tick jumped ahead of is still outstanding rather than dropped: the frame
        //  it was about to produce arrives next.
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.TelemetryEventType, events.Current.EventType);
    }

    [Fact]
    public async Task EveryTick_ReReadsTheFleetSoTheAgesAdvance()
    {
        // A console that stopped being told anything shows a picture it has no reason to believe is
        // current -- the hazard, exactly. Two ticks with nothing arriving in between must therefore
        // differ: same frame, older every time, and eventually a different state.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        List<TelemetryFrame> fleet = [Frame(clock)];

        using CancellationTokenSource subscription = new();
        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            Stream(source, fleet, clock, subscription.Token);

        VehicleFrameResponse first = await TickAsync(events, clock, TelemetryCurrency.StaleAfter);
        VehicleFrameResponse second = await TickAsync(
            events, clock, TelemetryCurrency.LostAfter - TelemetryCurrency.StaleAfter);

        Assert.Equal(VehicleState.Stale, first.State);
        Assert.Equal(VehicleState.Lost, second.State);
        Assert.True(
            second.AgeMilliseconds > first.AgeMilliseconds,
            "the tick re-read a fleet whose ages had not moved.");

        //  Same frame throughout: the vehicle did not report, the station's answer about it changed.
        Assert.Equal(first.ReceivedAtUtc, second.ReceivedAtUtc);
    }

    [Fact]
    public async Task AFrameEvent_CarriesTheStationsAnswerAboutIt()
    {
        // A frame can be stale by the time it is written -- it may have sat in a slow client's
        // subscriber queue -- and the age it carries has to be the one measured at that moment
        // rather than at arrival. The console has no way to work this out for itself and must not
        // try.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();

        Assert.True(source.Writer.TryWrite(Frame(clock)));
        source.Writer.Complete();

        clock.Advance(TelemetryCurrency.StaleAfter);

        List<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            await DrainAsync(source, [], clock, CancellationToken.None);

        VehicleFrameResponse vehicle = Assert.Single(
            Assert.Single(events, item => item.EventType == TelemetryEndpoints.TelemetryEventType)
                .Data);

        Assert.Equal(VehicleState.Stale, vehicle.State);
        Assert.Equal((long)TelemetryCurrency.StaleAfter.TotalMilliseconds, vehicle.AgeMilliseconds);
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

        Assert.Empty(await DrainAsync(source, [], clock, subscription.Token));
    }

    [Fact]
    public async Task WhenTheClientDisconnects_ItDisposesTheSubscription()
    {
        // The leak this guards is one subscription per page reload. It is also the case that spins:
        // a cancelled request completes the tick delay too, so a loop that only checks which task
        // won writes ticks as fast as the socket takes them.
        FakeTimeProvider clock = new();
        Channel<TelemetryFrame> source = Channel.CreateUnbounded<TelemetryFrame>();
        TrackingSource tracked = new(source.Reader.ReadAllAsync());

        using CancellationTokenSource subscription = new();

        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events =
            TelemetrySseStream
                .WithFleetTicks(tracked, () => [], Period, clock, subscription.Token)
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

    /// <summary>Advances the clock to the next tick and returns the single vehicle it carried.</summary>
    /// <param name="events">An open stream, suspended between events.</param>
    /// <param name="clock">The clock the stream's timer runs on.</param>
    /// <param name="silence">How much further to advance; must be at least one period.</param>
    private static async Task<VehicleFrameResponse> TickAsync(
        IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> events,
        FakeTimeProvider clock,
        TimeSpan silence)
    {
        //  Asked for before the clock moves, so the timer this fires is one the iterator is already
        //  waiting on rather than one it has yet to arm.
        ValueTask<bool> pending = events.MoveNextAsync();

        clock.Advance(silence);

        Assert.True(await pending.AsTask().WaitAsync(Responsiveness));
        Assert.Equal(TelemetryEndpoints.FleetEventType, events.Current.EventType);

        return Assert.Single(events.Current.Data);
    }

    /// <summary>Opens the interleaved stream over a channel.</summary>
    private static IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> Stream(
        Channel<TelemetryFrame> source,
        IReadOnlyList<TelemetryFrame> fleet,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        TelemetrySseStream
            .WithFleetTicks(
                source.Reader.ReadAllAsync(),

                //  Re-projected on every call, like the endpoint's own closure over the store: a
                //  tick that returned a list captured when the stream opened would report ages that
                //  age with the connection rather than with the fleet.
                () => VehicleFrameResponse.Fleet(fleet, clock),
                Period,
                clock,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

    /// <summary>Reads the stream to completion, bounded by the real clock.</summary>
    private static async Task<List<SseItem<IReadOnlyList<VehicleFrameResponse>>>> DrainAsync(
        Channel<TelemetryFrame> source,
        IReadOnlyList<TelemetryFrame> fleet,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        List<SseItem<IReadOnlyList<VehicleFrameResponse>>> events = [];

        await using IAsyncEnumerator<SseItem<IReadOnlyList<VehicleFrameResponse>>> stream =
            Stream(source, fleet, clock, cancellationToken);

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
