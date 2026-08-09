using System.Globalization;

namespace Mcs.Api.FakeFeed;

/// <summary>
/// What a vehicle flying a <see cref="CircularCourse"/> reports at one instant, before it is stamped
/// and written to the store.
/// </summary>
public readonly record struct CourseState(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double HeadingDegrees,
    double GroundSpeedMetersPerSecond,
    double BatteryPercent);

/// <summary>
/// The closed circuit the fake feed flies: a horizontal circle about a fixed origin, together with
/// the heading, speed and battery that flying it implies.
/// </summary>
/// <remarks>
/// Pure and stateless -- it maps an elapsed duration to a state, holding no clock and no store, so
/// one instance serves every vehicle on every thread and the properties below are testable without a
/// running host.
/// <para>
/// Every field is derived from the same angle, so the picture coheres: the heading is tangent to the
/// path and <c>speed x dt</c> matches the distance between two frames. A closed circuit also means a
/// long run never leaves the viewport.
/// </para>
/// </remarks>
public sealed class CircularCourse
{
    /// <summary>
    /// Length of one degree of latitude, and of one degree of longitude at the equator. One constant
    /// for both is wrong by up to ~0.5% -- under two metres on a circuit of a few hundred metres.
    /// </summary>
    private const double MetersPerDegreeLatitude = 111_320.0;

    /// <summary>Beyond this the longitude scaling below divides by a cosine near zero.</summary>
    private const double MaxOriginLatitudeDegrees = 85.0;

    private const double DegreesPerTurn = 360.0;

    private const double FullBatteryPercent = 100.0;

    private readonly double _originLatitudeDegrees;
    private readonly double _originLongitudeDegrees;
    private readonly double _radiusMeters;
    private readonly double _orbitPeriodSeconds;
    private readonly double _enduranceSeconds;
    private readonly double _metersPerDegreeLongitude;

    /// <summary>
    /// Defines a circuit, validating it here rather than at the first tick.
    /// </summary>
    /// <param name="originLatitudeDegrees">Centre latitude, within +/-85 degrees.</param>
    /// <param name="originLongitudeDegrees">Centre longitude, within +/-180 degrees.</param>
    /// <param name="radiusMeters">Circuit radius. Finite and positive.</param>
    /// <param name="orbitPeriodSeconds">Seconds for one lap. Finite and positive.</param>
    /// <param name="enduranceSeconds">Seconds from a full battery to a flat one. Finite and positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is non-finite or out of range.</exception>
    public CircularCourse(
        double originLatitudeDegrees,
        double originLongitudeDegrees,
        double radiusMeters,
        double orbitPeriodSeconds,
        double enduranceSeconds)
    {
        if (!double.IsFinite(originLatitudeDegrees)
            || Math.Abs(originLatitudeDegrees) > MaxOriginLatitudeDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originLatitudeDegrees),
                originLatitudeDegrees,
                $"The origin latitude must be a finite value within +/-{MaxOriginLatitudeDegrees} "
                + "degrees; nearer the poles the longitude scaling degenerates.");
        }

        if (!double.IsFinite(originLongitudeDegrees) || Math.Abs(originLongitudeDegrees) > 180.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originLongitudeDegrees),
                originLongitudeDegrees,
                "The origin longitude must be a finite value between -180 and 180 degrees.");
        }

        //  Zero is rejected with the negatives: a radius of zero is a vehicle that never moves, which
        //  is the one symptom this feed exists to rule out.
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusMeters),
                radiusMeters,
                "The circuit radius must be a finite, positive number of metres.");
        }

        if (!double.IsFinite(orbitPeriodSeconds) || orbitPeriodSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orbitPeriodSeconds),
                orbitPeriodSeconds,
                "The orbit period must be a finite, positive number of seconds.");
        }

        if (!double.IsFinite(enduranceSeconds) || enduranceSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enduranceSeconds),
                enduranceSeconds,
                "The endurance must be a finite, positive number of seconds.");
        }

        _originLatitudeDegrees = originLatitudeDegrees;
        _originLongitudeDegrees = originLongitudeDegrees;
        _radiusMeters = radiusMeters;
        _orbitPeriodSeconds = orbitPeriodSeconds;
        _enduranceSeconds = enduranceSeconds;

        //  A degree of longitude is a degree of latitude times cos(latitude) -- 0.82x at 34.7N. Skip
        //  it and equal degree offsets draw an ellipse a fifth too wide.
        _metersPerDegreeLongitude =
            MetersPerDegreeLatitude * Math.Cos(double.DegreesToRadians(originLatitudeDegrees));

        GroundSpeedMetersPerSecond = 2 * Math.PI * radiusMeters / orbitPeriodSeconds;
    }

    /// <summary>
    /// Gets the speed a vehicle holds all the way round: circumference over orbit period, so it
    /// agrees with the positions rather than being asserted alongside them.
    /// </summary>
    public double GroundSpeedMetersPerSecond { get; }

    /// <summary>Describes the circuit in one clause, for the feed's startup log line.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"a {_radiusMeters:0.##} m circuit about {_originLatitudeDegrees:0.#######}, "
            + $"{_originLongitudeDegrees:0.#######}, one lap in {_orbitPeriodSeconds:0.##} s at "
            + $"{GroundSpeedMetersPerSecond:0.##} m/s");

    /// <summary>
    /// Gets the state of a vehicle <paramref name="elapsed"/> into the run.
    /// </summary>
    /// <param name="elapsed">
    /// Time since the feed started. Negative means two clock readings were subtracted the wrong way
    /// round, and flying the circuit backwards is not a useful way to find that out.
    /// </param>
    /// <param name="phase">
    /// Where this vehicle sits on the circuit relative to the others, as a fraction of a lap. Only
    /// the fractional part matters.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed"/> is negative, or <paramref name="phase"/> is not finite.</exception>
    public CourseState At(TimeSpan elapsed, double phase = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (!double.IsFinite(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "The phase offset must be finite.");
        }

        double seconds = elapsed.TotalSeconds;

        //  A bearing from the centre -- 0 is due north of it, increasing clockwise -- so north takes
        //  the cosine and east the sine, the transpose of the usual convention. Getting it the other
        //  way round flies the circuit anticlockwise with a heading 180 degrees out, and neither
        //  error is visible on its own.
        double bearingRadians = 2 * Math.PI * ((seconds / _orbitPeriodSeconds) + phase);

        double northMeters = _radiusMeters * Math.Cos(bearingRadians);
        double eastMeters = _radiusMeters * Math.Sin(bearingRadians);

        double latitude = _originLatitudeDegrees + (northMeters / MetersPerDegreeLatitude);
        double longitude = _originLongitudeDegrees + (eastMeters / _metersPerDegreeLongitude);

        //  Travelling clockwise, the tangent is a quarter turn right of the bearing from the centre.
        double heading = double.RadiansToDegrees(bearingRadians) + 90.0;

        return new CourseState(
            latitude,
            NormaliseLongitude(longitude),
            NormaliseDegrees(heading),
            GroundSpeedMetersPerSecond,
            BatteryAt(seconds));
    }

    /// <summary>
    /// A linear drain from full to flat over the endurance, clamped at zero.
    /// </summary>
    /// <remarks>
    /// Floored rather than wrapped: a battery that climbs while the vehicle flies is a value the
    /// operator has to learn to disbelieve. Monotonicity is what a viewer can check against two
    /// frames.
    /// </remarks>
    private double BatteryAt(double seconds) =>
        Math.Max(0, FullBatteryPercent * (1.0 - (seconds / _enduranceSeconds)));

    /// <summary>Brings a longitude into [-180, 180), so an origin near the antimeridian still works.</summary>
    private static double NormaliseLongitude(double degrees) =>
        NormaliseDegrees(degrees + 180.0) - 180.0;

    /// <summary>Brings any finite angle into [0, 360).</summary>
    /// <remarks>
    /// Guarded, because folding a value already in range perturbs its low bits: a heading at the top
    /// of the circuit should read as 90, not 90.00000000000001.
    /// </remarks>
    private static double NormaliseDegrees(double degrees) =>
        degrees is > 0 and < DegreesPerTurn
            ? degrees
            : ((degrees % DegreesPerTurn) + DegreesPerTurn) % DegreesPerTurn;
}
