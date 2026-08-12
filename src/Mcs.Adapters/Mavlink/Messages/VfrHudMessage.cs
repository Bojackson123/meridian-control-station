using System.Buffers.Binary;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// VFR_HUD (id 74): the values an autopilot publishes for a head-up display, which are the two the
/// console needs in the units it wants them.
/// </summary>
/// <remarks>
/// <b>This is the station's source for ground speed and heading</b>, and the reasons are on
/// <see cref="MavlinkTelemetryAssembler"/> where the choice is made rather than here.
/// <para>
/// <b><see cref="HeadingDegrees"/> is heading, not course over ground.</b> It is where the nose
/// points; course is the direction of travel, and in any wind they differ. That distinction is why
/// this field is preferred over deriving an angle from GLOBAL_POSITION_INT's velocity components --
/// that arithmetic yields course, and <see cref="Mcs.Core.VehicleTelemetry.HeadingDegrees"/>
/// explicitly forbids putting one in the other's place.
/// </para>
/// <para>
/// <b>Four IEEE-754 floats, and they are the reason this message is worth a vector of its own.</b>
/// Everything else the station decodes is an integer, so this is the only place a byte-order or
/// width mistake shows up as a plausible-looking number rather than as an obvious one.
/// </para>
/// <para>
/// <see cref="AltitudeMetersMsl"/> is decoded and deliberately unused: altitude reaches telemetry
/// from GLOBAL_POSITION_INT, so that the altitude an operator reads was estimated at the same
/// instant as the latitude and longitude it is shown beside.
/// </para>
/// </remarks>
internal readonly record struct VfrHudMessage
{
    /// <summary>The declared payload length, checked against the framing table by the test suite.</summary>
    internal const int PayloadLength = 20;

    /// <summary>Gets indicated airspeed in metres per second. Not ground speed -- carried, unused.</summary>
    public required float AirspeedMetersPerSecond { get; init; }

    /// <summary>Gets speed over the ground in metres per second. The console's ground speed.</summary>
    public required float GroundSpeedMetersPerSecond { get; init; }

    /// <summary>Gets altitude above mean sea level in metres. Unused -- see the remarks.</summary>
    public required float AltitudeMetersMsl { get; init; }

    /// <summary>Gets climb rate in metres per second; negative is descending. Carried, unused.</summary>
    public required float ClimbRateMetersPerSecond { get; init; }

    /// <summary>
    /// Gets the heading in whole degrees clockwise from north. The console's heading.
    /// </summary>
    /// <remarks>
    /// Signed on the wire, and senders differ on whether they report 0-359 or -180 to 180.
    /// <see cref="Mcs.Core.VehicleTelemetry"/> normalises any finite value into [0, 360), so both
    /// conventions land in the same place without this decoder having to guess which one it is
    /// looking at.
    /// </remarks>
    public required short HeadingDegrees { get; init; }

    /// <summary>Gets throttle setting as a percentage. Carried, unused.</summary>
    public required ushort ThrottlePercent { get; init; }

    /// <summary>Reads the fields from a payload the framing layer has already restored to length.</summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is shorter than <see cref="PayloadLength"/>.</exception>
    internal static VfrHudMessage Read(ReadOnlySpan<byte> payload)
    {
        MavlinkPayload.EnsureLength(payload, PayloadLength, nameof(VfrHudMessage));

        //  ReadSingleLittleEndian rather than a BitConverter round-trip through an int: the former
        //  states the wire's byte order outright, where the latter is correct only on a
        //  little-endian machine and silently wrong on any other.
        return new VfrHudMessage
        {
            AirspeedMetersPerSecond = BinaryPrimitives.ReadSingleLittleEndian(payload),
            GroundSpeedMetersPerSecond = BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            AltitudeMetersMsl = BinaryPrimitives.ReadSingleLittleEndian(payload[8..]),
            ClimbRateMetersPerSecond = BinaryPrimitives.ReadSingleLittleEndian(payload[12..]),
            HeadingDegrees = BinaryPrimitives.ReadInt16LittleEndian(payload[16..]),
            ThrottlePercent = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
        };
    }
}
