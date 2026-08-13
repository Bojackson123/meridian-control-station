namespace Mcs.Simulator.Mavlink;

/// <summary>
/// One message stream's rate: decides, at each simulation step, whether that message is due.
/// </summary>
/// <remarks>
/// <b>Why every stream gets one of these rather than sharing a tick counter.</b> A real vehicle
/// sends its messages on independent schedules, and the station's assembler exists precisely
/// because they differ -- it folds several messages into one running state and emits when a
/// position arrives, which is code that a simulator sending everything in one bundle at one rate
/// would leave untested by construction. Four rates that are not multiples of each other is the
/// arrangement that exercises it: some positions arrive with a fresh HUD behind them and some carry
/// the one from before.
///
/// <para>
/// <b>Due times accumulate in simulated seconds, not in whole ticks.</b> Counting ticks would
/// force every rate to divide the physics step, which quietly rules out exactly the non-harmonic
/// rates this type exists to allow -- 3 Hz against a 20 Hz step is 6.67 ticks.
/// </para>
///
/// <para>
/// <b>A stream that falls behind drops what it missed rather than bursting to catch up.</b> If the
/// caller's elapsed time jumps -- a stalled container, a test advancing a fake clock in one step --
/// the next due time is walked forward past the present instead of firing once per missed
/// interval. A vehicle does not backfill telemetry: the frames it did not send while it was busy
/// are gone, and sending them all at once would put a burst of identical positions on the link,
/// which is a worse lie than the gap.
/// </para>
///
/// <para><b>Not thread-safe.</b> One schedule per stream, polled by the one loop flying the aircraft.</para>
/// </remarks>
internal sealed class MessageSchedule
{
    /// <summary>
    /// Slack on the due comparison, in seconds.
    /// </summary>
    /// <remarks>
    /// Elapsed time accumulates as a sum of step durations and due times as a sum of intervals, so
    /// the two drift apart in the last bits of a double. Without this a 4 Hz stream on a 20 Hz step
    /// misses its tick roughly whenever the two sums round differently, and the symptom is a rate
    /// that comes out a fraction low over a long run -- small enough to look like rounding and
    /// large enough to fail a ratio assertion. A microsecond is far below any rate worth
    /// configuring and far above the error.
    /// </remarks>
    private const double DueEpsilonSeconds = 1e-6;

    private readonly double _intervalSeconds;

    private double _nextDueSeconds;

    /// <summary>Builds a schedule for one stream.</summary>
    /// <param name="rateHz">How many messages per second. Finite and positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rateHz"/> is not usable.</exception>
    internal MessageSchedule(double rateHz)
    {
        if (!double.IsFinite(rateHz) || rateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rateHz), rateHz, "A message rate must be a finite, positive number of hertz.");
        }

        RateHz = rateHz;
        _intervalSeconds = 1.0 / rateHz;

        //  Zero, so every stream is due at t = 0 and the first frame of each goes out immediately.
        //  Staggering them would spread the load, which is not a problem this has, and would make
        //  the first seconds of a capture harder to reason about than the rest of it.
        _nextDueSeconds = 0;
    }

    /// <summary>Gets the configured rate, for the startup log line.</summary>
    internal double RateHz { get; }

    /// <summary>
    /// Returns whether this stream is due at <paramref name="elapsedSeconds"/>, consuming the slot
    /// if it is.
    /// </summary>
    /// <remarks>
    /// It mutates, which is unusual for something named <c>Is</c>-something and is the honest
    /// shape: asking whether a stream is due and then separately marking it sent is two calls that
    /// can be got out of step, and the failure that produces -- one message type sending at every
    /// tick forever -- is not visible in any counter this simulator keeps.
    /// </remarks>
    /// <param name="elapsedSeconds">Simulated seconds since the flight began.</param>
    internal bool IsDue(double elapsedSeconds)
    {
        if (elapsedSeconds + DueEpsilonSeconds < _nextDueSeconds)
        {
            return false;
        }

        //  Past the present, not merely one interval on: see the remarks on dropping rather than
        //  bursting. The loop runs once in the ordinary case.
        do
        {
            _nextDueSeconds += _intervalSeconds;
        }
        while (_nextDueSeconds <= elapsedSeconds + DueEpsilonSeconds);

        return true;
    }
}
