using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Tests;

/// <summary>
/// The follower's obligations: it gets round the route, it keeps going round, and it refuses a
/// route it cannot fly rather than flying part of one.
/// </summary>
public sealed class WaypointFollowerTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// The route loops, and the aircraft's position is continuous across the seam.
    /// </summary>
    /// <remarks>
    /// <b>Two separate claims, and the second is the one worth the machinery.</b> That the index
    /// wraps is easy; that nothing else is reset when it does is what makes the second lap a
    /// continuation rather than a restart. A follower that reinitialised the aircraft at the first
    /// waypoint each lap would satisfy every other test here and would show on the console as a
    /// vehicle that jumps across the map once a circuit -- which is HAZ-01 exactly: a confident
    /// picture that is not what happened.
    /// <para>
    /// The step-length bound is the detector. One tick moves the aircraft <c>v x dt</c> and nothing
    /// in the model can move it further, so any larger gap between consecutive samples is a
    /// teleport.
    /// </para>
    /// </remarks>
    [Fact]
    public void Route_LoopsWithoutTeleportingAtTheSeam()
    {
        IReadOnlyList<Waypoint> route = TestAircraft.Route();
        AircraftEnvelope envelope = TestAircraft.Envelope();
        LocalProjection projection = TestAircraft.Projection();

        WaypointFollower follower = new(
            route, envelope.TurnRadiusMeters * 1.5, envelope, projection);

        AircraftKinematics kinematics = new(envelope, projection);
        AircraftState state = TestAircraft.InitialState();

        double longestStepMeters = 0;
        List<int> captured = [];
        int previousIndex = follower.ActiveIndex;

        //  Long enough for two laps of a 3.2 km circuit at 22 m/s, with room to spare.
        for (int i = 0; i < 4_000; i++)
        {
            FlightCommand command = follower.Update(state);

            if (follower.ActiveIndex != previousIndex)
            {
                captured.Add(previousIndex);
                previousIndex = follower.ActiveIndex;
            }

            AircraftState next = kinematics.Advance(state, command, Step);

            longestStepMeters = Math.Max(
                longestStepMeters,
                projection.GroundDistanceMeters(
                    state.LatitudeDegrees,
                    state.LongitudeDegrees,
                    next.LatitudeDegrees,
                    next.LongitudeDegrees));

            state = next;
        }

        Assert.True(
            follower.LapCount >= 2,
            $"Expected at least two laps; the follower completed {follower.LapCount}.");

        //  The waypoints were visited in order, over and over, with none skipped: the capture
        //  sequence is the route's indexes repeating.
        Assert.Equal(
            [.. Enumerable.Range(0, captured.Count).Select(i => i % route.Count)],
            captured);

        double oneStep = envelope.CruiseSpeedMetersPerSecond * Step.TotalSeconds;

        Assert.True(
            longestStepMeters <= oneStep * 1.000001,
            $"A step of {longestStepMeters:0.###} m exceeds the {oneStep:0.###} m one tick of "
            + "flight can cover, so the aircraft moved without flying there.");
    }

    /// <summary>
    /// A capture radius smaller than the turn radius is refused, naming what it has to beat.
    /// </summary>
    /// <remarks>
    /// The failure it prevents is a livelock that looks like a feature: the aircraft settles into a
    /// tidy orbit around a waypoint whose capture radius it can never enter, and an operator sees a
    /// loiter. Rejected rather than clamped, because a clamped value would fly a route nobody
    /// configured -- the same reasoning as a clamped battery reading.
    /// </remarks>
    [Fact]
    public void CaptureRadius_BelowTheTurnRadiusIsRejected()
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaypointFollower(
                TestAircraft.Route(),
                envelope.TurnRadiusMeters * 0.99,
                envelope,
                TestAircraft.Projection()));

        Assert.Contains("turn radius", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capture radius large enough to swallow the route's legs is refused, naming the length a
    /// leg would have to beat.
    /// </summary>
    /// <remarks>
    /// The other end of the capture radius, and the failure is louder than the orbit at the small
    /// end while looking quieter in the log: with the radius past half the shortest leg, reaching
    /// one waypoint puts the aircraft inside the next one's radius, so the index advances on every
    /// step of the simulation and the aircraft flies a heading that changes several times a second
    /// around a route it never tracks. Nothing counts it -- the frames go out at their configured
    /// rates carrying a position that is real -- so the only place it can be caught is here.
    /// <para>
    /// 500 m against the shipped 800 m circuit, which clears the turn radius comfortably. That
    /// matters: a value failing both checks would pass this test while proving nothing about this
    /// one.
    /// </para>
    /// </remarks>
    [Fact]
    public void CaptureRadius_PastHalfTheShortestLegIsRejected()
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();

        const double CaptureRadiusMeters = 500.0;

        Assert.True(CaptureRadiusMeters > envelope.TurnRadiusMeters);

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaypointFollower(
                TestAircraft.Route(),
                CaptureRadiusMeters,
                envelope,
                TestAircraft.Projection()));

        Assert.Contains("shortest", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A route of fewer than two waypoints has nothing to fly between.</summary>
    [Fact]
    public void Route_WithFewerThanTwoWaypointsIsRejected()
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();

        Assert.Throws<ArgumentException>(() => new WaypointFollower(
            [TestAircraft.Route()[0]],
            envelope.TurnRadiusMeters * 1.5,
            envelope,
            TestAircraft.Projection()));
    }

    /// <summary>
    /// Two waypoints at the same place are refused, because the leg between them has no bearing.
    /// </summary>
    /// <remarks>
    /// <c>Atan2(0, 0)</c> is zero, so the commanded heading would be due north rather than an
    /// error, and the aircraft would fly north until it happened to capture the point it was
    /// standing on.
    /// </remarks>
    [Fact]
    public void Route_WithADegenerateLegIsRejected()
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();
        Waypoint first = TestAircraft.Route()[0];

        Assert.Throws<ArgumentException>(() => new WaypointFollower(
            [first, first, TestAircraft.Route()[2]],
            envelope.TurnRadiusMeters * 1.5,
            envelope,
            TestAircraft.Projection()));
    }

    /// <summary>The shortest leg is measured round the closed route, not just along it.</summary>
    /// <remarks>
    /// The leg from the last waypoint back to the first is flown every lap. Leaving it out of the
    /// measurement would let a route with a long outbound path and a two-metre return report its
    /// shape as fine, and the host's tight-route warning would never fire on the one leg that
    /// needed it.
    /// </remarks>
    [Fact]
    public void ShortestLeg_IncludesTheClosingLeg()
    {
        AircraftEnvelope envelope = TestAircraft.Envelope();
        LocalProjection projection = TestAircraft.Projection();
        IReadOnlyList<Waypoint> route = TestAircraft.Route();

        //  Three of the square's corners, with the third moved in along the western side so the
        //  closing leg is the shortest of the three. Short enough to be measurably not a side of
        //  the square, and still longer than the capture radius is allowed to swallow -- the
        //  follower refuses a route below that, and a fixture that tripped it would be asserting
        //  about the wrong rejection.
        Waypoint nearFirst = new(
            route[0].LatitudeDegrees + 0.004,
            route[0].LongitudeDegrees,
            route[0].AltitudeMetersMsl);

        WaypointFollower follower = new(
            [route[0], route[1], nearFirst],
            envelope.TurnRadiusMeters * 1.5,
            envelope,
            projection);

        double closingLeg = projection.GroundDistanceMeters(
            nearFirst.LatitudeDegrees,
            nearFirst.LongitudeDegrees,
            route[0].LatitudeDegrees,
            route[0].LongitudeDegrees);

        Assert.Equal(closingLeg, follower.ShortestLegMeters, 6);
    }
}
