using System.Reflection;

namespace Mcs.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="VehicleTelemetry"/> and <see cref="LinkStatus"/>.
/// </summary>
/// <remarks>
/// This type is the untrusted half of a telemetry report -- every field on it is a claim by a
/// vehicle -- and <see cref="VehicleTelemetry.Create"/> is the only door in. The cases below are
/// organised the way the factory is: the two struct arguments that the language will not let
/// refuse <c>default</c>, then each numeric range in turn, then heading normalisation (the one
/// input that is corrected rather than rejected), then the formatting and structural properties
/// the rest of the station leans on.
/// <para>
/// The recurring theme, asserted rather than assumed, is that <b>every check rejects and none
/// clamps</b>. A clamped 200% battery renders as a believable 100% and the operator never learns
/// the adapter is broken -- HAZ-01 arriving by a different road.
/// </para>
/// <para>
/// The absence of a receipt timestamp on this type is verified too. It is what stops an adapter,
/// which can produce nothing else, from stamping a frame with a time of its choosing (MCS-005).
/// </para>
/// </remarks>
public class VehicleTelemetryTests
{
    // --- Create: the two struct arguments that cannot refuse `default` -----------------------

    [Fact]
    public void Create_DefaultId_ThrowsArgumentExceptionNamingId()
    {
        // Assert.Throws matches the exact type, so this pins the thing the source comment
        // defends: the caller gets an ArgumentException naming the parameter they passed, not an
        // InvalidOperationException thrown from inside a property they never touched.
        ArgumentException ex =
            Assert.Throws<ArgumentException>(() => TelemetrySamples.Telemetry(id: default(VehicleId)));

        Assert.Equal("id", ex.ParamName);
        Assert.Contains("VehicleId.From", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Verifies("MCS-004")]
    public void Create_DefaultAltitude_ThrowsArgumentExceptionNamingAltitudeAndMcs004()
    {
        // The requirement stated directly: a frame with an unset altitude reference is rejected.
        // The Altitude type makes an omitted reference unrepresentable at construction; this closes
        // the one remaining route, `default(Altitude)`, which no factory can intercept.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => TelemetrySamples.Telemetry(altitude: default(Altitude)));

        Assert.Equal("altitude", ex.ParamName);
        Assert.Contains("MCS-004", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_DefaultIdAndOtherwiseInvalidArguments_StillReportsTheId()
    {
        // The `default` checks run first, so a report built from an uninitialised id is named as
        // such rather than as whichever range check happened to be written first.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => TelemetrySamples.Telemetry(id: default(VehicleId), latitudeDegrees: 95.0));

        Assert.Equal("id", ex.ParamName);
    }

    // --- Create: latitude ---------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(51.5074)]
    [InlineData(-33.8688)]
    [InlineData(90)]            // the poles are valid positions
    [InlineData(-90)]
    public void Create_LatitudeInRange_RoundTripsExactly(double latitude) =>
        Assert.Equal(latitude, TelemetrySamples.Telemetry(latitudeDegrees: latitude).LatitudeDegrees);

    [Theory]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(180)]
    [InlineData(515074000)]     // MAVLink's int32 degrees x 1e7, unscaled
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Verifies("MCS-012")]
    public void Create_LatitudeOutOfRange_ThrowsArgumentOutOfRangeNamingLatitude(double latitude)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(latitudeDegrees: latitude));

        Assert.Equal("latitudeDegrees", ex.ParamName);

        // Rejected, not clamped to 90: an adapter that forgot MAVLink's 1e7 scaling must fail
        // loudly here rather than produce a track pinned to the north pole.
        Assert.Equal(latitude, Assert.IsType<double>(ex.ActualValue));
    }

    // --- Create: longitude --------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1278)]
    [InlineData(180)]           // both antimeridian representations are accepted
    [InlineData(-180)]
    public void Create_LongitudeInRange_RoundTripsExactly(double longitude) =>
        Assert.Equal(
            longitude, TelemetrySamples.Telemetry(longitudeDegrees: longitude).LongitudeDegrees);

    [Fact]
    public void Create_BothAntimeridianRepresentations_AreAccepted()
    {
        // -180 and +180 name the same meridian. Rejecting one would fail a legitimate position
        // report over a difference of representation, so the bound is inclusive at both ends.
        Assert.Equal(180, TelemetrySamples.Telemetry(longitudeDegrees: 180).LongitudeDegrees);
        Assert.Equal(-180, TelemetrySamples.Telemetry(longitudeDegrees: -180).LongitudeDegrees);
    }

    [Theory]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    [InlineData(360)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Verifies("MCS-012")]
    public void Create_LongitudeOutOfRange_ThrowsArgumentOutOfRangeNamingLongitude(double longitude)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(longitudeDegrees: longitude));

        Assert.Equal("longitudeDegrees", ex.ParamName);
    }

    // --- Create: ground speed -----------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(14.2)]
    [InlineData(300)]
    [InlineData(1e6)]           // deliberately absurd: there is no upper bound to trip over
    [InlineData(double.MaxValue)]
    public void Create_GroundSpeedFiniteAndNonNegative_RoundTripsExactly(double speed) =>
        Assert.Equal(
            speed,
            TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: speed)
                .GroundSpeedMetersPerSecond);

    [Fact]
    [Verifies("MCS-012")]
    public void Create_ImplausiblyFastVehicle_IsAccepted()
    {
        // Pins the documented decision not to invent a ceiling. There is no defensible speed
        // limit in the requirements, and a guessed one would reject a legitimate report from
        // whatever airframe is added later. Plausibility belongs to the adapter that knows the
        // vehicle.
        Assert.Equal(1000, TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: 1000)
            .GroundSpeedMetersPerSecond);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-14.2)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Verifies("MCS-012")]
    public void Create_GroundSpeedNegativeOrNonFinite_ThrowsArgumentOutOfRange(double speed)
    {
        // Negative and non-finite are wrong regardless of airframe -- speed over the ground is a
        // magnitude, and direction is carried by HeadingDegrees.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: speed));

        Assert.Equal("groundSpeedMetersPerSecond", ex.ParamName);
    }

    [Fact]
    public void Create_NegativeZeroGroundSpeed_IsAcceptedAsZero()
    {
        // Characterisation, not a designed behaviour: `-0.0 < 0` is false in IEEE arithmetic, so
        // a negative zero arriving from an adapter's own subtraction passes the guard and stores
        // as zero. Recorded so a future change to the comparison is visible rather than
        // surprising.
        Assert.Equal(
            0.0,
            TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: -0.0)
                .GroundSpeedMetersPerSecond);
    }

    // --- Create: heading is normalised, not rejected -------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12.5, 12.5)]
    [InlineData(359.5, 359.5)]
    [InlineData(360, 0)]
    [InlineData(372.5, 12.5)]   // the XML example's own case
    [InlineData(720, 0)]
    [InlineData(1080.25, 0.25)]
    [InlineData(-1, 359)]
    [InlineData(-0.5, 359.5)]
    [InlineData(-360, 0)]
    [InlineData(-720.5, 359.5)]
    public void Create_Heading_IsNormalisedIntoZeroToThreeSixty(double raw, double expected)
    {
        // Normalised rather than rejected, unlike latitude: 361 and -1 are ordinary outputs of an
        // adapter's own arithmetic and mean something unambiguous, whereas a latitude of 95 does
        // not. Every value here is exact in binary, so the assertion needs no tolerance.
        Assert.Equal(expected, TelemetrySamples.Telemetry(headingDegrees: raw).HeadingDegrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(12.5)]
    [InlineData(87.3)]
    [InlineData(123.456)]
    [InlineData(180)]
    [InlineData(359.999)]
    [InlineData(359.99999999999994)]    // the last double below 360
    public void Create_HeadingAlreadyInRange_RoundTripsExactly(double heading)
    {
        // Normalisation must not perturb a value that needed none. Folding unconditionally would:
        // `% 360` is a no-op here, but the `+ 360` that follows rounds to the coarser spacing
        // available near 447 and the second remainder cannot undo it, so 87.3 comes back as
        // 87.30000000000001 and reaches the browser that way -- JSON writes the shortest
        // round-trippable form, which is the long one. Every other numeric field on this type has
        // a RoundTripsExactly case; this is heading's, and the decimals here are chosen because
        // they are *not* exact in binary, unlike the values the normalisation theory uses.
        Assert.Equal(heading, TelemetrySamples.Telemetry(headingDegrees: heading).HeadingDegrees);
    }

    [Fact]
    public void Create_NegativeZeroHeading_NormalisesToPositiveZero()
    {
        // -0.0 is not less than zero, so it survives the in-range guard unless the guard excludes
        // it deliberately. It has to reach the fold, which turns it into +0.0: stored as -0.0 it
        // would render and serialise as "-0", which is not a heading.
        double? reported = TelemetrySamples.Telemetry(headingDegrees: -0.0).HeadingDegrees;

        // Not null, because a heading was supplied: the sample passes one, so a null here would be
        // the normalisation losing it rather than the vehicle declining to report it.
        Assert.NotNull(reported);
        double heading = reported.Value;

        Assert.Equal(0.0, heading);
        Assert.False(double.IsNegative(heading));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(359.999)]
    [InlineData(-359.999)]
    [InlineData(1e9)]
    [InlineData(-1e9)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]
    public void Create_AnyFiniteHeading_LandsStrictlyBelowThreeSixty(double raw)
    {
        // The postcondition the console will index a compass rose with. Asserted as a range
        // rather than an exact value so the messy inputs -- the ones a real adapter produces --
        // are covered without hand-computing a decimal literal that would only be a guess.
        double? reported = TelemetrySamples.Telemetry(headingDegrees: raw).HeadingDegrees;

        Assert.NotNull(reported);
        double heading = reported.Value;

        Assert.True(heading >= 0, $"{raw} normalised to {heading}, which is negative.");
        Assert.True(heading < 360, $"{raw} normalised to {heading}, which is not below 360.");
    }

    [Fact]
    public void Create_TinyNegativeHeading_NormalisesToZeroRatherThanThreeSixty()
    {
        // The case the double-modulo comment calls out: a tiny negative rounds to exactly 360.0
        // at the addition, and the second remainder is what takes it back to 0. Without that
        // second `% 360` this returns 360 and every downstream `< 360` assumption breaks.
        Assert.Equal(0, TelemetrySamples.Telemetry(headingDegrees: -double.Epsilon).HeadingDegrees);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Verifies("MCS-012")]
    public void Create_NonFiniteHeading_ThrowsArgumentOutOfRange(double heading)
    {
        // Normalisation has no answer for these -- NaN % 360 is NaN -- so they are rejected
        // before it runs, rather than stored as a heading no compass can render.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(headingDegrees: heading));

        Assert.Equal("headingDegrees", ex.ParamName);
    }

    // --- Create: battery ----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(87)]
    [InlineData(100)]
    public void Create_BatteryInRange_RoundTripsExactly(double battery) =>
        Assert.Equal(battery, TelemetrySamples.Telemetry(batteryPercent: battery).BatteryPercent);

    [Fact]
    public void Create_UnreportedBattery_StaysNullRatherThanBecomingZero()
    {
        // The whole reason the property is nullable. Substituting 0 for "unknown" puts a number
        // in front of the operator that was never measured -- and the one number that would make
        // them abort. Null forces the console into an explicit "no data" state instead.
        Assert.Null(TelemetrySamples.Telemetry(batteryPercent: null).BatteryPercent);
    }

    [Fact]
    public void Create_UnreportedHeading_StaysNullRatherThanBecomingZero()
    {
        // Heading is the field with the least tolerance for a substituted value: it is drawn as the
        // direction the nose points, so a zero standing in for "unknown" is a marker confidently
        // claiming north. A vehicle reports its position and its velocity in separate messages at
        // separate rates, so knowing where something is without knowing which way it faces is an
        // ordinary state of the world, not an error.
        Assert.Null(TelemetrySamples.Telemetry(headingDegrees: null).HeadingDegrees);
    }

    [Fact]
    public void Create_UnreportedGroundSpeed_StaysNullRatherThanBecomingZero()
    {
        // Zero is a speed, and a vehicle shown at rest is a vehicle an operator stops watching.
        Assert.Null(
            TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: null).GroundSpeedMetersPerSecond);
    }

    [Fact]
    public void Create_UnreportedFields_AreNotSubjectedToTheRangeChecks()
    {
        // Absence is not a range violation, and collapsing the two would make "the vehicle did not
        // say" indistinguishable from "the vehicle said something impossible" -- one of which is
        // ordinary and one of which means an adapter is broken.
        VehicleTelemetry telemetry = TelemetrySamples.Telemetry(
            groundSpeedMetersPerSecond: null, headingDegrees: null, batteryPercent: null);

        Assert.Null(telemetry.GroundSpeedMetersPerSecond);
        Assert.Null(telemetry.HeadingDegrees);
        Assert.Null(telemetry.BatteryPercent);

        // Position is deliberately not nullable: a report with no position is not renderable at
        // all, so it is never constructed rather than constructed empty.
        Assert.Equal(51.5074, telemetry.LatitudeDegrees);
    }

    [Theory]
    [InlineData(100.1)]
    [InlineData(120)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [Verifies("MCS-012")]
    public void Create_BatteryOutOfRange_ThrowsArgumentOutOfRangeNamingBattery(double battery)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(batteryPercent: battery));

        Assert.Equal("batteryPercent", ex.ParamName);
        Assert.Equal(battery, Assert.IsType<double>(ex.ActualValue));
    }

    [Fact]
    public void Create_BatteryAboveOneHundred_IsRejectedRatherThanClamped()
    {
        // The strategy stated in the type's remarks, asserted as behaviour: a clamped 200%
        // renders as a believable 100% and nobody ever learns the adapter is broken. Loud
        // failure at the boundary is the only thing that surfaces it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(batteryPercent: 200));
    }

    [Fact]
    public void Create_FractionalBatteryMistakenlySentAsAFraction_IsAcceptedAndCannotBeDetected()
    {
        // Characterisation of a real limitation, recorded so nobody assumes the type catches it:
        // an adapter reporting a 0-1 fraction produces 0.87, which is a legal percentage and
        // reads as a nearly flat battery. Only the parameter name at the call site prevents this,
        // which is why the name says Percent.
        Assert.Equal(0.87, TelemetrySamples.Telemetry(batteryPercent: 0.87).BatteryPercent);
    }

    // --- Create: link status ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DeclaredLinkStatuses))]
    public void Create_EveryDeclaredLinkStatus_IsAccepted(LinkStatus status) =>
        Assert.Equal(status, TelemetrySamples.Telemetry(linkStatus: status).LinkStatus);

    public static TheoryData<LinkStatus> DeclaredLinkStatuses()
    {
        // Data-driven over the enum rather than hard-coded, so a member added later is covered by
        // this test automatically instead of silently escaping it.
        TheoryData<LinkStatus> data = [];
        foreach (LinkStatus status in Enum.GetValues<LinkStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    [Theory]
    [InlineData(0)]             // the uninitialised-field sentinel
    [InlineData(99)]            // out-of-band cast
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Create_UndeclaredLinkStatus_ThrowsArgumentOutOfRange(int raw)
    {
        LinkStatus status = (LinkStatus)raw;

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TelemetrySamples.Telemetry(linkStatus: status));

        Assert.Equal("linkStatus", ex.ParamName);
        Assert.Equal(status, Assert.IsType<LinkStatus>(ex.ActualValue));
    }

    // --- Accepted reports ---------------------------------------------------------------------

    [Fact]
    public void Create_TheDocumentedExample_ProducesTheDocumentedValues()
    {
        // The XML example asserted as a test, so the documentation cannot drift from behaviour.
        VehicleTelemetry telemetry = VehicleTelemetry.Create(
            id: VehicleId.From("UAV-01"),
            latitudeDegrees: 51.5074,
            longitudeDegrees: -0.1278,
            altitude: Altitude.FromMeters(120, AltitudeReference.Agl),
            groundSpeedMetersPerSecond: 14.2,
            headingDegrees: 372.5,
            batteryPercent: 87.0,
            linkStatus: LinkStatus.Healthy);

        Assert.Equal(VehicleId.From("UAV-01"), telemetry.Id);
        Assert.Equal(51.5074, telemetry.LatitudeDegrees);
        Assert.Equal(-0.1278, telemetry.LongitudeDegrees);
        Assert.Equal(Altitude.FromMeters(120, AltitudeReference.Agl), telemetry.Altitude);
        Assert.Equal(14.2, telemetry.GroundSpeedMetersPerSecond);
        Assert.Equal(12.5, telemetry.HeadingDegrees);   // 372.5 normalised
        Assert.Equal(87.0, telemetry.BatteryPercent);
        Assert.Equal(LinkStatus.Healthy, telemetry.LinkStatus);
    }

    [Fact]
    public void Create_AltitudeAndItsReference_TravelTogether() =>
        Assert.Equal(
            AltitudeReference.Msl,
            TelemetrySamples.Telemetry(altitude: Altitude.FromFeet(400, AltitudeReference.Msl))
                .Altitude.Reference);

    // --- ToString -----------------------------------------------------------------------------

    [Fact]
    public void ToString_FormatsEveryMemberInTheInvariantCulture() =>
        Assert.Equal(TelemetrySamples.TelemetryText, TelemetrySamples.Telemetry().ToString());

    [Fact]
    public void ToString_UnreportedBattery_SaysSoRatherThanLeavingTheSlotEmpty()
    {
        // "unreported" rather than a blank, so a missing battery reads as a decision in the log
        // line instead of something that looks like a formatting failure.
        Assert.Contains(
            "BatteryPercent = unreported",
            TelemetrySamples.Telemetry(batteryPercent: null).ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_EveryUnreportedField_ReadsTheSameWay()
    {
        // One word for all three absences. A log line where a missing heading reads differently
        // from a missing battery invites whoever is reading it to believe the difference means
        // something, and it does not -- both are the vehicle declining to say.
        string text = TelemetrySamples.Telemetry(
            groundSpeedMetersPerSecond: null, headingDegrees: null, batteryPercent: null).ToString();

        Assert.Contains("GroundSpeedMetersPerSecond = unreported", text, StringComparison.Ordinal);
        Assert.Contains("HeadingDegrees = unreported", text, StringComparison.Ordinal);
        Assert.Contains("BatteryPercent = unreported", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_LinkStatus_AppearsAsItsNameNotItsNumber() =>
        Assert.Contains(
            "LinkStatus = Degraded",
            TelemetrySamples.Telemetry(linkStatus: LinkStatus.Degraded).ToString(),
            StringComparison.Ordinal);

    [Fact]
    public void ToString_CoordinatesKeepSevenDecimals_WhileScalarsKeepTwo()
    {
        // Roughly 1.1 cm at the equator for the coordinates, which is more precision than any of
        // this is claiming; two decimals is plenty for a speed. Pinned because a shared format
        // would have to be wrong for one of them.
        string text = TelemetrySamples.Telemetry(
            latitudeDegrees: 51.1234567,
            longitudeDegrees: -0.7654321,
            groundSpeedMetersPerSecond: 14.239).ToString();

        Assert.Contains("LatitudeDegrees = 51.1234567", text, StringComparison.Ordinal);
        Assert.Contains("LongitudeDegrees = -0.7654321", text, StringComparison.Ordinal);
        Assert.Contains("GroundSpeedMetersPerSecond = 14.24", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_NamesTheTypeSoALogLineIsSelfIdentifying() =>
        Assert.StartsWith(
            "VehicleTelemetry { ", TelemetrySamples.Telemetry().ToString(), StringComparison.Ordinal);

    // --- Equality -----------------------------------------------------------------------------

    [Fact]
    public void Equality_IdenticalReports_AreEqualWithSameHashCode()
    {
        VehicleTelemetry a = TelemetrySamples.Telemetry();
        VehicleTelemetry b = TelemetrySamples.Telemetry();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_SameReportFromADifferentVehicle_IsNotEqual() =>
        Assert.NotEqual(
            TelemetrySamples.Telemetry(),
            TelemetrySamples.Telemetry(id: VehicleId.From("UAV-02")));

    [Fact]
    public void Equality_AnySingleFieldDiffering_IsNotEqual()
    {
        // One variation per field, so a member dropped from the synthesized equality -- the sort
        // of thing that happens when someone hand-writes Equals -- fails a case rather than none.
        // A [Fact] over a local array rather than a [Theory]: VehicleTelemetry is not xUnit-
        // serialisable, so it cannot travel through TheoryData.
        VehicleTelemetry baseline = TelemetrySamples.Telemetry();
        VehicleTelemetry[] variants =
        [
            TelemetrySamples.Telemetry(id: VehicleId.From("UAV-02")),
            TelemetrySamples.Telemetry(latitudeDegrees: 51.5),
            TelemetrySamples.Telemetry(longitudeDegrees: -0.2),
            TelemetrySamples.Telemetry(altitude: Altitude.FromMeters(121, AltitudeReference.Agl)),
            TelemetrySamples.Telemetry(altitude: Altitude.FromMeters(120, AltitudeReference.Msl)),
            TelemetrySamples.Telemetry(groundSpeedMetersPerSecond: 14.3),
            TelemetrySamples.Telemetry(headingDegrees: 13.5),
            TelemetrySamples.Telemetry(batteryPercent: 86),
            TelemetrySamples.Telemetry(batteryPercent: null),
            TelemetrySamples.Telemetry(linkStatus: LinkStatus.Degraded),
        ];

        Assert.All(variants, v => Assert.NotEqual(baseline, v));
    }

    [Fact]
    public void Equality_SameAltitudeDifferentReference_IsNotEqual()
    {
        // MCS-004 carried up to this level: 120 m AGL and 120 m MSL are different reports, and
        // nothing short of terrain data makes them interchangeable.
        Assert.NotEqual(
            TelemetrySamples.Telemetry(altitude: Altitude.FromMeters(120, AltitudeReference.Agl)),
            TelemetrySamples.Telemetry(altitude: Altitude.FromMeters(120, AltitudeReference.Msl)));
    }

    [Fact]
    public void Equality_HeadingsThatNormaliseToTheSameValue_AreEqual()
    {
        // Normalisation happens before storage, so 372.5 and 12.5 produce one report, not two.
        // This is what stops a ring buffer from treating a re-sent frame as new.
        Assert.Equal(
            TelemetrySamples.Telemetry(headingDegrees: 12.5),
            TelemetrySamples.Telemetry(headingDegrees: 372.5));
    }

    // --- Structural invariants ----------------------------------------------------------------

    [Fact]
    public void Type_CarriesNoTimestamp()
    {
        // The load-bearing absence for MCS-005. An adapter can produce nothing but this type, so
        // if there is no time-valued member here, stamping a frame early is not a discipline an
        // adapter author can fail at -- it is code they cannot write.
        Assert.DoesNotContain(
            typeof(VehicleTelemetry).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            p => p.PropertyType == typeof(DateTimeOffset)
                || p.PropertyType == typeof(DateTime)
                || p.PropertyType == typeof(DateTimeOffset?)
                || p.PropertyType == typeof(DateTime?));
    }

    [Fact]
    public void Type_ExposesNoPublicConstructor()
    {
        // Create() is the only way in. A public constructor appearing here would mean an
        // unvalidated report could reach the store -- the one thing the private constructor and
        // the factory exist to prevent.
        Assert.Empty(
            typeof(VehicleTelemetry).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Type_CreateIsTheOnlyPublicFactory()
    {
        MethodInfo[] factories = [.. typeof(VehicleTelemetry)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(VehicleTelemetry))];

        Assert.Equal("Create", Assert.Single(factories).Name);
    }

    [Fact]
    public void Type_AllPropertiesAreGetOnly()
    {
        // Not a positional record, and this is what that buys: `init` accessors are assigned
        // directly by a `with` expression without re-running validation, so
        // `telemetry with { BatteryPercent = -5 }` would be an unguarded hole. Get-only makes it
        // a compile error, and this test fails if anyone converts the type to positional form.
        PropertyInfo[] properties =
            typeof(VehicleTelemetry).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public void Type_AllInstanceFields_AreReadOnly()
    {
        // Backs the immutability the rest of the station assumes: a report crosses threads
        // between the feed and the SSE readers with no lock anywhere.
        FieldInfo[] fields = typeof(VehicleTelemetry)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.IsInitOnly, $"{f.Name} is not readonly."));
    }

    [Fact]
    public void Type_IsSealed() => Assert.True(typeof(VehicleTelemetry).IsSealed);

    [Fact]
    public void Type_IsAReferenceType()
    {
        // Deliberate: eight fields is past the size where copying beats a reference, and these
        // are handed to ring buffers and SSE subscribers rather than used as dictionary keys. It
        // also means `default` is null, which nullable reference types already police -- so this
        // type needs none of the uninitialised-sentinel machinery Altitude and VehicleId carry.
        Assert.False(typeof(VehicleTelemetry).IsValueType);
    }

    // --- LinkStatus ---------------------------------------------------------------------------

    [Fact]
    public void LinkStatus_HasNoZeroMember()
    {
        // Same mechanism as AltitudeReference: a field that was never assigned reads back as 0,
        // and 0 must not be mistakeable for Healthy. Adding `Unknown = 0` later would disarm the
        // Enum.IsDefined check in Create -- and this fails immediately if anyone does.
        Assert.DoesNotContain(Enum.GetValues<LinkStatus>(), s => (int)s == 0);
    }

    [Fact]
    public void LinkStatus_DeclaresTheThreeExpectedStates()
    {
        LinkStatus[] declared = Enum.GetValues<LinkStatus>();

        Assert.Equal(3, declared.Length);
        Assert.Contains(LinkStatus.Healthy, declared);
        Assert.Contains(LinkStatus.Degraded, declared);
        Assert.Contains(LinkStatus.Lost, declared);
    }

    [Fact]
    public void LinkStatus_NumericValues_AreStable()
    {
        // Pinned because renumbering would silently change the wire contract for anything that
        // ever serialises the underlying number. The API is meant to emit the name instead --
        // this is the belt to that braces.
        Assert.Equal(1, (int)LinkStatus.Healthy);
        Assert.Equal(2, (int)LinkStatus.Degraded);
        Assert.Equal(3, (int)LinkStatus.Lost);
    }

    [Fact]
    public void LinkStatus_IsNotStaleness()
    {
        // Not a behaviour of this type so much as a boundary of it, asserted so the boundary
        // stays visible: nothing here can answer "is this frame stale?". A vehicle can report
        // Healthy in the last frame before the link drops entirely, and the console must still
        // mark it stale three seconds later from TelemetryFrame.ReceivedAtUtc (MCS-002).
        Assert.DoesNotContain(
            typeof(VehicleTelemetry).GetMembers(BindingFlags.Instance | BindingFlags.Public),
            m => m.Name.Contains("Stale", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Age", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <see cref="VehicleTelemetry"/> tests that mutate
/// <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
/// </summary>
[Collection(CultureCollection.Name)]
public class VehicleTelemetryCultureTests
{
    [Fact]
    public void ToString_IsInvariant_RegardlessOfAmbientCulture()
    {
        // The reason PrintMembers is overridden at all. The synthesized version formats doubles
        // with the current culture, so this same report would log "51,5074" in a container with a
        // European locale -- a difference that survives into the station's JSON logs and breaks
        // anything parsing them.
        using CultureScope _ = new("de-DE");

        Assert.Equal(TelemetrySamples.TelemetryText, TelemetrySamples.Telemetry().ToString());
    }

    [Fact]
    public void ToString_NestedAltitude_IsAlsoInvariant()
    {
        // The nested value formats through Altitude's IFormattable overload, which follows the
        // framework convention of falling back to the current culture. This is the test that the
        // frame passes it an explicit invariant provider rather than letting it default.
        using CultureScope _ = new("fr-FR");

        Assert.Contains(
            "Altitude = 120 m Agl",
            TelemetrySamples.Telemetry().ToString(),
            StringComparison.Ordinal);
    }
}
