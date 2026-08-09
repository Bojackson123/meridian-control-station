using Npgsql;

using Testcontainers.PostgreSql;

namespace Mcs.Integration.Tests;

/// <summary>
/// One real Postgres container for the whole integration suite, handing out a fresh, empty database
/// per test.
/// </summary>
/// <remarks>
/// <b>One container, many databases.</b> Starting a container per test turns a five-second suite
/// into a ninety-second one; sharing a single database between tests makes the migration cases
/// depend on each other's leftovers, which is precisely what they are asserting about. Creating a
/// database on an already-running server costs milliseconds and gives back the isolation.
/// <para>
/// <b>The image tag is pinned and is shared with the rest of the stack.</b> Compose, CI and this
/// fixture must all name the same Postgres major version -- testing against one version and
/// deploying another is a bug waiting for a bad week, and the failure lands in whichever
/// environment nobody was looking at.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// The Postgres image the whole project runs against. Keep this identical to the tag used by
    /// <c>deploy/compose</c> and by the CI workflow.
    /// </summary>
    public const string PostgresImage = "postgres:18-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage).Build();

    private int _databaseCount;

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates an empty database on the shared server and returns a connection string for it.
    /// </summary>
    /// <param name="label">
    /// Included in the database name so a container left running after a failed run can be read by
    /// someone trying to work out which test left what behind.
    /// </param>
    public async Task<string> CreateDatabaseAsync(string label)
    {
        string name = $"mcs_{label.ToLowerInvariant()}_{Interlocked.Increment(ref _databaseCount)}";

        await using NpgsqlConnection admin = new(_container.GetConnectionString());
        await admin.OpenAsync();

        //  CREATE DATABASE takes no parameters, so the name is built rather than bound. It is
        //  composed here from a test-supplied label and a counter and never from anything external,
        //  and the quoting below is what keeps that true if the label ever grows a character
        //  Postgres would read as syntax.
        await using NpgsqlCommand create = new($"""CREATE DATABASE "{name}" """, admin);
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }
}

/// <summary>
/// Binds every integration test class to the one shared container.
/// </summary>
/// <remarks>
/// A collection also serialises the classes in it, which suits a suite whose cost is the container
/// rather than the tests.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name, quoted by every test class that needs Postgres.</summary>
    public const string Name = "postgres";
}
