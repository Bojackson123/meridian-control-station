namespace Mcs.Adapters.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> that moves forward by a fixed step every time a duration is
/// measured, standing in for a decode that takes real time.
/// </summary>
/// <remarks>
/// <see cref="FakeClock"/> is the right tool where the test drives the decode itself and can advance
/// the clock between the two readings. It cannot be used here: the decode happens inside the
/// adapter's read loop, and a test has no seam between <c>BeginReceive</c> and <c>Complete</c> to
/// reach into. A frozen clock would then report every decode as instantaneous, which is precisely
/// the assertion that cannot distinguish a frame stamped at arrival from one stamped afterwards --
/// so the clock has to move on its own.
/// <para>
/// <b>Only <see cref="GetTimestamp"/> advances it.</b> <see cref="GetUtcNow"/> is a pure read, so a
/// test -- or the store recording what the clock said -- can observe without perturbing what it is
/// measuring. The two readings move together, which is what makes an arrival stamp taken from
/// <see cref="GetUtcNow"/> comparable against a delay measured from <see cref="GetTimestamp"/>.
/// </para>
/// </remarks>
/// <param name="step">
/// How far the clock jumps per measurement. One step is what a receipt's <c>IngestDelay</c> comes
/// back as, because exactly one timestamp is taken between arrival and completion.
/// </param>
internal sealed class SteppingClock(TimeSpan step) : TimeProvider
{
    private DateTimeOffset _utcNow = FakeClock.Arrival;

    //  Not zero at construction, for the reason FakeClock gives: a receipt that kept the raw
    //  timestamp instead of a difference would still read as zero elapsed if this started at zero.
    private long _timestamp = TimeSpan.TicksPerHour;

    /// <summary>One tick per <see cref="TimeSpan"/> tick, so a step reads back as exactly itself.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Gets how far the clock moves per measurement.</summary>
    internal TimeSpan Step => step;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp()
    {
        long reading = _timestamp;

        _timestamp += step.Ticks;
        _utcNow = _utcNow.Add(step);

        return reading;
    }
}
