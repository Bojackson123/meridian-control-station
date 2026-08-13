using Mcs.Simulator;
using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Tests;

/// <summary>
/// The aircraft, route and settings the tests fly, in one place.
/// </summary>
/// <remarks>
/// Deliberately the shipped defaults rather than round numbers invented for the tests. A turn
/// radius asserted against a closed-form answer is true for any envelope; asserting it for the
/// envelope that actually flies in the container is what makes it evidence about this simulator
/// rather than about the formula.
/// </remarks>
internal static class TestAircraft
{
    /// <summary>The default cruise speed, in metres per second.</summary>
    internal const double CruiseSpeed = 22.0;

    /// <summary>The default bank limit, in degrees.</summary>
    internal const double MaxBank = 25.0;

    /// <summary>The default climb limit, in metres per second.</summary>
    internal const double MaxClimb = 3.0;

    /// <summary>The default descent limit, in metres per second.</summary>
    internal const double MaxDescent = 5.0;

    /// <summary>The default endurance, in seconds.</summary>
    internal const double Endurance = 2_700.0;

    /// <summary>Builds an envelope, optionally at a different speed or bank.</summary>
    internal static AircraftEnvelope Envelope(
        double cruiseSpeed = CruiseSpeed, double maxBank = MaxBank) =>
        new(cruiseSpeed, maxBank, MaxClimb, MaxDescent, Endurance);

    /// <summary>
    /// The shipped circuit: an 800 m square about Huntsville, climbing 40 m on its northern side.
    /// </summary>
    internal static IReadOnlyList<Waypoint> Route() =>
    [
        new Waypoint(34.733993, -86.590472, 300.0),
        new Waypoint(34.733993, -86.581728, 340.0),
        new Waypoint(34.726807, -86.581728, 340.0),
        new Waypoint(34.726807, -86.590472, 300.0),
    ];

    /// <summary>The same circuit in the shape configuration binds.</summary>
    internal static IList<WaypointOptions> RouteOptions() =>
    [
        .. Route().Select(waypoint => new WaypointOptions
        {
            LatitudeDegrees = waypoint.LatitudeDegrees,
            LongitudeDegrees = waypoint.LongitudeDegrees,
            AltitudeMetersMsl = waypoint.AltitudeMetersMsl,
        }),
    ];

    /// <summary>A projection anchored where the route starts, as the host anchors it.</summary>
    internal static LocalProjection Projection() => new(Route()[0].LatitudeDegrees);

    /// <summary>Options carrying the shipped defaults, for the validation tests to spoil.</summary>
    internal static SimulatorOptions Options() => new() { Route = RouteOptions() };

    /// <summary>
    /// The state the host starts from: at the first waypoint, pointed at the second, at cruise.
    /// </summary>
    internal static AircraftState InitialState()
    {
        IReadOnlyList<Waypoint> route = Route();
        LocalProjection projection = Projection();

        return new AircraftState(
            route[0].LatitudeDegrees,
            route[0].LongitudeDegrees,
            route[0].AltitudeMetersMsl,
            projection.BearingDegrees(
                route[0].LatitudeDegrees,
                route[0].LongitudeDegrees,
                route[1].LatitudeDegrees,
                route[1].LongitudeDegrees),
            CruiseSpeed,
            ClimbRateMetersPerSecond: 0,
            BatteryPercent: 100);
    }
}
