namespace Mcs.Core;

/// <summary>
/// The station's ingest boundary: the one place a receipt timestamp is taken, and the only
/// route by which a <see cref="TelemetryFrame"/> can come into existence (MCS-005).
/// </summary>
/// <remarks>
/// <b>The ordering problem this exists to solve.</b> A <see cref="VehicleTelemetry"/> cannot be
/// built until its message has been decoded, but the timestamp wanted is the one from
/// <i>before</i> the decode. Stamping at frame construction therefore bakes the entire decode
/// cost into the recorded age of the data, invisibly and on every single frame. Splitting
/// receipt into two steps fixes the ordering: <see cref="BeginReceive"/> reads the clock at
/// arrival, and <see cref="TelemetryReceipt.Complete"/> uses that earlier reading once the
/// decode has finished.
/// <code>
/// TelemetryReceipt receipt = ingest.BeginReceive();     // clock read here, at arrival
/// VehicleTelemetry telemetry = Decode(rawMessage);      // takes as long as it takes
/// TelemetryFrame frame = receipt.Complete(telemetry);   // stamped with the earlier reading
/// </code>
/// <para>
/// A concrete class rather than an interface, deliberately. The store gets an interface because
/// latency measurement will wrap it and the deconfliction tests will drive a fake one; this type
/// has one behaviour and is already fully controllable through its injected
/// <see cref="TimeProvider"/>, so an interface would add a seam nothing needs. One implementation
/// does not justify an abstraction.
/// </para>
/// <para>
/// Thread-safe: holds no mutable state, so a single instance is shared by every adapter. The
/// receipts it hands out are not -- see <see cref="TelemetryReceipt"/>.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// The station clock, and the only clock permitted to establish a receipt time. Production
/// passes <see cref="TimeProvider.System"/> explicitly; tests pass a fake so receipt times are
/// chosen rather than raced for. Required rather than defaulted, so no wall-clock path exists
/// to be taken by accident.
/// </param>
public sealed class TelemetryIngest(TimeProvider timeProvider)
{
    /// <summary>
    /// How long decoding a message may take between <see cref="BeginReceive"/> and
    /// <see cref="TelemetryReceipt.Complete"/> before the delay is worth a warning.
    /// </summary>
    /// <remarks>
    /// Derived from MCS-001, which allows one second from frame receipt to the field changing on
    /// screen. Everything after ingest -- store write, SSE push, browser render -- has to fit in
    /// that same second, so decode taking more than about 5% of it is a signal that work has
    /// crept in front of the stamp. The number is a budget, not a measurement; it is offered
    /// here rather than enforced because <c>Mcs.Core</c> may not reference a logger, and
    /// because a late frame still beats a dropped one -- the ingest pipeline compares
    /// <see cref="TelemetryReceipt.IngestDelay"/> against this and logs, it does not reject.
    /// </remarks>
    public static readonly TimeSpan RecommendedIngestBudget = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Records the instant a message arrived and returns the receipt that will stamp its frame.
    /// </summary>
    /// <remarks>
    /// Call this as the first statement after a message is read, before decoding, validating,
    /// queueing, or awaiting anything. Every unit of work placed in front of this call is
    /// latency that silently lands inside the measured age of the data and cannot be recovered
    /// afterwards; every unit of work placed after it is measured by
    /// <see cref="TelemetryReceipt.IngestDelay"/> instead of hidden.
    /// <para>
    /// Two readings are taken, because they answer different questions.
    /// <see cref="TimeProvider.GetUtcNow"/> gives the instant to stamp the frame with -- a real
    /// point on the calendar, which is what staleness and the API need.
    /// <see cref="TimeProvider.GetTimestamp"/> gives a monotonic tick count with no meaning of its
    /// own, which is what measuring a duration needs; see <see cref="TelemetryReceipt.Elapsed"/>.
    /// </para>
    /// </remarks>
    /// <returns>A single-use receipt carrying the arrival time.</returns>
    public TelemetryReceipt BeginReceive() =>
        new(_timeProvider, _timeProvider.GetUtcNow(), _timeProvider.GetTimestamp());
}

/// <summary>
/// A record that a message arrived at a particular instant, exchangeable exactly once for the
/// <see cref="TelemetryFrame"/> carrying that instant.
/// </summary>
/// <remarks>
/// Obtained only from <see cref="TelemetryIngest.BeginReceive"/>, and
/// <see cref="Complete"/> is the only caller of the frame's internal constructor. Together that
/// makes the MCS-005 rule structural rather than advisory: outside <c>Mcs.Core</c> there is no
/// expression that produces a frame without first having recorded an arrival.
/// <para>
/// <b>Single use, and enforced.</b> A receipt that could be completed twice would let one arrival
/// mint two frames bearing the same receipt time -- a replay, and one that would look entirely
/// ordinary in the store. The second call throws rather than returning a duplicate, because a
/// caller holding a spent receipt has a bug that silence would hide.
/// </para>
/// <para>
/// <b>Not thread-safe, by design.</b> A receipt belongs to the thread that received the message
/// and follows it through decode to <see cref="Complete"/>; that is the only sequence it is for.
/// Share the <see cref="TelemetryIngest"/> between threads, never a receipt.
/// <para>
/// One member holds anyway, under misuse, because what it protects is a safety property rather
/// than a convenience: <see cref="Complete"/> claims the receipt and records
/// <see cref="IngestDelay"/> in a single interlocked operation on one <c>long</c>. A raced receipt
/// still stamps exactly one frame, and a reader taking the delay off a logging continuation gets
/// the decode cost or nothing -- never a torn pair claiming a slow decode was instant, never a
/// completed receipt whose delay still reads <see langword="null"/>. That does not make the type
/// shareable; it only makes misuse fail loudly rather than quietly understate a latency.
/// </para>
/// </para>
/// </remarks>
public sealed class TelemetryReceipt
{
    /// <summary>
    /// The <see cref="_ingestDelayTicks"/> value meaning "not completed, no delay recorded".
    /// Unambiguous because <see cref="Elapsed"/> is monotonic and cannot come back negative, which
    /// is what lets one field carry both facts. It is also the comparand
    /// <see cref="Complete"/> tests against, so the receipt is claimed by exactly the thread that
    /// finds it unset.
    /// </summary>
    private const long NotYetCompleted = -1;

    private readonly TimeProvider _timeProvider;

    // The monotonic partner to ReceivedAtUtc, and the only thing durations are measured from.
    // Not exposed: a raw tick count means nothing outside the provider that issued it.
    private readonly long _receivedTimestamp;

    // The decode cost in ticks, or NotYetCompleted -- and the single-use flag as well, because
    // those are one fact and not two. A long rather than the TimeSpan? the property exposes:
    // TimeSpan? is a flag plus a long, two stores a reader can see half of, which is a 50 ms
    // decode logged as 0 ms. An aligned long is written atomically.
    //
    // One field so that "complete" and "here is the delay" cannot be observed apart. A separate
    // interlocked int flipped *before* this was written left exactly that window: a reader
    // observing completion any way other than by holding the returned frame could see a completed
    // receipt whose IngestDelay was still null. Interlocked.CompareExchange here is now the
    // test-and-set and the publication in one operation.
    private long _ingestDelayTicks = NotYetCompleted;

    /// <summary>
    /// Called only by <see cref="TelemetryIngest.BeginReceive"/>, which has already read the clock.
    /// </summary>
    internal TelemetryReceipt(
        TimeProvider timeProvider, DateTimeOffset receivedAtUtc, long receivedTimestamp)
    {
        _timeProvider = timeProvider;
        _receivedTimestamp = receivedTimestamp;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>
    /// Gets the instant the message arrived, from the station clock. UTC by construction --
    /// <see cref="TimeProvider.GetUtcNow"/> returns a zero offset, so there is no
    /// <c>DateTimeKind</c> flag for anyone to have set wrongly.
    /// </summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Gets the time since arrival, read live. Use it to check a decode against a deadline
    /// mid-flight; for the delay a frame was actually stamped with, read
    /// <see cref="IngestDelay"/> instead.
    /// </summary>
    /// <remarks>
    /// <b>Keeps running after <see cref="Complete"/>.</b> Nothing freezes it -- it is a live
    /// reading every time it is asked, before completion and after. A pipeline that logs this in
    /// the step following <c>Complete</c> records total time in the station rather than decode
    /// cost, and gets a different number on every read. <see cref="IngestDelay"/> is the frozen one.
    /// <para>
    /// Measured from <see cref="TimeProvider.GetTimestamp"/> rather than by subtracting
    /// <see cref="ReceivedAtUtc"/> from the current wall clock. Wall time is allowed to step --
    /// an NTP correction, a manual change, a resumed VM -- and a step of a few hundred
    /// milliseconds in either direction is larger than everything this measures. Subtracting two
    /// wall readings would turn a backward step into a negative delay that passes every budget
    /// check silently, and a forward step into a decode that appears to have taken a second. The
    /// monotonic timestamp cannot go backwards and does not move when the calendar is corrected.
    /// </para>
    /// </remarks>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_receivedTimestamp);

    /// <summary>
    /// Gets how long elapsed between arrival and <see cref="Complete"/>, or
    /// <see langword="null"/> if this receipt has not been completed.
    /// </summary>
    /// <remarks>
    /// This is the lateness that no type can prevent, converted into a number that can be logged
    /// and alerted on. Compare it against <see cref="TelemetryIngest.RecommendedIngestBudget"/>
    /// in the ingest pipeline, where a logger is available. Frozen at completion, unlike
    /// <see cref="Elapsed"/>, so it still reports the decode cost when read later.
    /// <para>
    /// Published safely even though the type is single-threaded by contract, because this gates
    /// the latency alarm and every way of getting it wrong is silent: a torn pair reporting a slow
    /// decode as instant, or a null read from a receipt that has in fact completed. Both look like
    /// a healthy pipeline. <see cref="Complete"/> therefore does not flip a flag and then write
    /// this -- it stores the delay <i>as</i> the claim, one interlocked write to one field, so
    /// there is no window between the two. The code that logs this is exactly the code most likely
    /// to be handed the receipt on a continuation.
    /// </para>
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
    /// <param name="telemetry">The decoded vehicle report, already validated by its own factory.</param>
    /// <returns>A frame stamped with <see cref="ReceivedAtUtc"/> -- the arrival time, not now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="telemetry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This receipt has already been completed.</exception>
    public TelemetryFrame Complete(VehicleTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // Measured before the frame is built, so the number reports decode cost rather than
        // decode cost plus construction. Reading the clock ahead of the guard costs a rejected
        // second call one timestamp and mutates nothing -- the CompareExchange below leaves the
        // field as it found it when it loses.
        long ingestDelayTicks = Elapsed.Ticks;

        // Claiming the receipt and recording the delay are one operation, so there is no ordering
        // between them to get wrong and no instant at which this receipt is complete but its
        // delay unreadable. Elapsed is monotonic and cannot produce the negative sentinel, so the
        // comparand is unambiguous.
        if (Interlocked.CompareExchange(ref _ingestDelayTicks, ingestDelayTicks, NotYetCompleted)
            != NotYetCompleted)
        {
            throw new InvalidOperationException(
                "This receipt has already stamped a frame. One arrival stamps exactly one frame "
                + "(MCS-005); call BeginReceive again for the next message.");
        }

        return TelemetryFrame.Create(telemetry, ReceivedAtUtc);
    }
}
