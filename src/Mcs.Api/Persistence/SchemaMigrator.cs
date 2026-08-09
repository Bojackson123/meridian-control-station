using System.Reflection;

using Npgsql;

namespace Mcs.Api.Persistence;

/// <summary>
/// Brings a database up to the schema this build was compiled against, by applying the embedded
/// <c>deploy/migrations</c> files that it has not already recorded.
/// </summary>
/// <remarks>
/// <b>Forty lines of SQL rather than a migrations framework.</b> There is no domain model to map at
/// this stage, an ORM would be the largest dependency in the API by some distance, and the whole
/// mechanism has to be explicable at a whiteboard. When the schema starts changing shape often
/// enough that ordering and rollback need real machinery, that is the moment to reach for
/// <c>DbUp</c> or <c>FluentMigrator</c> -- and by then the ledger below is already the table they
/// would want.
/// <para>
/// <b>Serialised across instances by a session advisory lock.</b> Two API replicas starting together
/// would otherwise both apply the same file, and the loser would fail on a duplicate object at the
/// least convenient moment. There is one replica today; the lock is two statements and closes the
/// case permanently.
/// </para>
/// <para>
/// <b>One transaction per file.</b> Postgres makes DDL transactional, so a script that fails halfway
/// leaves nothing behind and no ledger row -- the next start retries it from a known state. A single
/// transaction spanning every file would also be atomic, but it makes a five-migration deployment
/// fail as one opaque unit instead of naming the file that broke.
/// </para>
/// </remarks>
public sealed class SchemaMigrator
{
    /// <summary>
    /// The advisory lock every instance contends on: the ASCII bytes of <c>MCS_MIG</c>.
    /// </summary>
    /// <remarks>
    /// Advisory locks share one namespace per database, so the value is arbitrary but must be
    /// stable and unique to this use. Derived from text rather than picked at random so that a
    /// <c>pg_locks</c> row is identifiable by someone who has never read this file.
    /// </remarks>
    internal const long AdvisoryLockKey = 0x4D43535F4D4947;

    //  to_regclass returns NULL rather than throwing for an unknown relation, which is what makes
    //  "has this database ever been migrated?" a query rather than a caught 42P01. The ledger is
    //  created by 0001 itself -- keeping its definition in SQL next to every other table, instead
    //  of in a bootstrap string in here that nobody would think to look for.
    private const string LedgerExistsSql = "SELECT to_regclass('public.schema_version') IS NOT NULL";

    private const string ReadLedgerSql = """
        SELECT version, name, checksum
        FROM schema_version
        ORDER BY version
        """;

    private const string RecordSql = """
        INSERT INTO schema_version (version, name, checksum)
        VALUES (@version, @name, @checksum)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SchemaMigrator> _logger;
    private readonly IReadOnlyList<MigrationScript> _scripts;

    /// <summary>Creates a migrator over the migrations embedded in this assembly.</summary>
    public SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> logger)
        : this(dataSource, logger, MigrationScript.LoadAll())
    {
    }

    /// <summary>Creates a migrator over the migrations embedded in a given assembly.</summary>
    internal SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> logger, Assembly assembly)
        : this(dataSource, logger, MigrationScript.LoadAll(assembly))
    {
    }

    private SchemaMigrator(
        NpgsqlDataSource dataSource,
        ILogger<SchemaMigrator> logger,
        IReadOnlyList<MigrationScript> scripts)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scripts = scripts;
    }

    /// <summary>
    /// Gets the schema version this build expects to be running against, once
    /// <see cref="ApplyAsync"/> has succeeded.
    /// </summary>
    public int TargetVersion => _scripts[^1].Version;

    /// <summary>
    /// Applies every migration the database has not recorded, and returns the version it is left at.
    /// </summary>
    /// <remarks>
    /// Idempotent: with nothing pending this is three queries and no writes, which is what makes it
    /// safe to run unconditionally on every start.
    /// </remarks>
    /// <exception cref="SchemaDriftException">
    /// A migration the database has already applied no longer matches the file in this build.
    /// </exception>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "SELECT pg_advisory_lock(@key)", cancellationToken,
            new NpgsqlParameter<long>("key", AdvisoryLockKey)).ConfigureAwait(false);

        try
        {
            IReadOnlyDictionary<int, AppliedMigration> applied =
                await ReadLedgerAsync(connection, cancellationToken).ConfigureAwait(false);

            VerifyNothingHasDrifted(applied);

            int pending = 0;

            foreach (MigrationScript script in _scripts)
            {
                if (applied.ContainsKey(script.Version))
                {
                    continue;
                }

                await ApplyOneAsync(connection, script, cancellationToken).ConfigureAwait(false);
                pending++;
            }

            _logger.LogInformation(
                "Schema is at version {SchemaVersion} ({AppliedCount} migration(s) applied this start).",
                TargetVersion,
                pending);

            return TargetVersion;
        }
        finally
        {
            //  Not strictly required -- returning the connection to the pool issues DISCARD ALL,
            //  which drops advisory locks, and physically closing it would too. Explicit anyway,
            //  because "the lock is released by a side effect of connection pooling" is not a
            //  sentence anyone should have to reconstruct while a deployment is stuck.
            //
            //  CancellationToken.None deliberately: this runs on the way out of a cancelled
            //  migration too, and a cancelled unlock would hold the lock until the pool got round
            //  to resetting the connection.
            await ExecuteAsync(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None,
                new NpgsqlParameter<long>("key", AdvisoryLockKey)).ConfigureAwait(false);
        }
    }

    private async Task ApplyOneAsync(
        NpgsqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying migration {Migration}.", script.FileName);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand statements = new(script.Sql, connection, transaction))
        {
            //  No timeout. A migration that adds an index to a large table takes as long as it
            //  takes, and the default thirty seconds would abandon it mid-flight -- rolling back
            //  work that was progressing fine, on a schedule unrelated to anything.
            statements.CommandTimeout = 0;

            await statements.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand record = new(RecordSql, connection, transaction))
        {
            record.Parameters.Add(new NpgsqlParameter<int>("version", script.Version));
            record.Parameters.Add(new NpgsqlParameter<string>("name", script.Name));
            record.Parameters.Add(new NpgsqlParameter<string>("checksum", script.Checksum));

            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        //  The statements and the ledger row commit together, so there is no interval in which a
        //  migration has run without being recorded -- which the next start would replay.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<int, AppliedMigration>> ReadLedgerAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand exists = new(LedgerExistsSql, connection);

        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            _logger.LogInformation("No schema_version table: treating this as an unmigrated database.");
            return new Dictionary<int, AppliedMigration>();
        }

        Dictionary<int, AppliedMigration> applied = [];

        await using NpgsqlCommand read = new(ReadLedgerSql, connection);
        await using NpgsqlDataReader reader =
            await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = new AppliedMigration(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        }

        return applied;
    }

    private void VerifyNothingHasDrifted(IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        foreach (MigrationScript script in _scripts)
        {
            if (applied.TryGetValue(script.Version, out AppliedMigration? record)
                && !string.Equals(record.Checksum, script.Checksum, StringComparison.Ordinal))
            {
                throw new SchemaDriftException(script.FileName, record.Checksum, script.Checksum);
            }
        }

        //  A version in the ledger that this build has no file for means an older binary has been
        //  deployed over a newer schema. Logged, not fatal: the tables the newer migration added
        //  are ones this build does not reference, and refusing to start would make rolling a bad
        //  release back require a database operation first -- exactly when nobody wants one. The
        //  case that is fatal is the one above, where the same version means two different things.
        foreach (AppliedMigration record in applied.Values)
        {
            if (record.Version > TargetVersion)
            {
                _logger.LogWarning(
                    "Database is at schema version {DatabaseVersion}, ahead of this build's {TargetVersion}. "
                    + "Continuing -- this build does not use anything {Migration} added -- but it is running "
                    + "against a schema it was not compiled for.",
                    record.Version,
                    TargetVersion,
                    record.Name);
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AppliedMigration(int Version, string Name, string Checksum);
}

/// <summary>
/// Thrown when a migration the database has already applied no longer matches the file shipped in
/// this build.
/// </summary>
/// <remarks>
/// Fatal rather than advisory. The ledger says version N was applied and the code believes version N
/// is something else, so nothing downstream can be trusted to be looking at the schema it was
/// written against -- and a station that is confidently wrong about its own state is the failure
/// this whole system is designed to make loud. The fix is a new numbered migration, never an edit to
/// one that has shipped.
/// </remarks>
public sealed class SchemaDriftException : Exception
{
    /// <summary>Creates the exception for a migration whose checksum no longer matches.</summary>
    public SchemaDriftException(string fileName, string recordedChecksum, string currentChecksum)
        : base($"Migration '{fileName}' was applied to this database as checksum {recordedChecksum}, "
            + $"but the file in this build hashes to {currentChecksum}. A migration is immutable once "
            + "it has shipped: put the change in a new numbered file, and if the two databases have "
            + "genuinely diverged, reconcile them deliberately rather than by starting the API.")
    {
        FileName = fileName;
        RecordedChecksum = recordedChecksum;
        CurrentChecksum = currentChecksum;
    }

    /// <summary>Gets the migration that no longer matches.</summary>
    public string FileName { get; }

    /// <summary>Gets the checksum the database recorded when it applied the migration.</summary>
    public string RecordedChecksum { get; }

    /// <summary>Gets the checksum of the file in this build.</summary>
    public string CurrentChecksum { get; }
}
