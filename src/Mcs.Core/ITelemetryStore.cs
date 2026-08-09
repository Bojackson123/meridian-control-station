namespace Mcs.Core;

/// <summary>
/// The station's bounded record of what every vehicle is doing: where a
/// <see cref="TelemetryFrame"/> lands after ingest, and the only thing the API reads from.
/// </summary>
/// <remarks>
/// <b>Bounded in both directions, and that is the whole design.</b> The store holds at most
/// <see cref="MaxVehicles"/> vehicles and at most <see cref="HistoryDepthPerVehicle"/> frames for
/// each, so a station that runs for a week uses the same memory as one that has just started. A
/// feed that misbehaves -- a stuck adapter, a vehicle re-announcing itself under a new id every
/// second, a browser tab that stopped reading -- cannot grow it.
/// <para>
/// <b>Bounded is not self-healing.</b> That is a claim about memory only. Admission is permanent,
/// so the re-announcing feed above does not merely fail to grow the store: in twelve seconds it
/// fills the roster with ids that will never report again, after which every genuine vehicle is
/// refused and <see cref="GetLatestSnapshot"/> serves twelve dead tracks. Memory holds; the fleet
/// view does not. <see cref="Forget"/> is the way back, and it is an operator action rather than
/// an automatic policy -- see the note there.
/// </para>
/// <para>
/// <b>An interface, unlike <see cref="TelemetryIngest"/>.</b> The reasoning there was that one
/// implementation does not justify an abstraction. Here there will be more than one: the latency
/// work wraps this to time it, and the deconfliction tests drive a fake. The seam is being spent
/// on something.
/// </para>
/// <para>
/// <b>Implementations must be thread-safe.</b> Adapters write from their own receive threads while
/// the API reads and enumerates from request threads; there is no point in the process where those
/// are serialised for it.
/// </para>
/// <para>
/// <b>Why the sizing constants live here rather than on the implementation.</b> They are
/// system-wide commitments, not properties of one class: the panel layout, the map and the
/// end-to-end latency budget are all designed against <see cref="MaxVehicles"/>, and a fake store
/// written for the deconfliction tests has to honour the same cap or it is testing a different
/// system. Interface constants are not inherited, so every reference reads
/// <c>ITelemetryStore.MaxVehicles</c> -- which is the point: it names where the number is decided.
/// </para>
/// </remarks>
public interface ITelemetryStore
{
    /// <summary>
    /// The most vehicles the station tracks at once. A write introducing a
    /// <see cref="MaxVehicles"/>+1th vehicle is rejected, loudly.
    /// </summary>
    /// <remarks>
    /// Not a memory limit -- twelve rings is under a megabyte -- but a scope commitment. The
    /// console's vehicle panel, the map's track budget and the end-to-end latency figure in
    /// MCS-001 are all designed and measured at twelve; a thirteenth vehicle would be rendered
    /// somewhere nobody has laid out and counted against a budget nobody has measured. This is
    /// where that assumption is stated once instead of being implied in three places.
    /// </remarks>
    public const int MaxVehicles = 12;

    /// <summary>
    /// How many frames the store keeps per vehicle before the oldest is evicted.
    /// </summary>
    /// <remarks>
    /// Derived, not picked: at the 10 Hz ingest ceiling, 600 frames is exactly one minute of
    /// history per vehicle -- long enough to draw a track behind a moving vehicle and to answer
    /// "what was it doing just before that?", which is what the history is for. The cost is
    /// <see cref="MaxVehicles"/> x 600 x roughly 100 bytes, call it 700 KB for the whole station,
    /// which is not a number worth optimising. Eviction is per vehicle: a chatty vehicle cannot
    /// push a quiet one's history out.
    /// </remarks>
    public const int HistoryDepthPerVehicle = 600;

    /// <summary>
    /// How many frames a single subscriber may fall behind by before it starts losing the oldest
    /// of them.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxVehicles"/> vehicles at 10 Hz is 120 frames per second aggregate, so 256 is
    /// roughly two seconds of full-rate traffic. MCS-001 budgets one second from receipt to the
    /// screen, so a subscriber more than two seconds behind has already blown its budget twice
    /// over: the useful response is to drop what it missed and show it the present, not to buffer
    /// a past it can never catch up with. See <see cref="Subscribe"/> for why the frames dropped
    /// are the oldest ones.
    /// <para>
    /// On the interface rather than the implementation because it is observable behaviour, not an
    /// internal size -- a consumer reasoning about what its stream may skip needs this number.
    /// </para>
    /// </remarks>
    public const int SubscriberBufferCapacity = 256;

    /// <summary>
    /// Records a frame as the vehicle's current state and appends it to that vehicle's history,
    /// admitting the vehicle if this is the first frame seen from it.
    /// </summary>
    /// <remarks>
    /// <b>Never blocks on a subscriber.</b> Fan-out to subscribers is non-blocking by contract, so
    /// a wedged HTTP client cannot slow the ingest thread down. See <see cref="Subscribe"/>.
    /// <para>
    /// <b>Rejects loudly rather than dropping.</b> Exceeding <see cref="MaxVehicles"/> throws
    /// rather than returning a status, because a status can be discarded and this one must not be.
    /// The hazard being defended against is HAZ-01 -- the console showing the operator a picture he
    /// believes is current when it is not -- and a silently ignored return value is precisely how
    /// that happens. It also matches the rest of <c>Mcs.Core</c>: <see cref="VehicleId.From"/>,
    /// <see cref="Altitude.FromMeters"/> and <see cref="TelemetryReceipt.Complete"/> all throw on a
    /// contract violation, so a lone result enum would be the surprising shape.
    /// </para>
    /// <para>
    /// The throw is affordable. A .NET throw costs some tens of microseconds; a permanently
    /// misconfigured thirteen-vehicle feed at 10 Hz throws about ten times a second, which is a
    /// rounding error on one core. The real cost of that scenario is log volume, and that is the
    /// caller's to solve at the <c>Mcs.Api</c> boundary where a logger and per-vehicle state exist
    /// -- not a reason to weaken the contract here.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to record. Its vehicle id comes from <c>frame.Telemetry.Id</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="TelemetryStoreCapacityExceededException">
    /// The frame is from a vehicle the store has not seen, and it already holds
    /// <see cref="MaxVehicles"/>. Frames from vehicles already admitted are always accepted, and
    /// slots are never reclaimed on their own -- once this starts, it continues until something
    /// calls <see cref="Forget"/>.
    /// </exception>
    void Write(TelemetryFrame frame);

    /// <summary>
    /// Gets the most recent frame recorded for a vehicle, or <see langword="null"/> if the store
    /// has never seen it.
    /// </summary>
    /// <param name="id">The vehicle to look up.</param>
    /// <returns>The latest frame, or <see langword="null"/> for an unknown vehicle.</returns>
    TelemetryFrame? GetLatest(VehicleId id);

    /// <summary>
    /// Gets the most recent frame for every vehicle the store holds -- one per vehicle, in no
    /// particular order.
    /// </summary>
    /// <remarks>
    /// What <c>GET /api/vehicles</c> serves, and what <see cref="Subscribe"/> seeds a new
    /// subscriber with. Order is deliberately unspecified: the console sorts for display, and
    /// promising an order here would be a promise the implementation has to keep forever for no
    /// caller's benefit.
    /// </remarks>
    /// <returns>One frame per known vehicle. Empty if the store has seen nothing.</returns>
    IReadOnlyList<TelemetryFrame> GetLatestSnapshot();

    /// <summary>
    /// Gets a vehicle's retained history, oldest first, up to
    /// <see cref="HistoryDepthPerVehicle"/> frames.
    /// </summary>
    /// <remarks>
    /// A copy, taken at the moment of the call. A live view over a buffer that is being written
    /// and evicted concurrently would let a caller enumerate a frame that had already been
    /// overwritten -- so what escapes here is a snapshot, and it does not change afterwards.
    /// </remarks>
    /// <param name="id">The vehicle to look up.</param>
    /// <returns>The retained frames, oldest first. Empty for an unknown vehicle.</returns>
    IReadOnlyList<TelemetryFrame> GetHistory(VehicleId id);

    /// <summary>
    /// Drops a vehicle and its history, freeing its slot against <see cref="MaxVehicles"/>. The
    /// store forgets it entirely: a later frame from the same id is admitted as a new vehicle.
    /// </summary>
    /// <remarks>
    /// <b>The recovery path for a poisoned roster.</b> Admission is otherwise permanent, so
    /// without this a feed that invents ids costs the station its fleet view until the process is
    /// restarted. This clears the dead tracks once the feed itself has been fixed; it is not a
    /// defence against that feed, which will refill the roster as fast as it is emptied. That fix
    /// belongs upstream, at the boundary deciding which ids the station listens to at all.
    /// <para>
    /// <b>Why a deliberate action and not an eviction policy.</b> Every eviction rule available
    /// here keys on recency, and recency is what a bad feed has most of. Evicting the least
    /// recently heard vehicle under admission pressure hands the roster to whichever source is
    /// chattiest -- invented ids are always the freshest thing in the store, so the genuine
    /// vehicles are the ones thrown out. An idle timeout fails worse: a vehicle quiet for thirty
    /// seconds is usually mid-dropout, and its last known position is exactly what the operator
    /// needs while it is out of contact. Dropping it there trades a loud, diagnosable refusal for
    /// a silent disappearance -- HAZ-01 with the labels swapped. A human deciding "that track is
    /// dead" is the only rule with the information to be right.
    /// </para>
    /// <para>
    /// <b>Existing subscribers are not told.</b> The stream carries frames and a removal is not
    /// one, so a subscriber already seeded keeps showing the forgotten vehicle until it
    /// resubscribes. Re-open the stream to drop the track; that reseeds from
    /// <see cref="GetLatestSnapshot"/>.
    /// </para>
    /// </remarks>
    /// <param name="id">The vehicle to drop.</param>
    /// <returns>
    /// <see langword="true"/> if the vehicle was known and has been dropped;
    /// <see langword="false"/> if the store had never seen it, which is not an error.
    /// </returns>
    bool Forget(VehicleId id);

    /// <summary>
    /// Opens a live stream of frames: the current snapshot first, then every frame written after
    /// the subscription was taken.
    /// </summary>
    /// <remarks>
    /// <b>Seeded, so a late joiner sees the whole fleet.</b> The stream begins with
    /// <see cref="GetLatestSnapshot"/>. Without that, a consumer's natural implementation --
    /// snapshot, then subscribe -- drops any frame landing between the two calls, and a vehicle
    /// that has stopped transmitting never appears at all. Seeding inside the subscription is what
    /// closes that gap; the seed and the registration must be ordered against writes so that a
    /// subscriber sees no gap and never observes an older frame after a newer one.
    /// <para>
    /// <b>Bounded, and drops the oldest.</b> A subscriber that falls more than
    /// <see cref="SubscriberBufferCapacity"/> frames behind loses the <i>oldest</i> of what it has
    /// not read. This is a state stream, not an event log: when a stalled browser resumes, the
    /// operator needs to know where the vehicle is, not to replay where it was. Dropping the
    /// newest instead would leave the subscriber permanently behind reality while showing a
    /// smooth, complete and entirely stale picture -- HAZ-01, stated exactly. Leaving it unbounded
    /// would let one wedged tab grow the station without limit.
    /// </para>
    /// <para>
    /// <b>Registration is eager.</b> The subscription exists from the moment this returns, not
    /// from the first <c>MoveNextAsync</c>, so frames written between the call and the start of
    /// enumeration are buffered rather than lost. Implementations must therefore not write this
    /// method as an iterator -- an <c>async IAsyncEnumerable</c> body does not run until it is
    /// enumerated, which would silently reopen the gap the seeding exists to close.
    /// </para>
    /// <para>
    /// <b>Cancellation ends the stream by throwing.</b> Cancelling
    /// <paramref name="cancellationToken"/> raises an <see cref="OperationCanceledException"/> out
    /// of the enumeration -- the standard .NET contract -- and unregisters the subscriber.
    /// Abandoning the enumeration, by breaking out of an <c>await foreach</c> or disposing the
    /// enumerator, also unregisters it. A caller that does neither leaks a subscription, which is
    /// why the API's SSE endpoint will pass the request-aborted token.
    /// </para>
    /// <para>
    /// <b>The token is required, and a non-cancellable one is rejected.</b> Because registration
    /// is eager, this token is the only handle on a subscription whose enumeration has not
    /// started -- and <see cref="CancellationToken.Register(Action)"/> on a token that
    /// <see cref="CancellationToken.CanBeCanceled">cannot be cancelled</see> is a documented
    /// no-op. Nothing could release <c>Subscribe(default)</c> on the un-enumerated path, so the
    /// paragraph above would simply be false for it, and the leak is permanent, costs a write per
    /// frame inside the store's own gate, and has no symptom the caller can see. Implementations
    /// must therefore throw <see cref="ArgumentException"/> rather than accept a token that
    /// cannot do the job the contract gives it. A caller with no natural token owns a
    /// <see cref="CancellationTokenSource"/> instead -- the same one line, except cancellable.
    /// </para>
    /// <para>
    /// <b>The returned stream is single-use.</b> Because registration is eager, the subscription is
    /// the object returned here, not something a <c>foreach</c> creates -- so enumerating it a
    /// second time cannot mean "subscribe again", and it does not silently mean "receive nothing"
    /// either. Implementations must throw <see cref="InvalidOperationException"/> from the second
    /// enumeration rather than returning an empty or already-completed sequence, which a caller
    /// retrying in a loop cannot distinguish from a fleet that has gone quiet. Two readers means
    /// two calls to this method.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Ends the subscription and releases its buffer. Must be capable of being cancelled.
    /// </param>
    /// <returns>The seed frames followed by live frames, in the order they were written.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="cancellationToken"/> cannot be cancelled -- <see langword="default"/>,
    /// <see cref="CancellationToken.None"/>, or any other token whose
    /// <see cref="CancellationToken.CanBeCanceled"/> is <see langword="false"/>.
    /// </exception>
    IAsyncEnumerable<TelemetryFrame> Subscribe(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by <see cref="ITelemetryStore.Write"/> when a frame would introduce a vehicle beyond
/// <see cref="ITelemetryStore.MaxVehicles"/>.
/// </summary>
/// <remarks>
/// A dedicated type rather than <see cref="InvalidOperationException"/>, and one of the few worth
/// adding. The feed has to catch exactly this condition -- a known, survivable misconfiguration --
/// and log a warning without also swallowing a genuine bug from inside the store. Catching
/// <c>InvalidOperationException</c> around a <c>Write</c> would do both.
/// <para>
/// The two properties exist so the caller's log line does not have to parse
/// <see cref="Exception.Message"/> to say which vehicle was turned away.
/// </para>
/// <para>
/// <b>Repeating this is the symptom, not the fault.</b> Slots are never released on their own, so
/// once the roster is full every unknown vehicle throws until an operator calls
/// <see cref="ITelemetryStore.Forget"/> on whatever is occupying one. Treat a sustained run of
/// these as "the fleet view needs attention", not as noise to rate-limit away.
/// </para>
/// </remarks>
public sealed class TelemetryStoreCapacityExceededException : Exception
{
    /// <summary>
    /// Creates the exception for a rejected vehicle.
    /// </summary>
    /// <param name="rejectedId">The vehicle the store refused to admit.</param>
    public TelemetryStoreCapacityExceededException(VehicleId rejectedId)
        : base($"The telemetry store already holds its maximum of {ITelemetryStore.MaxVehicles} "
            + $"vehicles; the frame from '{rejectedId}' was rejected. This is a capacity "
            + "commitment, not a transient condition -- the feed is reporting more vehicles than "
            + "the station is built to display.")
    {
        RejectedId = rejectedId;
        MaxVehicles = ITelemetryStore.MaxVehicles;
    }

    /// <summary>Gets the vehicle whose frame was rejected.</summary>
    /// <remarks>
    /// Interpolating this into a log template is safe: <see cref="VehicleId.ToString"/> never
    /// throws, and the id's allowlist means it cannot carry a control character into a log record.
    /// </remarks>
    public VehicleId RejectedId { get; }

    /// <summary>
    /// Gets the cap that was reached -- <see cref="ITelemetryStore.MaxVehicles"/>, carried here so
    /// the log line reads without a second lookup.
    /// </summary>
    public int MaxVehicles { get; }
}
