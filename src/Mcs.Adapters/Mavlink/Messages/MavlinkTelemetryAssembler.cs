using System.Diagnostics.CodeAnalysis;

using Mcs.Core;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// One vehicle's running state, folded from the messages it sends and emitted as
/// <see cref="VehicleTelemetry"/> when it is renderable.
/// </summary>
/// <remarks>
/// <b>MAVLink does not send a telemetry frame.</b> It sends several messages on independent
/// schedules, so "the vehicle's current state" is something the station composes rather than
/// something it receives -- and the composition rules below are the substance of this type.
///
/// <para>
/// <b>One source per field, chosen once.</b> There is real overlap in the message set, and the
/// alternative to choosing is preferring whichever arrived last, which makes a field's provenance a
/// function of link timing:
/// <list type="bullet">
/// <item><description>
/// <b>Position and altitude from GLOBAL_POSITION_INT.</b> VFR_HUD carries an altitude too, but
/// pairing it with a latitude from a different message means the height and the point it is shown
/// at were estimated at different instants.
/// </description></item>
/// <item><description>
/// <b>Ground speed and heading from VFR_HUD.</b> GLOBAL_POSITION_INT could supply both -- speed as
/// <c>sqrt(vx² + vy²)</c> and heading from its centidegree <c>hdg</c> field, which is finer than
/// VFR_HUD's whole degrees and estimated at the same instant as the position. It was still rejected:
/// it adds a second source for two fields that already have one, and the derived angle is the trap
/// rather than the win -- velocity components give <i>course over ground</i>, which differs from
/// heading in any wind and which <see cref="VehicleTelemetry.HeadingDegrees"/> explicitly forbids
/// putting in heading's place. Taking both from the message an autopilot publishes for a display
/// keeps the pair coherent and the units already correct.
/// </description></item>
/// <item><description>
/// <b>Battery from SYS_STATUS's percentage</b>, never derived from its voltage. See
/// <see cref="SysStatusMessage"/>.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Emit on GLOBAL_POSITION_INT.</b> Emitting on every inbound message multiplies the frame rate
/// by the number of message types the vehicle happens to send, which makes the console's update rate
/// a property of the sender's configuration. Emitting on a timer decouples the console from the link
/// and puts a frame on screen at a moment nothing arrived. Position is the field that makes a frame
/// renderable at all, it arrives at the rate the operator cares about, and emitting on it keeps the
/// receipt timestamp tied to the position it stamps.
/// </para>
///
/// <para>
/// <b>A position is enough on its own.</b> Position and altitude are the fields
/// <see cref="VehicleTelemetry"/> refuses to be built without, and everything else on it is nullable
/// -- so a position that arrives before any VFR_HUD emits immediately, with speed and heading null,
/// and the vehicle appears on the map at the place it actually is with dashes where its speed and
/// heading will go. The alternative, holding the report back until a HUD arrives, keeps a vehicle
/// whose position is known entirely off the console; the one thing worse than an incomplete picture
/// is no picture of something that is flying.
/// </para>
///
/// <para>
/// <b>What is never done is substituting a number for an absent one.</b> Zeroes would draw a vehicle
/// stationary and pointing true north -- a confident claim the data does not support, and the
/// clamped-battery failure by another road. Absence travels as null the whole way to the console,
/// which renders it as a marker with no nose.
/// </para>
///
/// <para>
/// <b>What this does not yet do is age the fields it carries.</b> Once a VFR_HUD has been seen it is
/// carried onto every later position, so a sender whose HUD stops -- a stream rate renegotiated to
/// zero, or a ground speed gone NaN so every later HUD is rejected -- produces reports stamped now
/// that carry a heading from minutes ago. Frame staleness cannot catch it, because the frame really
/// is fresh; only the fields inside it are not. The fix is to compare each field's arrival against
/// the same threshold MCS-002 already sets for the frame as a whole -- one mechanism at a finer
/// granularity, not a second one -- and it belongs with the staleness work, where that threshold is
/// sourced rather than picked. Until then the behaviour is pinned by a test named for it, so closing
/// it is a decision someone makes on purpose.
/// </para>
///
/// <para>
/// <b>No clock, deliberately.</b> This type takes no <see cref="TimeProvider"/> and returns a
/// <see cref="VehicleTelemetry"/> rather than a <see cref="TelemetryFrame"/>, so it has no way to
/// stamp anything. The caller reads the station clock at arrival and exchanges the receipt for a
/// frame after decoding -- <c>BeginReceive()</c>, decode, <c>Complete(telemetry)</c> -- which is
/// what keeps decode cost measured as <see cref="TelemetryReceipt.IngestDelay"/> instead of baked
/// invisibly into the recorded age of the data (MCS-005). The vehicle's own
/// <see cref="GlobalPositionIntMessage.TimeBootMilliseconds"/> is read and never used for anything.
/// </para>
///
/// <para>
/// <b>Not thread-safe</b>, matching the parser it is fed from: one assembler per vehicle, driven by
/// the one loop reading that link.
/// </para>
/// </remarks>
/// <param name="vehicleId">The vehicle these messages describe, derived from the sending system id.</param>
/// <param name="statistics">The decoder's shared counters. Nothing here logs; see the type's remarks.</param>
internal sealed class MavlinkTelemetryAssembler(
    VehicleId vehicleId, MavlinkDecoderStatistics statistics)
{
    /// <summary>MAVLink carries latitude and longitude as degrees scaled by 1e7.</summary>
    private const double DegreesPerE7 = 1e7;

    private const double MillimetersPerMeter = 1000.0;

    private const sbyte MaxBatteryPercent = 100;

    //  Only VFR_HUD and the battery are held. A position is not: it is the message that emits, so
    //  it is consumed on arrival and there is no later reader for a stored copy. Keeping one anyway
    //  -- "the latest value of every field" -- would be state that only ever goes stale.
    private VfrHudMessage? _hud;

    //  Null covers both "no SYS_STATUS yet" and "SYS_STATUS said unmeasured". They are the same
    //  fact to everything downstream -- the station has no battery reading to show -- and splitting
    //  them would put a distinction in the model that nothing can act on.
    private double? _batteryPercent;

    /// <summary>
    /// Folds one frame into this vehicle's state, and emits telemetry if the frame completed a
    /// renderable picture.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> on any GLOBAL_POSITION_INT the station will represent. Everything else
    /// -- a message that only updated state, and one that was rejected -- returns
    /// <see langword="false"/> and is counted.
    /// </returns>
    internal bool TryAdd(MavlinkFrame frame, [NotNullWhen(true)] out VehicleTelemetry? telemetry)
    {
        telemetry = null;

        ReadOnlySpan<byte> payload = frame.Payload.Span;

        //  Only the four ids below can arrive: framing skips any message it has no CRC_EXTRA seed
        //  for, and the seed table holds exactly these. The default arm is therefore unreachable
        //  from the parser and kept anyway, because "unreachable" is a claim about another file.
        switch (frame.MessageId)
        {
            case MavlinkMessageId.Heartbeat:
                return AddHeartbeat(payload);

            case MavlinkMessageId.SysStatus:
                return AddSysStatus(payload);

            case MavlinkMessageId.VfrHud:
                return AddVfrHud(payload);

            case MavlinkMessageId.GlobalPositionInt:
                return AddGlobalPosition(payload, out telemetry);

            default:
                statistics.MessagesRejected++;
                return false;
        }
    }

    private bool AddHeartbeat(ReadOnlySpan<byte> payload)
    {
        //  Decoded and not retained. Nothing in VehicleTelemetry comes from a heartbeat: presence is
        //  the fact that one arrived, and the link state an operator sees is staleness measured
        //  against the station clock (MCS-002), not anything a vehicle claims about itself. Holding
        //  the message would be state that nothing reads. It is still decoded rather than skipped,
        //  because a heartbeat is what brings a vehicle into the fleet before it can be drawn, and
        //  because the arm-state and fault work reads base_mode from exactly here.
        _ = HeartbeatMessage.Read(payload);

        statistics.MessagesDecoded++;
        return false;
    }

    private bool AddSysStatus(ReadOnlySpan<byte> payload)
    {
        SysStatusMessage status = SysStatusMessage.Read(payload);

        //  -1 is the wire's own "unmeasured", and the reason BatteryPercent is nullable at all.
        if (status.BatteryRemainingPercent == SysStatusMessage.BatteryRemainingUnmeasured)
        {
            _batteryPercent = null;
            statistics.MessagesDecoded++;
            return false;
        }

        //  Any other out-of-band value is a broken sender, so the message goes and the last
        //  representable reading stays. Clamping 127 to 100 would put a believable number in front
        //  of an operator; blanking the battery instead would let a corrupt message erase what a
        //  merely absent one does not.
        if (status.BatteryRemainingPercent is < 0 or > MaxBatteryPercent)
        {
            statistics.MessagesRejected++;
            return false;
        }

        _batteryPercent = status.BatteryRemainingPercent;
        statistics.MessagesDecoded++;
        return false;
    }

    private bool AddVfrHud(ReadOnlySpan<byte> payload)
    {
        VfrHudMessage hud = VfrHudMessage.Read(payload);

        //  NaN and negative both reach VehicleTelemetry.Create as an exception, and an exception
        //  thrown out of a decode is a datagram's worth of good frames lost to one bad field. The
        //  check belongs here, where the unit of loss is one message. Heading needs no equivalent:
        //  it is an int16, so every value it can hold is finite and Create normalises the range.
        if (!float.IsFinite(hud.GroundSpeedMetersPerSecond) || hud.GroundSpeedMetersPerSecond < 0)
        {
            statistics.MessagesRejected++;
            return false;
        }

        _hud = hud;
        statistics.MessagesDecoded++;
        return false;
    }

    private bool AddGlobalPosition(
        ReadOnlySpan<byte> payload, [NotNullWhen(true)] out VehicleTelemetry? telemetry)
    {
        telemetry = null;

        GlobalPositionIntMessage position = GlobalPositionIntMessage.Read(payload);

        double latitude = position.LatitudeDegreesE7 / DegreesPerE7;
        double longitude = position.LongitudeDegreesE7 / DegreesPerE7;

        //  int32 scaled by 1e7 reaches ±214.7 degrees, so both bounds are genuinely reachable from
        //  a sender that has its scaling wrong -- and a longitude of 200 is the kind of value that
        //  renders somewhere rather than failing.
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            statistics.MessagesRejected++;
            return false;
        }

        statistics.MessagesDecoded++;

        //  Counted, not withheld. A sender that never emits VFR_HUD is otherwise indistinguishable
        //  from one whose speed and heading are genuinely unavailable for a moment, and the console
        //  cannot tell the difference from a dash either -- so the number is what says which.
        if (_hud is null)
        {
            statistics.PositionsWithoutHud++;
        }

        //  MSL, declared. The reference is not on the wire -- it is in the field's name, in a
        //  document -- and this is the line where it stops being implicit (MCS-004). MSL rather than
        //  relative_alt because it is absolute: it does not depend on where the vehicle was armed,
        //  and relative_alt is height above the home point, which is not AGL and is not relabelled
        //  as such anywhere in this station.
        Altitude altitude = Altitude.FromMeters(
            position.AltitudeMillimetersMsl / MillimetersPerMeter, AltitudeReference.Msl);

        telemetry = VehicleTelemetry.Create(
            vehicleId,
            latitude,
            longitude,
            altitude,

            //  Null until a HUD has been seen, and never a stand-in value. The console draws a
            //  marker with no nose rather than one pointing north by default.
            _hud?.GroundSpeedMetersPerSecond,
            _hud?.HeadingDegrees,
            _batteryPercent,

            //  Healthy, always, from this path. The parser only ever sees frames that arrived, so
            //  it holds no evidence of a degraded link to report; SYS_STATUS's drop rate counts what
            //  the vehicle dropped at the other end. "Lost" is staleness against the station clock
            //  (MCS-002), and two mechanisms deciding a vehicle is gone will eventually disagree --
            //  the one an operator sees must be the one tied to the station's own clock.
            LinkStatus.Healthy);

        statistics.TelemetryEmitted++;
        return true;
    }
}
