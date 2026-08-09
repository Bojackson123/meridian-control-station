using Mcs.Api.FakeFeed;
using Mcs.Api.Observability;
using Mcs.Core;
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

    var app = builder.Build();

    app.UseCorrelationId();

    // One summary line per request in place of ASP.NET's several.
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = static (httpContext, _, exception) =>
            exception is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
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

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Mcs.Api terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
