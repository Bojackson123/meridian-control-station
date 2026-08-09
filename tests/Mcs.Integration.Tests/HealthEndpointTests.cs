using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Mcs.Api.Observability;
using Mcs.Api.Persistence;

using Npgsql;

namespace Mcs.Integration.Tests;

/// <summary>
/// The two health endpoints, served by the real application against a real Postgres.
/// </summary>
/// <remarks>
/// What this proves that the migrator's own tests do not: the station migrates its database as part
/// of starting, and reports the result over HTTP. Those are the two halves of the claim that the
/// persistence layer is wired in rather than merely present, and neither is observable from inside a
/// unit test.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class HealthEndpointTests
{
    private readonly PostgresFixture _postgres;

    public HealthEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Startup_MigratesTheDatabase_AndReadinessReportsTheVersionItIsAt()
    {
        // The database handed to the application is empty. Nothing in the test migrates it, so a
        // version coming back at all is the evidence that starting the station is what applied the
        // schema.
        await using StationApplication application = await StartAsync(nameof(Startup_MigratesTheDatabase_AndReadinessReportsTheVersionItIsAt));
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(HealthPath.Readiness);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(
            body.GetProperty("expectedSchemaVersion").GetInt32(),
            body.GetProperty("schemaVersion").GetInt32());
        Assert.True(body.GetProperty("schemaVersion").GetInt32() >= 1);
    }

    [Fact]
    public async Task Readiness_ReadsThroughToPostgresOnEveryCall()
    {
        // Not answered from something cached at startup. A readiness endpoint that reports the
        // state of the world as it was when the process booted is the console-lying-to-the-operator
        // hazard wearing an ops costume -- it stays green through exactly the outage it exists to
        // report.
        await using StationApplication application = await StartAsync(nameof(Readiness_ReadsThroughToPostgresOnEveryCall));
        using HttpClient client = application.CreateClient();

        using (HttpResponseMessage warmUp = await client.GetAsync(HealthPath.Readiness))
        {
            Assert.Equal(HttpStatusCode.OK, warmUp.StatusCode);
        }

        //  Drop the table the check reads. Crude, and precisely the point: nothing about the
        //  process has changed, so a check that answers from memory cannot notice.
        await ExecuteAsync(application, "DROP TABLE schema_version");

        using HttpResponseMessage afterwards = await client.GetAsync(HealthPath.Readiness);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, afterwards.StatusCode);

        JsonElement body = await afterwards.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_WhenItFails_SaysNothingAboutTheConnection()
    {
        // /health/db is reachable by anything that can reach the port, and Npgsql's failure
        // messages carry host names, ports and sometimes usernames. The detail belongs in the
        // station log, which is where the check puts it.
        await using StationApplication application = await StartAsync(nameof(Readiness_WhenItFails_SaysNothingAboutTheConnection));
        using HttpClient client = application.CreateClient();

        await ExecuteAsync(application, "DROP TABLE schema_version");

        using HttpResponseMessage response = await client.GetAsync(HealthPath.Readiness);
        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Liveness_StaysGreenWhenTheDatabaseIsNot()
    {
        // The reason there are two endpoints. If liveness could fail on a database fault, a
        // container runtime would restart an API that was working perfectly and had nothing to
        // reconnect to -- turning a database outage into a crash loop on top of a database outage.
        await using StationApplication application = await StartAsync(nameof(Liveness_StaysGreenWhenTheDatabaseIsNot));
        using HttpClient client = application.CreateClient();

        await ExecuteAsync(application, "DROP TABLE schema_version");

        using HttpResponseMessage liveness = await client.GetAsync(HealthPath.Liveness);
        using HttpResponseMessage readiness = await client.GetAsync(HealthPath.Readiness);

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);

        JsonElement body = await liveness.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        //  Liveness runs no checks, so it has no schema version to report and must not invent one.
        Assert.False(body.TryGetProperty("schemaVersion", out _));
    }

    /// <summary>
    /// Starts the application against a fresh, empty database and waits for it to be listening.
    /// </summary>
    private async Task<StationApplication> StartAsync(string label)
    {
        StationApplication application = new(await _postgres.CreateDatabaseAsync(label));

        //  The host is built lazily on first use, and the migration runs as part of starting it, so
        //  this is where a failed migration surfaces.
        _ = application.Services.GetService(typeof(SchemaMigrator));

        return application;
    }

    /// <summary>
    /// Runs a statement against the same database the application is using, via the application's
    /// own data source.
    /// </summary>
    private static async Task ExecuteAsync(StationApplication application, string sql)
    {
        NpgsqlDataSource dataSource =
            (NpgsqlDataSource)application.Services.GetService(typeof(NpgsqlDataSource))!;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The paths, taken from the application rather than retyped.</summary>
    private static class HealthPath
    {
        public static string Liveness => HealthEndpoints.LivenessPath;

        public static string Readiness => HealthEndpoints.ReadinessPath;
    }
}
