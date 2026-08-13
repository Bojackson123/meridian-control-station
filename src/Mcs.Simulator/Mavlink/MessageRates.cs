using System.Globalization;

namespace Mcs.Simulator.Mavlink;

/// <summary>How often each of the four streams sends, in hertz.</summary>
/// <remarks>
/// <b>Four numbers rather than one, and the defaults are not multiples of each other.</b> A real
/// vehicle publishes each message on its own schedule, and the station's assembler is written for
/// exactly that: it folds several messages into one running state and emits when a position lands.
/// A simulator that sent everything together at one rate would leave that code untested by
/// construction and would make the console's update rate a property of the whole message set.
/// <para>
/// Grouped into their own type rather than passed as four loose doubles, because four adjacent
/// parameters of the same type in the same units is an argument-order bug waiting for a
/// refactor -- and the one it produces is a heartbeat at four hertz, which looks fine.
/// </para>
/// </remarks>
/// <param name="HeartbeatHz">HEARTBEAT. One hertz is the convention every ground station assumes.</param>
/// <param name="SysStatusHz">
/// SYS_STATUS. Slowest of the four: a battery percentage that moves once every two seconds is
/// still faster than a battery drains.
/// </param>
/// <param name="VfrHudHz">
/// VFR_HUD, the station's source for ground speed and heading. Deliberately not a divisor of
/// <paramref name="GlobalPositionHz"/>, so that some positions arrive with a HUD from the same
/// instant and some carry the previous one.
/// </param>
/// <param name="GlobalPositionHz">
/// GLOBAL_POSITION_INT. The station emits one telemetry report per position, so this is the rate
/// the console updates at.
/// </param>
internal readonly record struct MessageRates(
    double HeartbeatHz,
    double SysStatusHz,
    double VfrHudHz,
    double GlobalPositionHz)
{
    /// <summary>Describes the rates in one clause, for the startup log line.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"heartbeat {HeartbeatHz:0.##} Hz, sys_status {SysStatusHz:0.##} Hz, vfr_hud "
            + $"{VfrHudHz:0.##} Hz, global_position_int {GlobalPositionHz:0.##} Hz");
}
