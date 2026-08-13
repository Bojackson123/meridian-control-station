using System.Globalization;

namespace Mcs.Simulator.Flight;

/// <summary>A point the route passes through: a position and the altitude to hold at it.</summary>
/// <remarks>
/// Validated at construction rather than at the first step, so a route with a longitude of 200 in
/// it fails the host with the offending number named instead of flying somewhere nobody meant.
/// <para>
/// There is no speed, no loiter time and no action. Those are mission-plan concepts, this is a
/// simulator's flight path, and inventing the fields a mission format has before there is anything
/// to upload one from would be guessing at M2's shape.
/// </para>
/// </remarks>
internal readonly record struct Waypoint
{
    /// <summary>Beyond this the projection's longitude scaling degenerates.</summary>
    private const double MaxLatitudeDegrees = 85.0;

    /// <summary>Builds a waypoint, rejecting one that is not a place.</summary>
    /// <param name="latitudeDegrees">WGS-84 latitude, within +/-85 degrees.</param>
    /// <param name="longitudeDegrees">WGS-84 longitude, within +/-180 degrees.</param>
    /// <param name="altitudeMetersMsl">Altitude above mean sea level, in metres.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is non-finite or out of range.</exception>
    internal Waypoint(double latitudeDegrees, double longitudeDegrees, double altitudeMetersMsl)
    {
        if (!double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > MaxLatitudeDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitudeDegrees),
                latitudeDegrees,
                $"A waypoint's latitude must be a finite value within +/-{MaxLatitudeDegrees} "
                + "degrees; nearer the poles the flat-earth projection degenerates.");
        }

        if (!double.IsFinite(longitudeDegrees) || Math.Abs(longitudeDegrees) > 180.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitudeDegrees),
                longitudeDegrees,
                "A waypoint's longitude must be a finite value between -180 and 180 degrees.");
        }

        //  The same bounds Mcs.Core's Altitude accepts. Negative is legal -- the Dead Sea is below
        //  sea level and MSL is what this number is measured against.
        if (!double.IsFinite(altitudeMetersMsl) || altitudeMetersMsl is < -500.0 or > 20_000.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(altitudeMetersMsl),
                altitudeMetersMsl,
                "A waypoint's altitude must be a finite value between -500 and 20000 metres above "
                + "mean sea level.");
        }

        LatitudeDegrees = latitudeDegrees;
        LongitudeDegrees = longitudeDegrees;
        AltitudeMetersMsl = altitudeMetersMsl;
    }

    /// <summary>Gets the WGS-84 latitude.</summary>
    internal double LatitudeDegrees { get; }

    /// <summary>Gets the WGS-84 longitude.</summary>
    internal double LongitudeDegrees { get; }

    /// <summary>Gets the altitude to hold at this point, in metres above mean sea level.</summary>
    internal double AltitudeMetersMsl { get; }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{LatitudeDegrees:0.000000}, {LongitudeDegrees:0.000000} at {AltitudeMetersMsl:0.#} m MSL");
}
