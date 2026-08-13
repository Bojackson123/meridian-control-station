using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Mcs.Simulator.Flight;
using Mcs.Simulator.Mavlink;

namespace Mcs.Simulator;

/// <summary>
/// The <c>Simulator</c> configuration section: which station to transmit to, what the aircraft can
/// do, how often it says so, and the route it flies.
/// </summary>
/// <remarks>
/// <b>This section is the vehicle type.</b> A deconfliction margin is only meaningful if it was
/// computed from the same envelope the aircraft was actually flown with, so the numbers here are
/// the ones such a bound has to quote -- which is why the turn radius is <i>not</i> among them.
/// It follows from the cruise speed and the bank limit, and offering it as a setting would let the
/// two disagree.
/// <para>
/// Mutable properties because that is what the configuration binder needs, matching the station's
/// options classes. The validated, immutable forms are <see cref="AircraftEnvelope"/>,
/// <see cref="Waypoint"/> and <see cref="MessageRates"/>, and <see cref="Validate"/> builds all
/// three so that anything that passes startup validation is known to construct.
/// </para>
/// <para>
/// There is deliberately no <c>Enabled</c> flag. A simulator that is configured and silently not
/// flying is a station showing an empty map for a reason nothing reports, which is the failure the
/// whole of this section is arranged to prevent.
/// </para>
/// </remarks>
public sealed class SimulatorOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    /// <remarks>In the environment this is <c>Simulator__TargetHost</c>, and so on.</remarks>
    public const string SectionName = "Simulator";

    /// <summary>
    /// How many turn radii the capture radius is set to when it is not configured.
    /// </summary>
    /// <remarks>
    /// One radius is the hard floor -- below it the aircraft can orbit a waypoint it never reaches,
    /// which <see cref="WaypointFollower"/> explains and rejects. Half a radius of headroom keeps
    /// the default off that boundary, where floating-point luck decides whether a waypoint is
    /// captured on the pass that grazes it.
    /// </remarks>
    private const double DerivedCaptureRadiusTurnRadii = 1.5;

    /// <summary>
    /// Gets or sets the station's host name or address.
    /// </summary>
    /// <remarks>
    /// Defaults to loopback, which is what a developer running both processes on one machine
    /// needs. Under Compose it is the API service's name on the shared network, and it is resolved
    /// once at startup: a name that does not resolve stops this process rather than letting it
    /// transmit into nowhere, which looks exactly like a healthy simulator.
    /// </remarks>
    [Required]
    public string TargetHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the station's UDP port.
    /// </summary>
    /// <remarks>
    /// 14550, the port ground stations conventionally listen on, so this matches the adapter's own
    /// default without either side being told about the other. Zero is excluded here where the
    /// adapter allows it: on that side it means "any free port", and there is no sending equivalent.
    /// </remarks>
    [Range(1, 65535)]
    public int TargetPort { get; set; } = 14550;

    /// <summary>
    /// Gets or sets this vehicle's MAVLink system id, which the station turns into "MAV-001".
    /// </summary>
    /// <remarks>
    /// Configurable although M1 flies one aircraft: the store is built for twelve, running a second
    /// simulator is then a matter of a second Compose service, and the cost of making the id a
    /// setting today is nothing. Zero is excluded because it is reserved for broadcast.
    /// </remarks>
    [Range(1, 255)]
    public int SystemId { get; set; } = 1;

    /// <summary>
    /// Gets or sets the emitting component's id.
    /// </summary>
    /// <remarks>
    /// 1 is <c>MAV_COMP_ID_AUTOPILOT1</c>, which is what an aircraft's flight controller uses. The
    /// station keys its senders on the system and component pair, so a second component id from the
    /// same system is a second sender folding into the same vehicle.
    /// </remarks>
    [Range(1, 255)]
    public int ComponentId { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many times per second the flight model steps.
    /// </summary>
    /// <remarks>
    /// Independent of every message rate on purpose: raising the telemetry rate must not change how
    /// the aircraft flies, or the turn radius a separate document quotes would be a function of how
    /// chatty the link was configured to be. Independent, but not unrelated -- the streams are
    /// polled once per step, so none of them may ask for more than this, and
    /// <see cref="Validate"/> refuses the combination rather than letting a rate quietly come out
    /// at the step rate.
    /// </remarks>
    [Range(1.0, 200.0)]
    public double StepHz { get; set; } = 20.0;

    /// <summary>Gets or sets the HEARTBEAT rate, in hertz.</summary>
    [Range(0.01, 50.0)]
    public double HeartbeatHz { get; set; } = 1.0;

    /// <summary>Gets or sets the SYS_STATUS rate, in hertz.</summary>
    [Range(0.01, 50.0)]
    public double SysStatusHz { get; set; } = 0.5;

    /// <summary>Gets or sets the VFR_HUD rate, in hertz.</summary>
    /// <remarks>
    /// The default is deliberately not a divisor of <see cref="GlobalPositionHz"/>. See
    /// <see cref="MessageRates"/> for why that matters to the station.
    /// </remarks>
    [Range(0.01, 50.0)]
    public double VfrHudHz { get; set; } = 3.0;

    /// <summary>Gets or sets the GLOBAL_POSITION_INT rate, in hertz. The console's update rate.</summary>
    [Range(0.01, 50.0)]
    public double GlobalPositionHz { get; set; } = 4.0;

    /// <summary>
    /// Gets or sets the ground speed held all the way round, in metres per second. The default is a
    /// believable cruise for a small fixed-wing UAV.
    /// </summary>
    /// <remarks>
    /// The upper bound is the envelope's own constant rather than a number repeated here, so that
    /// the attribute cannot come to allow a speed the envelope rejects -- or, worse, allow one it
    /// accepts and the wire format does not.
    /// </remarks>
    [Range(1.0, AircraftEnvelope.MaxCruiseSpeedMetersPerSecond)]
    public double CruiseSpeedMetersPerSecond { get; set; } = 22.0;

    /// <summary>
    /// Gets or sets the steepest coordinated turn the aircraft will fly, in degrees.
    /// </summary>
    /// <remarks>
    /// With the default cruise speed this gives a turn radius of about 105 m. It is half of the
    /// pair the radius is derived from, and changing either changes the route the aircraft can fly
    /// -- which is why the follower checks the capture radius against the result.
    /// </remarks>
    [Range(1.0, 80.0)]
    public double MaxBankAngleDegrees { get; set; } = 25.0;

    /// <summary>Gets or sets the fastest rate of altitude gain, in metres per second.</summary>
    [Range(0.1, 50.0)]
    public double MaxClimbRateMetersPerSecond { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets the fastest rate of altitude loss as a positive magnitude, in metres per second.
    /// </summary>
    [Range(0.1, 50.0)]
    public double MaxDescentRateMetersPerSecond { get; set; } = 5.0;

    /// <summary>
    /// Gets or sets how many seconds the battery takes to drain from full to flat. The default is
    /// long enough that a demo never shows a flat battery and short enough that the number visibly
    /// moves within a minute of watching.
    /// </summary>
    [Range(60.0, 86_400.0)]
    public double EnduranceSeconds { get; set; } = 2_700.0;

    /// <summary>
    /// Gets or sets how close to a waypoint counts as having reached it, in metres.
    /// </summary>
    /// <remarks>
    /// <b>Null means derive it from the turn radius</b>, which is the right answer nearly always and
    /// is why this is nullable rather than defaulted to a number. A sentinel of 0 would have done
    /// the same job while making "derive this" indistinguishable from "someone set it to zero", and
    /// zero is a value the range check would otherwise have to allow.
    /// </remarks>
    public double? CaptureRadiusMeters { get; set; }

    /// <summary>
    /// Gets or sets the route, flown in order and then from the last waypoint back to the first.
    /// </summary>
    /// <remarks>
    /// Empty by default and supplied by <c>appsettings.json</c>, so a deployment that lost that
    /// file fails startup with a message saying so rather than flying a circuit compiled into the
    /// binary that nobody can see in the configuration.
    /// </remarks>
    public IList<WaypointOptions> Route { get; set; } = [];

    /// <summary>Builds the validated envelope. Valid only once <see cref="Validate"/> has passed.</summary>
    internal AircraftEnvelope CreateEnvelope() =>
        new(
            CruiseSpeedMetersPerSecond,
            MaxBankAngleDegrees,
            MaxClimbRateMetersPerSecond,
            MaxDescentRateMetersPerSecond,
            EnduranceSeconds);

    /// <summary>Builds the validated route. Valid only once <see cref="Validate"/> has passed.</summary>
    internal IReadOnlyList<Waypoint> CreateRoute() =>
        [.. Route.Select(waypoint => new Waypoint(
            waypoint.LatitudeDegrees, waypoint.LongitudeDegrees, waypoint.AltitudeMetersMsl))];

    /// <summary>Builds the message rates.</summary>
    internal MessageRates CreateRates() =>
        new(HeartbeatHz, SysStatusHz, VfrHudHz, GlobalPositionHz);

    /// <summary>
    /// Returns the capture radius to fly with: the configured one, or one derived from the
    /// envelope's turn radius when none was configured.
    /// </summary>
    internal double ResolveCaptureRadiusMeters(AircraftEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return CaptureRadiusMeters
            ?? (envelope.TurnRadiusMeters * DerivedCaptureRadiusTurnRadii);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>This builds the whole flight model rather than re-checking the settings.</b> The bounds
    /// that matter here are not per-property -- the capture radius is only wrong relative to a turn
    /// radius derived from two other settings, and a waypoint is only wrong relative to the route
    /// it sits in -- so a second set of range checks written out here would be a copy of
    /// <see cref="AircraftEnvelope"/>'s and <see cref="WaypointFollower"/>'s that could drift away
    /// from them. Constructing the real objects means anything that passes startup is known to
    /// construct at runtime, which is the only property worth having.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        //  A list rather than an iterator, because every check below is a try/catch around a
        //  constructor and C# will not let an iterator yield from one. Stopping at the first
        //  failure is deliberate in any case: each stage needs the object the one before it built,
        //  so continuing past a failure would report consequences alongside the cause.
        AircraftEnvelope envelope;

        try
        {
            envelope = CreateEnvelope();
        }
        catch (ArgumentException exception)
        {
            //  The per-property attributes above catch the ordinary out-of-range cases first, so
            //  reaching here means a combination they cannot express.
            return [new ValidationResult(Describe(exception))];
        }

        //  No stream can beat the step it is polled from: MessageSchedule fires at most once per
        //  call and walks its due time past the present, so a rate above StepHz comes out at StepHz
        //  and drops the difference. Rejected rather than left to emerge, because the symptom is a
        //  console updating more slowly than the configuration says it does, and from outside this
        //  process that is indistinguishable from a slow link -- which is the reading an operator
        //  would act on. It is the one cross-setting bound the rest of this file's per-property
        //  ranges cannot express, which is what Validate is for.
        ReadOnlySpan<(string Name, double RateHz)> streams =
        [
            (nameof(HeartbeatHz), HeartbeatHz),
            (nameof(SysStatusHz), SysStatusHz),
            (nameof(VfrHudHz), VfrHudHz),
            (nameof(GlobalPositionHz), GlobalPositionHz),
        ];

        foreach ((string name, double rateHz) in streams)
        {
            if (rateHz > StepHz)
            {
                return
                [
                    new ValidationResult(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{SectionName}:{name} is {rateHz} Hz, above the "
                            + $"{SectionName}:{nameof(StepHz)} of {StepHz} Hz that polls it. The "
                            + $"stream would send at {StepHz} Hz instead, and nothing "
                            + $"downstream would report the difference."),
                        [name]),
                ];
            }
        }

        if (Route.Count < 2)
        {
            return
            [
                new ValidationResult(
                    $"{SectionName}:{nameof(Route)} needs at least two waypoints; it has "
                    + $"{Route.Count}. The default circuit lives in appsettings.json.",
                    [nameof(Route)]),
            ];
        }

        IReadOnlyList<Waypoint> route;

        try
        {
            route = CreateRoute();
        }
        catch (ArgumentException exception)
        {
            return [new ValidationResult(Describe(exception), [nameof(Route)])];
        }

        //  The follower is where the capture radius meets the turn radius, and where a route with a
        //  degenerate leg is caught. Built here and thrown away: the point is that it can be.
        try
        {
            _ = new WaypointFollower(
                route,
                ResolveCaptureRadiusMeters(envelope),
                envelope,
                new LocalProjection(route[0].LatitudeDegrees));
        }
        catch (ArgumentException exception)
        {
            //  Blamed on the capture radius when one was configured and on the route when it was
            //  derived, because those are the two settings an operator can actually change to fix
            //  it -- the turn radius is not a setting.
            return
            [
                new ValidationResult(
                    Describe(exception),
                    [CaptureRadiusMeters is null ? nameof(Route) : nameof(CaptureRadiusMeters)]),
            ];
        }

        return [];
    }

    /// <summary>
    /// Renders a construction failure as a validation message, keeping the offending value.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentException.Message"/> carries the explanation and the parameter name;
    /// <see cref="ArgumentOutOfRangeException.ActualValue"/> carries the number that was wrong,
    /// which the message does not repeat. An operator reading the startup failure wants both.
    /// </remarks>
    private static string Describe(ArgumentException exception) =>
        exception is ArgumentOutOfRangeException { ActualValue: { } value }
            ? string.Create(
                CultureInfo.InvariantCulture, $"{exception.Message} The value was {value}.")
            : exception.Message;
}
