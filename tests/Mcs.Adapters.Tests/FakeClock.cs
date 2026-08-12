namespace Mcs.Adapters.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock stands still until a test moves it.
/// </summary>
/// <remarks>
/// MCS-005 is a statement about <i>which instant</i> a decoded frame is stamped with, so verifying
/// it here means naming both the arrival instant and the decode cost outright. Against the wall
/// clock a test could only assert that some time had passed -- which is exactly the assertion that
/// cannot distinguish a frame stamped at arrival from one stamped after the decode, and the decode
/// is what this suite added.
/// <para>
/// A near-copy of <c>Mcs.Core.Tests.FakeClock</c>, and hand-written for the same reason it is:
/// only <see cref="GetUtcNow"/> and <see cref="GetTimestamp"/> are exercised, nothing in this
/// assembly schedules a timer, and <c>Microsoft.Extensions.TimeProvider.Testing</c> would be a
/// package added for twenty lines that the solution's NuGet audit then clears at every level. The
/// alternative to copying it was a shared test-support project referenced by two suites, which is
/// more moving parts than the twenty lines are worth.
/// </para>
/// </remarks>
/// <param name="utcNow">The instant the clock starts at.</param>
internal sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
{
    /// <summary>
    /// An arbitrary but fixed arrival instant, carrying a zero offset like
    /// <see cref="TimeProvider.System"/> does -- so nothing here depends on the machine's local zone.
    /// </summary>
    public static readonly DateTimeOffset Arrival = new(2026, 8, 12, 9, 30, 0, TimeSpan.Zero);

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

    /// <summary>Moves both readings forward, standing in for however long a decode took.</summary>
    public void Advance(TimeSpan by)
    {
        _utcNow = _utcNow.Add(by);
        _timestamp += by.Ticks;
    }
}
