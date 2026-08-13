namespace Mcs.Simulator;

/// <summary>
/// What this vehicle has emitted and what became of it on the way out: frames built per message
/// type, and what the socket did with them.
/// </summary>
/// <remarks>
/// <b>One counter set, where the station's adapter has three.</b> That split exists because framing,
/// decoding and the socket can each throw work away independently, so a rise in a total has to be
/// attributable to one of them. Nothing comparable happens here: this process builds every frame it
/// intends to and hands each one straight to a socket, so there is exactly one place a frame can be
/// lost and it already has its own counter.
///
/// <para>
/// <b>The four per-message counts are the rate evidence at runtime.</b> Four numbers rising in
/// different proportions is what "sent at genuinely different rates" looks like from outside the
/// process, and it is the reading that catches a schedule quietly collapsed into one bundle -- which
/// nothing else here would report, because the total would be unchanged.
/// </para>
///
/// <para>
/// Mutable and not thread-safe, matching the emitter and transmitter it sits with: one vehicle, one
/// loop, every counter written from it.
/// </para>
/// </remarks>
internal sealed class SimulatorStatistics
{
    /// <summary>Gets the number of HEARTBEAT frames built.</summary>
    internal long HeartbeatsSent { get; set; }

    /// <summary>Gets the number of SYS_STATUS frames built.</summary>
    internal long SysStatusesSent { get; set; }

    /// <summary>Gets the number of VFR_HUD frames built.</summary>
    internal long VfrHudsSent { get; set; }

    /// <summary>Gets the number of GLOBAL_POSITION_INT frames built.</summary>
    /// <remarks>
    /// The one that corresponds to a console update: the station emits one telemetry report per
    /// position and folds the rest into a running state.
    /// </remarks>
    internal long PositionsSent { get; set; }

    /// <summary>Gets the number of datagrams the socket accepted.</summary>
    /// <remarks>
    /// One frame per datagram, so against the four counts above this should be their sum. It is
    /// counted separately anyway, because "built" and "handed to the network" are different claims
    /// and the gap between them is <see cref="SendErrors"/>.
    /// </remarks>
    internal long DatagramsSent { get; set; }

    /// <summary>Gets the total bytes handed to the socket.</summary>
    internal long BytesSent { get; set; }

    /// <summary>
    /// Gets the number of sends that failed and were absorbed.
    /// </summary>
    /// <remarks>
    /// Absorbed rather than fatal, and this is the counter that says how often. A station that is
    /// down, restarting, or still applying migrations is not this vehicle's problem: a real
    /// aircraft keeps flying and keeps transmitting into a link nobody is listening to, and one
    /// that shut down because its ground station blinked would be modelling the wrong failure. On
    /// Linux the usual cause is an ICMP port-unreachable from a container that is not up yet.
    /// </remarks>
    internal long SendErrors { get; set; }

    /// <summary>
    /// Gets the number of times the target host was resolved again after a send failed.
    /// </summary>
    /// <remarks>
    /// Anything above zero means the address this vehicle is transmitting to has been in doubt,
    /// which on a container network is what a restarted station looks like: the name is the same
    /// and the address behind it is not. Without this the recovery would be silent and a stale
    /// address would be indistinguishable from a station that is simply quiet.
    /// <para>
    /// <b>Not a count of failures</b> -- read <see cref="SendErrors"/> for those. The lookups are
    /// rationed to one per interval and skipped altogether when the target is an address literal,
    /// so this counts attempts at recovery rather than occasions for one.
    /// </para>
    /// </remarks>
    internal long TargetReresolutions { get; set; }

    public override string ToString() =>
        $"heartbeat={HeartbeatsSent}, sysStatus={SysStatusesSent}, vfrHud={VfrHudsSent}, "
        + $"position={PositionsSent}, datagrams={DatagramsSent}, bytes={BytesSent}, "
        + $"sendErrors={SendErrors}, reresolutions={TargetReresolutions}";
}
