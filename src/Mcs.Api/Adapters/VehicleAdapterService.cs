using Mcs.Core;

namespace Mcs.Api.Adapters;

/// <summary>
/// Runs every registered <see cref="IVehicleAdapter"/> for as long as the station is up.
/// </summary>
/// <remarks>
/// The whole of the hosting dependency, in one place. <see cref="IVehicleAdapter"/> lives in
/// <c>Mcs.Core</c>, which has no package references and so cannot name <c>BackgroundService</c> --
/// and should not want to: how a telemetry source is started is a fact about this host, not about
/// the station's boundary. Every adapter therefore describes itself as "run until cancelled" and
/// this class is the dozen lines that turn that into a hosted service.
/// <para>
/// <b>All of them under one service, rather than one service each.</b> The alternative registers a
/// hosted service per adapter and reads the same at startup, but it loses the thing worth having: a
/// single place where the fleet of adapters is started, logged and awaited together, so "which
/// sources is this station listening to?" is answered by one log line rather than by counting
/// registrations.
/// </para>
/// <para>
/// <b>A faulting adapter stops the station, on purpose.</b> The failures a link produces on its own
/// -- a malformed message, a socket error, a vehicle the store refused -- are the adapter's to
/// absorb and count, so an exception arriving here is the other kind: this source is not coming
/// back. Letting it reach the host stops the process, which a container restarts and an operator
/// sees. The alternative, logging it and continuing, leaves a console updating from whatever sources
/// remain with nothing on screen to say one has died -- a picture that is confidently not current,
/// which is the hazard this station is built against. <b>Getting that to actually happen takes the
/// cancellation in <see cref="ExecuteAsync"/></b>, for the reason recorded there.
/// </para>
/// </remarks>
public sealed class VehicleAdapterService : BackgroundService
{
    private readonly IVehicleAdapter[] _adapters;
    private readonly ILogger<VehicleAdapterService> _logger;

    /// <param name="adapters">
    /// Every registered telemetry source. An empty set is not an error here -- a station with no
    /// adapters is a legitimate thing to start while one is being swapped for another -- and it is
    /// logged rather than thrown, because the log line says it plainly and a throw at startup would
    /// make the swap require two changes in one commit.
    /// </param>
    /// <param name="logger">Where the fleet of adapters announces itself.</param>
    public VehicleAdapterService(
        IEnumerable<IVehicleAdapter> adapters, ILogger<VehicleAdapterService> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = [.. adapters];
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_adapters.Length == 0)
        {
            _logger.LogWarning(
                "No vehicle adapters are registered, so the station will show nothing.");

            return;
        }

        _logger.LogInformation(
            "Starting {AdapterCount} vehicle adapter(s): {Adapters}.",
            _adapters.Length,
            string.Join(", ", _adapters.Select(adapter => adapter.Name)));

        //  Linked, and cancelled by the first adapter to fail. Without that step this method is a
        //  trap as soon as a second source exists: WhenAll completes only when every task does, so
        //  an adapter that could not bind would fault into a WhenAll still waiting on a healthy one
        //  that runs until cancelled -- forever. The host would never see the exception, the
        //  station would keep serving, and the dead link would show as a console updating happily
        //  from whatever source remained. That is the failure this class documents itself as
        //  preventing, arrived at by the mechanism meant to prevent it.
        //
        //  One adapter is registered today, which makes this look redundant and is exactly when it
        //  would get deleted. The set is plural by construction and a ground adapter joins it, so
        //  the guard is written for the shape rather than for the count.
        using CancellationTokenSource stopping =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        //  Materialised before awaiting: Select is lazy, and WhenAll enumerating it is what starts
        //  each adapter. Left deferred, an adapter that threw synchronously would abandon the ones
        //  after it in the sequence unstarted and unawaited.
        Task[] running = [.. _adapters.Select(adapter => RunUntilOneFailsAsync(adapter, stopping))];

        //  Still WhenAll rather than WhenAny: the exception that stops the station should not race
        //  the shutdown of the sources that were still healthy, and this now completes because the
        //  healthy ones have been asked to stop.
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one adapter, and on failure asks every other adapter to stop before the exception is
    /// allowed to surface.
    /// </summary>
    /// <remarks>
    /// Only a fault trips this. An adapter that returns without throwing has stopped the way the
    /// interface says a cancelled one does, and cannot be told apart from one whose token was
    /// cancelled a moment earlier -- so a normal return is left to the host's own shutdown, and it
    /// is the fault that is treated as fatal.
    /// </remarks>
    private static async Task RunUntilOneFailsAsync(
        IVehicleAdapter adapter, CancellationTokenSource stopping)
    {
        try
        {
            await adapter.RunAsync(stopping.Token).ConfigureAwait(false);
        }
        catch
        {
            //  Cancelled before the rethrow, so the exception is already on its way out by the time
            //  the others are winding down. Their cancellation is ordinary and produces no second
            //  exception, which leaves WhenAll carrying exactly the fault that started this.
            await stopping.CancelAsync().ConfigureAwait(false);

            throw;
        }
    }
}
