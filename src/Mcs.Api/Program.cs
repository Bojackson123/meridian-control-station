using Mcs.Api.FakeFeed;
using Mcs.Core;

var builder = WebApplication.CreateBuilder(args);

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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
// Droped because we will be using nginx behind HTTP.
// app.UseHttpsRedirection();

app.Run();
