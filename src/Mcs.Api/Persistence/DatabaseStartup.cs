using Microsoft.Extensions.Options;

using Npgsql;

namespace Mcs.Api.Persistence;

/// <summary>
/// Runs the schema migration before the station accepts its first request, retrying a Postgres that
/// is still starting and stopping the host outright if it never arrives.
/// </summary>
/// <remarks>
/// <b><see cref="IHostedLifecycleService.StartingAsync"/>, not
/// <see cref="IHostedService.StartAsync"/>.</b> Every hosted service's <c>StartingAsync</c> runs
/// before any <c>StartAsync</c>, and Kestrel begins listening in its own <c>StartAsync</c> -- which
/// is registered by the web host before anything in <c>Program.cs</c>, so a plain hosted service
/// could not be ordered in front of it. This is the difference between a station that is unreachable
/// while it migrates and one that is briefly reachable and wrong.
/// <para>
/// <b>It is a hosted service rather than a call between <c>Build()</c> and <c>Run()</c></b> for the
/// same reason it is testable: the test host builds the application and starts it without ever
/// executing the lines after <c>Build()</c>, so a migration that lives there is a migration that
/// never runs under test.
/// </para>
/// <para>
/// <b>Failure stops the host.</b> An API that serves traffic without its database is HAZ-01's shape
/// exactly -- a system reporting healthy while something it depends on is missing -- and the
/// operator-visible consequence of limping is an empty console that looks like a quiet fleet.
/// </para>
/// </remarks>
public sealed class DatabaseStartup : IHostedLifecycleService
{
    //  Doubling from a quarter of a second, capped so the tail of a thirty-second budget is not one
    //  long sleep: a container that comes up at second 20 should be noticed at about second 20.
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);

    private readonly SchemaMigrator _migrator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabaseStartup> _logger;
    private readonly TimeSpan _budget;

    /// <summary>Creates the startup step.</summary>
    public DatabaseStartup(
        SchemaMigrator migrator,
        IOptions<DatabaseOptions> options,
        TimeProvider timeProvider,
        ILogger<DatabaseStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = TimeSpan.FromSeconds(options.Value.StartupTimeoutSeconds);
    }

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        long startedTimestamp = _timeProvider.GetTimestamp();
        TimeSpan delay = FirstRetryDelay;
        int attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                await _migrator.ApplyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsPostgresStillComingUp(exception))
            {
                TimeSpan elapsed = _timeProvider.GetElapsedTime(startedTimestamp);

                if (elapsed + delay > _budget)
                {
                    //  Fatal rather than one more attempt: the budget is the promise that a broken
                    //  deployment reports itself rather than hanging, and a container restarting
                    //  every thirty seconds with this line in its log is diagnosable from the log
                    //  alone.
                    _logger.LogCritical(
                        exception,
                        "Postgres did not accept a connection within {BudgetSeconds}s ({Attempts} attempts). "
                        + "The station will not start without its database.",
                        _budget.TotalSeconds,
                        attempt);

                    throw new InvalidOperationException(
                        $"Could not reach Postgres within {_budget.TotalSeconds:0} seconds. Check "
                        + $"{DatabaseOptions.ConnectionStringEnvironmentVariable} and that the database is "
                        + "running.",
                        exception);
                }

                //  Warning, not error: this is the ordinary case on a cold start, and a stack of red
                //  lines for something that resolves itself in two seconds trains people to ignore
                //  the log. The critical line above is the one that means something.
                _logger.LogWarning(
                    "Postgres is not accepting connections yet (attempt {Attempt}, {ElapsedSeconds:0.0}s of "
                    + "{BudgetSeconds}s); retrying in {DelayMilliseconds} ms. {Reason}",
                    attempt,
                    elapsed.TotalSeconds,
                    _budget.TotalSeconds,
                    delay.TotalMilliseconds,
                    exception.Message);

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                delay = delay * 2 > MaximumRetryDelay ? MaximumRetryDelay : delay * 2;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Distinguishes "the database is not up yet" from "the database said no".
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point of the retry budget. A refused socket or a server still
    /// replaying its write-ahead log is worth waiting thirty seconds for; a syntax error in a
    /// migration, a rejected password or a drifted checksum will say exactly the same thing thirty
    /// seconds later, and retrying it only delays the message and buries it under a dozen copies of
    /// itself.
    /// </remarks>
    private static bool IsPostgresStillComingUp(Exception exception) => exception switch
    {
        //  Npgsql marks connection-level failures transient itself, including 57P03
        //  (cannot_connect_now), which is precisely "starting up, come back".
        NpgsqlException { IsTransient: true } => true,

        //  A refused or unresolved socket does not always arrive wrapped, depending on where in the
        //  connect it failed.
        System.Net.Sockets.SocketException => true,
        TimeoutException => true,

        _ => false,
    };
}
