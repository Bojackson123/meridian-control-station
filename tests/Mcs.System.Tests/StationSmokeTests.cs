using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mcs.System.Tests;

/// <summary>
/// The whole station under <c>docker compose up</c>, driven over HTTP.
/// </summary>
/// <remarks>
/// These are the exit gate written down as assertions, and they should read like it: each one is
/// the smallest observation that would catch its clause being false. Nothing here reaches inside a
/// container -- if a claim cannot be made by a client over HTTP, it is not this suite's claim to
/// make.
/// <para>
/// <b>Rendering is deliberately out of scope.</b> No headless browser and no screenshot diffing:
/// the assertions below confirm the console's assets are served, and the demo recording and a pair
/// of eyes confirm they draw. That trade is what keeps this suite fast, and fast is what keeps it
/// from being the step people skip.
/// </para>
/// <para>
/// <b>Nothing here retries.</b> Every wait is a bounded deadline on a single attempt, and every
/// failure names what was expected and what arrived, because a smoke suite that goes green on the
/// second run is one that gets re-run rather than read.
/// </para>
/// </remarks>
[Collection(SmokeCollection.Name)]
public class StationSmokeTests
{
    /// <summary>Bound on anything that answers in one response.</summary>
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bound on a stream read. Generous against the aircraft's 4 Hz position reports -- sixty
    /// frames' worth for the two these tests want, and it was already generous at the 1 Hz the
    /// station used to fly -- because the cost of a tight bound is a red build on a loaded runner
    /// with no defect behind it, and the cost of no bound at all is a job that hangs until the
    /// runner's own limit kills it hours later with nothing to say.
    /// </summary>
    private static readonly TimeSpan StreamBudget = TimeSpan.FromSeconds(15);

    /// <summary>The datums MCS-004 allows. A frame reporting anything else is not readable.</summary>
    private static readonly string[] AltitudeReferences = ["Msl", "Agl", "Hae"];

    /// <summary>The three states MCS-002 defines. Spelled out here rather than imported, as ever.</summary>
    private static readonly string[] VehicleStates = ["Live", "Stale", "Lost"];

    /// <summary>
    /// MCS-002's stale threshold, restated. A vehicle being flown by the simulator in the same
    /// Compose network and reporting at 4 Hz is nowhere near it.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private readonly SmokeStackFixture _stack;

    public StationSmokeTests(SmokeStackFixture stack) => _stack = stack;

    /// <summary>G2 -- the API container is up and serving.</summary>
    [SmokeFact]
    public async Task Liveness_Answers()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        HealthReport health = await ReadJsonAsync<HealthReport>(
            _stack.Api, Routes.Liveness, deadline.Token);

        Assert.Equal("Healthy", health.Status);

        //  Liveness runs no checks at all, so it carries no schema version. If one appears here,
        //  a readiness check has been registered without a tag and liveness can now go red for a
        //  database fault -- which would have Compose restarting a station that is correctly
        //  refusing to serve.
        Assert.Null(health.SchemaVersion);
        Assert.Null(health.ExpectedSchemaVersion);
    }

    /// <summary>G3 -- Postgres is in the stack and the station migrated it, not just reached it.</summary>
    [SmokeFact]
    public async Task Readiness_ReportsTheSchemaTheStationMigratedTo()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        using HttpResponseMessage response = await GetAsync(
            _stack.Api, Routes.Readiness, HttpCompletionOption.ResponseContentRead, deadline.Token);

        HealthReport health = await ReadJsonAsync<HealthReport>(response, deadline.Token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"readiness answered {(int)response.StatusCode}: {health.Detail ?? health.Status}.");

        int expected = Assert.NotNull(health.ExpectedSchemaVersion);

        Assert.True(expected >= 1, $"the build expects schema version {expected}; versions start at 1.");

        //  Compared against what the build expects rather than against a literal. The invariant is
        //  "the database is at the version this station was compiled against", and a hardcoded 1
        //  would have to be edited every time a migration ships -- which is how a constant nobody
        //  remembers to update becomes an assertion that no longer asserts anything.
        Assert.True(
            health.SchemaVersion == expected,
            $"the database is at schema version {health.SchemaVersion?.ToString() ?? "none"} and "
                + $"this build expects {expected}.");
    }

    /// <summary>G1 -- there is a vehicle, and its altitude says what it is measured against.</summary>
    [SmokeFact]
    public async Task Snapshot_ShowsAVehicleWithAnAltitudeReference()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        VehicleFrame[] fleet = await ReadJsonAsync<VehicleFrame[]>(
            _stack.Api, Routes.Snapshot, deadline.Token);

        Assert.True(fleet.Length > 0, "the snapshot is empty; nothing is flying.");

        VehicleFrame vehicle = fleet[0];

        Assert.False(
            string.IsNullOrWhiteSpace(vehicle.VehicleId), "a vehicle arrived without an id.");

        //  An altitude without its datum is a number nobody can act on -- 300 above sea level and
        //  300 above the ground are different places, and converting between them needs terrain
        //  the station does not hold (MCS-004).
        Assert.Contains(vehicle.Altitude.Reference, AltitudeReferences);
    }

    /// <summary>
    /// G9 -- the station, not the browser, says how current each vehicle is.
    /// </summary>
    /// <remarks>
    /// The age arrives already computed, which is the point of asserting it here: a console that
    /// worked it out from <c>receivedAtUtc</c> and its own clock would render a live aircraft as
    /// lost, or a lost one as live, on any machine whose clock is off -- and this suite's client has
    /// no more claim to a correct clock than a browser does.
    /// </remarks>
    [SmokeFact]
    public async Task Snapshot_SaysHowCurrentEachVehicleIs()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        VehicleFrame[] fleet = await ReadJsonAsync<VehicleFrame[]>(
            _stack.Api, Routes.Snapshot, deadline.Token);

        Assert.True(fleet.Length > 0, "the snapshot is empty; nothing is flying.");

        VehicleFrame vehicle = fleet[0];

        Assert.Contains(vehicle.State, VehicleStates);

        //  An aircraft being flown by the simulator right now is Live, and its age is a fraction of
        //  its 4 Hz reporting interval. A stale one here means the simulator is not transmitting,
        //  the adapter is not receiving, or the two are on different ports -- all of which look
        //  identical on a map that does not say how old its markers are.
        Assert.Equal("Live", vehicle.State);
        Assert.InRange(vehicle.AgeMilliseconds, 0, (long)StaleAfter.TotalMilliseconds);
    }

    /// <summary>
    /// G9 -- the fleet is re-stated on a schedule, so an age advances with nothing arriving.
    /// </summary>
    [SmokeFact]
    public async Task Stream_TicksTheWholeFleet()
    {
        using CancellationTokenSource deadline = new(StreamBudget);
        using HttpResponseMessage response = await OpenStreamAsync(_stack.Api, deadline.Token);

        AssertIsEventStream(_stack.Api, response);

        await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);

        await foreach (VehicleFrame[] tick in
            EventsAsync(body, Routes.FleetEventType, deadline.Token))
        {
            //  The tick carries every vehicle the station holds, which is what makes it the answer
            //  for the ones that have stopped reporting: they appear here and nowhere else.
            Assert.True(tick.Length > 0, "the fleet tick was empty; nothing is flying.");
            Assert.All(tick, vehicle => Assert.Contains(vehicle.State, VehicleStates));

            return;
        }

        Assert.Fail(
            $"no {Routes.FleetEventType} event arrived within {StreamBudget.TotalSeconds:0} s; a "
                + "vehicle that goes quiet would never be reported stale.");
    }

    /// <summary>G1 -- the stream is live, straight from the API.</summary>
    [SmokeFact]
    public async Task Stream_EmitsTelemetryEvents()
    {
        using CancellationTokenSource deadline = new(StreamBudget);
        using HttpResponseMessage response =
            await OpenStreamAsync(_stack.Api, deadline.Token);

        AssertIsEventStream(_stack.Api, response);

        IReadOnlyList<VehicleFrame> frames = await ReadTelemetryAsync(
            await response.Content.ReadAsStreamAsync(deadline.Token), 2, deadline.Token);

        Assert.Equal(2, frames.Count);
    }

    /// <summary>
    /// G1 -- the vehicle is flying, not merely present.
    /// </summary>
    /// <remarks>
    /// The one assertion here that separates "the endpoint responds" from "the skeleton walks". An
    /// aircraft frozen at a position it once reported satisfies every other test in this file.
    /// </remarks>
    [SmokeFact]
    public async Task Stream_ShowsTheVehicleActuallyMoving()
    {
        using CancellationTokenSource deadline = new(StreamBudget);
        using HttpResponseMessage response =
            await OpenStreamAsync(_stack.Api, deadline.Token);

        //  Checked here as well as in the tests either side of this one, because a stream that
        //  answers 503 parses as zero events and would otherwise report this test's own message --
        //  that the vehicle is frozen. Of everything in this file, this is the assertion that must
        //  not name the wrong fault.
        AssertIsEventStream(_stack.Api, response);

        (VehicleFrame first, VehicleFrame second) = await ReadConsecutiveFramesAsync(
            await response.Content.ReadAsStreamAsync(deadline.Token), deadline.Token);

        //  Exact inequality, no epsilon. At 22 m/s and 4 Hz the aircraft covers about 5.5 m between
        //  position reports, which is ~5e-5 degrees of latitude -- and the wire carries them as
        //  int32 degE7, so the smallest step either coordinate can take is 1e-7. Two orders of
        //  magnitude of headroom on a quantised value that cannot drift. An epsilon added here
        //  later "to be safe" would be the width of the gap a stopped vehicle slips through.
        Assert.True(
            first.LatitudeDegrees != second.LatitudeDegrees
                || first.LongitudeDegrees != second.LongitudeDegrees,
            $"{first.VehicleId} reported the same position twice running: "
                + $"{first.LatitudeDegrees}, {first.LongitudeDegrees}. It is not being flown.");
    }

    /// <summary>G1 -- the console itself is served.</summary>
    [SmokeFact]
    public async Task WebRoot_ServesTheConsole()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        using HttpResponseMessage response = await GetAsync(
            _stack.Web, Routes.WebRoot, HttpCompletionOption.ResponseContentRead, deadline.Token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{SmokeStack.WebOrigin} answered {(int)response.StatusCode} for the console.");

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// G1, G2 -- the basemap comes from the console's own origin, and still asks nobody else for
    /// anything.
    /// </summary>
    /// <remarks>
    /// The <c>sprite</c> and <c>glyphs</c> check is the offline claim, not a style-file detail.
    /// MapLibre fetches glyph range files from the <c>glyphs</c> URL the moment any layer uses a
    /// text field, so either key appearing is a third-party request re-entering a console that says
    /// it runs with the network off. Asserted here over HTTP, against what the container actually
    /// serves, so it fails a build rather than waiting to be noticed in DevTools.
    /// </remarks>
    [SmokeFact]
    public async Task Basemap_IsServedFromTheWebOrigin()
    {
        using CancellationTokenSource deadline = new(RequestBudget);

        using HttpResponseMessage response = await GetAsync(
            _stack.Web,
            Routes.BasemapStyle,
            HttpCompletionOption.ResponseContentRead,
            deadline.Token);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{SmokeStack.WebOrigin} answered {(int)response.StatusCode} for the basemap style.");

        //  200 is not enough on its own: nginx falls back to index.html for anything it cannot
        //  find, so a build that stopped copying public/basemap serves the console here with a
        //  cheerful 200. Without this the failure is a JSON parser complaining about '<'.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument style = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(deadline.Token));

        Assert.Equal(8, style.RootElement.GetProperty("version").GetInt32());

        Assert.False(
            style.RootElement.TryGetProperty("sprite", out _),
            "the basemap style grew a 'sprite' key, which fetches an image from outside the style.");

        Assert.False(
            style.RootElement.TryGetProperty("glyphs", out _),
            "the basemap style grew a 'glyphs' key; MapLibre will fetch glyph ranges from it.");
    }

    /// <summary>
    /// G1, G2 -- the stream survives the proxy, which is the only way the browser ever sees it.
    /// </summary>
    /// <remarks>
    /// Not redundant with <see cref="Stream_EmitsTelemetryEvents"/>, and the reason this suite hits
    /// two origins at all: the browser only ever reaches the stream through nginx, so a fault that
    /// lives in the proxy is invisible from the API's own port. The sharpest one it catches is a
    /// wrong upstream -- <c>proxy_pass</c> resolves through a variable, which makes a bad service
    /// name a 502 at request time rather than a refusal to boot, and nothing else here is looking.
    /// <para>
    /// <b>What it does not cover, measured rather than assumed.</b> Turning <c>proxy_buffering</c>
    /// on does not make this fail. Buffering lets nginx read ahead of a slow client; against a
    /// client on a local socket there is nothing to decouple and events still arrive at 1 Hz, with
    /// or without it. So the <c>proxy_buffering off</c> line in the nginx config is correct and
    /// unguarded, and this test is not the thing standing behind it -- see
    /// <c>docs/notes/stuck.md</c>, 2026-08-10, before deleting that line on the strength of a
    /// green suite.
    /// </para>
    /// </remarks>
    [SmokeFact]
    public async Task Stream_SurvivesTheProxy()
    {
        using CancellationTokenSource deadline = new(StreamBudget);
        using HttpResponseMessage response =
            await OpenStreamAsync(_stack.Web, deadline.Token);

        AssertIsEventStream(_stack.Web, response);

        IReadOnlyList<VehicleFrame> frames = await ReadTelemetryAsync(
            await response.Content.ReadAsStreamAsync(deadline.Token), 2, deadline.Token);

        Assert.Equal(2, frames.Count);
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
        GetAsync(client, Routes.Stream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <summary>
    /// Issues a GET, turning "nobody was there" into an assertion that names the URL.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="HttpRequestException"/> escaping a test reports a connection refused
    /// against a socket address, which is a fact about a port rather than about the station. Every
    /// request in this file goes through here so that a container being down reads as the endpoint
    /// it took out.
    /// </remarks>
    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetAsync(path, completion, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException)
        {
            Assert.Fail(
                $"{Url(client, path)} did not answer: "
                    + (exception is OperationCanceledException
                        ? "the deadline expired before any response arrived."
                        : exception.Message));

            throw;
        }
    }

    private static void AssertIsEventStream(HttpClient client, HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{Url(client, Routes.Stream)} answered {(int)response.StatusCode}.");

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    private static string Url(HttpClient client, string path) =>
        $"{client.BaseAddress}{path.TrimStart('/')}";

    /// <summary>
    /// Reads <paramref name="count"/> <c>telemetry</c> events off an open stream, ignoring
    /// heartbeats.
    /// </summary>
    /// <remarks>
    /// Parsed with the framework's own <see cref="SseParser"/> rather than by matching strings, so
    /// what this accepts is what an <c>EventSource</c> accepts -- a payload with a raw newline in
    /// it breaks framing here exactly as it would in a browser.
    /// </remarks>
    private static async Task<IReadOnlyList<VehicleFrame>> ReadTelemetryAsync(
        Stream body, int count, CancellationToken cancellationToken)
    {
        List<VehicleFrame> frames = new(count);

        await foreach (VehicleFrame frame in TelemetryAsync(body, cancellationToken))
        {
            frames.Add(frame);

            if (frames.Count == count)
            {
                return frames;
            }
        }

        Assert.Fail(
            $"expected {count} telemetry event(s) within {StreamBudget.TotalSeconds:0} s, got "
                + $"{frames.Count}.");

        return frames;
    }

    /// <summary>
    /// Reads until one vehicle has reported twice, and returns both of its frames in order.
    /// </summary>
    /// <remarks>
    /// Two events off the stream are not necessarily two frames from the same vehicle -- they are
    /// today, because the simulator flies one aircraft, but the fleet grows and a test that quietly
    /// compared two different vehicles' positions would go on passing while saying nothing.
    /// </remarks>
    private static async Task<(VehicleFrame First, VehicleFrame Second)> ReadConsecutiveFramesAsync(
        Stream body, CancellationToken cancellationToken)
    {
        Dictionary<string, VehicleFrame> latest = new(StringComparer.Ordinal);

        await foreach (VehicleFrame frame in TelemetryAsync(body, cancellationToken))
        {
            if (latest.TryGetValue(frame.VehicleId, out VehicleFrame? previous))
            {
                return (previous, frame);
            }

            latest[frame.VehicleId] = frame;
        }

        Assert.Fail(
            $"no vehicle reported twice within {StreamBudget.TotalSeconds:0} s; saw "
                + $"{latest.Count} vehicle(s), one frame each.");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// The telemetry frames on a stream, with fleet ticks dropped and the deadline turned into an
    /// ordinary end of sequence.
    /// </summary>
    /// <remarks>
    /// Swallowing the cancellation is what lets the callers above fail as assertions naming what
    /// they were short of, rather than as a bare <see cref="OperationCanceledException"/> that says
    /// only that some clock somewhere ran out.
    /// <para>
    /// Both event types carry an array -- one vehicle for a report, the whole fleet for a tick -- so
    /// the ticks are skipped by name rather than by shape. Skipping them is right here: what these
    /// callers are asking about is an aircraft reporting, and a tick is the station talking about
    /// one that has not.
    /// </para>
    /// </remarks>
    private static async IAsyncEnumerable<VehicleFrame> TelemetryAsync(
        Stream body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (VehicleFrame[] vehicles in
            EventsAsync(body, Routes.TelemetryEventType, cancellationToken))
        {
            foreach (VehicleFrame frame in vehicles)
            {
                yield return frame;
            }
        }
    }

    /// <summary>The payloads of every <paramref name="eventType"/> event on a stream.</summary>
    private static async IAsyncEnumerable<VehicleFrame[]> EventsAsync(
        Stream body,
        string eventType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SseParser<VehicleFrame[]?> parser = SseParser.Create<VehicleFrame[]?>(
            body,
            static (_, data) => JsonSerializer.Deserialize<VehicleFrame[]>(data, WireFormat.Options));

        IAsyncEnumerator<SseItem<VehicleFrame[]?>> events =
            parser.EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (events.Current.EventType != eventType
                    || events.Current.Data is not VehicleFrame[] vehicles)
                {
                    continue;
                }

                yield return vehicles;
            }
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpClient client, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(
            client, path, HttpCompletionOption.ResponseContentRead, cancellationToken);

        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(WireFormat.Options, cancellationToken)
        ?? throw new InvalidOperationException(
            $"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode} with a "
                + $"body that deserialised to null.");
}
