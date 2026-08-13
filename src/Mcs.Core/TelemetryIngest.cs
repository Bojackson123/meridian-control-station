namespace Mcs.Core;

/// <summary>
/// The station's ingest boundary: the one place a receipt timestamp is taken, and the only route by
/// which a <see cref="TelemetryFrame"/> can come into existence (MCS-005).
/// </summary>
/// <remarks>
/// <b>The ordering problem this solves.</b> A <see cref="VehicleTelemetry"/> cannot be built until
/// its message is decoded, but the timestamp wanted is the one from <i>before</i> the decode.
/// Stamping at frame construction bakes the entire decode cost into the recorded age of the data,
/// invisibly and on every frame. Two steps fix the ordering:
/// <code>
/// TelemetryReceipt receipt = ingest.BeginReceive();     // clock read here, at arrival
/// VehicleTelemetry telemetry = Decode(rawMessage);      // takes as long as it takes
/// TelemetryFrame frame = receipt.Complete(telemetry);   // stamped with the earlier reading
/// </code>
/// <para>
/// Concrete rather than an interface: the store gets one because latency measurement will wrap it
/// and the deconfliction tests will drive a fake, but this type has one behaviour and is already
/// controllable through its injected <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// Thread-safe -- no mutable state, so one instance is shared by every adapter. The receipts it
/// hands out are not.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// The station clock, and the only clock permitted to establish a receipt time. Required rather
/// than defaulted, so no wall-clock path exists to be taken by accident.
/// </param>
public sealed class TelemetryIngest(TimeProvider timeProvider)
{
    /// <summary>
    /// How long decoding may take before the delay is worth a warning.
    /// </summary>
    /// <remarks>
    /// Derived from MCS-001's one second from receipt to screen: everything after ingest has to fit
    /// in that same second, so decode taking more than ~5% of it signals work has crept in front of
    /// the stamp. Offered rather than enforced -- <c>Mcs.Core</c> may not reference a logger, and a
    /// late frame beats a dropped one.
    /// </remarks>
    public static readonly TimeSpan RecommendedIngestBudget = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Records the instant a message arrived and returns the receipt that will stamp its frame.
    /// Call it as the first statement after a read, before decoding or awaiting anything: work in
    /// front of this call lands silently inside the measured age of the data, work after it is
    /// measured by <see cref="TelemetryReceipt.IngestDelay"/>.
    /// </summary>
    /// <remarks>
    /// Two readings, answering different questions. <see cref="TimeProvider.GetUtcNow"/> gives a
    /// real point on the calendar, which staleness and the API need;
    /// <see cref="TimeProvider.GetTimestamp"/> gives a monotonic tick count, which measuring a
    /// duration needs. See <see cref="TelemetryReceipt.Elapsed"/>.
    /// </remarks>
    public TelemetryReceipt BeginReceive() =>
        new(_timeProvider, _timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
}

/// <summary>
/// A record that a message arrived at a particular instant, exchangeable exactly once for the
/// <see cref="TelemetryFrame"/> carrying that instant.
/// </summary>
/// <remarks>
/// <b>Single use, and enforced.</b> A receipt completable twice would let one arrival mint two
/// frames bearing the same receipt time -- a replay that would look entirely ordinary in the store.
/// <para>
/// <b>Not thread-safe, by design.</b> A receipt belongs to the thread that received the message and
/// follows it through decode to <see cref="Complete"/>. Share the <see cref="TelemetryIngest"/>,
/// never a receipt. One member holds anyway under misuse, because what it protects is a safety
/// property: see <see cref="IngestDelay"/>.
/// </para>
/// </remarks>
public sealed class TelemetryReceipt
{
    //  Unambiguous because Elapsed is monotonic and cannot come back negative, which is what lets
    //  one field carry both "not completed" and the delay.
    private const long NotYetCompleted = -1;

    private readonly TimeProvider _timeProvider;

    //  The monotonic partner to ReceivedAtUtc. Not exposed: a raw tick count means nothing outside
    //  the provider that issued it. It does travel onto the frame, internally, because measuring an
    //  age from a calendar reading is what an NTP step corrupts -- see TelemetryFrame.
    private readonly long _receivedTimestamp;

    //  The decode cost in ticks, or NotYetCompleted -- and the single-use flag as well, because those
    //  are one fact and not two. A long rather than the TimeSpan? the property exposes: TimeSpan? is
    //  a flag plus a long, two stores a reader can see half of, which is a 50 ms decode logged as
    //  0 ms. An aligned long is written atomically.
    private long _ingestDelayTicks = NotYetCompleted;

    internal TelemetryReceipt(
        TimeProvider timeProvider, DateTimeOffset receivedAtUtc, long receivedTimestamp)
    {
        _timeProvider = timeProvider;
        _receivedTimestamp = receivedTimestamp;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>Gets the instant the message arrived, from the station clock. UTC by construction.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Gets the time since arrival, read live -- use it to check a decode against a deadline
    /// mid-flight. Keeps running after <see cref="Complete"/>; <see cref="IngestDelay"/> is the
    /// frozen one.
    /// </summary>
    /// <remarks>
    /// Measured from <see cref="TimeProvider.GetTimestamp"/> rather than by subtracting wall-clock
    /// readings, which can step by more than everything this measures: a backward step would give a
    /// negative delay that passes every budget check silently, a forward one a decode that appears
    /// to have taken a second.
    /// </remarks>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_receivedTimestamp);

    /// <summary>
    /// Gets how long elapsed between arrival and <see cref="Complete"/>, or <see langword="null"/>
    /// if this receipt has not been completed. Compare it against
    /// <see cref="TelemetryIngest.RecommendedIngestBudget"/> where a logger is available.
    /// </summary>
    /// <remarks>
    /// Published safely despite the type being single-threaded by contract, because this gates the
    /// latency alarm and every way of getting it wrong is silent -- a torn pair reporting a slow
    /// decode as instant, or a null read from a receipt that has completed. Both look like a healthy
    /// pipeline, and the code that logs this is the code most likely to be handed the receipt on a
    /// continuation.
    /// </remarks>
    public TimeSpan? IngestDelay
    {
        get
        {
            long ticks = Volatile.Read(ref _ingestDelayTicks);

            return ticks == NotYetCompleted ? null : new TimeSpan(ticks);
        }
    }

    /// <summary>
    /// Exchanges this receipt for the frame carrying its arrival time. May be called once.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="telemetry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This receipt has already been completed.</exception>
    public TelemetryFrame Complete(VehicleTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // Measured before the frame is built, so the number reports decode cost rather than decode
        // cost plus construction. A rejected second call costs one timestamp and mutates nothing.
        long ingestDelayTicks = Elapsed.Ticks;

        // Claiming the receipt and recording the delay are one operation, so there is no instant at
        // which this receipt is complete but its delay unreadable.
        if (Interlocked.CompareExchange(ref _ingestDelayTicks, ingestDelayTicks, NotYetCompleted)
            != NotYetCompleted)
        {
            throw new InvalidOperationException(
                "This receipt has already stamped a frame. One arrival stamps exactly one frame "
                + "(MCS-005); call BeginReceive again for the next message.");
        }

        //  Both of the readings taken at BeginReceive, and neither of them taken here: the frame's
        //  age is measured from the monotonic one, so a decode that took 40 ms is 40 ms of age
        //  rather than none.
        return TelemetryFrame.Create(telemetry, ReceivedAtUtc, _receivedTimestamp);
    }
}
