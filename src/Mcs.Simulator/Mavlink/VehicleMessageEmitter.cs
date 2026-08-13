using Mcs.Adapters.Mavlink;
using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// Turns the aircraft's state into the MAVLink frames that are due at this instant: four streams on
/// four independent schedules, framed with the station's own serialiser.
/// </summary>
/// <remarks>
/// <b>What the vehicle says, separated from how it gets there.</b> This type produces bytes and
/// owns no socket; <see cref="MavlinkTransmitter"/> owns the socket and knows nothing about
/// messages. The split is not a testing seam bolted on afterwards -- it is the reason the
/// rate, sequence and payload behaviour can be asserted against exact byte values with no network
/// in the test at all, and the reason a link problem cannot be mistaken for an encoding one.
///
/// <para>
/// <b>Framing comes from <c>MavlinkFrameWriter</c>, the station's own serialiser, and the payloads
/// do not.</b> That is deliberate and it is what the loop-closing test rests on: the payload
/// writers here were written against the message definitions independently of the station's
/// readers, so a field at the wrong offset in one cannot cancel against the same mistake in the
/// other. Framing is the opposite case -- both sides share this code, so a framing bug would
/// cancel exactly, which is why the committed pymavlink vectors remain the only evidence for it.
/// </para>
///
/// <para>
/// <b>Sequence numbers belong here</b> rather than to the serialiser, which is stateless on
/// purpose. One counter for this component, incremented per frame sent and wrapping at 255, which
/// is what lets a receiver count what it lost. A wrong wrap is invisible until something starts
/// counting drops, and then it reads as a link fault.
/// </para>
///
/// <para><b>Not thread-safe.</b> One emitter per vehicle, driven by the one loop flying it.</para>
/// </remarks>
internal sealed class VehicleMessageEmitter
{
    private readonly byte _systemId;
    private readonly byte _componentId;
    private readonly double _homeAltitudeMetersMsl;
    private readonly SimulatorStatistics _statistics;

    private readonly MessageSchedule _heartbeat;
    private readonly MessageSchedule _sysStatus;
    private readonly MessageSchedule _vfrHud;
    private readonly MessageSchedule _globalPosition;

    private byte _sequence;

    /// <summary>Builds the emitter and its four schedules.</summary>
    /// <param name="systemId">This vehicle's MAVLink system id; the station derives its id from it.</param>
    /// <param name="componentId">The emitting component's id.</param>
    /// <param name="rates">How often each stream sends.</param>
    /// <param name="homeAltitudeMetersMsl">
    /// What GLOBAL_POSITION_INT's <c>relative_alt</c> is measured from: the first waypoint, which
    /// is where this aircraft is treated as having armed.
    /// </param>
    /// <param name="statistics">The counters this emitter fills in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="statistics"/> is <see langword="null"/>.</exception>
    internal VehicleMessageEmitter(
        byte systemId,
        byte componentId,
        MessageRates rates,
        double homeAltitudeMetersMsl,
        SimulatorStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        _systemId = systemId;
        _componentId = componentId;
        _homeAltitudeMetersMsl = homeAltitudeMetersMsl;
        _statistics = statistics;

        _heartbeat = new MessageSchedule(rates.HeartbeatHz);
        _sysStatus = new MessageSchedule(rates.SysStatusHz);
        _vfrHud = new MessageSchedule(rates.VfrHudHz);
        _globalPosition = new MessageSchedule(rates.GlobalPositionHz);
    }

    /// <summary>
    /// Returns the frames due at <paramref name="elapsedSeconds"/>, in the order they should go out.
    /// </summary>
    /// <remarks>
    /// A fresh list per call, allocated a few dozen times a second. The alternative -- a buffer
    /// reused between calls -- would hand the caller memory that the next tick overwrites, which is
    /// a trap for a caller that queues rather than sends, in exchange for an allocation this
    /// process will never notice.
    /// </remarks>
    /// <param name="elapsedSeconds">Simulated seconds since the flight began.</param>
    /// <param name="state">The aircraft's current state.</param>
    internal List<byte[]> FramesDue(double elapsedSeconds, in AircraftState state)
    {
        List<byte[]> frames = [];

        //  time_boot_ms, from the same simulated clock everything else here runs on. The station
        //  reads it and stamps nothing with it, so its only job is to be self-consistent.
        uint timeBootMilliseconds = (uint)(elapsedSeconds * 1000.0);

        if (_heartbeat.IsDue(elapsedSeconds))
        {
            Span<byte> payload = stackalloc byte[HeartbeatPayload.PayloadLength];
            HeartbeatPayload.Write(payload);
            frames.Add(Frame(VehicleMessageId.Heartbeat, payload));
            _statistics.HeartbeatsSent++;
        }

        if (_sysStatus.IsDue(elapsedSeconds))
        {
            Span<byte> payload = stackalloc byte[SysStatusPayload.PayloadLength];
            SysStatusPayload.Write(payload, state);
            frames.Add(Frame(VehicleMessageId.SysStatus, payload));
            _statistics.SysStatusesSent++;
        }

        if (_vfrHud.IsDue(elapsedSeconds))
        {
            Span<byte> payload = stackalloc byte[VfrHudPayload.PayloadLength];
            VfrHudPayload.Write(payload, state);
            frames.Add(Frame(VehicleMessageId.VfrHud, payload));
            _statistics.VfrHudsSent++;
        }

        //  Position last of the four. The station emits telemetry on a position and folds the rest
        //  into a running state, so when a HUD and a position fall on the same instant this order
        //  is the one where the report carries the speed and heading estimated at that instant
        //  rather than the previous ones. The rates are not multiples of each other, so most
        //  positions coincide with no HUD at all and the carry-forward path is exercised anyway.
        if (_globalPosition.IsDue(elapsedSeconds))
        {
            Span<byte> payload = stackalloc byte[GlobalPositionIntPayload.PayloadLength];
            GlobalPositionIntPayload.Write(
                payload, state, timeBootMilliseconds, _homeAltitudeMetersMsl);
            frames.Add(Frame(VehicleMessageId.GlobalPositionInt, payload));
            _statistics.PositionsSent++;
        }

        return frames;
    }

    /// <summary>Frames one payload and consumes a sequence number.</summary>
    private byte[] Frame(uint messageId, ReadOnlySpan<byte> payload)
    {
        byte[] frame = MavlinkFrameWriter.Write(
            messageId, payload, _sequence, _systemId, _componentId);

        //  Unchecked, because wrapping past 255 is the specified behaviour rather than an overflow.
        //  A checked context here would crash the aircraft after 256 frames.
        unchecked
        {
            _sequence++;
        }

        return frame;
    }
}
