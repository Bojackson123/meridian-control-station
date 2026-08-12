using System.Text.Json;

using Mcs.Adapters.Mavlink;
using Mcs.Adapters.Mavlink.Messages;

namespace Mcs.Adapters.Tests;

/// <summary>
/// Each message's fields, checked against the values pymavlink packed them from.
/// </summary>
/// <remarks>
/// The expectations come from the fixture's own <c>fields</c> map rather than from numbers typed
/// into this file. That is not laziness: a hand-typed expectation is a second transcription of the
/// same value, and the failure mode of transcribing twice is transcribing identically wrong. The
/// generator emits what it packed, so an assertion against it is an assertion that this decoder
/// recovers what the reference implementation encoded.
/// <para>
/// <b>These are field-mapping tests, and framing has its own.</b> A wrong offset here is the quiet
/// failure: nothing is rejected, no counter moves, and one number on the console is simply another
/// field's value. That is why every field of every message is asserted rather than a representative
/// few -- the ones most likely to be wrong are the ones nobody thought to check.
/// </para>
/// </remarks>
public class MavlinkMessageDecodeTests
{
    // --- The declared lengths, against the framing table -----------------------------------------

    /// <summary>
    /// Each message type's own <c>PayloadLength</c> agrees with the framing table's.
    /// </summary>
    /// <remarks>
    /// The number exists in two places on purpose -- framing needs it to undo truncation before a
    /// decoder exists, and a decoder needs it to know how much it may read -- so this is what stops
    /// the two from drifting. The framing table is in turn checked against pymavlink by
    /// <see cref="MavlinkCodecAgreementTests.MessageDefinition_AgreesWithPymavlink"/>, which is what
    /// makes agreement here mean agreement with the reference rather than with a shared mistake.
    /// </remarks>
    [Theory]
    [InlineData(MavlinkMessageId.Heartbeat, HeartbeatMessage.PayloadLength)]
    [InlineData(MavlinkMessageId.SysStatus, SysStatusMessage.PayloadLength)]
    [InlineData(MavlinkMessageId.GlobalPositionInt, GlobalPositionIntMessage.PayloadLength)]
    [InlineData(MavlinkMessageId.VfrHud, VfrHudMessage.PayloadLength)]
    public void PayloadLength_AgreesWithTheFramingTable(uint messageId, int payloadLength)
    {
        Assert.True(
            MavlinkMessageId.TryGetDefinition(messageId, out _, out int declaredLength));

        Assert.Equal(declaredLength, payloadLength);
    }

    // --- HEARTBEAT ------------------------------------------------------------------------------

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("heartbeat_all_zero")]
    public void Heartbeat_DecodesEveryField(string vectorName)
    {
        //  The all-zero case is here because its payload is one byte on the wire and nine after the
        //  framing layer restores it. A decoder reading a field the truncation removed gets the
        //  right answer only if that restoration actually happened.
        JsonElement expected = MavlinkVectors.Named(vectorName).Fields;
        MavlinkFrame frame = MavlinkFrames.FromVector(vectorName);

        HeartbeatMessage message = HeartbeatMessage.Read(frame.Payload.Span);

        Assert.Equal(expected.GetProperty("custom_mode").GetUInt32(), message.CustomMode);
        Assert.Equal(expected.GetProperty("type").GetByte(), message.VehicleType);
        Assert.Equal(expected.GetProperty("autopilot").GetByte(), message.Autopilot);
        Assert.Equal(expected.GetProperty("base_mode").GetByte(), message.BaseMode);
        Assert.Equal(expected.GetProperty("system_status").GetByte(), message.SystemStatus);
        Assert.Equal(expected.GetProperty("mavlink_version").GetByte(), message.MavlinkVersion);
    }

    // --- GLOBAL_POSITION_INT --------------------------------------------------------------------

    [Theory]
    [InlineData("global_position_int")]
    [InlineData("global_position_int_truncated")]
    public void GlobalPositionInt_DecodesEveryField(string vectorName)
    {
        JsonElement expected = MavlinkVectors.Named(vectorName).Fields;
        MavlinkFrame frame = MavlinkFrames.FromVector(vectorName);

        GlobalPositionIntMessage message = GlobalPositionIntMessage.Read(frame.Payload.Span);

        Assert.Equal(expected.GetProperty("time_boot_ms").GetUInt32(), message.TimeBootMilliseconds);
        Assert.Equal(expected.GetProperty("lat").GetInt32(), message.LatitudeDegreesE7);
        Assert.Equal(expected.GetProperty("lon").GetInt32(), message.LongitudeDegreesE7);
        Assert.Equal(expected.GetProperty("alt").GetInt32(), message.AltitudeMillimetersMsl);
        Assert.Equal(
            expected.GetProperty("relative_alt").GetInt32(), message.RelativeAltitudeMillimeters);
        Assert.Equal(expected.GetProperty("vx").GetInt16(), message.VelocityNorthCentimetersPerSecond);
        Assert.Equal(expected.GetProperty("vy").GetInt16(), message.VelocityEastCentimetersPerSecond);
        Assert.Equal(expected.GetProperty("vz").GetInt16(), message.VelocityDownCentimetersPerSecond);
        Assert.Equal(expected.GetProperty("hdg").GetUInt16(), message.HeadingCentiDegrees);
    }

    /// <summary>
    /// The two altitudes are distinct values and are not transposed.
    /// </summary>
    /// <remarks>
    /// Stated separately from the field sweep above because this is the pair MCS-004 is written
    /// against, and because they are the two fields of this message that would still look entirely
    /// plausible if they were swapped -- 1250.5 m and 118.3 m are both altitudes an aircraft flies
    /// at. Nothing but their order distinguishes them on the wire.
    /// </remarks>
    [Fact]
    public void GlobalPositionInt_KeepsTheTwoAltitudesApart()
    {
        MavlinkFrame frame = MavlinkFrames.FromVector("global_position_int");

        GlobalPositionIntMessage message = GlobalPositionIntMessage.Read(frame.Payload.Span);

        Assert.Equal(1_250_500, message.AltitudeMillimetersMsl);
        Assert.Equal(118_300, message.RelativeAltitudeMillimeters);
    }

    /// <summary>
    /// Latitude, longitude and the velocities survive as signed values.
    /// </summary>
    /// <remarks>
    /// The vector's coordinates are deliberately southern and western, and its northward velocity
    /// deliberately negative, because reading any of them unsigned does not fail -- it produces a
    /// coordinate on the far side of the planet and a vehicle flying the wrong way, both of which
    /// render.
    /// </remarks>
    [Fact]
    public void GlobalPositionInt_ReadsNegativeFieldsAsNegative()
    {
        MavlinkFrame frame = MavlinkFrames.FromVector("global_position_int");

        GlobalPositionIntMessage message = GlobalPositionIntMessage.Read(frame.Payload.Span);

        Assert.True(message.LatitudeDegreesE7 < 0);
        Assert.True(message.VelocityNorthCentimetersPerSecond < 0);
        Assert.True(message.VelocityDownCentimetersPerSecond < 0);
    }

    // --- SYS_STATUS -----------------------------------------------------------------------------

    [Theory]
    [InlineData("sys_status")]
    [InlineData("sys_status_battery")]
    public void SysStatus_DecodesEveryField(string vectorName)
    {
        JsonElement expected = MavlinkVectors.Named(vectorName).Fields;
        MavlinkFrame frame = MavlinkFrames.FromVector(vectorName);

        SysStatusMessage message = SysStatusMessage.Read(frame.Payload.Span);

        Assert.Equal(
            expected.GetProperty("onboard_control_sensors_present").GetUInt32(),
            message.SensorsPresent);
        Assert.Equal(
            expected.GetProperty("onboard_control_sensors_enabled").GetUInt32(),
            message.SensorsEnabled);
        Assert.Equal(
            expected.GetProperty("onboard_control_sensors_health").GetUInt32(),
            message.SensorsHealth);
        Assert.Equal(expected.GetProperty("load").GetUInt16(), message.LoadTenthsOfPercent);
        Assert.Equal(
            expected.GetProperty("voltage_battery").GetUInt16(), message.BatteryVoltageMillivolts);
        Assert.Equal(
            expected.GetProperty("current_battery").GetInt16(), message.BatteryCurrentCentiAmps);
        Assert.Equal(
            expected.GetProperty("drop_rate_comm").GetUInt16(), message.CommDropRateCentiPercent);
        Assert.Equal(expected.GetProperty("errors_comm").GetUInt16(), message.ErrorsComm);
        Assert.Equal(expected.GetProperty("errors_count1").GetUInt16(), message.ErrorsCount1);
        Assert.Equal(expected.GetProperty("errors_count2").GetUInt16(), message.ErrorsCount2);
        Assert.Equal(expected.GetProperty("errors_count3").GetUInt16(), message.ErrorsCount3);
        Assert.Equal(expected.GetProperty("errors_count4").GetUInt16(), message.ErrorsCount4);
        Assert.Equal(
            expected.GetProperty("battery_remaining").GetSByte(), message.BatteryRemainingPercent);
    }

    /// <summary>
    /// <c>battery_remaining</c> is signed, and -1 does not arrive as 255.
    /// </summary>
    /// <remarks>
    /// The single most consequential sign in the message set. Read unsigned, the wire's "I could not
    /// measure this" becomes a battery at 255%, which the telemetry model rejects outright -- and
    /// rejecting a whole SYS_STATUS is the visible failure. The invisible one is the same mistake
    /// followed by a clamp, which puts a full battery on screen for a vehicle whose charge is
    /// unknown. Both are avoided by the field being read as <c>int8</c>, which is what this pins.
    /// </remarks>
    [Fact]
    public void SysStatus_ReadsAnUnmeasuredBatteryAsMinusOne()
    {
        MavlinkFrame frame = MavlinkFrames.FromVector("sys_status");

        SysStatusMessage message = SysStatusMessage.Read(frame.Payload.Span);

        Assert.Equal(SysStatusMessage.BatteryRemainingUnmeasured, message.BatteryRemainingPercent);
        Assert.Equal(-1, message.BatteryRemainingPercent);
    }

    /// <summary>A measured battery survives as itself, which the -1 vector alone cannot show.</summary>
    /// <remarks>
    /// Without this, a decoder that mapped every value of the field to "unmeasured" would pass the
    /// case above and lose the battery reading on every real flight.
    /// </remarks>
    [Fact]
    public void SysStatus_ReadsAMeasuredBatteryAsItsPercentage()
    {
        MavlinkFrame frame = MavlinkFrames.FromVector("sys_status_battery");

        SysStatusMessage message = SysStatusMessage.Read(frame.Payload.Span);

        Assert.Equal(73, message.BatteryRemainingPercent);
    }

    // --- VFR_HUD --------------------------------------------------------------------------------

    [Fact]
    public void VfrHud_DecodesEveryField()
    {
        //  The only message of the four built from floats, so the only one where a byte-order or
        //  width mistake produces a plausible number rather than an obvious one.
        JsonElement expected = MavlinkVectors.Named("vfr_hud").Fields;
        MavlinkFrame frame = MavlinkFrames.FromVector("vfr_hud");

        VfrHudMessage message = VfrHudMessage.Read(frame.Payload.Span);

        Assert.Equal(expected.GetProperty("airspeed").GetSingle(), message.AirspeedMetersPerSecond);
        Assert.Equal(
            expected.GetProperty("groundspeed").GetSingle(), message.GroundSpeedMetersPerSecond);
        Assert.Equal(expected.GetProperty("alt").GetSingle(), message.AltitudeMetersMsl);
        Assert.Equal(expected.GetProperty("climb").GetSingle(), message.ClimbRateMetersPerSecond);
        Assert.Equal(expected.GetProperty("heading").GetInt16(), message.HeadingDegrees);
        Assert.Equal(expected.GetProperty("throttle").GetUInt16(), message.ThrottlePercent);
    }

    /// <summary>Airspeed and ground speed are not interchangeable and are not transposed.</summary>
    /// <remarks>
    /// Adjacent floats of the same width, both plausible speeds, and MCS-001 asks for one of them.
    /// In any wind they differ, which is the whole reason a vehicle reports both -- so a transposed
    /// pair is a console that is quietly wrong exactly when the difference matters.
    /// </remarks>
    [Fact]
    public void VfrHud_KeepsAirspeedAndGroundSpeedApart()
    {
        MavlinkFrame frame = MavlinkFrames.FromVector("vfr_hud");

        VfrHudMessage message = VfrHudMessage.Read(frame.Payload.Span);

        Assert.Equal(23.75f, message.AirspeedMetersPerSecond);
        Assert.Equal(21.5f, message.GroundSpeedMetersPerSecond);
    }

    // --- The framing contract these decoders rely on ---------------------------------------------

    /// <summary>
    /// A payload shorter than the definition throws rather than reading whatever follows it.
    /// </summary>
    /// <remarks>
    /// Unreachable from the parser, which zero-extends to the declared length before a frame is
    /// handed out -- so this is an assertion about the boundary between the two layers, not about
    /// anything a vehicle can send. It throws instead of counting because a broken internal
    /// invariant is a loud failure, and the whole point of keeping framing and semantics apart is
    /// that the quiet failures do not end up where the loud ones are.
    /// </remarks>
    [Fact]
    public void Read_ThrowsWhenThePayloadIsShorterThanTheDefinition()
    {
        byte[] tooShort = new byte[GlobalPositionIntMessage.PayloadLength - 1];

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => GlobalPositionIntMessage.Read(tooShort));

        Assert.Equal("payload", ex.ParamName);
        Assert.Contains(nameof(GlobalPositionIntMessage), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A payload <i>longer</i> than the definition is read from the front, not rejected.
    /// </summary>
    /// <remarks>
    /// This is what arrives from a newer sender: v2 extension fields are excluded from
    /// <c>CRC_EXTRA</c> by design, so the frame validates against this station's older seed and
    /// carries bytes past the declared length. SYS_STATUS has grown three such fields since the
    /// dialect these vectors came from, so rejecting the longer form would break exactly one message
    /// type against current firmware and leave the rest working -- the per-message failure this
    /// codec is arranged to prevent.
    /// </remarks>
    [Fact]
    public void Read_IgnoresBytesPastTheDefinition()
    {
        MavlinkVector vector = MavlinkVectors.Named("sys_status_battery");

        byte[] extended = [.. vector.FullPayload, 0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Equal(
            SysStatusMessage.Read(vector.FullPayload), SysStatusMessage.Read(extended));
    }
}
