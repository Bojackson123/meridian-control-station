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
    private readonly Action<IServiceCollection>? _configureServices;

    /// <param name="connectionString">The database this instance migrates and reads.</param>
    /// <param name="configureServices">
    /// Applied after the application has registered its own services, so a registration here wins.
    /// For observing the station, not for rebuilding it -- a test that replaces a component is
    /// testing something other than what a deployment runs.
    /// </param>
    public StationApplication(
        string connectionString,
        Action<IServiceCollection>? configureServices = null)
    {
        _connectionString = connectionString;
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

            //  An ephemeral port, because the station really does bind one. On the configured
            //  14550 these tests would fail against a developer's own station running beside them,
            //  and two of them in one process would fail against each other -- both as a bind error
            //  in a suite that is about the database.
            ["Adapters:Mavlink:Port"] = "0",
        };

        builder.ConfigureAppConfiguration(
            builderConfiguration => builderConfiguration.AddInMemoryCollection(configuration));

        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
