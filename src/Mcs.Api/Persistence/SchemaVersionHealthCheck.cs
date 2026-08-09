using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace Mcs.Api.Persistence;

/// <summary>
/// Readiness: reads the applied schema version out of Postgres and compares it with the version this
/// build was compiled against.
/// </summary>
/// <remarks>
/// <b>A real round trip, and a meaningful one.</b> <c>SELECT 1</c> would prove a socket is open;
/// checking that the connection object is non-null would prove nothing at all. Reading
/// <c>schema_version</c> exercises the pool, the credentials, the database and the table the rest of
/// the system depends on, and it answers the question a deployment actually has -- <i>is this
/// database the one this build expects?</i> -- rather than the question a ping answers.
/// <para>
/// The version is read every call rather than cached from startup: a cached answer would report the
/// database healthy for as long as the process lived, whatever happened to it afterwards.
/// </para>
/// </remarks>
public sealed class SchemaVersionHealthCheck : IHealthCheck
{
    /// <summary>The tag that routes this check to <c>/health/db</c> and away from <c>/health</c>.</summary>
    public const string ReadinessTag = "db";

    /// <summary>The registered name of this check.</summary>
    public const string Name = "postgres";

    //  Aggregate rather than a row count, so an unexpected extra row cannot be read as the current
    //  version by accident. NULL comes back for an empty ledger, which is a database that has the
    //  table but has recorded nothing -- someone's hand-rolled restore, and not healthy.
    private const string ReadVersionSql = "SELECT max(version) FROM schema_version";

    //  Well inside any sensible probe interval. A readiness probe that hangs is indistinguishable
    //  from a failing one to the orchestrator but takes far longer to say so.
    private const int CommandTimeoutSeconds = 5;

    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<SchemaVersionHealthCheck> _logger;

    /// <summary>Creates the check.</summary>
    public SchemaVersionHealthCheck(
        NpgsqlDataSource dataSource,
        SchemaMigrator migrator,
        ILogger<SchemaVersionHealthCheck> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        int expected = _migrator.TargetVersion;

        try
        {
            await using NpgsqlConnection connection =
                await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using NpgsqlCommand command = new(ReadVersionSql, connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };

            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (scalar is not int version)
            {
                return HealthCheckResult.Unhealthy(
                    "The schema_version table is present but empty.",
                    data: Data(null, expected, "the schema_version table is present but empty"));
            }

            return version == expected
                ? HealthCheckResult.Healthy(
                    $"Schema version {version}.",
                    data: Data(version, expected, null))
                : HealthCheckResult.Unhealthy(
                    $"Schema version {version}, expected {expected}.",
                    data: Data(version, expected,
                        $"the database is at schema version {version} and this build expects {expected}"));
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            //  The exception goes to the log, never to the response body: an unreachable database
            //  reports connection strings, host names and sometimes usernames in its message, and
            //  /health/db is the one endpoint guaranteed to be reachable by anything that can see
            //  the port.
            _logger.LogError(exception, "Readiness check could not read the schema version.");

            return HealthCheckResult.Unhealthy(
                "The schema version could not be read.",
                exception,
                Data(null, expected, "the schema version could not be read -- see the station log"));
        }
    }

    private static Dictionary<string, object> Data(int? version, int expected, string? detail)
    {
        Dictionary<string, object> data = new(StringComparer.Ordinal)
        {
            ["expectedSchemaVersion"] = expected,
        };

        if (version is int applied)
        {
            data["schemaVersion"] = applied;
        }

        if (detail is not null)
        {
            data["detail"] = detail;
        }

        return data;
    }
}
