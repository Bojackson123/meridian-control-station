using System.Buffers.Binary;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// GLOBAL_POSITION_INT (id 33): the fused position estimate, and the message that makes a vehicle
/// renderable.
/// </summary>
/// <remarks>
/// <b>Two altitudes, neither of them labelled on the wire.</b> The reference for each is in its
/// field name, in a document -- which is precisely the failure MCS-004 is written against.
/// <see cref="AltitudeMillimetersMsl"/> is above mean sea level;
/// <see cref="RelativeAltitudeMillimeters"/> is above the point the vehicle called home when it
/// armed. The names here are where that implicit reference becomes explicit, and
/// <see cref="MavlinkTelemetryAssembler"/> then pairs the MSL one with an
/// <see cref="Mcs.Core.AltitudeReference"/> so no path downstream can carry a bare number.
/// <para>
/// <b><see cref="RelativeAltitudeMillimeters"/> is not AGL</b> and must never be called that. It is
/// height above one fixed point, which equals height above the ground beneath the vehicle only over
/// flat terrain. <see cref="Mcs.Core.AltitudeReference"/> has no member for "above home" and this
/// station does not invent one, so the value stays here, in the units and under the name it was
/// sent with, and reaches no telemetry. Relabelling it AGL is the quiet lie MCS-004 exists to
/// prevent, and the MSL/AGL conversion this station will eventually need would inherit it.
/// </para>
/// <para>
/// <b><see cref="TimeBootMilliseconds"/> is the vehicle's clock and stamps nothing.</b> It is
/// carried because a frame should be a faithful record of what arrived, and it is used by nothing:
/// MCS-005 puts the receipt time on the station's own clock at ingest, and a vehicle whose boot
/// counter is wrong would otherwise be able to age its own data.
/// </para>
/// </remarks>
internal readonly record struct GlobalPositionIntMessage
{
    /// <summary>The declared payload length, checked against the framing table by the test suite.</summary>
    internal const int PayloadLength = 28;

    /// <summary>
    /// The value <see cref="HeadingCentiDegrees"/> carries when the vehicle has no heading estimate.
    /// </summary>
    /// <remarks>
    /// Defined here rather than only in a comment because it is a real value in the field's range:
    /// 655.35 degrees is not a heading, but arithmetic on it produces 295.35, which is.
    /// </remarks>
    internal const ushort HeadingUnknown = ushort.MaxValue;

    /// <summary>Gets the sender's own milliseconds-since-boot. Stamps nothing -- see the remarks.</summary>
    public required uint TimeBootMilliseconds { get; init; }

    /// <summary>Gets the latitude in degrees times 1e7, WGS-84.</summary>
    public required int LatitudeDegreesE7 { get; init; }

    /// <summary>Gets the longitude in degrees times 1e7, WGS-84.</summary>
    public required int LongitudeDegreesE7 { get; init; }

    /// <summary>Gets the altitude above mean sea level, in millimetres.</summary>
    public required int AltitudeMillimetersMsl { get; init; }

    /// <summary>
    /// Gets the height above the home point in millimetres. <b>Not AGL</b> -- see the remarks on
    /// this type before using it for anything.
    /// </summary>
    public required int RelativeAltitudeMillimeters { get; init; }

    /// <summary>Gets the northward ground velocity in centimetres per second (NED frame).</summary>
    public required short VelocityNorthCentimetersPerSecond { get; init; }

    /// <summary>Gets the eastward ground velocity in centimetres per second (NED frame).</summary>
    public required short VelocityEastCentimetersPerSecond { get; init; }

    /// <summary>Gets the downward velocity in centimetres per second -- positive is descending.</summary>
    public required short VelocityDownCentimetersPerSecond { get; init; }

    /// <summary>
    /// Gets the heading in centidegrees, or <see cref="HeadingUnknown"/> if unestimated.
    /// </summary>
    /// <remarks>
    /// Decoded but deliberately unused: heading reaches telemetry from VFR_HUD, so that the console
    /// has exactly one source per field. See <see cref="MavlinkTelemetryAssembler"/>, which records
    /// why that way round.
    /// </remarks>
    public required ushort HeadingCentiDegrees { get; init; }

    /// <summary>Reads the fields from a payload the framing layer has already restored to length.</summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is shorter than <see cref="PayloadLength"/>.</exception>
    internal static GlobalPositionIntMessage Read(ReadOnlySpan<byte> payload)
    {
        MavlinkPayload.EnsureLength(payload, PayloadLength, nameof(GlobalPositionIntMessage));

        //  Signed reads for lat/lon/alt and the velocities, which is not a formality: every one of
        //  them is negative somewhere ordinary. A decoder reading latitude unsigned puts a vehicle
        //  in the southern hemisphere somewhere north of Siberia, and the vectors carry a negative
        //  latitude and a negative velocity for exactly that reason.
        return new GlobalPositionIntMessage
        {
            TimeBootMilliseconds = BinaryPrimitives.ReadUInt32LittleEndian(payload),
            LatitudeDegreesE7 = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
            LongitudeDegreesE7 = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]),
            AltitudeMillimetersMsl = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]),
            RelativeAltitudeMillimeters = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]),
            VelocityNorthCentimetersPerSecond = BinaryPrimitives.ReadInt16LittleEndian(payload[20..]),
            VelocityEastCentimetersPerSecond = BinaryPrimitives.ReadInt16LittleEndian(payload[22..]),
            VelocityDownCentimetersPerSecond = BinaryPrimitives.ReadInt16LittleEndian(payload[24..]),
            HeadingCentiDegrees = BinaryPrimitives.ReadUInt16LittleEndian(payload[26..]),
        };
    }
}
