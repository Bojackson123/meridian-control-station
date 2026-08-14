using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Serialization;

using Mcs.Api.Persistence;
using Mcs.Api.Telemetry;
using Mcs.Core;

using Xunit.Abstractions;

namespace Mcs.Integration.Tests;

/// <summary>
/// How long the station takes to put a frame in front of a client, with the store full at twelve
/// vehicles (MCS-001).
/// </summary>
/// <remarks>
/// <b>This is the one requirement in the baseline that needs an instrument rather than an
/// assertion</b>, and the one most likely to be marked verified on the strength of "it looks fast".
/// So it is measured, at the fleet size the console was designed for, and the numbers are written to
/// the test output where a run records them.
/// <para>
/// <b>What this measures and what it does not.</b> The clock starts immediately before the frame
/// enters <see cref="TelemetryIngest"/> and stops when a client has parsed it off the stream, so it
/// covers admission, fan-out, projection, JSON serialisation, SSE framing and the client's own
/// deserialisation -- everything the station does with a frame. It does not cover the kernel's
/// loopback, nginx, or the browser: <see cref="StationApplication"/> is a
/// <c>WebApplicationFactory</c> and its transport is in memory. The rest of the path is measured in
/// the browser, against the running stack, and the two halves are composed in
/// <c>docs/requirements.md</c> rather than pretended to be one number here.
/// </para>
/// <para>
/// <b>One clock throughout.</b> Both readings come from <see cref="TimeProvider.System"/>, in the
/// one process that holds the station and the client, so this is an elapsed monotonic duration and
/// not the subtraction of two calendar times taken in two places.
/// </para>
/// <para>
/// The reported age travels beside it. Every frame carries the station's own account of how old it
/// was at the moment it was serialised, so the run yields an inside number and an outside number
/// that have to agree about the same event -- and a projection that stamped the age at the wrong
/// moment would show up as the two diverging.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
//  Tagged on the class rather than the method: measuring the budget is the whole of what this
//  suite does, so a per-method tag would say the same thing once per measurement.
[Verifies("MCS-001")]
public class TelemetryLatencyTests
{
    /// <summary>A full store, because the number is only interesting at the size it was designed for.</summary>
    private const int VehicleCount = ITelemetryStore.MaxVehicles;

    /// <summary>How many rounds each vehicle reports after the stream is open.</summary>
    /// <remarks>
    /// Twenty at <see cref="RoundPeriod"/> is five seconds of feed and 240 measured frames -- enough
    /// that the worst one is a property of the station rather than of whichever frame happened to
    /// land during a garbage collection, and short enough that this stays a test rather than a
    /// benchmark.
    /// </remarks>
    private const int MeasuredRounds = 20;

    /// <summary>
    /// The gap between rounds: the position rate the simulator actually transmits at (4 Hz).
    /// </summary>
    /// <remarks>
    /// Each round reports all twelve vehicles back to back, which is the burst the fan-out has to
    /// survive rather than an evenly spread arrival. Real vehicles interleave; measuring the
    /// interleaved case would report a smaller number for a load the station does not face.
    /// </remarks>
    private static readonly TimeSpan RoundPeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>MCS-001's budget, whole: frame receipt at the station to the field on screen.</summary>
    private static readonly TimeSpan DisplayBudget = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The share of <see cref="DisplayBudget"/> this half of the path is allowed.
    /// </summary>
    /// <remarks>
    /// A quarter, leaving the wire and the browser three quarters between them. It is an allocation
    /// rather than a measurement, and it is deliberately far above what the station does -- the
    /// assertion exists to catch a regression of the kind that turns milliseconds into hundreds of
    /// them (a fan-out that started blocking, a projection that started allocating per subscriber),
    /// not to pin the current number. The current number is in the output, and in
    /// <c>docs/notes/latency-at-twelve.md</c>.
    /// <para>
    /// <b>Checked against the 95th percentile, not the worst frame.</b> This runs on every push,
    /// against wall-clock samples, on a shared runner sharing its cores with a Postgres container:
    /// one CPU-steal stall or one garbage collection landing on one of 240 frames would redden an
    /// unrelated change, and a suite that goes red for no defect is one people learn to re-run
    /// rather than read. A sustained regression moves the whole distribution and fails this;
    /// a single stalled frame does not, and is caught by <see cref="DisplayBudget"/> below.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StationShareOfTheBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounds the whole run: five seconds of feed, and the rest is the station starting.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Matches what the API writes: camelCase, and enums as names.</summary>
    private static readonly JsonSerializerOptions WireFormat =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly PostgresFixture _postgres;
    private readonly ITestOutputHelper _output;

    public TelemetryLatencyTests(PostgresFixture postgres, ITestOutputHelper output)
    {
        _postgres = postgres;
        _output = output;
    }

    [Fact]
    public async Task Stream_AtTwelveVehicles_DeliversEveryFrameWellInsideTheDisplayBudget()
    {
        await using StationApplication application = await StartAsync();
        using HttpClient client = application.CreateClient();

        TestVehicle[] fleet =
        [
            .. Enumerable.Range(1, VehicleCount)
                .Select(index => new TestVehicle(application.Services, $"TEST-{index:00}"))
        ];

        //  One report each before the stream opens, so the subscription seeds with a full fleet.
        //  Those twelve arrivals are discarded below: their latency includes opening the stream,
        //  which is a different question from how fast a frame reaches an open one.
        foreach (TestVehicle vehicle in fleet)
        {
            vehicle.Report();
        }

        using CancellationTokenSource deadline = new(Patience);

        //  ResponseHeadersRead, or the client buffers a body that does not end.
        using HttpResponseMessage response = await client.GetAsync(
            TelemetryEndpoints.StreamPath, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);

        ConcurrentQueue<Arrival> arrivals = new();

        //  Started before the first measured report and left running: a reader that only ran between
        //  writes would measure its own scheduling. Not awaited here -- the writer loop below yields
        //  on every delay, which is what lets this make progress.
        Task reader = ReadArrivalsAsync(
            body, arrivals, VehicleCount * (MeasuredRounds + 1), deadline.Token);

        Dictionary<string, List<long>> reportedAt =
            fleet.ToDictionary(vehicle => vehicle.Id, _ => new List<long>(MeasuredRounds));

        for (int round = 0; round < MeasuredRounds; round++)
        {
            await Task.Delay(RoundPeriod, deadline.Token);

            foreach (TestVehicle vehicle in fleet)
            {
                //  Stamped before Report rather than inside it: the receipt is taken a few
                //  microseconds later, so this over-reports slightly, which is the safe direction
                //  for a budget.
                reportedAt[vehicle.Id].Add(TimeProvider.System.GetTimestamp());
                vehicle.Report();
            }
        }

        await reader;

        List<TimeSpan> latencies = new(VehicleCount * MeasuredRounds);
        long worstReportedAge = 0;

        foreach (TestVehicle vehicle in fleet)
        {
            Arrival[] delivered = [.. arrivals.Where(arrival => arrival.Id == vehicle.Id)];

            //  Nothing dropped, and nothing duplicated. A missing frame here would be the store's
            //  subscriber queue overflowing at the console's own fleet size, which would make every
            //  latency below a measurement of the frames that happened to survive.
            Assert.Equal(MeasuredRounds + 1, delivered.Length);

            for (int round = 0; round < MeasuredRounds; round++)
            {
                //  delivered[0] is the seed; round n is the arrival after it.
                Arrival arrival = delivered[round + 1];

                latencies.Add(TimeProvider.System.GetElapsedTime(
                    reportedAt[vehicle.Id][round], arrival.Timestamp));

                worstReportedAge = Math.Max(worstReportedAge, arrival.ReportedAgeMilliseconds);
            }
        }

        latencies.Sort();

        TimeSpan worst = latencies[^1];
        TimeSpan median = latencies[latencies.Count / 2];
        TimeSpan ninetyFifth = latencies[(int)(latencies.Count * 0.95)];

        _output.WriteLine(
            $"MCS-001, station half, {VehicleCount} vehicles x {MeasuredRounds} rounds "
            + $"({latencies.Count} frames at {1 / RoundPeriod.TotalSeconds:0.#} Hz each): "
            + $"median {median.TotalMilliseconds:0.00} ms, "
            + $"p95 {ninetyFifth.TotalMilliseconds:0.00} ms, "
            + $"worst {worst.TotalMilliseconds:0.00} ms. "
            + $"Worst age the station reported for a frame: {worstReportedAge} ms.");

        Assert.True(
            ninetyFifth < StationShareOfTheBudget,
            $"95% of {latencies.Count} frames reached a client within "
            + $"{ninetyFifth.TotalMilliseconds:0.00} ms, past the "
            + $"{StationShareOfTheBudget.TotalMilliseconds:0} ms this half of MCS-001's "
            + $"{DisplayBudget.TotalSeconds:0} s budget is allowed.");

        //  And no single frame may spend the whole of MCS-001 on its own. A stalled frame is
        //  survivable where a stalled distribution is not -- but a frame that took a second to
        //  cross the station has left nothing for the browser, and the requirement is per field
        //  rather than on average.
        Assert.True(
            worst < DisplayBudget,
            $"the slowest of {latencies.Count} frames took {worst.TotalMilliseconds:0.00} ms to "
            + $"reach a client, which is the whole of MCS-001's "
            + $"{DisplayBudget.TotalSeconds:0} s budget spent before the browser sees it.");

        //  The station's own account of the same frames. If this ever exceeds the outside
        //  measurement, the age is being stamped somewhere other than where the frame is written.
        Assert.True(
            worstReportedAge <= worst.TotalMilliseconds + 1,
            $"the station reported an age of {worstReportedAge} ms for a frame a client had in "
            + $"{worst.TotalMilliseconds:0.00} ms; the age is being taken at the wrong moment.");
    }

    /// <summary>Starts the application against a fresh, empty database.</summary>
    private async Task<StationApplication> StartAsync()
    {
        StationApplication application = new(
            await _postgres.CreateDatabaseAsync(nameof(TelemetryLatencyTests)));

        //  The host is built lazily on first use, and the migration runs as part of starting it.
        _ = application.Services.GetService(typeof(SchemaMigrator));

        return application;
    }

    /// <summary>
    /// Reads <c>telemetry</c> events off an open stream until <paramref name="expected"/> vehicle
    /// entries have arrived, stamping each with the moment it became available to a caller.
    /// </summary>
    /// <remarks>
    /// The stamp is taken after the parser has deserialised the payload, so the client's own JSON
    /// cost is inside the measurement. It belongs there: a console cannot render a frame it has not
    /// finished parsing.
    /// <para>
    /// <c>fleet</c> events are skipped. They restate vehicles that reported earlier, so counting one
    /// as an arrival would credit the station with delivering a frame it had already delivered.
    /// </para>
    /// </remarks>
    private static async Task ReadArrivalsAsync(
        Stream body,
        ConcurrentQueue<Arrival> arrivals,
        int expected,
        CancellationToken cancellationToken)
    {
        SseParser<VehicleFrameResponse[]?> parser = SseParser.Create<VehicleFrameResponse[]?>(
            body,
            static (_, data) => JsonSerializer.Deserialize<VehicleFrameResponse[]>(data, WireFormat));

        await foreach (SseItem<VehicleFrameResponse[]?> item in
            parser.EnumerateAsync(cancellationToken))
        {
            if (item.EventType != TelemetryEndpoints.TelemetryEventType)
            {
                continue;
            }

            long stamp = TimeProvider.System.GetTimestamp();

            foreach (VehicleFrameResponse vehicle in item.Data ?? [])
            {
                arrivals.Enqueue(new Arrival(vehicle.VehicleId, stamp, vehicle.AgeMilliseconds));
            }

            if (arrivals.Count >= expected)
            {
                return;
            }
        }

        Assert.Fail(
            $"the stream ended after {arrivals.Count} of {expected} expected telemetry frames.");
    }

    /// <summary>One frame reaching a client: which vehicle, when, and how old the station said it was.</summary>
    private readonly record struct Arrival(string Id, long Timestamp, long ReportedAgeMilliseconds);
}
