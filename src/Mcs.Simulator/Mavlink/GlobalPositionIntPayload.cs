using System.Buffers.Binary;

using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// Writes a GLOBAL_POSITION_INT (id 33) payload: where the aircraft is, how fast it is going, and
/// which way it is pointing.
/// </summary>
/// <remarks>
/// <b>The message that puts a marker on the map</b>, and the one the station emits telemetry on, so
/// its rate is the console's update rate.
/// <para>
/// <b>Two altitudes, and they are not the same reference.</b> <c>alt</c> is above mean sea level.
/// <c>relative_alt</c> is above the point the vehicle called home when it armed, which for this
/// simulator is the first waypoint. <b>It is not AGL</b> -- it equals height above the ground only
/// over flat terrain, and this simulator models no terrain at all. Nothing may relabel it.
/// </para>
/// <para>
/// <b>Everything here is a scaled integer, which is where a simulator quietly lies if it is going
/// to.</b> Degrees are scaled by 1e7, metres by 1000, metres per second by 100, degrees of heading
/// by 100. Truncating instead of rounding would bias every position a fraction of a centimetre
/// toward the equator and the prime meridian -- invisible, systematic, and exactly the kind of
/// thing that only shows up when someone compares two implementations.
/// </para>
/// <para>
/// <b>The velocity components are NED and <c>vz</c> is positive downward</b>, so a climb is a
/// negative <c>vz</c>. Getting that sign wrong produces an aircraft that a receiver believes is
/// descending while its altitude rises, which no single field contradicts.
/// </para>
/// </remarks>
internal static class GlobalPositionIntPayload
{
    /// <summary>The full payload length GLOBAL_POSITION_INT declares, before v2 truncation.</summary>
    internal const int PayloadLength = 28;

    private const double DegreesToE7 = 1e7;

    private const double MetersToMillimeters = 1000.0;

    private const double MetersPerSecondToCentimetersPerSecond = 100.0;

    private const double DegreesToCentiDegrees = 100.0;

    /// <summary>Writes the payload.</summary>
    /// <param name="destination">Exactly <see cref="PayloadLength"/> bytes.</param>
    /// <param name="state">The aircraft's current state.</param>
    /// <param name="timeBootMilliseconds">
    /// The vehicle's own milliseconds-since-boot. The station reads it and stamps nothing with it:
    /// arrival time comes from the station's clock, so a vehicle cannot age its own data.
    /// </param>
    /// <param name="homeAltitudeMetersMsl">
    /// The altitude <c>relative_alt</c> is measured from. The first waypoint, which is where this
    /// aircraft is considered to have armed.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    internal static void Write(
        Span<byte> destination,
        in AircraftState state,
        uint timeBootMilliseconds,
        double homeAltitudeMetersMsl)
    {
        MavlinkPayloadBuffer.EnsureLength(
            destination, PayloadLength, nameof(GlobalPositionIntPayload));

        double headingRadians = double.DegreesToRadians(state.HeadingDegrees);
        double speed = state.GroundSpeedMetersPerSecond;

        BinaryPrimitives.WriteUInt32LittleEndian(destination, timeBootMilliseconds);

        BinaryPrimitives.WriteInt32LittleEndian(
            destination[4..], Scale(state.LatitudeDegrees, DegreesToE7));

        BinaryPrimitives.WriteInt32LittleEndian(
            destination[8..], Scale(state.LongitudeDegrees, DegreesToE7));

        BinaryPrimitives.WriteInt32LittleEndian(
            destination[12..], Scale(state.AltitudeMetersMsl, MetersToMillimeters));

        BinaryPrimitives.WriteInt32LittleEndian(
            destination[16..],
            Scale(state.AltitudeMetersMsl - homeAltitudeMetersMsl, MetersToMillimeters));

        //  North takes the cosine and east the sine, the same transpose the kinematics uses, so the
        //  velocity a receiver integrates matches the positions it is sent. Fitting in int16 is
        //  AircraftEnvelope.MaxCruiseSpeedMetersPerSecond's job -- 300 m/s is 30000 cm/s, inside
        //  the field with room to spare -- and that constant names this scaling as what it exists
        //  for, so the guarantee is stated in both directions rather than assumed in one.
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[20..],
            ScaleToInt16(speed * Math.Cos(headingRadians), MetersPerSecondToCentimetersPerSecond));

        BinaryPrimitives.WriteInt16LittleEndian(
            destination[22..],
            ScaleToInt16(speed * Math.Sin(headingRadians), MetersPerSecondToCentimetersPerSecond));

        //  Negated: vz is positive down and the state's climb rate is positive up.
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[24..],
            ScaleToInt16(
                -state.ClimbRateMetersPerSecond, MetersPerSecondToCentimetersPerSecond));

        //  Heading is normalised into [0, 360), so this is at most 35999 -- comfortably short of
        //  the 65535 the field uses to mean "no heading estimate", which this vehicle always has.
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[26..],
            (ushort)Scale(state.HeadingDegrees, DegreesToCentiDegrees));
    }

    /// <summary>Scales a value and rounds to the nearest integer rather than truncating.</summary>
    private static int Scale(double value, double factor) => (int)Math.Round(value * factor);

    /// <summary>Scales a value into the 16-bit range the velocity fields use.</summary>
    private static short ScaleToInt16(double value, double factor) =>
        (short)Math.Round(value * factor);
}
