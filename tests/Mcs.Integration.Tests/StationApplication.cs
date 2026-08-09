using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    /// <param name="connectionString">The database this instance migrates and reads.</param>
    /// <param name="settings">
    /// Configuration entries layered over the defaults below. The telemetry tests raise the feed
    /// rate this way, because the default here is chosen to keep the feed out of the log rather
    /// than to make frames arrive.
    /// </param>
    /// <param name="configureServices">
    /// Applied after the application has registered its own services, so a registration here wins.
    /// For observing the station, not for rebuilding it -- a test that replaces a component is
    /// testing something other than what a deployment runs.
    /// </param>
    public StationApplication(
        string connectionString,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _connectionString = connectionString;
        _settings = settings ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        _configureServices = configureServices;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        //  Not Development: the environment decides whether appsettings.Development.json is loaded,
        //  and its localhost connection string would quietly win over the container's if it were.
        builder.UseEnvironment("Testing");

        Dictionary<string, string?> configuration = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Mcs"] = _connectionString,

            //  One vehicle at the slowest rate the feed allows. The fake feed is not what most of
            //  these tests are about, and it writes a log line per frame per vehicle.
            ["FakeFeed:VehicleCount"] = "1",
            ["FakeFeed:RateHz"] = "0.1",
        };

        foreach (KeyValuePair<string, string?> setting in _settings)
        {
            configuration[setting.Key] = setting.Value;
        }

        builder.ConfigureAppConfiguration(
            builderConfiguration => builderConfiguration.AddInMemoryCollection(configuration));

        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
