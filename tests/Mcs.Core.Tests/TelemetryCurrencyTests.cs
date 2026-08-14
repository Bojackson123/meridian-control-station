namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="TelemetryCurrency"/> -- the station's answer to "how old is
/// this, and is that too old" (<b>MCS-002</b>).
/// </summary>
/// <remarks>
/// This is the direct mitigation for the hazard the whole system is arranged against: a console
/// showing an operator a picture they believe is current when it is not. So the cases below are
/// less about arithmetic than about the three ways that arithmetic could be made to lie:
/// <list type="bullet">
/// <item><description>
/// <b>The boundary is where the requirement says it is.</b> Three seconds, measured from arrival,
/// inclusive -- a vehicle silent for exactly the threshold is already stale.
/// </description></item>
/// <item><description>
/// <b>Only the station clock participates.</b> Not the vehicle's, which the model gives no field
/// to carry, and not a corrected wall clock -- an NTP step must not take a minute off every
/// vehicle's age at once and bring a lost fleet back to life.
/// </description></item>
/// <item><description>
/// <b>Nothing is ever assumed current.</b> An uninitialised reading throws rather than reporting a
/// zero age, a negative age throws rather than clamping to one, and a vehicle never heard from is
/// absent rather than live.
/// </description></item>
/// </list>
/// Every case runs on <see cref="FakeClock"/>, and every frame is minted through the real ingest
/// boundary by <see cref="TelemetrySamples.Frame"/> -- there is no <c>Thread.Sleep</c> here and no
/// way to fabricate a frame with a timestamp of the test's choosing.
/// </remarks>
//  In the culture collection only for ToString's invariance case; every test here is synchronous,
//  which is what that collection requires.
[Collection(CultureCollection.Name)]
public class TelemetryCurrencyTests
{
    /// <summary>Just inside the stale boundary -- close enough that an off-by-one shows.</summary>
    private static readonly TimeSpan JustLive = TelemetryCurrency.StaleAfter
        - TimeSpan.FromMilliseconds(100);

    /// <summary>Just inside the lost boundary, from the stale side.</summary>
    private static readonly TimeSpan JustStale = TelemetryCurrency.LostAfter
        - TimeSpan.FromMilliseconds(100);

    // --- The thresholds themselves --------------------------------------------------------------

    [Fact]
    [Verifies("MCS-002")]
    public void StaleAfter_IsThreeSeconds()
    {
        // MCS-002, verbatim: three seconds, being three times the slowest configured telemetry
        // period. Pinned so that changing it is a deliberate act against a published requirement
        // rather than a tuning tweak.
        Assert.Equal(TimeSpan.FromSeconds(3), TelemetryCurrency.StaleAfter);
    }

    [Fact]
    [Verifies("MCS-002")]
    public void LostAfter_IsFiveTimesStale_AndInsideTheConsolesDeadStreamTimeout()
    {
        // Sourced by construction: a multiple of the same slowest telemetry period stale is built
        // from, so the two numbers cannot drift into meaning different things.
        Assert.Equal(TimeSpan.FromSeconds(15), TelemetryCurrency.LostAfter);
        Assert.Equal(5 * TelemetryCurrency.StaleAfter, TelemetryCurrency.LostAfter);

        // And bounded from above by something real. The console calls the stream dead after forty
        // seconds of silence; a vehicle has to reach lost well inside that, or "one aircraft went
        // quiet" and "the station stopped talking to me" arrive as the same picture at the same
        // moment. Restated here rather than imported because the console owns that number.
        Assert.True(
            TelemetryCurrency.LostAfter < TimeSpan.FromSeconds(40),
            "lost must be reachable while the event stream is still known to be healthy.");
    }

    // --- The boundaries -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, VehicleState.Live)]
    [InlineData(2900, VehicleState.Live)]
    [InlineData(2999, VehicleState.Live)]
    [InlineData(3000, VehicleState.Stale)]
    [InlineData(3100, VehicleState.Stale)]
    [InlineData(14_999, VehicleState.Stale)]
    [InlineData(15_000, VehicleState.Lost)]
    [InlineData(15_100, VehicleState.Lost)]
    [InlineData(3_600_000, VehicleState.Lost)]
    [Verifies("MCS-002")]
    public void FromAge_PutsTheBoundaryWhereTheRequirementDoes(int ageMilliseconds, VehicleState expected)
    {
        // Inclusive at the bottom of each band: MCS-002 says stale *when* no frame has been
        // received for three seconds, so 3.000 s is the first stale instant and not the last live
        // one. The pair either side of each threshold is the whole point of the table.
        TelemetryCurrency currency =
            TelemetryCurrency.FromAge(TimeSpan.FromMilliseconds(ageMilliseconds));

        Assert.Equal(expected, currency.State);
        Assert.Equal(TimeSpan.FromMilliseconds(ageMilliseconds), currency.Age);
    }

    [Fact]
    [Verifies("MCS-012")]
    public void FromAge_NegativeAge_ThrowsRatherThanClamping()
    {
        // Reject, never clamp. The clamp a reasonable person would write is to zero, and a zero age
        // reports Live -- so the one thing that must never happen would be the documented behaviour
        // of the code meant to prevent it.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetryCurrency.FromAge(TimeSpan.FromSeconds(-1)));

        Assert.Equal("age", ex.ParamName);
    }

    // --- Against a frame and the station clock --------------------------------------------------

    [Theory]
    [InlineData(2900, VehicleState.Live)]
    [InlineData(3000, VehicleState.Stale)]
    [InlineData(15_000, VehicleState.Lost)]
    public void Of_MeasuresTheFramesAgeFromArrival(int silenceMilliseconds, VehicleState expected)
    {
        FakeClock clock = new();
        TelemetryFrame frame = TelemetrySamples.Frame(clock);

        clock.Advance(TimeSpan.FromMilliseconds(silenceMilliseconds));

        TelemetryCurrency currency = TelemetryCurrency.Of(frame, clock);

        Assert.Equal(expected, currency.State);
        Assert.Equal(TimeSpan.FromMilliseconds(silenceMilliseconds), currency.Age);
    }

    [Fact]
    public void Of_CountsTheDecodeCostAsAge()
    {
        // The other half of MCS-005's two-phase ingest. The clock is read at arrival and the frame
        // is stamped with that reading, so a decode that took 37 ms produces a frame that is
        // already 37 ms old -- rather than one that claims to have arrived when the decode ended.
        FakeClock clock = new();
        TelemetryReceipt receipt = new TelemetryIngest(clock).BeginReceive();

        clock.Advance(TimeSpan.FromMilliseconds(37));

        TelemetryFrame frame = receipt.Complete(TelemetrySamples.Telemetry());

        Assert.Equal(TimeSpan.FromMilliseconds(37), TelemetryCurrency.Of(frame, clock).Age);
    }

    [Fact]
    public void Of_ANewFrame_ReturnsAStaleVehicleToLive()
    {
        // Nothing has to be reset, unstuck or re-flagged for this to happen: the state is derived
        // from whichever frame is latest, so recovery is a consequence of a frame arriving rather
        // than of anything noticing that one did.
        FakeClock clock = new();
        InMemoryTelemetryStore store = new();

        store.Write(TelemetrySamples.Frame(clock));
        clock.Advance(TelemetryCurrency.StaleAfter);

        Assert.Equal(VehicleState.Stale, Latest(store, clock).State);

        store.Write(TelemetrySamples.Frame(clock));

        Assert.Equal(VehicleState.Live, Latest(store, clock).State);
    }

    [Fact]
    public void AVehicleNeverHeardFrom_IsAbsentRatherThanLive()
    {
        // There is no Unknown member and no fourth state, because there is nothing to report a
        // state *about*: a vehicle the station has never received a frame from has no last known
        // position either. Absence is the honest answer, and it is the one the store already gives.
        InMemoryTelemetryStore store = new();

        Assert.Null(store.GetLatest(TelemetrySamples.Vehicle(1)));
        Assert.Empty(store.GetLatestSnapshot());
    }

    // --- The clocks that must not participate ---------------------------------------------------

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    [Verifies("MCS-002")]
    public void Of_AWallClockCorrection_DoesNotMoveTheAge(int stepSeconds)
    {
        // The station's own clock stepping, which is what NTP does to a container that has been up
        // for a while. Backwards is the direction that does the damage: an hour off every age at
        // once would return a fleet that stopped reporting this morning to Live, on a display whose
        // entire job is to say otherwise. The age is measured monotonically, so neither direction
        // touches it.
        FakeClock clock = new();
        TelemetryFrame frame = TelemetrySamples.Frame(clock);

        clock.Advance(TelemetryCurrency.LostAfter);
        clock.StepWallClock(TimeSpan.FromSeconds(stepSeconds));

        TelemetryCurrency currency = TelemetryCurrency.Of(frame, clock);

        Assert.Equal(VehicleState.Lost, currency.State);
        Assert.Equal(TelemetryCurrency.LostAfter, currency.Age);
    }

    [Fact]
    [Verifies("MCS-002")]
    public void Of_IgnoresEverythingTheVehicleClaimed()
    {
        // The untrusted-clock case, and the reason VehicleTelemetry and TelemetryFrame are two
        // types rather than one: there is no field on a report for a vehicle's own idea of the time
        // to arrive in, so no amount of nonsense in the payload can reach this calculation. Two
        // reports agreeing on nothing at all, received at one instant, are equally current.
        FakeClock clock = new();

        TelemetryFrame ordinary = TelemetrySamples.Frame(clock);
        TelemetryFrame peculiar = TelemetrySamples.Frame(
            clock,
            TelemetrySamples.Telemetry(
                id: TelemetrySamples.Vehicle(2),
                latitudeDegrees: -89.9,
                longitudeDegrees: 179.9,
                altitude: Altitude.FromMeters(-400, AltitudeReference.Hae),
                groundSpeedMetersPerSecond: null,
                headingDegrees: null,
                batteryPercent: null,
                linkStatus: LinkStatus.Lost));

        clock.Advance(JustStale);

        Assert.Equal(
            TelemetryCurrency.Of(ordinary, clock), TelemetryCurrency.Of(peculiar, clock));
    }

    [Fact]
    [Verifies("MCS-012")]
    public void Of_AFrameFromAnotherProvider_ThrowsRatherThanReportingItLive()
    {
        // Two clocks means two tick origins, and the difference between them is not an age. Left
        // unchecked this reads as a frame received in the future, which every threshold below the
        // boundary accepts as Live -- a silent wrong answer in the one place a wrong answer is the
        // hazard. The station has exactly one clock; this is what says so out loud.
        FakeClock stamping = new();
        stamping.Advance(TimeSpan.FromSeconds(5));

        TelemetryFrame frame = TelemetrySamples.Frame(stamping);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetryCurrency.Of(frame, new FakeClock()));
    }

    [Fact]
    public void Of_ReadsTheClockOncePerFleet()
    {
        // The overload the API uses for a snapshot: one reading, shared. Two vehicles heard from at
        // the same instant have the same age to the tick, rather than differing by however long the
        // loop between them took.
        FakeClock clock = new();

        TelemetryFrame first = TelemetrySamples.Frame(clock);
        TelemetryFrame second = TelemetrySamples.Frame(
            clock, TelemetrySamples.Telemetry(id: TelemetrySamples.Vehicle(2)));

        clock.Advance(JustLive);
        long now = clock.GetTimestamp();

        Assert.Equal(
            TelemetryCurrency.Of(first, clock, now), TelemetryCurrency.Of(second, clock, now));
    }

    // --- The uninitialised value ----------------------------------------------------------------

    [Fact]
    public void Default_ThrowsRatherThanReportingAZeroAge()
    {
        // A default struct reads back as a zero age, which is the one value meaning "just arrived".
        // Same argument as Altitude's uninitialised sentinel: the plausible reading is the
        // dangerous one, so it is unreachable rather than merely discouraged.
        TelemetryCurrency uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => uninitialised.Age);
        Assert.Throws<InvalidOperationException>(() => uninitialised.State);
    }

    [Fact]
    public void ToString_IsSafeForLogsInBothStates()
    {
        // Formatting must never be the thing that throws: the caller is a log line or a debugger,
        // usually one already looking into a fault.
        Assert.Equal(
            "TelemetryCurrency(uninitialised)", default(TelemetryCurrency).ToString());

        using (new CultureScope("de-DE"))
        {
            // Invariant, so a container's locale cannot turn 7.4 into 7,4 halfway through a log.
            Assert.Equal(
                "Stale after 7.4 s",
                TelemetryCurrency.FromAge(TimeSpan.FromMilliseconds(7400)).ToString());
        }
    }

    private static TelemetryCurrency Latest(ITelemetryStore store, TimeProvider clock) =>
        TelemetryCurrency.Of(
            store.GetLatest(TelemetrySamples.Vehicle(1))
                ?? throw new InvalidOperationException("the store lost the vehicle under test."),
            clock);
}
