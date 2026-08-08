using System.Globalization;
using System.Text;

namespace Mcs.Core;

/// <summary>
/// The health of the radio link to a vehicle, as reported by the link layer.
/// </summary>
/// <remarks>
/// Deliberately has no zero member, for the same reason as <see cref="AltitudeReference"/>: a
/// field that was never assigned reads back as 0, and 0 must not be mistakeable for
/// <see cref="Healthy"/>. <see cref="VehicleTelemetry.Create"/> rejects any undeclared value.
/// <para>
/// This is <i>not</i> staleness. Staleness is MCS-002 -- derived by the console from
/// <see cref="TelemetryFrame.ReceivedAtUtc"/> against the station clock, and computed fresh on
/// every render. Link status is a claim carried inside a frame that did arrive; a vehicle can
/// report <see cref="Healthy"/> in the last frame before the link drops entirely, and the
/// console must still mark it stale three seconds later. Never derive one from the other.
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
/// The absence of a receipt timestamp here is the point. Every field on this type is a claim
/// from an untrusted source; the station's own observation of when the claim arrived lives one
/// level up, on <see cref="TelemetryFrame"/>. Keeping them in separate types means "is this
/// value trustworthy?" is answered by which type you are holding rather than by remembering
/// which field came from where -- and it means an adapter, which can only produce this type,
/// has no way to stamp a frame with a time of its choosing. See MCS-005 and the remarks on
/// <see cref="TelemetryFrame"/>.
/// <para>
/// A sealed record class rather than a record struct: eight fields is past the size where
/// copying beats a reference, and frames are handed to ring buffers and SSE subscribers rather
/// than used as dictionary keys. The reference type also means <c>default</c> is <c>null</c>,
/// which nullable reference types already police -- so this type needs none of the
/// uninitialised-sentinel machinery that <see cref="Altitude"/> and <see cref="VehicleId"/> carry.
/// </para>
/// <para>
/// Not a positional record, for the reason given on <see cref="Altitude"/>: positional records
/// generate <c>init</c> accessors, and a <c>with</c> expression writes those directly without
/// re-running validation. Get-only properties make <c>telemetry with { BatteryPercent = -5 }</c>
/// a compile error rather than an unguarded hole.
/// </para>
/// <para>
/// Every check below rejects rather than clamps. A clamped 200% battery renders as a
/// believable 100% and the operator never learns the adapter is broken -- that is HAZ-01
/// (*"the console shows the operator a picture he believes is current, and it isn't"*) arriving
/// by a different road. Loud failure at the boundary is the whole strategy.
/// </para>
/// <para>
/// Serialisation: this type does not go on the wire as-is. It has no public constructor and no
/// settable members, so System.Text.Json cannot rehydrate it; inbound JSON lands in a DTO and
/// passes through <see cref="Create"/>, and outbound JSON is projected from a DTO that flattens
/// this and its <see cref="TelemetryFrame"/> into one object. That also keeps
/// <c>JsonStringEnumConverter</c> -- required so <see cref="LinkStatus"/> reaches the browser as
/// a name, not a renumberable integer -- at the API layer where it belongs.
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
///
/// // Rejected -- ArgumentOutOfRangeException:
/// // ...latitudeDegrees: 95.0
/// // ...batteryPercent: 120.0
/// // ...altitude: default        // ArgumentException: never initialised
/// </code>
/// </remarks>
public sealed record VehicleTelemetry
{
    /// <summary>Latitude bound in degrees; the poles are valid positions.</summary>
    private const double MaxLatitudeDegrees = 90.0;

    /// <summary>
    /// Longitude bound in degrees. Both endpoints are accepted: -180 and +180 name the same
    /// antimeridian, and rejecting one of them would fail a legitimate position report for a
    /// difference of representation.
    /// </summary>
    private const double MaxLongitudeDegrees = 180.0;

    private const double DegreesPerTurn = 360.0;

    /// <summary>Battery is a percentage, not a fraction -- see <see cref="BatteryPercent"/>.</summary>
    private const double MaxBatteryPercent = 100.0;

    /// <summary>Roughly 1.1 cm at the equator; more precision than any of this is claiming.</summary>
    private const string CoordinateFormat = "0.#######";

    private const string ScalarFormat = "0.##";

    /// <summary>
    /// Validates and stores. Private: <see cref="Create"/> is the only way in, so no instance
    /// reaches the store or the ring buffer unvalidated.
    /// </summary>
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
    /// <param name="latitudeDegrees">Latitude in signed decimal degrees, WGS-84. -90 to 90.</param>
    /// <param name="longitudeDegrees">Longitude in signed decimal degrees, WGS-84. -180 to 180.</param>
    /// <param name="altitude">Altitude with its reference (MCS-004). Must not be <c>default</c>.</param>
    /// <param name="groundSpeedMetersPerSecond">Speed over the ground in m/s. Must be finite and non-negative.</param>
    /// <param name="headingDegrees">
    /// Heading in degrees from true north. Any finite value is accepted and normalised into
    /// [0, 360) -- see <see cref="HeadingDegrees"/> for what this must and must not be filled with.
    /// </param>
    /// <param name="batteryPercent">
    /// Remaining charge, 0 to 100, or <see langword="null"/> if the link did not report it.
    /// </param>
    /// <param name="linkStatus">Link health as reported. Must be a declared <see cref="LinkStatus"/>.</param>
    /// <returns>A validated <see cref="VehicleTelemetry"/>, with no receipt timestamp.</returns>
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
        // Both of these are structs whose smart constructors cannot stop `default` from
        // existing; each guards itself by throwing InvalidOperationException on property read.
        // Comparing against `default` catches the same case here without provoking that
        // exception, so the caller gets an ArgumentException naming the parameter they passed
        // rather than an InvalidOperationException from inside a property they never touched.
        // Sound because both are records: their synthesized equality compares every field, and
        // a validly constructed instance always differs from the all-zero one.
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

        // Each range check is written out longhand rather than routed through a shared helper so
        // that the message names the quantity and its unit. A generic "value out of range" sends
        // the reader to the stack trace to find out which of six doubles was wrong.
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

        // No upper bound on speed. There is no defensible ceiling in the requirements, and an
        // invented one would reject a legitimate report from whatever airframe is added later.
        // Non-finite and negative are the cases that are wrong regardless of vehicle.
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

        // Catches the uninitialised 0 and any out-of-band cast, e.g. (LinkStatus)99.
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

    /// <summary>
    /// Gets the latitude in signed decimal degrees (WGS-84), -90 to 90.
    /// </summary>
    /// <remarks>
    /// The unit is in the name because the wire formats disagree with it: MAVLink carries
    /// position as <c>int32</c> degrees times 1e7. An adapter that forgets the scaling produces
    /// a value this type rejects outright rather than a track somewhere off Africa.
    /// </remarks>
    public double LatitudeDegrees { get; }

    /// <summary>Gets the longitude in signed decimal degrees (WGS-84), -180 to 180.</summary>
    public double LongitudeDegrees { get; }

    /// <summary>Gets the altitude together with the reference it was measured against (MCS-004).</summary>
    public Altitude Altitude { get; }

    /// <summary>Gets the speed over the ground in metres per second. Non-negative.</summary>
    /// <remarks>
    /// Metres per second, stated in the name, because a good deal of aviation equipment reports
    /// knots and the conversion belongs at the adapter boundary -- the same reasoning that gave
    /// <see cref="Altitude.FromFeet"/> its name.
    /// </remarks>
    public double GroundSpeedMetersPerSecond { get; }

    /// <summary>
    /// Gets the heading in degrees clockwise from <b>true</b> north, normalised into [0, 360).
    /// </summary>
    /// <remarks>
    /// Two conversions belong to the adapter, not here, and both are silent if skipped:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <b>True, not magnetic.</b> An adapter reading a magnetic heading must apply declination
    /// before calling <see cref="Create"/>. Nothing in the value distinguishes the two, and the
    /// error is a slow rotation of the whole picture -- up to a couple of degrees in western
    /// Europe, far more at high latitude.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Heading, not course over ground.</b> Where the nose points, not the direction of
    /// travel. In wind they differ, and MCS-001 asks for heading. An adapter with only COG
    /// available should say so upstream rather than quietly substituting it.
    /// </description>
    /// </item>
    /// </list>
    /// Out-of-range inputs are normalised rather than rejected: 361 and -1 are ordinary outputs
    /// of an adapter's own arithmetic and mean something unambiguous, unlike a latitude of 95.
    /// </remarks>
    public double HeadingDegrees { get; }

    /// <summary>
    /// Gets the remaining charge as a percentage from 0 to 100, or <see langword="null"/> if the
    /// vehicle did not report it.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. Not every link carries a battery reading, and substituting 0 for
    /// "unknown" puts a number in front of the operator that was never measured -- and the one
    /// number that would make them abort. Null forces the console to render an explicit "no
    /// data" state instead of a plausible lie.
    /// <para>
    /// A percentage rather than a 0-1 fraction, and the name says so: the two are
    /// indistinguishable at a glance in a debugger, and a fraction rendered as a percentage
    /// reads as a flat battery.
    /// </para>
    /// </remarks>
    public double? BatteryPercent { get; }

    /// <summary>Gets the link health as reported by the vehicle. Not staleness -- see <see cref="LinkStatus"/>.</summary>
    public LinkStatus LinkStatus { get; }

    /// <summary>
    /// Brings any finite heading into [0, 360). 372.5 becomes 12.5; -1 becomes 359.
    /// </summary>
    /// <remarks>
    /// The double modulo keeps the sign of its left operand, so a single <c>% 360</c> maps -1 to
    /// -1 rather than 359; adding a turn and taking the remainder again folds negatives up
    /// without a branch. The result is always strictly below 360: a tiny negative input rounds
    /// to exactly 360.0 at the addition and the second remainder takes it to 0.
    /// <para>
    /// The guard in front of that is not an optimisation. Folding is lossy for a value that was
    /// already in range: <c>87.3 % 360</c> is a no-op, but <c>+ 360</c> rounds to the coarser
    /// spacing available near 447 and the second remainder cannot undo it, so an untouched
    /// heading comes back as 87.30000000000001 -- and reaches the browser that way, since JSON
    /// writes the shortest round-trippable form. Passing in-range values through unchanged is
    /// what makes this the only normalisation on the type that is exact. Zero is deliberately
    /// left to the fold: <c>-0.0</c> is not less than zero, and only the fold turns it back into
    /// <c>0.0</c> rather than a heading that renders as "-0".
    /// </para>
    /// </remarks>
    private static double NormaliseHeading(double degrees) =>
        degrees is > 0 and < DegreesPerTurn
            ? degrees
            : ((degrees % DegreesPerTurn) + DegreesPerTurn) % DegreesPerTurn;

    /// <summary>
    /// Formats the members for the compiler-generated <c>ToString</c>, in the invariant culture.
    /// </summary>
    /// <remarks>
    /// Overridden solely to pin the culture. The synthesized <c>PrintMembers</c> formats doubles
    /// with the <i>current</i> culture, so the same frame would log "51,5074" in a container
    /// with a European locale and "51.5074" on the developer's machine -- a difference that
    /// survives into the station's JSON logs and breaks anything parsing them. Same reasoning as
    /// <see cref="Altitude.ToString()"/> defaulting to invariant.
    /// </remarks>
    private bool PrintMembers(StringBuilder builder)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;

        builder.Append(invariant, $"{nameof(Id)} = {Id}, ");
        builder.Append(invariant, $"{nameof(LatitudeDegrees)} = {LatitudeDegrees.ToString(CoordinateFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(LongitudeDegrees)} = {LongitudeDegrees.ToString(CoordinateFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(Altitude)} = {Altitude.ToString(ScalarFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(GroundSpeedMetersPerSecond)} = {GroundSpeedMetersPerSecond.ToString(ScalarFormat, invariant)}, ");
        builder.Append(invariant, $"{nameof(HeadingDegrees)} = {HeadingDegrees.ToString(ScalarFormat, invariant)}, ");

        // "unreported" rather than an empty slot, so a missing battery is visibly a decision
        // in the log line rather than something that looks like a formatting failure.
        string battery = BatteryPercent is { } value
            ? value.ToString(ScalarFormat, invariant)
            : "unreported";
        builder.Append(invariant, $"{nameof(BatteryPercent)} = {battery}, ");
        builder.Append(invariant, $"{nameof(LinkStatus)} = {LinkStatus}");

        return true;
    }
}
