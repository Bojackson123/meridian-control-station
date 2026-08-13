using System.Buffers.Binary;

using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// Writes a VFR_HUD (id 74) payload: the values an autopilot publishes for a head-up display.
/// </summary>
/// <remarks>
/// <b>This is where the station gets ground speed and heading from</b>, so the two fields that
/// matter most are the two that look least interesting.
/// <para>
/// <b><c>airspeed</c> equals <c>groundspeed</c> here because no wind is modelled.</b> That is worth
/// stating rather than leaving to be inferred, because wind is the entire reason the station
/// prefers this message's <c>heading</c> over an angle derived from GLOBAL_POSITION_INT's velocity
/// components: those give course over ground, which differs from where the nose points in any wind.
/// With no wind the two agree, so this simulator cannot exercise that distinction -- and a
/// simulator that made up a wind to look thorough would be putting a number in front of a test that
/// nothing else in the repository could check.
/// </para>
/// <para>
/// <b>Four IEEE-754 floats.</b> Everything else this vehicle sends is an integer, so this is the
/// only message where a width or byte-order mistake shows up as a plausible number rather than an
/// obviously broken one.
/// </para>
/// <para>
/// <c>alt</c> is carried and the station ignores it, taking altitude from GLOBAL_POSITION_INT so
/// that the height an operator reads was estimated at the same instant as the position it is shown
/// beside. It is still filled in correctly: a field sent wrong because nobody reads it is a trap
/// for whoever reads it next.
/// </para>
/// </remarks>
internal static class VfrHudPayload
{
    /// <summary>The full payload length VFR_HUD declares, before v2 truncation.</summary>
    internal const int PayloadLength = 20;

    private const int DegreesPerTurn = 360;

    /// <summary>
    /// The throttle setting reported, as a percentage.
    /// </summary>
    /// <remarks>
    /// Constant, and deliberately not derived from the climb rate. There is no thrust, drag or mass
    /// in this model, so any throttle it computed would be an invented relationship dressed up as a
    /// measurement -- and the station does not read the field. A plausible cruise number that
    /// nobody can mistake for a modelled one is the honest option.
    /// </remarks>
    private const ushort CruiseThrottlePercent = 55;

    /// <summary>Writes the payload.</summary>
    /// <param name="destination">Exactly <see cref="PayloadLength"/> bytes.</param>
    /// <param name="state">The aircraft's current state.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    internal static void Write(Span<byte> destination, in AircraftState state)
    {
        MavlinkPayloadBuffer.EnsureLength(destination, PayloadLength, nameof(VfrHudPayload));

        float speed = (float)state.GroundSpeedMetersPerSecond;

        BinaryPrimitives.WriteSingleLittleEndian(destination, speed);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], speed);

        BinaryPrimitives.WriteSingleLittleEndian(
            destination[8..], (float)state.AltitudeMetersMsl);

        BinaryPrimitives.WriteSingleLittleEndian(
            destination[12..], (float)state.ClimbRateMetersPerSecond);

        //  Whole degrees, which is coarser than the centidegrees GLOBAL_POSITION_INT carries. The
        //  modulo catches the one case rounding produces: 359.7 rounds to 360, which is a legal
        //  int16 and not a legal bearing. The station normalises anything finite, so this is
        //  tidiness rather than a fix -- but a receiver that does not would draw the nose north.
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[16..],
            (short)((int)Math.Round(state.HeadingDegrees) % DegreesPerTurn));

        BinaryPrimitives.WriteUInt16LittleEndian(destination[18..], CruiseThrottlePercent);
    }
}
