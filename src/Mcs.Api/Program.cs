using Mcs.Api.FakeFeed;
using Mcs.Api.Observability;
using Mcs.Api.Persistence;
using Mcs.Core;
using Npgsql;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Stage one of Serilog's two-stage init. This logger only has to survive until the host is built,
// but without it a failure during construction -- bad config, missing connection string -- kills the
// container while printing nothing.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Stage two: levels and sinks come from the Serilog section of appsettings, so
    // Serilog__MinimumLevel__Default can turn logging up on a running container (ticket 11) without
    // a rebuild. FromLogContext stays in code -- CorrelationIdMiddleware depends on it, so it is not
    // an operator knob to be switched off.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Add services to the container.
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    // The station clock, injected rather than read from DateTimeOffset.UtcNow anywhere.
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();
    builder.Services.AddSingleton<TelemetryIngest>();

    // ValidateOnStart, so a bad FakeFeed section stops the host with the offending setting named.
    builder.Services
        .AddOptions<FakeFeedOptions>()
        .Bind(builder.Configuration.GetSection(FakeFeedOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHostedService<FakeVehicleFeed>();

    // Nothing durable is written yet -- mission plans, the command lifecycle, overrides and alert
    // acknowledgements are what this database exists for, and each arrives with the feature that
    // defines it. What is real now is the mechanism: a migration ledger applied on startup and read
    // back over HTTP. A Postgres container that nothing talks to would be worse than none, because
    // it puts a claim in the stack diagram that the software does not honour.
    builder.Services
        .AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // One pooled data source for the process. Npgsql logs through the same Serilog pipeline as
    // everything else, so a connection failure is a structured record rather than a message that
    // exists only inside an exception nobody printed.
    builder.Services.AddSingleton(serviceProvider =>
    {
        // Read here rather than off the builder above, because configuration is not final until the
        // host is built -- the integration tests supply the connection string that way, and reading
        // early would mean the tests exercised a startup path the deployment does not use.
        string connectionString = serviceProvider
            .GetRequiredService<IConfiguration>()
            .GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // By name, and fatal. No localhost default: an unset variable would otherwise become a
            // connection attempt against whatever the deployment host happens to be running, which
            // succeeds far too often to be safe. This resolves while the host is starting, so the
            // station stops here rather than listening without a database.
            throw new InvalidOperationException(
                $"No '{DatabaseOptions.ConnectionStringName}' connection string is configured. Set "
                + $"{DatabaseOptions.ConnectionStringEnvironmentVariable} in the environment "
                + "(deploy/compose builds it from .env; appsettings.Development.json has one for the "
                + "local inner loop).");
        }

        NpgsqlDataSourceBuilder dataSourceBuilder = new(connectionString);
        dataSourceBuilder.UseLoggerFactory(serviceProvider.GetRequiredService<ILoggerFactory>());

        return dataSourceBuilder.Build();
    });

    builder.Services.AddSingleton<SchemaMigrator>();
    builder.Services.AddHostedService<DatabaseStartup>();

    builder.Services
        .AddHealthChecks()
        .AddCheck<SchemaVersionHealthCheck>(
            SchemaVersionHealthCheck.Name,
            tags: [SchemaVersionHealthCheck.ReadinessTag]);

    var app = builder.Build();

    app.UseCorrelationId();

    // One summary line per request in place of ASP.NET's several.
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = static (httpContext, _, exception) =>
            exception is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : httpContext.Request.Path.StartsWithSegments(HealthEndpoints.LivenessPath)
                    // Probes hit these every few seconds forever. At Information they are most of
                    // the log by volume, and a log nobody scrolls through is a log nobody reads. A
                    // failing probe still surfaces: the 503 takes the Error branch above.
                    ? LogEventLevel.Debug
                    : httpContext.Request.Path.StartsWithSegments("/api/telemetry/stream")
                    // The SSE stream (ticket 07) is a long-lived connection, so its completed line
                    // lands minutes late with an alarming elapsed time. Debug rather than filtered
                    // out: still there when a dropped stream is what you are chasing.
                    ? LogEventLevel.Debug
                    : LogEventLevel.Information;

        options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString());
        };
    });

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }
    // Droped because we will be using nginx behind HTTP.
    // app.UseHttpsRedirection();

    app.MapStationHealthChecks();

    app.Run();

    return 0;
}
// HostAbortedException is how the test host stops the entry point once it has the built
// application; swallowing it here would leave WebApplicationFactory waiting for a host it was
// never handed.
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "Mcs.Api terminated unexpectedly.");

    // Non-zero, so the thing that started this process knows. A container that exits 0 after a
    // fatal error is a container the runtime will not restart and a CI step that will not go red --
    // the failure gets reported as success, which is the same class of lie the station itself is
    // built to avoid telling.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Named so the integration tests can host this application in process.
/// </summary>
/// <remarks>
/// Top-level statements compile to an internal entry point class; <c>WebApplicationFactory</c> needs
/// a public one to locate it. Declaring the partial here is the documented way to opt in, and it
/// keeps the test host running the real startup path -- including the migration -- rather than a
/// second one assembled by the tests.
/// </remarks>
public partial class Program;
