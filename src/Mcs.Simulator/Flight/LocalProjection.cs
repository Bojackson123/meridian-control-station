namespace Mcs.Simulator.Flight;

/// <summary>
/// Converts between metres and degrees about a fixed origin latitude: a flat-earth approximation,
/// which is all a simulator flying a few kilometres of circuit needs.
/// </summary>
/// <remarks>
/// <b>The scaling is fixed at construction, from the origin's latitude, and not recomputed as the
/// aircraft moves.</b> That is the approximation: a degree of longitude shrinks with latitude, so a
/// projection anchored at one point drifts as you fly away from it. Over the few kilometres a
/// circuit covers the error is centimetres; over a hundred it would be metres, and this type would
/// be the wrong tool rather than a badly tuned one. The rejected alternative was a proper geodesic
/// solver, which is a dependency and a page of arithmetic bought to make an aircraft that does not
/// exist land in a slightly better imaginary place.
/// <para>
/// What must not be dropped is the cosine itself. A degree of longitude is a degree of latitude
/// times <c>cos(latitude)</c>, which is 0.82x at Huntsville; treat them as equal and a square
/// circuit is drawn a fifth too wide, which reads as a plausible route rather than as a bug.
/// </para>
/// <para>
/// Pure and immutable, so one instance serves the kinematics and the follower on any thread.
/// </para>
/// </remarks>
internal sealed class LocalProjection
{
    /// <summary>
    /// Length of one degree of latitude, and of one degree of longitude at the equator. One
    /// constant for both is wrong by up to ~0.5%, which is under two metres on a circuit of a few
    /// hundred metres.
    /// </summary>
    private const double MetersPerDegreeLatitude = 111_320.0;

    /// <summary>Beyond this the longitude scaling below divides by a cosine near zero.</summary>
    private const double MaxOriginLatitudeDegrees = 85.0;

    private const double DegreesPerTurn = 360.0;

    private readonly double _metersPerDegreeLongitude;

    /// <summary>
    /// Anchors the projection at a latitude, validating it here rather than at the first step.
    /// </summary>
    /// <param name="originLatitudeDegrees">
    /// The latitude the longitude scaling is computed at. In practice the route's first waypoint.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="originLatitudeDegrees"/> is not finite, or is nearer a pole than the
    /// scaling can survive.
    /// </exception>
    internal LocalProjection(double originLatitudeDegrees)
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

        _metersPerDegreeLongitude =
            MetersPerDegreeLatitude * Math.Cos(double.DegreesToRadians(originLatitudeDegrees));
    }

    /// <summary>
    /// Moves a position by a north/east displacement in metres.
    /// </summary>
    /// <returns>The new latitude and longitude, the longitude normalised into [-180, 180).</returns>
    internal (double LatitudeDegrees, double LongitudeDegrees) Offset(
        double latitudeDegrees, double longitudeDegrees, double northMeters, double eastMeters)
    {
        double latitude = latitudeDegrees + (northMeters / MetersPerDegreeLatitude);
        double longitude = longitudeDegrees + (eastMeters / _metersPerDegreeLongitude);

        return (latitude, NormaliseLongitude(longitude));
    }

    /// <summary>Returns the ground distance between two positions in metres, ignoring altitude.</summary>
    /// <remarks>
    /// Ground distance, deliberately: the waypoint capture test is a horizontal question, and an
    /// aircraft that is over its waypoint but two hundred metres below it has arrived. Folding
    /// altitude in would make capture depend on how far the climb had got, which is how an aircraft
    /// ends up orbiting a waypoint it is flying directly at.
    /// </remarks>
    internal double GroundDistanceMeters(
        double fromLatitudeDegrees,
        double fromLongitudeDegrees,
        double toLatitudeDegrees,
        double toLongitudeDegrees)
    {
        (double north, double east) = Displacement(
            fromLatitudeDegrees, fromLongitudeDegrees, toLatitudeDegrees, toLongitudeDegrees);

        return double.Hypot(north, east);
    }

    /// <summary>
    /// Returns the bearing from one position to another, in degrees clockwise from north.
    /// </summary>
    /// <remarks>
    /// <c>Atan2(east, north)</c>, which is the transpose of the usual <c>Atan2(y, x)</c>: a compass
    /// bearing is measured from north and increases toward east, where the mathematical convention
    /// measures from east and increases toward north. Getting it the other way round produces a
    /// heading mirrored about the north-east diagonal, and the aircraft flies a route that looks
    /// deliberate and is not the one configured.
    /// </remarks>
    internal double BearingDegrees(
        double fromLatitudeDegrees,
        double fromLongitudeDegrees,
        double toLatitudeDegrees,
        double toLongitudeDegrees)
    {
        (double north, double east) = Displacement(
            fromLatitudeDegrees, fromLongitudeDegrees, toLatitudeDegrees, toLongitudeDegrees);

        return NormaliseDegrees(double.RadiansToDegrees(Math.Atan2(east, north)));
    }

    /// <summary>Brings any finite angle into [0, 360).</summary>
    /// <remarks>
    /// Guarded, because folding a value already in range perturbs its low bits: a due-east bearing
    /// should read as 90, not 90.00000000000001.
    /// </remarks>
    internal static double NormaliseDegrees(double degrees) =>
        degrees is > 0 and < DegreesPerTurn
            ? degrees
            : ((degrees % DegreesPerTurn) + DegreesPerTurn) % DegreesPerTurn;

    /// <summary>
    /// Returns the signed difference <c>to - from</c> brought into (-180, 180], so that steering
    /// toward it takes the short way round the compass.
    /// </summary>
    /// <remarks>
    /// The whole reason this exists: a raw subtraction makes a turn from 350 degrees to 010 look
    /// like -340, and an aircraft limited to a few degrees per second then spends a minute going
    /// the long way round to reach a heading twenty degrees away. The symptom is a plausible turn
    /// in the wrong direction, which is the hardest kind to notice on a map.
    /// </remarks>
    internal static double SignedDifferenceDegrees(double fromDegrees, double toDegrees)
    {
        double difference = NormaliseDegrees(toDegrees - fromDegrees);

        return difference > DegreesPerTurn / 2 ? difference - DegreesPerTurn : difference;
    }

    /// <summary>Returns the north/east displacement in metres between two positions.</summary>
    private (double North, double East) Displacement(
        double fromLatitudeDegrees,
        double fromLongitudeDegrees,
        double toLatitudeDegrees,
        double toLongitudeDegrees)
    {
        double north = (toLatitudeDegrees - fromLatitudeDegrees) * MetersPerDegreeLatitude;

        //  Through the same normalisation the offset applies, so a route straddling the
        //  antimeridian measures the short way across it rather than most of the way round.
        double east =
            SignedDifferenceDegrees(fromLongitudeDegrees, toLongitudeDegrees)
            * _metersPerDegreeLongitude;

        return (north, east);
    }

    /// <summary>Brings a longitude into [-180, 180), so an origin near the antimeridian still works.</summary>
    private static double NormaliseLongitude(double degrees) =>
        NormaliseDegrees(degrees + 180.0) - 180.0;
}
