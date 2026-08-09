using System.Globalization;

using Mcs.Core;

using Microsoft.Extensions.Options;

namespace Mcs.Api.FakeFeed;

/// <summary>
/// A hardcoded telemetry source: a timing loop that flies
/// <see cref="FakeFeedOptions.VehicleCount"/> vehicles around a <see cref="CircularCourse"/> and
/// writes a frame each, every tick.
/// </summary>
/// <remarks>
/// This class does two things -- schedule ticks and hand frames to the store. The arithmetic is the
/// course's and the stamping is <c>Mcs.Core</c>'s, so replacing the source later means replacing
/// this class and nothing else; a real adapter copies the ingest-then-write shape below.
/// <para>
/// <b>The stamping location must not move.</b> <c>BeginReceive</c> is the first statement of each
/// frame, before any state is computed, so a frame's age measures the time since the tick fired
/// rather than since the arithmetic finished (MCS-005).
/// </para>
/// </remarks>
public sealed class FakeVehicleFeed : BackgroundService
{
    //  Two digits so ids sort lexicographically: "UAV-9" after "UAV-10" is a papercut for the sake of
    //  one character.
    private const string VehicleIdFormat = "UAV-{0:00}";

    private readonly ITelemetryStore _store;
    private readonly TelemetryIngest _ingest;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FakeVehicleFeed> _logger;

    private readonly CircularCourse _course;
    private readonly Altitude _altitude;
    private readonly TimeSpan _tickPeriod;

    //  Kept alongside the period it produced so the startup line reports the configured rate:
    //  1 / (1 / 3) comes back as 2.9999999999999996.
    private readonly double _rateHz;

    private readonly VehicleId[] _vehicles;
    private readonly double[] _phases;

    /// <summary>
    /// Builds the feed and everything derived from configuration, so a bad setting fails at startup
    /// rather than on the first tick.
    /// </summary>
    public FakeVehicleFeed(
        IOptions<FakeFeedOptions> options,
        ITelemetryStore store,
        TelemetryIngest ingest,
        TimeProvider timeProvider,
        ILogger<FakeVehicleFeed> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        FakeFeedOptions settings = options.Value;

        _course = new CircularCourse(
            settings.OriginLatitudeDegrees,
            settings.OriginLongitudeDegrees,
            settings.RadiusMeters,
            settings.OrbitPeriodSeconds,
            settings.EnduranceSeconds);

        _altitude = Altitude.FromMeters(settings.AltitudeMetersMsl, AltitudeReference.Msl);
        _rateHz = settings.RateHz;
        _tickPeriod = TimeSpan.FromSeconds(1.0 / settings.RateHz);

        _vehicles = new VehicleId[settings.VehicleCount];
        _phases = new double[settings.VehicleCount];

        for (int i = 0; i < settings.VehicleCount; i++)
        {
            _vehicles[i] = VehicleId.From(
                string.Format(CultureInfo.InvariantCulture, VehicleIdFormat, i + 1));

            //  Evenly spaced around the lap, so a raised vehicle count produces a ring rather than a
            //  stack of markers on one pixel.
            _phases[i] = (double)i / settings.VehicleCount;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //  Monotonic: the course is evaluated at elapsed time, and a wall-clock difference would fly
        //  the vehicle backwards through an NTP correction.
        long startedTimestamp = _timeProvider.GetTimestamp();

        //  PeriodicTimer over Task.Delay in a loop, which restarts its wait after each iteration's
        //  work and so sheds a few milliseconds every tick. Built with the injected TimeProvider so
        //  the schedule is drivable by a test clock.
        using PeriodicTimer timer = new(_tickPeriod, _timeProvider);

        _logger.LogInformation(
            "Fake vehicle feed started: {VehicleCount} vehicle(s) at {RateHz} Hz on {Course}.",
            _vehicles.Length,
            _rateHz,
            _course);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                PublishTick(_timeProvider.GetElapsedTime(startedTimestamp));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            //  Ordinary shutdown. Left to propagate it reaches the host as a faulted background
            //  service and is logged as a crash on every clean Ctrl-C.
        }

        _logger.LogInformation("Fake vehicle feed stopped.");
    }

    /// <summary>Writes one frame per vehicle for a single tick.</summary>
    private void PublishTick(TimeSpan elapsed)
    {
        for (int i = 0; i < _vehicles.Length; i++)
        {
            //  First statement of the frame -- see the remarks on this type.
            TelemetryReceipt receipt = _ingest.BeginReceive();

            CourseState state = _course.At(elapsed, _phases[i]);

            VehicleTelemetry telemetry = VehicleTelemetry.Create(
                _vehicles[i],
                state.LatitudeDegrees,
                state.LongitudeDegrees,
                _altitude,
                state.GroundSpeedMetersPerSecond,
                state.HeadingDegrees,
                state.BatteryPercent,
                LinkStatus.Healthy);

            try
            {
                _store.Write(receipt.Complete(telemetry));
            }
            catch (TelemetryStoreCapacityExceededException ex)
            {
                //  Unreachable from this feed alone -- VehicleCount is capped at the store's own
                //  MaxVehicles -- but the store is shared. Catching the dedicated type keeps a genuine
                //  bug inside Write from being swallowed here.
                _logger.LogWarning(
                    ex, "Fake vehicle feed could not record a frame for {VehicleId}.", ex.RejectedId);
            }

            //  Debug, not Information: twelve vehicles at 10 Hz is 120 lines a second. The whole
            //  record, so this line stays in step with the model, and its ToString is invariant.
            _logger.LogDebug("Fake vehicle feed wrote {Telemetry}.", telemetry);
        }
    }
}
