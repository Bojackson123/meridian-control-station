using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Mcs.Integration.Tests;

/// <summary>
/// Hosts the real <c>Mcs.Api</c> in process, pointed at a database supplied by the test.
/// </summary>
/// <remarks>
/// The startup path is the thing under test, so nothing about it is replaced: this runs the same
/// migration on the same schedule, behind the same host, as a container would. The only override is
/// the connection string, which is the one piece of configuration a deployment supplies anyway.
/// </remarks>
internal sealed class StationApplication : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public StationApplication(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        //  Not Development: the environment decides whether appsettings.Development.json is loaded,
        //  and its localhost connection string would quietly win over the container's if it were.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mcs"] = _connectionString,

                //  One vehicle at the slowest rate the feed allows. The fake feed is not what these
                //  tests are about, and it writes a log line per frame per vehicle.
                ["FakeFeed:VehicleCount"] = "1",
                ["FakeFeed:RateHz"] = "0.1",
            }));
    }
}
