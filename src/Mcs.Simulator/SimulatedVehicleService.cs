using Mcs.Simulator.Flight;
using Mcs.Simulator.Mavlink;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mcs.Simulator;

/// <summary>
/// The aircraft: a fixed-rate loop that flies the route, and hands whatever MAVLink is due at each
/// step to the link.
/// </summary>
/// <remarks>
/// This class does three things -- schedule steps, ask the follower where to go, and pass the
/// resulting frames to the transmitter. The flight arithmetic is <see cref="AircraftKinematics"/>'s,
/// the message building is <see cref="VehicleMessageEmitter"/>'s and the socket is
/// <see cref="MavlinkTransmitter"/>'s, so each of those can be reasoned about, and tested, without
/// a host.
///
/// <para>
/// <b>Everything is built in the constructor, so a bad setting stops the host rather than the first
/// tick.</b> That includes resolving the station's address: a name that does not resolve is a
/// configuration fault, and a simulator transmitting into nowhere behaves exactly like a healthy one.
/// </para>
///
/// <para>
/// <b>The step is the configured nominal duration, not the measured time since the last tick.</b>
/// Using measured elapsed time would keep the aircraft synchronised with the wall clock on a loaded
/// host, at the cost of making the flight path depend on scheduling jitter -- and the flight path is
/// the thing a deconfliction bound is computed against, so it has to be reproducible. What this
/// gives up is stated plainly: a container starved of CPU flies its aircraft slower than real time.
/// That is a simulator behaving like a simulator, and the station's own staleness measurement,
/// which runs on the station's clock, reports the gap for what it is.
/// </para>
///
/// <para>
/// <b>Elapsed time is a tick count times the step</b> rather than a running sum. Summing a step of
/// 0.05 s ten thousand times accumulates a visible error in the last bits, and the message
/// schedules compare against that number -- so a rate would come out a fraction low over a long
/// flight, which reads as rounding rather than as a bug.
/// </para>
///
/// <para>
/// <b>Cancellation must not escape.</b> A clean <c>docker compose down</c> reported as a crashed
/// background service, every time, is how the one line that matters stops being read.
/// </para>
/// </remarks>
internal sealed class SimulatedVehicleService : BackgroundService
{
    /// <summary>How often the counters are summarised into the log while the aircraft flies.</summary>
    /// <remarks>
    /// The same interval the station's adapter reports on, so the two sides of one link can be read
    /// against each other in a single <c>docker compose logs</c> without arithmetic.
    /// </remarks>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SimulatedVehicleService> _logger;

    private readonly AircraftEnvelope _envelope;
    private readonly WaypointFollower _follower;
    private readonly AircraftKinematics _kinematics;
    private readonly VehicleMessageEmitter _emitter;
    private readonly MavlinkTransmitter _transmitter;

    private readonly TimeSpan _step;
    private readonly double _stepSeconds;
    private readonly MessageRates _rates;

    private AircraftState _state;

    /// <summary>
    /// Builds the aircraft, the route it flies and the link it transmits on, so that anything wrong
    /// with the configuration stops the host here.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any dependency is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The station's address could not be resolved.</exception>
    public SimulatedVehicleService(
        IOptions<SimulatorOptions> options,
        TimeProvider timeProvider,
        ILogger<SimulatedVehicleService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        SimulatorOptions settings = options.Value;

        _envelope = settings.CreateEnvelope();
        IReadOnlyList<Waypoint> route = settings.CreateRoute();

        //  Anchored at the first waypoint, which is also the point relative_alt is measured from.
        //  One origin for both keeps "home" a single fact rather than two that can disagree.
        LocalProjection projection = new(route[0].LatitudeDegrees);

        _follower = new WaypointFollower(
            route, settings.ResolveCaptureRadiusMeters(_envelope), _envelope, projection);

        _kinematics = new AircraftKinematics(_envelope, projection);
        _rates = settings.CreateRates();

        Statistics = new SimulatorStatistics();

        _emitter = new VehicleMessageEmitter(
            (byte)settings.SystemId,
            (byte)settings.ComponentId,
            _rates,
            route[0].AltitudeMetersMsl,
            Statistics);

        _transmitter = new MavlinkTransmitter(
            settings.TargetHost, settings.TargetPort, Statistics, timeProvider, logger);

        _step = TimeSpan.FromSeconds(1.0 / settings.StepHz);
        _stepSeconds = _step.TotalSeconds;

        //  Airborne from the first frame, at the first waypoint and already pointed at the second.
        //  There is no takeoff to model: arming and mode changes arrive with the command lifecycle,
        //  and an aircraft that sat on the ground would need a command it cannot yet receive before
        //  it could ever fly.
        _state = new AircraftState(
            route[0].LatitudeDegrees,
            route[0].LongitudeDegrees,
            route[0].AltitudeMetersMsl,
            projection.BearingDegrees(
                route[0].LatitudeDegrees,
                route[0].LongitudeDegrees,
                route[1].LatitudeDegrees,
                route[1].LongitudeDegrees),
            _envelope.CruiseSpeedMetersPerSecond,
            ClimbRateMetersPerSecond: 0,
            BatteryPercent: 100);
    }

    /// <summary>Gets what this vehicle has emitted, and what the link did with it.</summary>
    internal SimulatorStatistics Statistics { get; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //  PeriodicTimer rather than Task.Delay in a loop, which restarts its wait after each
        //  iteration's work and so sheds a few milliseconds every tick. Built with the injected
        //  TimeProvider, so nothing here reads a wall clock directly.
        using PeriodicTimer timer = new(_step, _timeProvider);

        _logger.LogInformation(
            "Simulated vehicle flying: {Envelope}. Route: {WaypointCount} waypoints, shortest leg "
            + "{ShortestLeg:0.#} m, capture radius {CaptureRadius:0.#} m. Messages: {Rates}. "
            + "Stepping at {StepHz:0.##} Hz.",
            _envelope,
            _follower.WaypointCount,
            _follower.ShortestLegMeters,
            _follower.CaptureRadiusMeters,
            _rates,
            1.0 / _stepSeconds);

        WarnAboutTightRoute();

        long lastReportTimestamp = _timeProvider.GetTimestamp();
        long ticks = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                ticks++;

                //  Multiplied, not accumulated. See the remarks on this type.
                double elapsedSeconds = ticks * _stepSeconds;

                FlightCommand command = _follower.Update(_state);
                _state = _kinematics.Advance(_state, command, _step);

                foreach (byte[] frame in _emitter.FramesDue(elapsedSeconds, _state))
                {
                    await _transmitter.SendAsync(frame, stoppingToken).ConfigureAwait(false);
                }

                if (_timeProvider.GetElapsedTime(lastReportTimestamp) >= ReportInterval)
                {
                    _logger.LogInformation(
                        "Simulated vehicle at {State}, flying to waypoint {ActiveWaypoint} of "
                        + "{WaypointCount}, lap {LapCount}. Link: {Statistics}.",
                        _state,
                        _follower.ActiveIndex + 1,
                        _follower.WaypointCount,
                        _follower.LapCount,
                        Statistics);

                    lastReportTimestamp = _timeProvider.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            //  Ordinary shutdown. Left to propagate it reaches the host as a faulted background
            //  service and is logged as a crash on every clean stop.
        }

        _logger.LogInformation("Simulated vehicle stopped. Link: {Statistics}.", Statistics);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _transmitter.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Warns when the route's legs are short enough that the turns at their ends overlap.
    /// </summary>
    /// <remarks>
    /// A warning rather than a refusal. Such a route is flyable and the aircraft will follow it;
    /// what it will not do is trace the shape that was drawn, because it is turning for the whole
    /// of every leg. Refusing would be this process deciding what a demo is allowed to show, and
    /// saying nothing would leave someone comparing a map against a config wondering which lied.
    /// </remarks>
    private void WarnAboutTightRoute()
    {
        double advisory = _envelope.TurnRadiusMeters * WaypointFollower.AdvisoryLegTurnRadii;

        if (_follower.ShortestLegMeters >= advisory)
        {
            return;
        }

        _logger.LogWarning(
            "The route's shortest leg is {ShortestLeg:0.#} m, under the {Advisory:0.#} m that this "
            + "aircraft's {TurnRadius:0.#} m turn radius wants. It will fly the route, but it will "
            + "be turning for most of every leg rather than tracking it, so the path on the map "
            + "will be rounder than the one configured.",
            _follower.ShortestLegMeters,
            advisory,
            _envelope.TurnRadiusMeters);
    }
}
