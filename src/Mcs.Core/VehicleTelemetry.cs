using System.Globalization;
using System.Text;

namespace Mcs.Core;

/// <summary>
/// The health of the radio link to a vehicle, as reported by the link layer.
/// </summary>
/// <remarks>
/// No zero member, for the same reason as <see cref="AltitudeReference"/>: 0 must not be
/// mistakeable for <see cref="Healthy"/>.
/// <para>
/// This is <i>not</i> staleness. Staleness is MCS-002, derived by the console from
/// <see cref="TelemetryFrame.ReceivedAtUtc"/> on every render. A vehicle can report
/// <see cref="Healthy"/> in the last frame before the link drops entirely, and the console must
/// still mark it stale three seconds later. Never derive one from the other.
/// </para>
/// </remarks>
public enum LinkStatus
{
    /// <summary>The link is up and within its expected signal and loss budget.</summary>
    Healthy = 1,

    /// <summary>The link is up but impaired -- weak signal, elevated packet loss, or a fallback radio.</summary>
    Degraded = 2,

    /// <summary>The link is down. A frame reporting this was relayed by something other than the lost link.</summary>
    Lost = 3,
}

/// <summary>
/// One vehicle's reported state at a single instant: everything the <i>vehicle</i> claimed, and
/// nothing the station observed.
/// </summary>
/// <remarks>
/// The absence of a receipt timestamp is the point -- the station's observation lives one level up,
/// on <see cref="TelemetryFrame"/>, so an adapter has no way to stamp a frame with a time of its
/// choosing (MCS-005).
/// <para>
/// A sealed record class rather than a record struct: eight fields is past the size where copying
/// beats a reference, and these go to ring buffers and SSE subscribers rather than keying
/// dictionaries. Being a reference type also means <c>default</c> is <c>null</c>, which nullable
/// reference types already police -- so this needs none of the uninitialised-sentinel machinery
/// <see cref="Altitude"/> and <see cref="VehicleId"/> carry. Not positional, for the reason given on
/// <see cref="Altitude"/>.
/// </para>
/// <para>
/// <b>Every check below rejects rather than clamps.</b> A clamped 200% battery renders as a
/// believable 100% and the operator never learns the adapter is broken -- HAZ-01 arriving by a
/// different road.
/// </para>
/// <para>
/// Units are in the property names because the wire formats disagree with them: MAVLink carries
/// position as <c>int32</c> degrees times 1e7, and much aviation equipment reports knots and feet.
/// Conversion belongs at the adapter boundary, and an adapter that forgets produces a value this
/// type rejects outright rather than a track somewhere off Africa.
/// </para>
/// <para>
/// Serialisation: no public constructor and no settable members, so inbound JSON lands in a DTO and
/// passes through <see cref="Create"/>, and outbound is projected from a DTO flattening this and its
/// frame into one object. That keeps <c>JsonStringEnumConverter</c> -- required so
/// <see cref="LinkStatus"/> reaches the browser as a name -- at the API layer where it belongs.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// var telemetry = VehicleTelemetry.Create(
///     id: VehicleId.From("UAV-01"),
///     latitudeDegrees: 51.5074,
///     longitudeDegrees: -0.1278,
///     altitude: Altitude.FromMeters(120, AltitudeReference.Agl),
///     groundSpeedMetersPerSecond: 14.2,
///     headingDegrees: 372.5,      // normalised to 12.5
///     batteryPercent: 87.0,       // null means "not reported", never 0
///     linkStatus: LinkStatus.Healthy);
/// </code>
/// </remarks>
public sealed record VehicleTelemetry
{
    /// <summary>Latitude bound in degrees; the poles are valid positions.</summary>
    private const double MaxLatitudeDegrees = 90.0;

    /// <summary>
    /// Longitude bound. Both endpoints are accepted: -180 and +180 name the same antimeridian, and
    /// rejecting one would fail a legitimate report over a difference of representation.
    /// </summary>
    private const double MaxLongitudeDegrees = 180.0;

    private const double DegreesPerTurn = 360.0;

    /// <summary>Battery is a percentage, not a fraction -- see <see cref="BatteryPercent"/>.</summary>
    private const double MaxBatteryPercent = 100.0;

    /// <summary>Roughly 1.1 cm at the equator; more precision than any of this is claiming.</summary>
    private const string CoordinateFormat = "0.#######";

    private const string ScalarFormat = "0.##";

    private VehicleTelemetry(
        VehicleId id,
        double latitudeDegrees,
        double longitudeDegrees,
        Altitude altitude,
        double groundSpeedMetersPerSecond,
        double headingDegrees,
        double? batteryPercent,
        LinkStatus linkStatus)
    {
        Id = id;
        LatitudeDegrees = latitudeDegrees;
        LongitudeDegrees = longitudeDegrees;
        Altitude = altitude;
        GroundSpeedMetersPerSecond = groundSpeedMetersPerSecond;
        HeadingDegrees = headingDegrees;
        BatteryPercent = batteryPercent;
        LinkStatus = linkStatus;
    }

    /// <summary>
    /// Creates a validated telemetry report. The only way an instance comes into being.
    /// </summary>
    /// <param name="id">The vehicle the report describes. Must not be <c>default</c>.</param>
    /// <param name="latitudeDegrees">Signed decimal degrees, WGS-84. -90 to 90.</param>
    /// <param name="longitudeDegrees">Signed decimal degrees, WGS-84. -180 to 180.</param>
    /// <param name="altitude">Altitude with its reference (MCS-004). Must not be <c>default</c>.</param>
    /// <param name="groundSpeedMetersPerSecond">Speed over the ground. Finite and non-negative.</param>
    /// <param name="headingDegrees">Any finite value; normalised into [0, 360). See <see cref="HeadingDegrees"/>.</param>
    /// <param name="batteryPercent">0 to 100, or <see langword="null"/> if unreported.</param>
    /// <param name="linkStatus">Must be a declared <see cref="LinkStatus"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="altitude"/> was never initialised.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any numeric argument is non-finite or out of range, or <paramref name="linkStatus"/> is undeclared.</exception>
    public static VehicleTelemetry Create(
        VehicleId id,
        double latitudeDegrees,
        double longitudeDegrees,
        Altitude altitude,
        double groundSpeedMetersPerSecond,
        double headingDegrees,
        double? batteryPercent,
        LinkStatus linkStatus)
    {
        //  Compared against `default` rather than reading a property, so the caller gets an
        //  ArgumentException naming the parameter they passed rather than an InvalidOperationException
        //  from inside a property they never touched. Sound because both are records: synthesized
        //  equality compares every field, and a valid instance always differs from the all-zero one.
        if (id == default)
        {
            throw new ArgumentException(
                "Vehicle id was never initialised; construct it with VehicleId.From.", nameof(id));
        }

        if (altitude == default)
        {
            throw new ArgumentException(
                "Altitude was never initialised and so declares no reference (MCS-004); "
                + "construct it with Altitude.FromMeters or Altitude.FromFeet.",
                nameof(altitude));
        }

        //  Longhand rather than a shared helper so each message names the quantity and its unit. A
        //  generic "value out of range" sends the reader to the stack trace to find out which of six
        //  doubles was wrong.
        if (!double.IsFinite(latitudeDegrees)
            || latitudeDegrees < -MaxLatitudeDegrees
            || latitudeDegrees > MaxLatitudeDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitudeDegrees),
                latitudeDegrees,
                "Latitude must be a finite value between -90 and 90 degrees.");
        }

        if (!double.IsFinite(longitudeDegrees)
            || longitudeDegrees < -MaxLongitudeDegrees
            || longitudeDegrees > MaxLongitudeDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitudeDegrees),
                longitudeDegrees,
                "Longitude must be a finite value between -180 and 180 degrees.");
        }

        //  No upper bound on speed: there is no defensible ceiling in the requirements, and an
        //  invented one would reject a legitimate report from whatever airframe is added later.
        if (!double.IsFinite(groundSpeedMetersPerSecond) || groundSpeedMetersPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(groundSpeedMetersPerSecond),
                groundSpeedMetersPerSecond,
                "Ground speed must be a finite, non-negative number of metres per second.");
        }

        if (!double.IsFinite(headingDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(headingDegrees),
                headingDegrees,
                "Heading must be a finite number of degrees.");
        }

        if (batteryPercent is { } battery
            && (!double.IsFinite(battery) || battery < 0 || battery > MaxBatteryPercent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(batteryPercent),
                batteryPercent,
                "Battery must be a finite percentage between 0 and 100, or null if unreported.");
        }

        //  Catches the uninitialised 0 and any out-of-band cast, e.g. (LinkStatus)99.
        if (!Enum.IsDefined(linkStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(linkStatus), linkStatus, "Telemetry must declare a known link status.");
        }

        return new VehicleTelemetry(
            id,
            latitudeDegrees,
            longitudeDegrees,
            altitude,
            groundSpeedMetersPerSecond,
            NormaliseHeading(headingDegrees),
            batteryPercent,
            linkStatus);
    }

    /// <summary>Gets the vehicle this report describes.</summary>
    public VehicleId Id { get; }

    /// <summary>Gets the latitude in signed decimal degrees (WGS-84), -90 to 90.</summary>
    public double LatitudeDegrees { get; }

    /// <summary>Gets the longitude in signed decimal degrees (WGS-84), -180 to 180.</summary>
    public double LongitudeDegrees { get; }

    /// <summary>Gets the altitude together with the reference it was measured against (MCS-004).</summary>
    public Altitude Altitude { get; }

    /// <summary>Gets the speed over the ground in metres per second. Non-negative.</summary>
    public double GroundSpeedMetersPerSecond { get; }

    /// <summary>
    /// Gets the heading in degrees clockwise from <b>true</b> north, normalised into [0, 360).
    /// </summary>
    /// <remarks>
    /// Two conversions belong to the adapter, and both are silent if skipped. <b>True, not
    /// magnetic</b> -- declination must be applied first, or the whole picture rotates slowly.
    /// <b>Heading, not course over ground</b> -- where the nose points, not the direction of travel;
    /// in wind they differ, and MCS-001 asks for heading.
    /// <para>
    /// Normalised rather than rejected because 361 and -1 are ordinary outputs of an adapter's own
    /// arithmetic and mean something unambiguous, unlike a latitude of 95.
    /// </para>
    /// </remarks>
    public double HeadingDegrees { get; }

    /// <summary>
    /// Gets the remaining charge as a percentage from 0 to 100, or <see langword="null"/> if the
    /// vehicle did not report it.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose: substituting 0 for "unknown" puts a number in front of the operator that
    /// was never measured -- and the one number that would make them abort. Null forces the console
    /// to render an explicit "no data" state instead of a plausible lie. A percentage rather than a
    /// 0-1 fraction, since a fraction rendered as a percentage reads as a flat battery.
    /// </remarks>
    public double? BatteryPercent { get; }

    /// <summary>Gets the link health as reported. Not staleness -- see <see cref="LinkStatus"/>.</summary>
    public LinkStatus LinkStatus { get; }

    /// <summary>Brings any finite heading into [0, 360). 372.5 becomes 12.5; -1 becomes 359.</summary>
    /// <remarks>
    /// Double modulo keeps the sign of its left operand, so a single <c>% 360</c> maps -1 to -1;
    /// adding a turn and taking the remainder again folds negatives up without a branch.
    /// <para>
    /// The guard is not an optimisation. Folding is lossy for a value already in range: <c>+ 360</c>
    /// rounds to the coarser spacing near 447 and the second remainder cannot undo it, so an
    /// untouched 87.3 comes back as 87.30000000000001 -- and reaches the browser that way, since JSON
    /// writes the shortest round-trippable form. Zero is left to the fold deliberately: only the fold
    /// turns <c>-0.0</c> back into <c>0.0</c> rather than a heading rendering as "-0".
    /// </para>
    /// </remarks>
    private static double NormaliseHeading(double degrees) =>
        degrees is > 0 and < DegreesPerTurn
            ? degrees
            : ((degrees % DegreesPerTurn) + DegreesPerTurn) % DegreesPerTurn;

    //  Overridden solely to pin the culture: the synthesized PrintMembers formats doubles with the
    //  current culture, so the same frame would log "51,5074" in a European-locale container and
    //  "51.5074" on the developer's machine -- a difference that survives into the JSON logs.
    private bool PrintMembers(StringBuilder builder)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;

        builder.Append(invariant, $"{nameof(Id)} = {Id}, ");
        builder.Append(invariant, $"{nameof(LatitudeDegrees)} = {LatitudeDegrees.ToString(CoordinateFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(LongitudeDegrees)} = {LongitudeDegrees.ToString(CoordinateFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(Altitude)} = {Altitude.ToString(ScalarFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(GroundSpeedMetersPerSecond)} = {GroundSpeedMetersPerSecond.ToString(ScalarFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(HeadingDegrees)} = {HeadingDegrees.ToString(ScalarFormat, invariant)}, ");

        //  "unreported" rather than an empty slot, so a missing battery is visibly a decision rather
        //  than something that looks like a formatting failure.
        string battery = BatteryPercent is { } value
            ? value.ToString(ScalarFormat, invariant)
            : "unreported";
        builder.Append(invariant, $"{nameof(BatteryPercent)} = {battery}, ");
        builder.Append(invariant, $"{nameof(LinkStatus)} = {LinkStatus}");

        return true;
    }
}
