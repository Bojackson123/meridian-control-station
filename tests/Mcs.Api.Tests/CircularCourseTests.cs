using Mcs.Api.FakeFeed;

namespace Mcs.Api.Tests;

/// <summary>
/// The properties a viewer of the map would check by eye, asserted arithmetically: the circuit
/// closes, it is round <i>in metres</i>, the nose points along the path, and the speed agrees with
/// the distance covered.
/// </summary>
/// <remarks>
/// Distances and bearings here are computed spherically, on purpose. <see cref="CircularCourse"/>
/// converts metres to degrees with a flat-earth constant; a test reusing that conversion would agree
/// with the implementation by construction and pass just as happily with the longitude scaling left
/// out -- which costs 21% on the east-west radius at this latitude.
/// </remarks>
public class CircularCourseTests
{
    private const double OriginLatitudeDegrees = 34.7304;
    private const double OriginLongitudeDegrees = -86.5861;
    private const double RadiusMeters = 400.0;
    private const double OrbitPeriodSeconds = 120.0;
    private const double EnduranceSeconds = 2_700.0;

    /// <summary>IUGG mean earth radius, used only by the haversine helper below.</summary>
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>
    /// One percent of the radius: wide enough for the flat-earth conversion's own error, far narrower
    /// than a missing longitude scaling.
    /// </summary>
    private const double RadiusToleranceMeters = RadiusMeters / 100.0;

    /// <summary>
    /// Half a percent of the ~21 m covered in a second. The systematic errors in play -- chord
    /// against arc, and the two earth radii -- come to about 0.12% together.
    /// </summary>
    private const double DistanceToleranceMeters = 0.1;

    private static readonly TimeSpan OrbitPeriod = TimeSpan.FromSeconds(OrbitPeriodSeconds);

    private static CircularCourse Course(
        double originLatitudeDegrees = OriginLatitudeDegrees,
        double originLongitudeDegrees = OriginLongitudeDegrees,
        double radiusMeters = RadiusMeters,
        double orbitPeriodSeconds = OrbitPeriodSeconds,
        double enduranceSeconds = EnduranceSeconds) =>
        new(originLatitudeDegrees,
            originLongitudeDegrees,
            radiusMeters,
            orbitPeriodSeconds,
            enduranceSeconds);

    [Fact]
    public void Position_AfterOneLap_ReturnsToTheStart()
    {
        CircularCourse course = Course();

        CourseState start = course.At(TimeSpan.Zero);
        CourseState lap = course.At(OrbitPeriod);

        Assert.Equal(start.LatitudeDegrees, lap.LatitudeDegrees, 1e-9);
        Assert.Equal(start.LongitudeDegrees, lap.LongitudeDegrees, 1e-9);
        Assert.Equal(start.HeadingDegrees, lap.HeadingDegrees, 1e-9);
    }

    [Fact]
    public void Position_StaysOneRadiusFromTheOrigin()
    {
        CircularCourse course = Course();

        //  Sixteen points, so the east-west extremes -- the only places a missing cos(latitude) shows
        //  up -- are sampled along with the north-south ones that would pass either way.
        for (int i = 0; i < 16; i++)
        {
            CourseState state = course.At(OrbitPeriod * (i / 16.0));

            double distance = DistanceMeters(
                OriginLatitudeDegrees, OriginLongitudeDegrees, state.LatitudeDegrees, state.LongitudeDegrees);

            Assert.Equal(RadiusMeters, distance, RadiusToleranceMeters);
        }
    }

    [Theory]
    [InlineData(0.0, 90.0)]    // due north of the origin, heading east
    [InlineData(0.25, 180.0)]  // due east of it, heading south
    [InlineData(0.5, 270.0)]
    [InlineData(0.75, 0.0)]
    public void Heading_IsTangentToTheCircuit_AtTheQuarterPoints(double lapFraction, double expectedHeading)
    {
        CourseState state = Course().At(OrbitPeriod * lapFraction);

        //  Pins the direction of travel too: flown anticlockwise, every one of these is 180 degrees
        //  out, which no round-trip or radius check would notice.
        Assert.Equal(expectedHeading, state.HeadingDegrees, 1e-9);
    }

    [Fact]
    public void Heading_MatchesTheBearingBetweenConsecutivePositions()
    {
        CircularCourse course = Course();
        TimeSpan step = OrbitPeriod / 360.0;

        for (int i = 0; i < 36; i++)
        {
            TimeSpan at = OrbitPeriod * (i / 36.0);

            CourseState from = course.At(at);
            CourseState to = course.At(at + step);
            CourseState midpoint = course.At(at + (step / 2));

            double travelled = BearingDegrees(
                from.LatitudeDegrees, from.LongitudeDegrees, to.LatitudeDegrees, to.LongitudeDegrees);

            Assert.Equal(travelled, midpoint.HeadingDegrees, 0.05);
        }
    }

    [Fact]
    public void GroundSpeed_IsTheCircumferenceOverTheOrbitPeriod()
    {
        CircularCourse course = Course();

        Assert.Equal(2 * Math.PI * RadiusMeters / OrbitPeriodSeconds, course.GroundSpeedMetersPerSecond, 1e-9);
        Assert.Equal(course.GroundSpeedMetersPerSecond, course.At(TimeSpan.Zero).GroundSpeedMetersPerSecond);
    }

    [Fact]
    public void GroundSpeed_MatchesTheDistanceActuallyTravelled()
    {
        CircularCourse course = Course();
        TimeSpan step = TimeSpan.FromSeconds(1);

        //  The coherence check a reviewer can make against two frames: speed x dt is the distance
        //  between them.
        for (int i = 0; i < 12; i++)
        {
            TimeSpan at = OrbitPeriod * (i / 12.0);

            CourseState from = course.At(at);
            CourseState to = course.At(at + step);

            double travelled = DistanceMeters(
                from.LatitudeDegrees, from.LongitudeDegrees, to.LatitudeDegrees, to.LongitudeDegrees);

            Assert.Equal(course.GroundSpeedMetersPerSecond * step.TotalSeconds, travelled, DistanceToleranceMeters);
        }
    }

    [Fact]
    public void Phase_SpacesVehiclesAroundTheSameCircuit()
    {
        CircularCourse course = Course();

        CourseState lead = course.At(TimeSpan.Zero);
        CourseState opposite = course.At(TimeSpan.Zero, phase: 0.5);

        double separation = DistanceMeters(
            lead.LatitudeDegrees, lead.LongitudeDegrees, opposite.LatitudeDegrees, opposite.LongitudeDegrees);

        //  Half a lap apart is the diameter, and the second vehicle is still on the circuit rather
        //  than merely far away.
        Assert.Equal(2 * RadiusMeters, separation, RadiusToleranceMeters);
        Assert.Equal(
            RadiusMeters,
            DistanceMeters(
                OriginLatitudeDegrees, OriginLongitudeDegrees, opposite.LatitudeDegrees, opposite.LongitudeDegrees),
            RadiusToleranceMeters);
    }

    [Fact]
    public void Phase_OfOneWholeLap_ChangesNothingButTheBattery()
    {
        CircularCourse course = Course();

        CourseState first = course.At(TimeSpan.Zero);
        CourseState wrapped = course.At(TimeSpan.Zero, phase: 1.0);

        Assert.Equal(first.LatitudeDegrees, wrapped.LatitudeDegrees, 1e-9);
        Assert.Equal(first.LongitudeDegrees, wrapped.LongitudeDegrees, 1e-9);
    }

    [Fact]
    public void Battery_DrainsMonotonicallyAndFloorsAtZero()
    {
        CircularCourse course = Course();

        Assert.Equal(100.0, course.At(TimeSpan.Zero).BatteryPercent, 1e-9);
        Assert.Equal(50.0, course.At(TimeSpan.FromSeconds(EnduranceSeconds / 2)).BatteryPercent, 1e-9);

        double previous = 100.0;
        for (int minute = 1; minute <= 60; minute++)
        {
            double battery = course.At(TimeSpan.FromMinutes(minute)).BatteryPercent;

            Assert.InRange(battery, 0.0, previous);
            previous = battery;
        }

        Assert.Equal(0.0, course.At(TimeSpan.FromSeconds(EnduranceSeconds * 10)).BatteryPercent);
    }

    [Fact]
    public void Longitude_StaysInRangeAcrossTheAntimeridian()
    {
        //  A 50 km circuit centred a hundredth of a degree from 180 crosses it twice per lap, and
        //  VehicleTelemetry.Create would throw on the 180.54 that follows from not wrapping.
        CircularCourse course = Course(originLongitudeDegrees: 179.99, radiusMeters: 50_000);

        for (int i = 0; i < 36; i++)
        {
            CourseState state = course.At(OrbitPeriod * (i / 36.0));

            Assert.InRange(state.LongitudeDegrees, -180.0, 180.0);
        }
    }

    [Fact]
    public void At_RejectsNegativeElapsedTime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Course().At(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(90.0, 0.0, 400.0, 120.0, 2_700.0)]     // origin latitude past the cap
    [InlineData(34.7, 181.0, 400.0, 120.0, 2_700.0)]   // origin longitude off the globe
    [InlineData(34.7, 0.0, 0.0, 120.0, 2_700.0)]       // a radius of zero is a stationary vehicle
    [InlineData(34.7, 0.0, 400.0, 0.0, 2_700.0)]       // a lap in no time
    [InlineData(34.7, 0.0, 400.0, 120.0, -1.0)]        // a battery that fills up
    [InlineData(double.NaN, 0.0, 400.0, 120.0, 2_700.0)]
    [InlineData(34.7, 0.0, double.PositiveInfinity, 120.0, 2_700.0)]
    public void Constructor_RejectsAnUnflyableCircuit(
        double latitude, double longitude, double radius, double period, double endurance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CircularCourse(latitude, longitude, radius, period, endurance));
    }

    /// <summary>Great-circle distance between two points, haversine.</summary>
    private static double DistanceMeters(
        double fromLatitudeDegrees,
        double fromLongitudeDegrees,
        double toLatitudeDegrees,
        double toLongitudeDegrees)
    {
        double fromLatitude = double.DegreesToRadians(fromLatitudeDegrees);
        double toLatitude = double.DegreesToRadians(toLatitudeDegrees);
        double deltaLatitude = toLatitude - fromLatitude;
        double deltaLongitude = double.DegreesToRadians(toLongitudeDegrees - fromLongitudeDegrees);

        double a = (Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2))
            + (Math.Cos(fromLatitude) * Math.Cos(toLatitude)
                * Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2));

        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>Initial great-circle bearing from one point to another, in [0, 360).</summary>
    private static double BearingDegrees(
        double fromLatitudeDegrees,
        double fromLongitudeDegrees,
        double toLatitudeDegrees,
        double toLongitudeDegrees)
    {
        double fromLatitude = double.DegreesToRadians(fromLatitudeDegrees);
        double toLatitude = double.DegreesToRadians(toLatitudeDegrees);
        double deltaLongitude = double.DegreesToRadians(toLongitudeDegrees - fromLongitudeDegrees);

        double y = Math.Sin(deltaLongitude) * Math.Cos(toLatitude);
        double x = (Math.Cos(fromLatitude) * Math.Sin(toLatitude))
            - (Math.Sin(fromLatitude) * Math.Cos(toLatitude) * Math.Cos(deltaLongitude));

        return (double.RadiansToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
    }
}
