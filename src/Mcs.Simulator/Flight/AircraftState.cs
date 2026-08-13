using System.Globalization;

namespace Mcs.Simulator.Flight;

/// <summary>
/// Where the simulated aircraft is and what it is doing, at one instant.
/// </summary>
/// <remarks>
/// The whole of the simulation's state. It is a value rather than an object with methods because
/// <see cref="AircraftKinematics"/> maps one of these to the next and nothing else mutates it,
/// which is what lets the turn tests integrate a flight path with no clock, no socket and no host.
/// <para>
/// <b>These are the aircraft's own facts, not a telemetry report.</b> Nothing here is nullable and
/// nothing here is absent: a vehicle knows its own heading. What the station may not know is a
/// different question, decided by which messages actually arrive.
/// </para>
/// <para>
/// <see cref="ClimbRateMetersPerSecond"/> is an output rather than an input: it is what the last
/// step <i>achieved</i> after the envelope's limit was applied, which is what a vehicle would
/// report and what makes the climb-limit test assertable from the state alone.
/// </para>
/// </remarks>
/// <param name="LatitudeDegrees">WGS-84 latitude.</param>
/// <param name="LongitudeDegrees">WGS-84 longitude.</param>
/// <param name="AltitudeMetersMsl">
/// Altitude above mean sea level. MSL rather than above the ground: the reference is stated here so
/// that nothing downstream has to infer it, and this simulator models no terrain to be above.
/// </param>
/// <param name="HeadingDegrees">Where the nose points, clockwise from true north, in [0, 360).</param>
/// <param name="GroundSpeedMetersPerSecond">Speed over the ground.</param>
/// <param name="ClimbRateMetersPerSecond">Rate of altitude gain; negative is a descent.</param>
/// <param name="BatteryPercent">Remaining charge, from 100 down to 0.</param>
internal readonly record struct AircraftState(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeMetersMsl,
    double HeadingDegrees,
    double GroundSpeedMetersPerSecond,
    double ClimbRateMetersPerSecond,
    double BatteryPercent)
{
    /// <summary>Describes the state in one clause, for the periodic log line.</summary>
    /// <remarks>Invariant, so a container in another locale logs a decimal point.</remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{LatitudeDegrees:0.000000}, {LongitudeDegrees:0.000000} at "
            + $"{AltitudeMetersMsl:0.#} m MSL, heading {HeadingDegrees:0.#}, "
            + $"{GroundSpeedMetersPerSecond:0.#} m/s, climb {ClimbRateMetersPerSecond:0.##} m/s, "
            + $"battery {BatteryPercent:0.#}%");
}
