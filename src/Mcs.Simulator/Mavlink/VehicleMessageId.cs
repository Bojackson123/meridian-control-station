namespace Mcs.Simulator.Mavlink;

/// <summary>
/// The MAVLink message ids this vehicle emits, and the payload length each definition declares.
/// </summary>
/// <remarks>
/// <b>A second table, on purpose.</b> <c>Mcs.Adapters</c> has one of these -- the ids the
/// <i>station</i> has a decoder and a <c>CRC_EXTRA</c> seed for -- and it is internal. Reaching
/// into it with an <c>InternalsVisibleTo</c> was the obvious saving and was rejected: the two
/// tables answer different questions, and a simulator that read the station's answer could not be
/// used to check it. Four ids and four lengths transcribed from the message definitions is a
/// morning's care, and the byte-level test that closes the loop only means something because the
/// two sides were written apart.
/// <para>
/// The lengths are the <i>full</i>, untruncated payload each message declares.
/// <c>MavlinkFrameWriter</c> requires exactly that and applies v2's trailing-zero truncation
/// itself, which is what keeps the bytes on the wire identical to the reference implementation's
/// for the same field values.
/// </para>
/// <para>
/// <b>The station's table bounds this one.</b> <c>MavlinkFrameWriter.Write</c> refuses any id it
/// has no seed for, so this vehicle cannot emit a message the station cannot read. That is a real
/// coupling and the right one for now: a simulator whose traffic the station discards would be
/// testing the discard path and nothing else.
/// </para>
/// </remarks>
internal static class VehicleMessageId
{
    /// <summary>HEARTBEAT: this vehicle exists, is this type, and is in this mode.</summary>
    internal const uint Heartbeat = 0;

    /// <summary>SYS_STATUS: battery state and the sensor health bitmasks.</summary>
    internal const uint SysStatus = 1;

    /// <summary>GLOBAL_POSITION_INT: the fused position estimate. The message that puts a marker on the map.</summary>
    internal const uint GlobalPositionInt = 33;

    /// <summary>VFR_HUD: the values an autopilot publishes for a head-up display.</summary>
    internal const uint VfrHud = 74;
}
