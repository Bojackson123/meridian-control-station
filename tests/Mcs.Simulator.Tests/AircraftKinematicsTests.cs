using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Tests;

/// <summary>
/// The flight model's obligations: it turns like an aircraft, it changes altitude no faster than it
/// is allowed to, and it never teleports.
/// </summary>
/// <remarks>
/// Every test here integrates a flight path with no clock, no socket and no host, which is the
/// whole reason <see cref="AircraftKinematics"/> is pure. A turn radius compared against a
/// closed-form answer is an assertion; a turn radius compared against a recording of last week's
/// behaviour is a change detector.
/// </remarks>
public sealed class AircraftKinematicsTests
{
    /// <summary>A fine step, so the polygon the integrator traces is close to the circle it means.</summary>
    private static readonly TimeSpan FineStep = TimeSpan.FromSeconds(0.01);

    /// <summary>
    /// The turn radius the aircraft actually flies is <c>v²/(g·tan φ)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Measured geometrically, from the path.</b> Reading
    /// <see cref="AircraftEnvelope.MaxTurnRateDegreesPerSecond"/> back and dividing would restate
    /// the formula that produced it and prove nothing about the integrator that has to fly it: a
    /// position update that used the wrong trigonometric function, or applied the turn after moving
    /// rather than before, passes that test and fails this one.
    /// <para>
    /// Two speeds, because the relation is quadratic. A model that limited the turn <i>rate</i>
    /// instead would give a radius proportional to <c>v</c>, which matches at one speed and is
    /// half wrong at twice it -- so the pair catches what either alone would miss. This is the
    /// property a deconfliction bound is computed from.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(TestAircraft.CruiseSpeed)]
    [InlineData(TestAircraft.CruiseSpeed * 2)]
    public void TurnRadius_MatchesTheClosedFormForTheEnvelope(double cruiseSpeed)
    {
        AircraftEnvelope envelope = TestAircraft.Envelope(cruiseSpeed);
        LocalProjection projection = TestAircraft.Projection();
        AircraftKinematics kinematics = new(envelope, projection);

        AircraftState start = TestAircraft.InitialState() with
        {
            GroundSpeedMetersPerSecond = cruiseSpeed,
        };

        AircraftState state = start;
        double turnedDegrees = 0;

        //  Commanding a heading a quarter turn away every step keeps the turn saturated at the bank
        //  limit for the whole half-circle, which is the only condition under which the radius is
        //  the envelope's rather than something between it and straight flight.
        while (turnedDegrees < 180.0)
        {
            AircraftState next = kinematics.Advance(
                state,
                new FlightCommand(state.HeadingDegrees + 90.0, state.AltitudeMetersMsl),
                FineStep);

            turnedDegrees += Math.Abs(
                LocalProjection.SignedDifferenceDegrees(
                    state.HeadingDegrees, next.HeadingDegrees));

            state = next;
        }

        //  Half a turn apart, so the two positions are a diameter of the circle flown.
        double diameter = projection.GroundDistanceMeters(
            start.LatitudeDegrees,
            start.LongitudeDegrees,
            state.LatitudeDegrees,
            state.LongitudeDegrees);

        double expected = 2 * envelope.TurnRadiusMeters;

        //  1%, which is far wider than the integration error and far narrower than the 4x a
        //  rate-limited model would be out by at the doubled speed.
        Assert.InRange(diameter, expected * 0.99, expected * 1.01);
    }

    /// <summary>
    /// Doubling the speed quadruples the radius, which is the shape of the relation rather than one
    /// of its values.
    /// </summary>
    /// <remarks>
    /// Stated separately from the test above because it is a different claim: that one says the
    /// radius is right at each speed, this one says the two are right <i>relative to each other</i>,
    /// which is what survives someone changing the default envelope.
    /// </remarks>
    [Fact]
    public void TurnRadius_ScalesWithTheSquareOfSpeed()
    {
        double slow = TestAircraft.Envelope(TestAircraft.CruiseSpeed).TurnRadiusMeters;
        double fast = TestAircraft.Envelope(TestAircraft.CruiseSpeed * 2).TurnRadiusMeters;

        Assert.InRange(fast / slow, 3.99, 4.01);
    }

    /// <summary>The heading takes the short way round rather than the long way.</summary>
    /// <remarks>
    /// A raw subtraction turns a twenty-degree correction across north into a 340-degree one. The
    /// aircraft still arrives, half a minute later, having flown a circle nobody asked for -- which
    /// on a map reads as a deliberate manoeuvre.
    /// </remarks>
    [Fact]
    public void Heading_TurnsTheShortWayAcrossNorth()
    {
        AircraftKinematics kinematics =
            new(TestAircraft.Envelope(), TestAircraft.Projection());

        AircraftState state = TestAircraft.InitialState() with { HeadingDegrees = 350.0 };

        AircraftState next = kinematics.Advance(
            state, new FlightCommand(10.0, state.AltitudeMetersMsl), FineStep);

        //  Clockwise through north, so the heading rises past 360 and wraps to just above zero.
        Assert.True(
            next.HeadingDegrees > 350.0 || next.HeadingDegrees < 10.0,
            $"Expected a turn toward 010 through north; the heading became {next.HeadingDegrees}.");

        Assert.True(
            LocalProjection.SignedDifferenceDegrees(state.HeadingDegrees, next.HeadingDegrees) > 0,
            "Expected the turn to be clockwise, the short way round.");
    }

    /// <summary>Altitude never changes faster than the climb limit allows.</summary>
    [Fact]
    public void Altitude_RespectsTheClimbLimit()
    {
        AssertAltitudeRateIsBounded(
            commandedAltitude: 5_000.0, expectedRate: TestAircraft.MaxClimb);
    }

    /// <summary>
    /// And never faster than the descent limit, which is a different number.
    /// </summary>
    /// <remarks>
    /// Separate from the climb because one limit used for both is the easy mistake, and it is
    /// invisible in a route that only ever climbs.
    /// </remarks>
    [Fact]
    public void Altitude_RespectsTheDescentLimit()
    {
        AssertAltitudeRateIsBounded(
            commandedAltitude: -400.0, expectedRate: -TestAircraft.MaxDescent);
    }

    /// <summary>The battery drains toward flat and stops there rather than going negative.</summary>
    /// <remarks>
    /// Floored, not wrapped: a battery that climbs while the aircraft flies is a reading an operator
    /// has to learn to disbelieve, and monotonicity is what a viewer can check from two frames.
    /// </remarks>
    [Fact]
    public void Battery_DrainsMonotonicallyAndFloorsAtZero()
    {
        AircraftKinematics kinematics =
            new(TestAircraft.Envelope(), TestAircraft.Projection());

        AircraftState state = TestAircraft.InitialState();
        TimeSpan step = TimeSpan.FromSeconds(10);

        //  Half again as long as the endurance, so the floor is genuinely reached.
        int steps = (int)(TestAircraft.Endurance * 1.5 / step.TotalSeconds);

        for (int i = 0; i < steps; i++)
        {
            AircraftState next = kinematics.Advance(
                state, new FlightCommand(state.HeadingDegrees, state.AltitudeMetersMsl), step);

            Assert.True(
                next.BatteryPercent <= state.BatteryPercent,
                "The battery rose between two consecutive states.");

            state = next;
        }

        Assert.Equal(0, state.BatteryPercent);
    }

    /// <summary>A zero or negative step is rejected rather than flying the aircraft backwards.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Advance_RejectsAStepThatIsNotPositive(int seconds)
    {
        AircraftKinematics kinematics =
            new(TestAircraft.Envelope(), TestAircraft.Projection());

        AircraftState state = TestAircraft.InitialState();

        Assert.Throws<ArgumentOutOfRangeException>(() => kinematics.Advance(
            state,
            new FlightCommand(state.HeadingDegrees, state.AltitudeMetersMsl),
            TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// Flies toward a commanded altitude far enough away that the limit binds on every step, and
    /// checks both the reported rate and the altitude it actually moved.
    /// </summary>
    /// <remarks>
    /// Both, because they can disagree: a model that reported the commanded rate while moving by
    /// the limited amount would pass one of these and put a climb rate on the wire that the
    /// altitudes beside it contradict.
    /// </remarks>
    private static void AssertAltitudeRateIsBounded(
        double commandedAltitude, double expectedRate)
    {
        AircraftKinematics kinematics =
            new(TestAircraft.Envelope(), TestAircraft.Projection());

        AircraftState state = TestAircraft.InitialState();
        TimeSpan step = TimeSpan.FromSeconds(0.05);

        for (int i = 0; i < 200; i++)
        {
            AircraftState next = kinematics.Advance(
                state, new FlightCommand(state.HeadingDegrees, commandedAltitude), step);

            double change = next.AltitudeMetersMsl - state.AltitudeMetersMsl;

            Assert.Equal(expectedRate * step.TotalSeconds, change, 9);
            Assert.Equal(expectedRate, next.ClimbRateMetersPerSecond, 9);

            state = next;
        }
    }
}
