using System.Buffers.Binary;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// SYS_STATUS (id 1): battery state, and the sensor health bitmasks the fault work will read.
/// </summary>
/// <remarks>
/// <b>Battery level, not battery voltage.</b> MCS-001 asks for a level, and
/// <see cref="BatteryRemainingPercent"/> is the only field here that is one. Voltage is carried
/// because it is genuinely diagnostic, and it is not substituted when the percentage is missing: a
/// pack's voltage curve is flat across most of its useful charge, so a number derived from it would
/// be an estimate presented in the place an operator reads a measurement.
/// <para>
/// <b><see cref="BatteryRemainingPercent"/> is <c>int8</c> and -1 means unmeasured</b>, which is the
/// distinction the whole nullable <see cref="Mcs.Core.VehicleTelemetry.BatteryPercent"/> exists for.
/// Read unsigned it is 255; clamped it is 0 -- a flat battery, and the one reading that would make
/// an operator abort. The mapping to "unreported" happens in
/// <see cref="MavlinkTelemetryAssembler"/>; this type reports the wire value as it stands.
/// </para>
/// <para>
/// <b>The health bitmasks are read and deliberately go nowhere.</b> Fault flags are stubbed, and
/// the stub is exactly this: the bits are decoded, they are carried, and nothing consumes them. A
/// stub that mapped them onto <see cref="Mcs.Core.LinkStatus"/> would be worse than none -- sensor
/// health is not link health, and an operator reading "degraded link" from a failed magnetometer
/// would go and check the radio.
/// </para>
/// <para>
/// This message has since grown three v2 extension fields. They are excluded from
/// <c>CRC_EXTRA</c> by design, so a frame carrying them validates against this station's seed and
/// arrives longer than <see cref="PayloadLength"/> -- which is read from the front and otherwise
/// ignored, exactly as the format instructs.
/// </para>
/// </remarks>
internal readonly record struct SysStatusMessage
{
    /// <summary>The declared payload length, checked against the framing table by the test suite.</summary>
    internal const int PayloadLength = 31;

    /// <summary>The value <see cref="BatteryRemainingPercent"/> carries when no charge estimate exists.</summary>
    internal const sbyte BatteryRemainingUnmeasured = -1;

    /// <summary>Gets the bitmask of sensors present, a <c>MAV_SYS_STATUS_SENSOR</c>. Stubbed -- unread.</summary>
    public required uint SensorsPresent { get; init; }

    /// <summary>Gets the bitmask of sensors enabled. Stubbed -- unread.</summary>
    public required uint SensorsEnabled { get; init; }

    /// <summary>Gets the bitmask of sensors reporting healthy. Stubbed -- decoded and unread.</summary>
    public required uint SensorsHealth { get; init; }

    /// <summary>Gets mainloop load in tenths of a percent, so 1000 is fully loaded.</summary>
    public required ushort LoadTenthsOfPercent { get; init; }

    /// <summary>Gets pack voltage in millivolts, or <see cref="ushort.MaxValue"/> if unmeasured.</summary>
    public required ushort BatteryVoltageMillivolts { get; init; }

    /// <summary>Gets pack current in centiamps, or -1 if unmeasured.</summary>
    public required short BatteryCurrentCentiAmps { get; init; }

    /// <summary>
    /// Gets the packet drop rate on all links in hundredths of a percent.
    /// </summary>
    /// <remarks>
    /// The most tempting field in the message and still not a source for
    /// <see cref="Mcs.Core.LinkStatus"/>: it counts what the <i>vehicle</i> dropped on its own
    /// links, which is the other direction and the other end from the one an operator watching this
    /// station is looking at. Deriving a link state from it would need a threshold nothing in the
    /// requirements supplies.
    /// </remarks>
    public required ushort CommDropRateCentiPercent { get; init; }

    /// <summary>Gets the count of communication errors. Stubbed -- unread.</summary>
    public required ushort ErrorsComm { get; init; }

    /// <summary>Gets the first autopilot-specific error count. Stubbed -- unread.</summary>
    public required ushort ErrorsCount1 { get; init; }

    /// <summary>Gets the second autopilot-specific error count. Stubbed -- unread.</summary>
    public required ushort ErrorsCount2 { get; init; }

    /// <summary>Gets the third autopilot-specific error count. Stubbed -- unread.</summary>
    public required ushort ErrorsCount3 { get; init; }

    /// <summary>Gets the fourth autopilot-specific error count. Stubbed -- unread.</summary>
    public required ushort ErrorsCount4 { get; init; }

    /// <summary>
    /// Gets remaining charge as a percentage from 0 to 100, or
    /// <see cref="BatteryRemainingUnmeasured"/>. Signed -- see the remarks on this type.
    /// </summary>
    public required sbyte BatteryRemainingPercent { get; init; }

    /// <summary>Reads the fields from a payload the framing layer has already restored to length.</summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is shorter than <see cref="PayloadLength"/>.</exception>
    internal static SysStatusMessage Read(ReadOnlySpan<byte> payload)
    {
        MavlinkPayload.EnsureLength(payload, PayloadLength, nameof(SysStatusMessage));

        return new SysStatusMessage
        {
            SensorsPresent = BinaryPrimitives.ReadUInt32LittleEndian(payload),
            SensorsEnabled = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            SensorsHealth = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            LoadTenthsOfPercent = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]),
            BatteryVoltageMillivolts = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]),
            BatteryCurrentCentiAmps = BinaryPrimitives.ReadInt16LittleEndian(payload[16..]),
            CommDropRateCentiPercent = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
            ErrorsComm = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]),
            ErrorsCount1 = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]),
            ErrorsCount2 = BinaryPrimitives.ReadUInt16LittleEndian(payload[24..]),
            ErrorsCount3 = BinaryPrimitives.ReadUInt16LittleEndian(payload[26..]),
            ErrorsCount4 = BinaryPrimitives.ReadUInt16LittleEndian(payload[28..]),

            //  The last byte, and the only signed 8-bit field the station reads. Cast rather than
            //  indexed into an sbyte span so the sign survives: payload[30] is a byte, and 0xFF
            //  assigned through it would be 255.
            BatteryRemainingPercent = (sbyte)payload[30],
        };
    }
}
