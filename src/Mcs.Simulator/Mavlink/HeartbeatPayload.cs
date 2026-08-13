using System.Buffers.Binary;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// Writes a HEARTBEAT (id 0) payload: what this vehicle is, and that it is still here.
/// </summary>
/// <remarks>
/// <b>Every field here is a constant, and that is the message.</b> A heartbeat carries no state
/// that changes in flight for this simulator -- the airframe does not change type and the mode
/// does not change without a command, which is M2's. What makes it worth sending is that it
/// arrives: presence is the fact, and the station's link status is staleness measured against its
/// own clock rather than anything claimed in here.
/// <para>
/// The field order is <c>custom_mode</c> first, which is not the order the fields are listed in
/// the message definition: MAVLink orders a payload by descending field width, so the
/// <c>uint32</c> leads and the five <c>uint8</c>s follow. Writing them in declaration order
/// produces a payload that passes its own checksum and decodes to nonsense, which is exactly the
/// mistake the committed byte vectors exist to catch.
/// </para>
/// </remarks>
internal static class HeartbeatPayload
{
    /// <summary>The full payload length HEARTBEAT declares, before v2 truncation.</summary>
    internal const int PayloadLength = 9;

    /// <summary><c>MAV_TYPE_FIXED_WING</c>. The airframe this envelope describes.</summary>
    private const byte FixedWing = 1;

    /// <summary>
    /// <c>MAV_AUTOPILOT_GENERIC</c>. Not ArduPilot and not PX4: claiming either would invite a
    /// receiver to interpret <c>custom_mode</c> against a mode table this vehicle does not
    /// implement.
    /// </summary>
    private const byte GenericAutopilot = 0;

    /// <summary>
    /// <c>MAV_MODE_FLAG_SAFETY_ARMED | MAV_MODE_FLAG_STABILIZE_ENABLED |
    /// MAV_MODE_FLAG_GUIDED_ENABLED</c>: armed, stabilised, flying a route.
    /// </summary>
    /// <remarks>
    /// Armed always, and the aircraft is airborne from the first frame. There is no ground state to
    /// model because there is no takeoff: arming and mode changes arrive with the command
    /// lifecycle, and a simulator that pretended to sit disarmed would need a command it cannot yet
    /// receive to ever start flying.
    /// </remarks>
    private const byte BaseMode = 128 | 16 | 8;

    /// <summary><c>MAV_STATE_ACTIVE</c>: powered, armed, and doing something.</summary>
    private const byte SystemStatusActive = 4;

    /// <summary>The version field a v2 sender carries. Always 3.</summary>
    private const byte MavlinkVersion = 3;

    /// <summary>Writes the payload.</summary>
    /// <param name="destination">Exactly <see cref="PayloadLength"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    internal static void Write(Span<byte> destination)
    {
        MavlinkPayloadBuffer.EnsureLength(destination, PayloadLength, nameof(HeartbeatPayload));

        //  custom_mode: meaningless without an autopilot family to interpret it, and this vehicle
        //  reports the generic family, so zero is the only honest value.
        BinaryPrimitives.WriteUInt32LittleEndian(destination, 0);

        destination[4] = FixedWing;
        destination[5] = GenericAutopilot;
        destination[6] = BaseMode;
        destination[7] = SystemStatusActive;
        destination[8] = MavlinkVersion;
    }
}
