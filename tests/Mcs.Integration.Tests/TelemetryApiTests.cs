using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Serialization;

using Mcs.Api.Persistence;
using Mcs.Api.Telemetry;
using Mcs.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Mcs.Integration.Tests;

/// <summary>
/// The two telemetry endpoints, served by the real application against a real Postgres.
/// </summary>
/// <remarks>
/// These pin the wire contract that the map console and the smoke suite are both written against,
/// so a change to either endpoint's output fails here first. What they prove that a unit test
/// cannot: the framing that reaches a socket, the JSON that reaches a parser, and that a client
/// hanging up releases what it held.
/// <para>
/// <b>Every wait is bounded and every failure names what it was waiting for.</b> An SSE test that
/// hangs burns a CI job's whole timeout before telling anyone anything.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TelemetryApiTests
{
    /// <summary>How long any of these will wait for the station to do something.</summary>
    /// <remarks>
    /// Nothing here now waits on a feed's rate -- <see cref="TestVehicle"/> reports when the test
    /// says so -- so this bounds the station's own work: a request served, a subscription released.
    /// Still generous, because the cost of a tight bound is a suite that goes red on a loaded CI
    /// runner for no defect.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Matches what the API writes: camelCase, and enums as names. Spelled out here rather than
    /// taken from the host, so a change to the server's options fails a test instead of being
    /// silently agreed with.
    /// </summary>
    private static readonly JsonSerializerOptions WireFormat =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly PostgresFixture _postgres;

    public TelemetryApiTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Vehicles_ReturnsAVehicleOnceItHasReported()
    {
        await using StationApplication application =
            await StartAsync(nameof(Vehicles_ReturnsAVehicleOnceItHasReported));
        using HttpClient client = application.CreateClient();

        TestVehicle vehicleUnderTest = new(application.Services);
        vehicleUnderTest.Report();

        //  Bounds the request rather than a poll -- there is nothing to wait for. Left untokened it
        //  would fall back to HttpClient's own 100 seconds and fail naming neither the endpoint nor
        //  the wait, which is the hang this class is arranged to avoid.
        using CancellationTokenSource deadline = new(Patience);

        //  No polling: the frame is in the store before the request is made, so an empty snapshot
        //  here is a defect rather than a race, and it fails immediately instead of after fifteen
        //  seconds of looking.
        VehicleFrameResponse[] vehicles = await client.GetFromJsonAsync<VehicleFrameResponse[]>(
            TelemetryEndpoints.SnapshotPath, WireFormat, deadline.Token) ?? [];

        VehicleFrameResponse vehicle = Assert.Single(vehicles);

        Assert.Equal(vehicleUnderTest.Id, vehicle.VehicleId);
        Assert.Equal(LinkStatus.Healthy, vehicle.LinkStatus);
        Assert.Equal(AltitudeReference.Msl, vehicle.Altitude.Reference);

        //  UTC with the offset, not local and not epoch. A zero offset is what makes the timestamp
        //  comparable across a station, a container and a browser in three different zones.
        Assert.Equal(TimeSpan.Zero, vehicle.ReceivedAtUtc.Offset);

        //  MCS-002's answer travels with the frame, worked out station-side. A vehicle that reported
        //  a moment ago is Live with an age in the milliseconds -- and the age is asserted as a
        //  bound rather than a value because this one runs on the real clock.
        Assert.Equal(VehicleState.Live, vehicle.State);
        Assert.InRange(
            vehicle.AgeMilliseconds, 0, (long)TelemetryCurrency.StaleAfter.TotalMilliseconds);
    }

    [Fact]
    public async Task Vehicles_WhenNothingHasReported_IsAnEmptyArrayRatherThanNotFound()
    {
        // A console asking "what is flying?" before anything has is asking a valid question, and
        // "nothing" is a valid answer to it. A 404 would put the client's error path and its
        // empty-fleet path on the same branch, which is how an outage comes to render as a calm,
        // empty map.
        await using StationApplication application = await StartAsync(
            nameof(Vehicles_WhenNothingHasReported_IsAnEmptyArrayRatherThanNotFound),
            configureServices: RemoveEveryAdapter);
        using HttpClient client = application.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(TelemetryEndpoints.SnapshotPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        VehicleFrameResponse[]? snapshot =
            await response.Content.ReadFromJsonAsync<VehicleFrameResponse[]>(WireFormat);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task Stream_IsFramedAsServerSentEventsAndTheVehicleMoves()
    {
        await using StationApplication application =
            await StartAsync(nameof(Stream_IsFramedAsServerSentEventsAndTheVehicleMoves));
        using HttpClient client = application.CreateClient();

        TestVehicle vehicleUnderTest = new(application.Services);

        //  Reported before the stream is opened, not after: Subscribe seeds a new subscriber with
        //  the latest frame per vehicle under the same gate it registers on, and that seed is what
        //  puts the first bytes on the wire. Without a frame already in the store this request
        //  would block on its own headers until the first fleet tick fired.
        vehicleUnderTest.Report();

        using CancellationTokenSource deadline = new(Patience);
        using HttpResponseMessage response = await OpenStreamAsync(client, deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        //  nginx buffers a proxied response by default, and a buffered SSE stream arrives in
        //  bursts -- which reads as a stuttering feed and is never diagnosed as a proxy setting.
        Assert.True(
            response.Headers.TryGetValues("X-Accel-Buffering", out IEnumerable<string>? buffering),
            "the stream did not set X-Accel-Buffering, so nginx will buffer it.");
        Assert.Equal("no", Assert.Single(buffering));

        await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);

        //  The second one live, and it cannot be missed: registration is eager rather than deferred
        //  to the first enumeration, so by the time this request has a response at all the
        //  subscription exists and is buffering.
        vehicleUnderTest.Report();

        IReadOnlyList<VehicleFrameResponse> frames =
            await ReadTelemetryAsync(body, count: 2, deadline.Token);

        //  Two frames from a moving vehicle have to differ somewhere. Equal positions mean a
        //  repeated frame or a stalled course, and both render as a live map showing something
        //  that is not happening.
        Assert.True(
            frames[0].LatitudeDegrees != frames[1].LatitudeDegrees
                || frames[0].LongitudeDegrees != frames[1].LongitudeDegrees,
            "two consecutive stream frames reported the same position.");

        Assert.True(
            frames[1].ReceivedAtUtc >= frames[0].ReceivedAtUtc,
            "the stream delivered an older frame after a newer one.");
    }

    [Fact]
    public async Task Stream_KeepsAgeingAVehicleThatHasStoppedReporting()
    {
        // The mitigation, end to end and over a socket. A vehicle that has gone quiet produces no
        // frames, so a stream carrying only frames could never say it had gone quiet -- the console
        // would hold the last thing it was told, indefinitely, about the one vehicle the operator
        // most needs to know about. The fleet tick is what makes that impossible.
        await using StationApplication application =
            await StartAsync(nameof(Stream_KeepsAgeingAVehicleThatHasStoppedReporting));
        using HttpClient client = application.CreateClient();

        //  Reports exactly once and then never again, which is the whole scenario.
        new TestVehicle(application.Services).Report();

        using CancellationTokenSource deadline = new(Patience);
        using HttpResponseMessage response = await OpenStreamAsync(client, deadline.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);

        IReadOnlyList<VehicleFrameResponse[]> ticks = await ReadEventsAsync(
            body, TelemetryEndpoints.FleetEventType, count: 2, deadline.Token);

        VehicleFrameResponse first = Assert.Single(ticks[0]);
        VehicleFrameResponse second = Assert.Single(ticks[1]);

        //  The same frame both times -- nothing reported in between -- and the station saying it is
        //  older than it was. That difference is the entire content of the event.
        Assert.Equal(first.ReceivedAtUtc, second.ReceivedAtUtc);
        Assert.True(
            second.AgeMilliseconds > first.AgeMilliseconds,
            $"the fleet tick repeated an age of {first.AgeMilliseconds} ms instead of advancing it.");
    }

    [Fact]
    public async Task Vehicles_CarryTheAltitudeReferenceAsAName()
    {
        // MCS-004 says the reference travels with the value. A wire format that drops it, or that
        // ships it as an integer someone may renumber, puts the requirement back exactly where it
        // came from -- at the boundary. Asserted against raw text rather than a deserialised
        // object, because a deserialiser configured like the server's would agree with either.
        await using StationApplication application =
            await StartAsync(nameof(Vehicles_CarryTheAltitudeReferenceAsAName));
        using HttpClient client = application.CreateClient();

        new TestVehicle(application.Services).Report();

        using CancellationTokenSource deadline = new(Patience);

        string snapshot =
            await client.GetStringAsync(TelemetryEndpoints.SnapshotPath, deadline.Token);

        Assert.Contains("\"altitude\":{", snapshot, StringComparison.Ordinal);
        Assert.Contains("\"reference\":\"Msl\"", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_WhenTheClientHangsUp_ReleasesTheSubscription()
    {
        // Getting this wrong leaks one subscription per page reload: the store keeps writing into
        // a bounded channel nobody will ever read, under its own gate, forever. The symptom is
        // memory climbing across a long demo and pointing at nothing in particular.
        SubscriptionCountingStore? store = null;

        await using StationApplication application = await StartAsync(
            nameof(Stream_WhenTheClientHangsUp_ReleasesTheSubscription),
            configureServices: services =>
            {
                services.AddSingleton<InMemoryTelemetryStore>();
                services.AddSingleton<ITelemetryStore>(provider =>
                    store = new SubscriptionCountingStore(
                        provider.GetRequiredService<InMemoryTelemetryStore>()));
            });
        using HttpClient client = application.CreateClient();

        //  Before the stream opens, so the subscription's seed carries it and the response does not
        //  wait on a fleet tick for its first byte.
        new TestVehicle(application.Services).Report();

        using (CancellationTokenSource abort = new(Patience))
        {
            using HttpResponseMessage response = await OpenStreamAsync(client, abort.Token);
            await using Stream body = await response.Content.ReadAsStreamAsync(abort.Token);

            //  Read a frame first, so the handler is demonstrably inside its loop rather than
            //  still being routed -- otherwise this asserts about a subscription never taken.
            _ = await ReadTelemetryAsync(body, count: 1, abort.Token);

            Assert.Equal(1, RequireStore(store).OpenSubscriptions);

            await abort.CancelAsync();
        }

        _ = await EventuallyAsync(
            _ => Task.FromResult(RequireStore(store).OpenSubscriptions),
            open => open == 0,
            "the subscription outlived the client that opened it");
    }

    /// <summary>
    /// Starts the application against a fresh, empty database and waits for it to be listening.
    /// </summary>
    private async Task<StationApplication> StartAsync(
        string label,
        Action<IServiceCollection>? configureServices = null)
    {
        StationApplication application =
            new(await _postgres.CreateDatabaseAsync(label), configureServices);

        //  The host is built lazily on first use, and the migration runs as part of starting it.
        _ = application.Services.GetService(typeof(SchemaMigrator));

        return application;
    }

    /// <summary>
    /// Opens the stream and returns once the headers are in, leaving the body to the caller.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> is the whole point: the default
    /// buffers the entire body before returning, and this body does not end.
    /// </remarks>
    private static Task<HttpResponseMessage> OpenStreamAsync(
        HttpClient client, CancellationToken cancellationToken) =>
        client.GetAsync(
            TelemetryEndpoints.StreamPath,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

    /// <summary>
    /// Reads <paramref name="count"/> events of <paramref name="eventType"/> off an open stream,
    /// ignoring the other kind. Leaves the stream open, so a caller can go on to assert about a live
    /// connection.
    /// </summary>
    /// <remarks>
    /// Parsed with the framework's own <see cref="SseParser"/> rather than by matching strings, so
    /// what these tests accept is what an <c>EventSource</c> accepts -- a payload with a raw newline
    /// in it breaks framing here exactly as it would in a browser.
    /// <para>
    /// Every event carries a list of vehicles, one long for a report and fleet-long for a tick, so
    /// one parser reads both and the event name is the only thing that separates them.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<VehicleFrameResponse[]>> ReadEventsAsync(
        Stream body, string eventType, int count, CancellationToken cancellationToken)
    {
        SseParser<VehicleFrameResponse[]?> parser = SseParser.Create<VehicleFrameResponse[]?>(
            body,
            static (_, data) => JsonSerializer.Deserialize<VehicleFrameResponse[]>(data, WireFormat));

        List<VehicleFrameResponse[]> events = new(count);

        try
        {
            await foreach (SseItem<VehicleFrameResponse[]?> item in
                parser.EnumerateAsync(cancellationToken))
            {
                if (item.EventType != eventType)
                {
                    continue;
                }

                events.Add(item.Data
                    ?? throw new InvalidOperationException($"A {eventType} event carried no payload."));

                if (events.Count == count)
                {
                    return events;
                }
            }
        }
        catch (OperationCanceledException)
        {
            //  Reported below rather than rethrown, so a timed-out read fails as an assertion
            //  naming what it was short of instead of as a bare cancellation.
        }

        Assert.Fail(
            $"expected {count} {eventType} event(s) within {Patience.TotalSeconds:0} s, got {events.Count}.");

        return events;
    }

    /// <summary>The vehicles from <paramref name="count"/> <c>telemetry</c> events, in order.</summary>
    private static async Task<IReadOnlyList<VehicleFrameResponse>> ReadTelemetryAsync(
        Stream body, int count, CancellationToken cancellationToken)
    {
        IReadOnlyList<VehicleFrameResponse[]> events = await ReadEventsAsync(
            body, TelemetryEndpoints.TelemetryEventType, count, cancellationToken);

        //  One vehicle per telemetry event: the event says which vehicle reported, and a second
        //  entry would mean the stream had started batching without anything asking it to.
        return [.. events.Select(Assert.Single)];
    }

    /// <summary>
    /// Polls <paramref name="read"/> until <paramref name="satisfied"/> holds or
    /// <see cref="Patience"/> runs out.
    /// </summary>
    /// <param name="because">
    /// The failure message. Say what was being waited for, not that a timeout happened.
    /// </param>
    private static async Task<T> EventuallyAsync<T>(
        Func<CancellationToken, Task<T>> read, Func<T, bool> satisfied, string because)
    {
        using CancellationTokenSource deadline = new(Patience);

        T last = default!;

        try
        {
            while (true)
            {
                last = await read(deadline.Token);

                if (satisfied(last))
                {
                    return last;
                }

                await Task.Delay(PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"{because} (waited {Patience.TotalSeconds:0} s; last read: {last}).");
            throw;
        }
    }

    /// <summary>
    /// Drops every telemetry source so the store stays empty, leaving every hosted service --
    /// notably the migration -- in place.
    /// </summary>
    /// <remarks>
    /// Every adapter rather than the one the station happens to register today, because "nothing
    /// has reported" is the state under test and a source that is merely silent would make it true
    /// by luck -- the MAVLink adapter binds a port nothing transmits to and would do exactly that.
    /// The service that runs them stays registered and starts with nothing to run, which is a state
    /// the station is expected to survive.
    /// </remarks>
    private static void RemoveEveryAdapter(IServiceCollection services)
    {
        ServiceDescriptor[] adapters = [.. services.Where(
            descriptor => descriptor.ServiceType == typeof(IVehicleAdapter))];

        Assert.NotEmpty(adapters);

        foreach (ServiceDescriptor adapter in adapters)
        {
            services.Remove(adapter);
        }
    }

    /// <summary>
    /// The decorator, once the host has resolved it. Null here means nothing ever asked for the
    /// store, which would make every count in the calling test vacuously right.
    /// </summary>
    private static SubscriptionCountingStore RequireStore(SubscriptionCountingStore? store) =>
        store ?? throw new InvalidOperationException(
            "The counting store was never constructed; nothing resolved ITelemetryStore.");
}
