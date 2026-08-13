namespace Mcs.Core;

/// <summary>
/// A source of telemetry: something that produces <see cref="VehicleTelemetry"/> into a
/// <see cref="TelemetryIngest"/> and an <see cref="ITelemetryStore"/> until it is told to stop.
/// </summary>
/// <remarks>
/// <b>Narrow because it was derived rather than designed.</b> Written against two implementations
/// that already existed -- a synthetic feed on a timer and a MAVLink link on a UDP socket -- and
/// deliberately not widened to anything only one of them has. What they genuinely share is this:
/// start producing telemetry, keep going until stopped. Everything else about them differs, and an
/// interface member that only one implementation can answer honestly is a member every later
/// implementation has to lie about.
///
/// <para>
/// The synthetic feed has since been deleted -- the station flies a real aircraft now, and a
/// second source of truth about what the console shows was not worth keeping. The shape it argued
/// for stays: a contract derived from two unlike sources is why a ground adapter can arrive
/// without this file changing, and re-deriving it from the one implementation left would narrow it
/// to whatever MAVLink happens to need.
/// </para>
///
/// <para>
/// <b>Telemetry only. There is no command member here</b>, and its absence is a decision rather than
/// an omission: the command lifecycle has no implementation and no caller yet, so any signature
/// written now would be a guess at a design that is close enough to arrive on its own. When it does,
/// it may well be a separate interface -- a receive-only adapter is a real thing (a listener on a
/// telemetry-only feed), and folding commands in here would make every such adapter implement a
/// method that throws.
/// </para>
///
/// <para>
/// <b>The adapter owns its loop.</b> <see cref="RunAsync"/> is entered once and does not return until
/// cancellation, rather than a <c>Tick()</c> the host calls on a schedule. Both implementations it
/// was derived from wanted it this way -- one blocked on a socket read, the other on a timer, and
/// neither had a natural unit of work small enough for a caller to drive. The argument for a driven
/// shape is testability,
/// and it is answered by what the tests actually drive: the parser and the assembler take bytes and
/// frames directly, which is where the logic worth testing lives.
/// </para>
///
/// <para>
/// <b>Not <c>IHostedService</c>, and not a <c>BackgroundService</c>.</b> Both are hosting types, and
/// <c>Mcs.Core</c> has no package references and keeps none -- the contract describing the station's
/// own boundary cannot be phrased in terms of the framework that happens to host it today. The host
/// supplies one background service that runs every registered adapter, which is a dozen lines and
/// keeps the hosting dependency where the host is.
/// </para>
///
/// <para>
/// <b>No statistics member either.</b> Each adapter counts what its own link can go wrong in --
/// resynced bytes and unknown message ids meant nothing to the timer-driven feed, and mean nothing
/// to a file replayed from disk -- so a common counter shape would be invented here rather than
/// observed in any implementation. Adapters expose their
/// own concrete statistics; this interface is how they are started, not how they are read.
/// </para>
/// </remarks>
public interface IVehicleAdapter
{
    /// <summary>
    /// Gets a short, stable name for this adapter, used in log lines and startup messages.
    /// </summary>
    /// <remarks>
    /// Present because more than one adapter runs at once -- and on the day two of them are running,
    /// "adapter stopped" without a name is a line that cannot be acted on. Stable rather than
    /// generated, so the same string is greppable across a restart.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Runs the adapter until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// <b>Returns on cancellation, and does not throw for it.</b> An
    /// <see cref="OperationCanceledException"/> allowed out of here reaches the host as a faulted
    /// background service, which logs a crash on every clean shutdown and trains the reader to
    /// ignore the one line that would have mattered.
    /// <para>
    /// <b>Any other exception is fatal to the station, on purpose.</b> An adapter that failed and
    /// returned quietly is a console that has stopped updating and does not say so, which is HAZ-01
    /// exactly. Faults a link can produce on its own -- a malformed message, a socket error, a
    /// rejected vehicle -- are the adapter's to absorb and count; what escapes should be the class of
    /// failure that means this adapter is not coming back.
    /// </para>
    /// </remarks>
    /// <param name="stoppingToken">Cancelled when the station is shutting down.</param>
    Task RunAsync(CancellationToken stoppingToken);
}
