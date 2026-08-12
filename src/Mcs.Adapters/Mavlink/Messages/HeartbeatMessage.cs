using System.Buffers.Binary;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// HEARTBEAT (id 0): a vehicle announcing that it exists, what it is, and what mode it is in.
/// </summary>
/// <remarks>
/// <b>Presence, not liveness.</b> A heartbeat is evidence that a frame arrived, and nothing here
/// turns that into a judgement about the link -- that judgement is staleness, it is measured against
/// the station clock (MCS-002), and it lives in <c>Mcs.Core</c> rather than in any adapter. Two
/// mechanisms deciding a vehicle is gone will eventually disagree, and the one an operator sees must
/// be the one tied to the station's own clock.
/// <para>
/// Every component on a vehicle emits this -- autopilot, gimbal, companion computer -- so a
/// heartbeat is the one message of the four that routinely arrives from something that is not the
/// thing being flown. <see cref="MavlinkTelemetryDecoder"/> handles that by keying on the sender
/// pair rather than by filtering here.
/// </para>
/// <para>
/// Fields are the wire's own values under names that carry their units, and no conversion happens
/// here. The assembler converts, because a message type that both re-expressed and reinterpreted its
/// payload would have no form in which it could be compared against the reference vectors.
/// </para>
/// </remarks>
internal readonly record struct HeartbeatMessage
{
    /// <summary>The declared payload length, checked against the framing table by the test suite.</summary>
    internal const int PayloadLength = 9;

    /// <summary>Gets the autopilot-specific flight mode. Meaningless without <see cref="Autopilot"/>.</summary>
    public required uint CustomMode { get; init; }

    /// <summary>
    /// Gets the airframe class, a <c>MAV_TYPE</c>. Not interpreted -- the station draws no
    /// distinction by airframe yet, and inventing one here would put a guess in the model.
    /// </summary>
    public required byte VehicleType { get; init; }

    /// <summary>Gets the autopilot family, a <c>MAV_AUTOPILOT</c>.</summary>
    public required byte Autopilot { get; init; }

    /// <summary>Gets the mode flags, a <c>MAV_MODE_FLAG</c> bitmask -- armed state lives in bit 7.</summary>
    public required byte BaseMode { get; init; }

    /// <summary>Gets the system status, a <c>MAV_STATE</c>.</summary>
    public required byte SystemStatus { get; init; }

    /// <summary>
    /// Gets the MAVLink version the sender speaks. Always 3 from a v2 sender, and read rather than
    /// assumed because it is the one field of this message that says anything about the protocol.
    /// </summary>
    public required byte MavlinkVersion { get; init; }

    /// <summary>Reads the fields from a payload the framing layer has already restored to length.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="payload"/> is shorter than <see cref="PayloadLength"/>, which means the
    /// framing layer's zero-extension contract was broken rather than that a vehicle sent something
    /// odd -- a v2 sender cannot produce a short payload that survives its checksum.
    /// </exception>
    internal static HeartbeatMessage Read(ReadOnlySpan<byte> payload)
    {
        MavlinkPayload.EnsureLength(payload, PayloadLength, nameof(HeartbeatMessage));

        return new HeartbeatMessage
        {
            CustomMode = BinaryPrimitives.ReadUInt32LittleEndian(payload),
            VehicleType = payload[4],
            Autopilot = payload[5],
            BaseMode = payload[6],
            SystemStatus = payload[7],
            MavlinkVersion = payload[8],
        };
    }
}
