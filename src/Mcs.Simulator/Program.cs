using Mcs.Simulator;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Formatting.Compact;

// Stage one of Serilog's two-stage init, matching Mcs.Api. This logger only has to survive until
// the host is built, but without it a failure during construction -- a bad route, an unresolvable
// station -- kills the container while printing nothing.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

    // Stage two: levels come from the Serilog section of appsettings, so
    // Serilog__MinimumLevel__Default can turn logging up on a running container without a rebuild.
    // The sink stays in code. Reading it from configuration as well meant that a container missing
    // its appsettings.json -- the exact case this project's csproj warns about -- built a logger
    // with no sinks at all, and then the configuration error below was written to nowhere: the
    // process exited non-zero having printed not one byte, and `docker compose logs` was empty.
    // A stage-one logger that prints and a stage-two logger that does not is worse than no
    // two-stage init at all. An operator who adds a WriteTo section gets that sink as well as this.
    builder.Services.AddSerilog((services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(new CompactJsonFormatter()));

    // The simulation clock, injected rather than read from DateTimeOffset.UtcNow anywhere. A
    // vehicle has no more business reading a wall clock directly than the station does.
    builder.Services.AddSingleton(TimeProvider.System);

    // ValidateOnStart, so a bad Simulator section stops the host with the offending setting named.
    // It matters as much here as it does for the station's adapter and for the same reason: an
    // aircraft flying a circuit somewhere nobody meant, or transmitting to an address nothing is
    // listening on, is indistinguishable from a healthy one from inside this process.
    builder.Services
        .AddOptions<SimulatorOptions>()
        .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHostedService<SimulatedVehicleService>();

    IHost host = builder.Build();

    host.Run();

    return 0;
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "Mcs.Simulator terminated unexpectedly.");

    // Non-zero, so the thing that started this process knows. A container that exits 0 after a
    // fatal error is one the runtime will not restart and a CI step that will not go red -- the
    // failure gets reported as success, which is the class of lie the station itself is built to
    // avoid telling.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
