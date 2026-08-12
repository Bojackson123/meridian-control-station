using System.Buffers.Binary;

using Mcs.Adapters.Mavlink;
using Mcs.Adapters.Mavlink.Messages;

using Mcs.Core;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The composition rules: what several messages arriving at different rates add up to, and when
/// that is enough to put in front of an operator.
/// </summary>
/// <remarks>
/// Driven through <see cref="MavlinkTelemetryDecoder"/> rather than against the per-vehicle
/// assembler directly, because the routing and the folding are one behaviour from every caller's
/// point of view and testing the inner half alone would leave the sender keying -- the part that
/// decides whether a gimbal can overwrite an aircraft's battery -- asserted nowhere.
/// <para>
/// The field values these assert on are the committed vectors'. What they add is the layer above:
/// which message a field is taken from, what is retained between messages, when a report is emitted,
/// and what happens to a value the station will not represent.
/// </para>
/// </remarks>
public class MavlinkTelemetryAssemblerTests
{
    /// <summary>The vector's own values, restated so an assertion says what it expects.</summary>
    private const double ExpectedLatitude = -33.72134;
    private const double ExpectedLongitude = 151.16277;
    private const double ExpectedAltitudeMetersMsl = 1250.5;
    private const double ExpectedRelativeAltitudeMeters = 118.3;
    private const double ExpectedGroundSpeed = 21.5;
    private const double ExpectedHeading = 142;

    /// <summary>Seven decimal places is the resolution of the 1e7 scaling itself.</summary>
    private const int CoordinatePrecision = 7;

    // --- When a report is emitted ----------------------------------------------------------------

    /// <summary>
    /// Position is the floor: no report until one arrives, whatever else has.
    /// </summary>
    /// <remarks>
    /// A battery and a heading with no position is not renderable -- there is nowhere to draw it --
    /// so emitting on those messages would either put a vehicle at a made-up coordinate or add a
    /// track with no location, and HAZ-01 is a console showing a picture that is not current.
    /// </remarks>
    [Fact]
    public void Emits_NothingBeforeAPositionArrives()
    {
        MavlinkTelemetryDecoder decoder = new();

        Assert.False(decoder.TryDecode(MavlinkFrames.FromVector("heartbeat"), out _));
        Assert.False(decoder.TryDecode(MavlinkFrames.FromVector("sys_status_battery"), out _));
        Assert.False(decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _));

        Assert.Equal(0, decoder.Statistics.TelemetryEmitted);

        //  The sender is known even though nothing is renderable yet, which is what a heartbeat
        //  buys: the station can tell "a vehicle is talking and cannot be drawn" from "nothing is
        //  there", and those need different answers.
        Assert.Equal(1, decoder.SenderCount);
    }

    /// <summary>
    /// A position with no VFR_HUD behind it emits with speed and heading absent, rather than being
    /// withheld or filled in.
    /// </summary>
    /// <remarks>
    /// The three-way choice, and why this is the honest corner of it. Substituting zeroes draws the
    /// vehicle stationary and pointing true north, which is a claim the data does not support.
    /// Deriving an angle from the velocity components gives course over ground, which is a different
    /// quantity. Withholding the report keeps a vehicle whose position is known entirely off the
    /// console, and the one thing worse than an incomplete picture is no picture of something that
    /// is flying. Null says exactly what is true, and the console has a rendering for it.
    /// </remarks>
    [Fact]
    public void Emits_APositionWithNoHudAsAbsentSpeedAndHeading()
    {
        MavlinkTelemetryDecoder decoder = new();

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        //  The position is present and exact -- that is the whole reason to emit it.
        Assert.Equal(ExpectedLatitude, telemetry.LatitudeDegrees, CoordinatePrecision);
        Assert.Equal(ExpectedAltitudeMetersMsl, telemetry.Altitude.Meters);

        Assert.Null(telemetry.GroundSpeedMetersPerSecond);
        Assert.Null(telemetry.HeadingDegrees);

        //  Counted, because a sender that never emits VFR_HUD at all looks on the console like a
        //  fleet permanently showing dashes, and no other counter distinguishes it from health.
        Assert.Equal(1, decoder.Statistics.TelemetryEmitted);
        Assert.Equal(1, decoder.Statistics.PositionsWithoutHud);
    }

    [Fact]
    public void Emits_OnAPositionOnceAHudHasArrived()
    {
        MavlinkTelemetryDecoder decoder = new();

        Assert.False(decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _));

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Equal(VehicleId.From("MAV-255"), telemetry.Id);
        Assert.Equal(1, decoder.Statistics.TelemetryEmitted);
        Assert.Equal(0, decoder.Statistics.PositionsWithoutHud);
    }

    /// <summary>
    /// Only a position emits, so a burst of other messages does not multiply the console's rate.
    /// </summary>
    /// <remarks>
    /// The rejected alternative was emitting on every inbound message, which makes the update rate a
    /// function of how many message types the sender happens to be configured for rather than of
    /// how often it knows where it is.
    /// </remarks>
    [Fact]
    public void Emits_OncePerPositionRegardlessOfWhatElseArrives()
    {
        MavlinkTelemetryDecoder decoder = new();
        Seed(decoder);

        foreach (string vectorName in new[] { "heartbeat", "sys_status_battery", "vfr_hud" })
        {
            Assert.False(decoder.TryDecode(MavlinkFrames.FromVector(vectorName), out _));
        }

        Assert.True(decoder.TryDecode(MavlinkFrames.FromVector("global_position_int"), out _));

        //  Two: the one Seed produced, and the one above.
        Assert.Equal(2, decoder.Statistics.TelemetryEmitted);
    }

    // --- Which message each field comes from ------------------------------------------------------

    [Fact]
    public void Position_ComesFromGlobalPositionIntAtItsWireScaling()
    {
        VehicleTelemetry telemetry = Fly();

        Assert.Equal(ExpectedLatitude, telemetry.LatitudeDegrees, CoordinatePrecision);
        Assert.Equal(ExpectedLongitude, telemetry.LongitudeDegrees, CoordinatePrecision);
    }

    /// <summary>
    /// Ground speed and heading come from VFR_HUD, not from the velocity components.
    /// </summary>
    /// <remarks>
    /// The vector's velocities are 1250 and 430 cm/s, whose magnitude is about 13.2 m/s -- nothing
    /// like VFR_HUD's 21.5 -- so this fails loudly if the derivation is ever preferred. That it
    /// would be a plausible number is the point: the two disagree because they are different
    /// quantities, one of which is course-and-speed over the ground computed from a horizontal
    /// velocity, and the other of which is what the autopilot publishes for a display.
    /// </remarks>
    [Fact]
    public void GroundSpeedAndHeading_ComeFromVfrHud()
    {
        VehicleTelemetry telemetry = Fly();

        Assert.Equal(ExpectedGroundSpeed, telemetry.GroundSpeedMetersPerSecond);
        Assert.Equal(ExpectedHeading, telemetry.HeadingDegrees);
    }

    // --- The altitude reference (MCS-004) ---------------------------------------------------------

    /// <summary>
    /// The altitude carries a declared reference, and it is MSL.
    /// </summary>
    /// <remarks>
    /// The requirement that no position report may travel without its reference is met by the type
    /// -- <see cref="Altitude"/> cannot be constructed without one -- so what is worth asserting is
    /// the mapping: that the field named for mean sea level is the one that becomes
    /// <see cref="AltitudeReference.Msl"/>, and not the relative one.
    /// </remarks>
    [Fact]
    public void Altitude_IsMeanSeaLevelAndSaysSo()
    {
        VehicleTelemetry telemetry = Fly();

        Assert.Equal(AltitudeReference.Msl, telemetry.Altitude.Reference);
        Assert.Equal(ExpectedAltitudeMetersMsl, telemetry.Altitude.Meters);
    }

    /// <summary>
    /// <c>relative_alt</c> does not reach telemetry, under any label.
    /// </summary>
    /// <remarks>
    /// It is height above the home point, which equals AGL only over flat ground, so carrying it as
    /// <see cref="AltitudeReference.Agl"/> would be a quiet lie of exactly the kind MCS-004 exists
    /// to prevent -- and the MSL/AGL conversion this station will eventually need would inherit it
    /// and produce arithmetic that is correct about the wrong quantity. The value stays on the
    /// message type, in millimetres, under
    /// a name that does not say AGL.
    /// </remarks>
    [Fact]
    public void Altitude_IsNeverTheRelativeOne()
    {
        VehicleTelemetry telemetry = Fly();

        Assert.NotEqual(ExpectedRelativeAltitudeMeters, telemetry.Altitude.Meters);
        Assert.NotEqual(AltitudeReference.Agl, telemetry.Altitude.Reference);
    }

    // --- Battery, carried across messages ---------------------------------------------------------

    /// <summary>
    /// A battery reported once is carried onto later position frames.
    /// </summary>
    /// <remarks>
    /// The reason the assembler exists. SYS_STATUS arrives at a fraction of the position rate, so a
    /// station that only reported the battery on the frame it arrived in would show a charge level
    /// that blinked out between reports.
    /// </remarks>
    [Fact]
    public void Battery_FromAnEarlierSysStatus_IsCarriedOntoALaterPosition()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("sys_status_battery"), out _);
        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? first));
        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? second));

        Assert.Equal(73, first.BatteryPercent);
        Assert.Equal(73, second.BatteryPercent);
    }

    /// <summary>
    /// A vehicle reporting an unmeasured battery produces null, never zero.
    /// </summary>
    /// <remarks>
    /// Zero is the one substitution that would make an operator abort a mission, and -1 on the wire
    /// means the vehicle does not know. Null forces the console into an explicit "no data" state
    /// instead of a plausible lie.
    /// </remarks>
    [Fact]
    public void Battery_ReportedUnmeasured_IsNullAndNotZero()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("sys_status"), out _);
        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Null(telemetry.BatteryPercent);
    }

    /// <summary>
    /// A position emitted before any SYS_STATUS has arrived still emits, with no battery.
    /// </summary>
    /// <remarks>
    /// Position and altitude are the fields <see cref="VehicleTelemetry"/> refuses to be built
    /// without, and that is the whole of "enough to show" -- battery, ground speed and heading are
    /// all nullable and none of them holds a report back. This pins the battery half of it; the
    /// speed and heading half is <see cref="Emits_APositionWithNoHudAsAbsentSpeedAndHeading"/>. A
    /// vehicle whose position is known and whose charge is not is worth drawing.
    /// </remarks>
    [Fact]
    public void Battery_NeverReported_DoesNotHoldUpTheReport()
    {
        VehicleTelemetry telemetry = Fly();

        Assert.Null(telemetry.BatteryPercent);
    }

    // --- Values the station will not represent -----------------------------------------------------

    /// <summary>
    /// A latitude past the pole discards that message and leaves the loop running.
    /// </summary>
    /// <remarks>
    /// <see cref="VehicleTelemetry.Create"/> would throw on it, and an exception escaping a decode
    /// costs a whole datagram of good frames for one bad field -- so the check is here, where the
    /// unit of loss is one message. Discarded rather than clamped: a latitude clamped to 90 renders
    /// at the pole, which is a place, and the adapter that produced it is never suspected.
    /// </remarks>
    [Fact]
    public void Position_PastThePole_IsRejectedAndCountedWithoutThrowing()
    {
        MavlinkTelemetryDecoder decoder = new();
        Seed(decoder);

        byte[] payload = MavlinkVectors.Named("global_position_int").FullPayload;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 950_000_000);

        Assert.False(decoder.TryDecode(
            MavlinkFrames.FromPayload(MavlinkMessageId.GlobalPositionInt, payload), out _));

        Assert.Equal(1, decoder.Statistics.MessagesRejected);
    }

    /// <summary>A ground speed that is negative or not a number goes the same way.</summary>
    /// <remarks>
    /// Both reach <see cref="VehicleTelemetry.Create"/> as an exception. NaN is the one worth
    /// naming separately: it compares false against every bound, so a range check written as
    /// <c>speed &gt; max</c> alone would let it through to become a distance that never closes.
    /// </remarks>
    [Theory]
    [InlineData(-3.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void GroundSpeed_Implausible_IsRejectedAndCounted(float groundSpeed)
    {
        MavlinkTelemetryDecoder decoder = new();

        byte[] payload = MavlinkVectors.Named("vfr_hud").FullPayload;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), groundSpeed);

        Assert.False(decoder.TryDecode(
            MavlinkFrames.FromPayload(MavlinkMessageId.VfrHud, payload), out _));

        Assert.Equal(1, decoder.Statistics.MessagesRejected);

        //  The position behind it still emits -- withholding it would lose a known position over a
        //  bad speed -- but it carries no speed and no heading, because the HUD that would have
        //  supplied them is the one that was thrown away. The rejection is visible on the console
        //  as a dash rather than papered over with a plausible number.
        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Null(telemetry.GroundSpeedMetersPerSecond);
        Assert.Null(telemetry.HeadingDegrees);
        Assert.Equal(1, decoder.Statistics.PositionsWithoutHud);
    }

    /// <summary>
    /// A battery outside 0-100 is discarded and the last representable reading stays.
    /// </summary>
    /// <remarks>
    /// The alternative -- blanking the battery on a bad message -- was rejected because a
    /// <i>missing</i> SYS_STATUS already leaves the previous reading in place, so blanking would
    /// make a corrupt message erase knowledge that an absent one does not. Age is staleness's
    /// problem and it is measured against the station clock, not patched here.
    /// </remarks>
    [Fact]
    public void Battery_OutOfRange_IsRejectedAndTheLastGoodReadingStays()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("sys_status_battery"), out _);
        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        //  120% -- an int8 reaches 127, so this is a value a broken sender really can produce.
        byte[] payload = MavlinkVectors.Named("sys_status_battery").FullPayload;
        payload[30] = 120;

        Assert.False(decoder.TryDecode(
            MavlinkFrames.FromPayload(MavlinkMessageId.SysStatus, payload), out _));

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Equal(73, telemetry.BatteryPercent);
        Assert.Equal(1, decoder.Statistics.MessagesRejected);
    }

    /// <summary>
    /// A HUD that stops arriving is carried forward indefinitely, and the reports keep coming.
    /// </summary>
    /// <remarks>
    /// <b>This pins a known exposure rather than a desirable behaviour.</b> Once a VFR_HUD has been
    /// seen, every later position emits with it, however old it is -- so a sender whose stream rate
    /// is renegotiated to zero produces reports stamped now that carry a heading from minutes ago,
    /// and staleness cannot catch them because the frame really is fresh. The same retention is
    /// deliberate for battery, where a level blinking out between SYS_STATUS reports would be worse.
    /// <para>
    /// It is asserted here so that closing it is a decision someone makes on purpose: the fix needs
    /// an age threshold, no requirement supplies one, and inventing a per-field one here would put a
    /// second mechanism in front of the operator alongside station-clock staleness. Whoever adds
    /// that threshold should expect this test to fail and should change it, which is the point of
    /// writing it down as a test rather than only as a paragraph.
    /// </para>
    /// </remarks>
    [Fact]
    public void Hud_ThatStopsArriving_IsCarriedForwardWithNoAgeBound()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        //  A hundred positions and no further HUD. Nothing here advances a clock, because nothing in
        //  the assembler reads one -- which is precisely why the age cannot be bounded from inside.
        VehicleTelemetry? last = null;
        for (int i = 0; i < 100; i++)
        {
            Assert.True(decoder.TryDecode(MavlinkFrames.FromVector("global_position_int"), out last));
        }

        Assert.Equal(ExpectedGroundSpeed, last!.GroundSpeedMetersPerSecond);
        Assert.Equal(ExpectedHeading, last.HeadingDegrees);
        Assert.Equal(100, decoder.Statistics.TelemetryEmitted);

        //  And nothing counts it: the one counter that could have is for positions with no HUD at
        //  all, which is a different fact from a HUD that has gone quiet.
        Assert.Equal(0, decoder.Statistics.PositionsWithoutHud);
    }

    // --- Link status, and what it is not -----------------------------------------------------------

    /// <summary>
    /// Telemetry from this path reports a healthy link, and nothing derives otherwise.
    /// </summary>
    /// <remarks>
    /// A decoded frame is by definition one that arrived, so this layer holds no evidence of a
    /// degraded link to report; SYS_STATUS's sensor-health mask and drop rate describe the vehicle's
    /// own sensors and the vehicle's own links, at the other end. "Lost" belongs to staleness
    /// against the station clock (MCS-002) -- and two mechanisms deciding a vehicle is gone will
    /// eventually disagree, so the one an operator sees must be the one tied to the station's clock.
    /// </remarks>
    [Fact]
    public void LinkStatus_IsHealthyAndIsNotDerivedFromSensorHealth()
    {
        MavlinkTelemetryDecoder decoder = new();

        //  This vector's health mask has most of its bits clear against a present mask of
        //  0x0FFFFFFF -- a vehicle with unhealthy sensors, which must not read as a bad radio.
        decoder.TryDecode(MavlinkFrames.FromVector("sys_status_battery"), out _);
        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Equal(LinkStatus.Healthy, telemetry.LinkStatus);
    }

    // --- Routing ------------------------------------------------------------------------------------

    [Fact]
    public void TwoSystemIds_ProduceTwoIndependentTracks()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud", systemId: 7), out _);

        //  System 9 has sent no HUD of its own, so it emits with dashes while system 7 emits with
        //  7's speed and heading. Sharing state between the two would give 9 the other vehicle's
        //  heading -- a marker pointing somewhere nothing reported, which is the failure that makes
        //  independent tracks worth asserting rather than assuming.
        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int", systemId: 9),
            out VehicleTelemetry? other));

        Assert.Equal(VehicleId.From("MAV-009"), other.Id);
        Assert.Null(other.HeadingDegrees);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int", systemId: 7),
            out VehicleTelemetry? telemetry));

        Assert.Equal(VehicleId.From("MAV-007"), telemetry.Id);
        Assert.Equal(ExpectedHeading, telemetry.HeadingDegrees);
        Assert.Equal(2, decoder.SenderCount);
        Assert.Equal(1, decoder.Statistics.PositionsWithoutHud);
    }

    /// <summary>
    /// A second component of the same system cannot overwrite the autopilot's state.
    /// </summary>
    /// <remarks>
    /// Every component on a vehicle heartbeats and several report status, so keying on the system id
    /// alone would let a companion computer's battery reading become the aircraft's. Keying on the
    /// pair needs no allowlist of component ids -- and an allowlist would have been wrong anyway,
    /// since <c>MAV_COMP_ID_AUTOPILOT1</c> is a convention rather than a rule, and these very
    /// vectors are packed as component 190.
    /// </remarks>
    [Fact]
    public void TwoComponentsOfOneSystem_DoNotShareState()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        //  A different component reporting a measured battery. It belongs to that component's own
        //  state, not to the one the positions are arriving on.
        decoder.TryDecode(
            MavlinkFrames.FromVector("sys_status_battery", componentId: 42), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        Assert.Null(telemetry.BatteryPercent);
        Assert.Equal(2, decoder.SenderCount);
    }

    /// <summary>Both components still describe one vehicle, so the id comes from the system alone.</summary>
    [Fact]
    public void VehicleId_ComesFromTheSystemIdNotTheComponent()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud", systemId: 3, componentId: 42), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int", systemId: 3, componentId: 42),
            out VehicleTelemetry? telemetry));

        Assert.Equal(VehicleId.From("MAV-003"), telemetry.Id);
    }

    // --- The ingest boundary (MCS-005) ---------------------------------------------------------------

    /// <summary>
    /// The receipt timestamp is the arrival instant from the injected clock, and the decode is
    /// measured rather than folded into it.
    /// </summary>
    /// <remarks>
    /// The decode this ticket added is exactly the work the two-phase boundary exists to keep in
    /// front of the stamp. Stamping at the end would make every frame's recorded age include its own
    /// parse cost, invisibly and on every frame -- a console reporting data younger than it is,
    /// which is HAZ-01 in the direction that looks fine.
    /// </remarks>
    [Fact]
    public void ReceiptTimestamp_IsTakenAtArrivalAndNotAfterTheDecode()
    {
        FakeClock clock = new();
        TelemetryIngest ingest = new(clock);
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        TelemetryReceipt receipt = ingest.BeginReceive();

        //  Stands in for however long the decode took.
        clock.Advance(TimeSpan.FromMilliseconds(37));

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        TelemetryFrame frame = receipt.Complete(telemetry);

        Assert.Equal(FakeClock.Arrival, frame.ReceivedAtUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(37), receipt.IngestDelay);
    }

    //  There is no test here that the decoder cannot stamp a frame of its own, because there is no
    //  way to write one that could fail: TryDecode returns VehicleTelemetry, which carries no time
    //  at all, and TelemetryFrame's constructor is internal to Mcs.Core with TelemetryReceipt.
    //  Complete as its only caller. The property is held by the type system, and a test asserting
    //  it would only be asserting that the code still compiles.

    // --- Helpers --------------------------------------------------------------------------------------

    /// <summary>Brings a decoder to the point where the next position emits.</summary>
    private static void Seed(MavlinkTelemetryDecoder decoder)
    {
        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);
        decoder.TryDecode(MavlinkFrames.FromVector("global_position_int"), out _);
    }

    /// <summary>The ordinary case: a HUD, then a position, and the report that comes out.</summary>
    private static VehicleTelemetry Fly()
    {
        MavlinkTelemetryDecoder decoder = new();

        decoder.TryDecode(MavlinkFrames.FromVector("vfr_hud"), out _);

        Assert.True(decoder.TryDecode(
            MavlinkFrames.FromVector("global_position_int"), out VehicleTelemetry? telemetry));

        return telemetry;
    }
}
