namespace Mcs.Core;

/// <summary>
/// The station's bounded record of what every vehicle is doing: where a
/// <see cref="TelemetryFrame"/> lands after ingest, and the only thing the API reads from.
/// </summary>
/// <remarks>
/// <b>Bounded in both directions.</b> At most <see cref="MaxVehicles"/> vehicles and
/// <see cref="HistoryDepthPerVehicle"/> frames each, so a station that runs for a week uses the same
/// memory as one that has just started.
/// <para>
/// <b>Bounded is not self-healing.</b> That is a claim about memory only. Admission is permanent, so
/// a feed re-announcing itself under a new id every second fills the roster in twelve seconds with
/// ids that will never report again, after which every genuine vehicle is refused.
/// <see cref="Forget"/> is the way back.
/// </para>
/// <para>
/// An interface, unlike <see cref="TelemetryIngest"/>, because there will be more than one
/// implementation: the latency work wraps this, and the deconfliction tests drive a fake.
/// <b>Implementations must be thread-safe</b> -- adapters write from receive threads while the API
/// reads from request threads.
/// </para>
/// <para>
/// The sizing constants live here because they are system-wide commitments: the panel, the map and
/// the latency budget are all designed against <see cref="MaxVehicles"/>, and a fake store has to
/// honour the same cap or it is testing a different system.
/// </para>
/// </remarks>
public interface ITelemetryStore
{
    /// <summary>
    /// The most vehicles the station tracks at once; a write introducing one more is rejected.
    /// </summary>
    /// <remarks>
    /// Not a memory limit -- twelve rings is under a megabyte -- but a scope commitment. The vehicle
    /// panel, the map's track budget and MCS-001's latency figure are all designed and measured at
    /// twelve; a thirteenth would be rendered somewhere nobody has laid out.
    /// </remarks>
    public const int MaxVehicles = 12;

    /// <summary>
    /// How many frames the store keeps per vehicle before the oldest is evicted.
    /// </summary>
    /// <remarks>
    /// Derived, not picked: at the 10 Hz ceiling, 600 frames is exactly one minute per vehicle --
    /// enough to draw a track and answer "what was it doing just before that?". Twelve vehicles at
    /// roughly 100 bytes a frame is about 700 KB for the whole station.
    /// </remarks>
    public const int HistoryDepthPerVehicle = 600;

    /// <summary>
    /// How many frames a subscriber may fall behind by before it starts losing the oldest of them.
    /// </summary>
    /// <remarks>
    /// 120 frames a second aggregate, so 256 is roughly two seconds of full-rate traffic. MCS-001
    /// budgets one second from receipt to screen, so a subscriber this far behind has blown its
    /// budget twice over: the useful response is to show it the present. On the interface because it
    /// is observable behaviour -- a consumer reasoning about what its stream may skip needs it.
    /// </remarks>
    public const int SubscriberBufferCapacity = 256;

    /// <summary>
    /// Records a frame as the vehicle's current state and appends it to that vehicle's history,
    /// admitting the vehicle if this is the first frame seen from it.
    /// </summary>
    /// <remarks>
    /// <b>Never blocks on a subscriber</b>, so a wedged HTTP client cannot slow the ingest thread.
    /// <para>
    /// <b>Rejects loudly rather than dropping.</b> Exceeding <see cref="MaxVehicles"/> throws rather
    /// than returning a status, because a status can be discarded and this one must not be -- HAZ-01
    /// is precisely what a silently ignored return value causes. It also matches the rest of
    /// <c>Mcs.Core</c>, which throws on contract violations. The throw is affordable: a
    /// misconfigured feed throws about ten times a second, and the resulting log volume is the
    /// caller's to solve at the <c>Mcs.Api</c> boundary.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to record. Its vehicle id comes from <c>frame.Telemetry.Id</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="TelemetryStoreCapacityExceededException">
    /// An unknown vehicle, and the store already holds <see cref="MaxVehicles"/>. Slots are never
    /// reclaimed on their own -- once this starts it continues until something calls
    /// <see cref="Forget"/>.
    /// </exception>
    void Write(TelemetryFrame frame);

    /// <summary>
    /// Gets the most recent frame recorded for a vehicle, or <see langword="null"/> if the store has
    /// never seen it.
    /// </summary>
    TelemetryFrame? GetLatest(VehicleId id);

    /// <summary>
    /// Gets the most recent frame for every vehicle the store holds -- one per vehicle, in no
    /// particular order. What <c>GET /api/vehicles</c> serves and what <see cref="Subscribe"/> seeds
    /// with.
    /// </summary>
    /// <remarks>
    /// Order is deliberately unspecified: the console sorts for display, and promising an order here
    /// would be a promise the implementation has to keep forever for no caller's benefit.
    /// </remarks>
    IReadOnlyList<TelemetryFrame> GetLatestSnapshot();

    /// <summary>
    /// Gets a vehicle's retained history, oldest first, up to
    /// <see cref="HistoryDepthPerVehicle"/> frames. Empty for an unknown vehicle.
    /// </summary>
    /// <remarks>
    /// A copy taken at the moment of the call. A live view over a buffer being written and evicted
    /// concurrently would let a caller enumerate a frame that had already been overwritten.
    /// </remarks>
    IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id);

    /// <summary>
    /// Drops a vehicle and its history, freeing its slot. A later frame from the same id is admitted
    /// as a new vehicle.
    /// </summary>
    /// <remarks>
    /// <b>The recovery path for a poisoned roster.</b> It clears dead tracks once the feed itself has
    /// been fixed; it is not a defence against that feed, which will refill the roster as fast as it
    /// is emptied.
    /// <para>
    /// <b>Why a deliberate action and not an eviction policy.</b> Every eviction rule available here
    /// keys on recency, and recency is what a bad feed has most of -- invented ids are always the
    /// freshest thing in the store, so evicting the least recently heard throws out the genuine
    /// vehicles. An idle timeout fails worse: a vehicle quiet for thirty seconds is usually
    /// mid-dropout, and its last known position is exactly what the operator needs. Dropping it there
    /// trades a loud, diagnosable refusal for a silent disappearance -- HAZ-01 with the labels
    /// swapped.
    /// </para>
    /// <para>
    /// <b>Existing subscribers are not told</b> -- the stream carries frames and a removal is not
    /// one, so a seeded subscriber keeps showing the forgotten vehicle until it resubscribes.
    /// </para>
    /// </remarks>
    /// <returns><see langword="false"/> if the store had never seen it, which is not an error.</returns>
    bool Forget(VehicleId id);

    /// <summary>
    /// Opens a live stream of frames: the current snapshot first, then every frame written after the
    /// subscription was taken.
    /// </summary>
    /// <remarks>
    /// <b>Seeded, so a late joiner sees the whole fleet.</b> Without it, the natural consumer
    /// implementation -- snapshot, then subscribe -- drops any frame landing between the two calls,
    /// and a vehicle that has stopped transmitting never appears at all. The seed and the
    /// registration must be ordered against writes so a subscriber sees no gap and never observes an
    /// older frame after a newer one.
    /// <para>
    /// <b>Bounded, and drops the oldest.</b> This is a state stream, not an event log: when a stalled
    /// browser resumes, the operator needs where the vehicle is, not a replay of where it was.
    /// Dropping the newest would leave the subscriber permanently behind reality while showing a
    /// smooth, complete and entirely stale picture -- HAZ-01, stated exactly.
    /// </para>
    /// <para>
    /// <b>Registration is eager</b> -- the subscription exists from the moment this returns, so
    /// frames written before enumeration starts are buffered rather than lost. Implementations must
    /// therefore not write this as an iterator, whose body would not run until enumerated, silently
    /// reopening the gap the seeding exists to close.
    /// </para>
    /// <para>
    /// <b>Cancellation ends the stream by throwing</b> <see cref="OperationCanceledException"/> and
    /// unregisters the subscriber; abandoning the enumeration does the same. <b>A non-cancellable
    /// token is rejected</b>, because eager registration makes the token the only handle on a
    /// subscription that has not been enumerated -- and
    /// <see cref="CancellationToken.Register(Action)"/> on such a token is a documented no-op, so the
    /// leak would be permanent, cost a write per frame inside the store's own gate, and have no
    /// symptom the caller can see.
    /// </para>
    /// <para>
    /// <b>The returned stream is single-use.</b> Implementations must throw
    /// <see cref="InvalidOperationException"/> from a second enumeration rather than returning an
    /// empty or completed sequence, which a caller retrying in a loop cannot distinguish from a fleet
    /// that has gone quiet.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Ends the subscription and releases its buffer. Must be capable of being cancelled.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="cancellationToken"/> cannot be cancelled.</exception>
    IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by <see cref="ITelemetryStore.Write"/> when a frame would introduce a vehicle beyond
/// <see cref="ITelemetryStore.MaxVehicles"/>.
/// </summary>
/// <remarks>
/// A dedicated type so the feed can catch exactly this -- a known, survivable misconfiguration --
/// without also swallowing a genuine bug from inside the store, which catching
/// <see cref="InvalidOperationException"/> would do.
/// <para>
/// <b>Repeating this is the symptom, not the fault.</b> Slots are never released on their own, so
/// once the roster is full every unknown vehicle throws until an operator calls
/// <see cref="ITelemetryStore.Forget"/>. Treat a sustained run as "the fleet view needs attention",
/// not as noise to rate-limit away.
/// </para>
/// </remarks>
public sealed class TelemetryStoreCapacityExceededException : Exception
{
    /// <summary>Creates the exception for a rejected vehicle.</summary>
    public TelemetryStoreCapacityExceededException(VehicleId rejectedId)
        : base($"The telemetry store already holds its maximum of {ITelemetryStore.MaxVehicles} "
            + $"vehicles; the frame from '{rejectedId}' was rejected. This is a capacity "
            + "commitment, not a transient condition -- the feed is reporting more vehicles than "
            + "the station is built to display.")
    {
        RejectedId = rejectedId;
        MaxVehicles = ITelemetryStore.MaxVehicles;
    }

    /// <summary>
    /// Gets the vehicle whose frame was rejected. Safe to interpolate into a log template:
    /// <see cref="VehicleId.ToString"/> never throws, and the id's allowlist means it cannot carry a
    /// control character into a log record.
    /// </summary>
    public VehicleId RejectedId { get; }

    /// <summary>Gets the cap that was reached, so the log line reads without a second lookup.</summary>
    public int MaxVehicles { get; }
}
