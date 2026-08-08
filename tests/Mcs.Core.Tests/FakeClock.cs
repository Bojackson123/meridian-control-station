namespace Mcs.Core.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock stands still until a test moves it.
/// </summary>
/// <remarks>
/// MCS-005 is a statement about <i>which instant</i> a frame is stamped with, so verifying it
/// means naming both the arrival instant and the decode cost outright. Against the wall clock a
/// test could only assert that some time had passed -- which is exactly the assertion that
/// cannot distinguish a frame stamped at arrival from one stamped after the decode.
/// <para>
/// Hand-written rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the only
/// members exercised here are <see cref="GetUtcNow"/> and <see cref="GetTimestamp"/>, since
/// nothing in <c>Mcs.Core</c> schedules a timer, and a package added for twenty lines is one more
/// thing the solution's NuGet audit has to keep clearing at every level.
/// </para>
/// <para>
/// <b>Two clocks, because the production types use two.</b> The calendar reading is what stamps a
/// frame; the monotonic reading is what durations are measured from. <see cref="Advance"/> moves
/// both together, which is the ordinary case. <see cref="StepWallClock"/> moves only the calendar,
/// which is what an NTP correction does to a running station -- the case that must not disturb a
/// measured decode cost.
/// </para>
/// <para>
/// <see cref="Advance"/> is not thread-safe. The one concurrent test races
/// <see cref="TelemetryReceipt.Complete"/> and holds the clock still while it does.
/// </para>
/// </remarks>
/// <param name="utcNow">The instant the clock starts at.</param>
internal sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
{
    /// <summary>
    /// An arbitrary but fixed arrival instant, carrying a zero offset like
    /// <see cref="TimeProvider.System"/> does -- so nothing here depends on the machine's
    /// local zone.
    /// </summary>
    public static readonly DateTimeOffset Arrival = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _utcNow = utcNow;

    // Deliberately not zero at construction: a receipt that accidentally kept the raw timestamp
    // instead of a difference would still look like zero elapsed if this started at zero.
    private long _timestamp = TimeSpan.TicksPerHour;

    /// <summary>Starts the clock at <see cref="Arrival"/>.</summary>
    public FakeClock()
        : this(Arrival)
    {
    }

    /// <summary>
    /// One tick per <see cref="TimeSpan"/> tick, so <see cref="TimeProvider.GetElapsedTime(long)"/>
    /// converts without a scaling factor and an advance of 37 ms reads back as exactly 37 ms.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    /// <summary>
    /// Moves both readings forward, standing in for however long a decode took.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        _utcNow = _utcNow.Add(by);
        _timestamp += by.Ticks;
    }

    /// <summary>
    /// Steps the calendar without advancing the monotonic reading -- an NTP correction, a manual
    /// change, or a VM resuming. Accepts a negative <paramref name="by"/>: stepping backwards is
    /// the direction that does the damage.
    /// </summary>
    public void StepWallClock(TimeSpan by) => _utcNow = _utcNow.Add(by);
}
