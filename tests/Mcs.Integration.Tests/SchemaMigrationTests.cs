using Mcs.Api.Persistence;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Mcs.Integration.Tests;

/// <summary>
/// The migration runner against a real Postgres: it applies the baseline to an empty database,
/// records what it applied, does nothing at all the second time, refuses to run against a schema
/// that has drifted, and cannot be made to apply the same file twice by two instances starting
/// together.
/// </summary>
/// <remarks>
/// These run against a container rather than a fake because every claim being made here is a claim
/// about Postgres -- transactional DDL, advisory locks, <c>to_regclass</c> on a missing table. A test
/// double would be asserting that this file agrees with itself.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SchemaMigrationTests
{
    private readonly PostgresFixture _postgres;

    public SchemaMigrationTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Apply_ToAnEmptyDatabase_CreatesTheLedgerAndRecordsTheBaseline()
    {
        string connectionString = await _postgres.CreateDatabaseAsync(nameof(Apply_ToAnEmptyDatabase_CreatesTheLedgerAndRecordsTheBaseline));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        SchemaMigrator migrator = Migrator(dataSource);

        int version = await migrator.ApplyAsync(CancellationToken.None);

        Assert.Equal(migrator.TargetVersion, version);

        // Recorded, not just executed: the ledger is what the next start reads to decide there is
        // nothing to do, so a migration that ran without a row would be replayed forever.
        IReadOnlyList<(int Version, string Name, string Checksum)> ledger = await ReadLedgerAsync(dataSource);

        Assert.Equal(migrator.TargetVersion, ledger.Count);
        Assert.Equal(1, ledger[0].Version);
        Assert.Equal("baseline", ledger[0].Name);
        Assert.Equal(64, ledger[0].Checksum.Length);
    }

    [Fact]
    public async Task Apply_ARunWithNothingPending_ChangesNothing()
    {
        // Idempotence is what makes it safe to migrate unconditionally on every start, which is in
        // turn what removes the "did someone run the migrations?" step from every deployment.
        string connectionString = await _postgres.CreateDatabaseAsync(nameof(Apply_ARunWithNothingPending_ChangesNothing));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        await Migrator(dataSource).ApplyAsync(CancellationToken.None);
        DateTime firstApplied = await ReadAppliedAtAsync(dataSource, version: 1);

        await Migrator(dataSource).ApplyAsync(CancellationToken.None);

        IReadOnlyList<(int Version, string Name, string Checksum)> ledger = await ReadLedgerAsync(dataSource);

        Assert.Single(ledger);

        // The timestamp is the assertion that matters. A second run that re-executed the file and
        // replaced its row would still leave one row behind, and a row count alone would call that
        // a pass.
        Assert.Equal(firstApplied, await ReadAppliedAtAsync(dataSource, version: 1));
    }

    [Fact]
    [Verifies("MCS-011")]
    public async Task Apply_WhenAnAppliedMigrationHasBeenEditedSinceItShipped_Fails()
    {
        // Simulated from the database side because the file side is immutable by policy: a
        // recorded checksum that no longer matches the build is the same state whether the file
        // changed or the database came from somewhere else, and either way this build cannot
        // assume it knows what the schema looks like.
        string connectionString = await _postgres.CreateDatabaseAsync(nameof(Apply_WhenAnAppliedMigrationHasBeenEditedSinceItShipped_Fails));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await Migrator(dataSource).ApplyAsync(CancellationToken.None);

        await ExecuteAsync(dataSource, "UPDATE schema_version SET checksum = 'not-the-checksum' WHERE version = 1");

        SchemaDriftException ex = await Assert.ThrowsAsync<SchemaDriftException>(
            () => Migrator(dataSource).ApplyAsync(CancellationToken.None));

        Assert.Equal("0001_baseline.sql", ex.FileName);
        Assert.Equal("not-the-checksum", ex.RecordedChecksum);
    }

    [Fact]
    [Verifies("MCS-011")]
    public async Task Apply_ByTwoInstancesStartingTogether_AppliesEachMigrationOnce()
    {
        // The advisory lock, stated as the failure it prevents. Without it both replicas read an
        // empty ledger, both run 0001, and the loser dies on a duplicate key -- at startup, on a
        // deployment, which is the worst available moment to discover a race.
        string connectionString = await _postgres.CreateDatabaseAsync(nameof(Apply_ByTwoInstancesStartingTogether_AppliesEachMigrationOnce));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        //  Separate data sources, because two replicas do not share a connection pool.
        await using NpgsqlDataSource first = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlDataSource second = NpgsqlDataSource.Create(connectionString);

        await Task.WhenAll(
            Migrator(first).ApplyAsync(CancellationToken.None),
            Migrator(second).ApplyAsync(CancellationToken.None));

        Assert.Single(await ReadLedgerAsync(dataSource));
    }

    [Fact]
    [Verifies("MCS-011")]
    public async Task Apply_ToADatabaseAheadOfThisBuild_ContinuesRatherThanRefusingToStart()
    {
        // Deploying an older binary over a newer schema has to stay possible: the tables a later
        // migration added are ones this build does not reference, and making a rollback require a
        // database operation first would make the safest button in a deployment the slowest one.
        string connectionString = await _postgres.CreateDatabaseAsync(nameof(Apply_ToADatabaseAheadOfThisBuild_ContinuesRatherThanRefusingToStart));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        SchemaMigrator migrator = Migrator(dataSource);

        await migrator.ApplyAsync(CancellationToken.None);

        await ExecuteAsync(
            dataSource,
            $"INSERT INTO schema_version (version, name, checksum) VALUES ({migrator.TargetVersion + 1}, 'from_a_newer_build', 'x')");

        Assert.Equal(migrator.TargetVersion, await migrator.ApplyAsync(CancellationToken.None));
    }

    private static SchemaMigrator Migrator(NpgsqlDataSource dataSource) =>
        new(dataSource, NullLogger<SchemaMigrator>.Instance);

    private static async Task<IReadOnlyList<(int Version, string Name, string Checksum)>> ReadLedgerAsync(
        NpgsqlDataSource dataSource)
    {
        List<(int, string, string)> rows = [];

        await using NpgsqlCommand command =
            dataSource.CreateCommand("SELECT version, name, checksum FROM schema_version ORDER BY version");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<DateTime> ReadAppliedAtAsync(NpgsqlDataSource dataSource, int version)
    {
        await using NpgsqlCommand command =
            dataSource.CreateCommand($"SELECT applied_at FROM schema_version WHERE version = {version}");

        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
