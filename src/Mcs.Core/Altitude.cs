using System.Globalization;

namespace Mcs.Core;

/// <summary>
/// The datum an altitude is measured against.
/// </summary>
/// <remarks>
/// Deliberately has no zero member. A struct field that was never assigned reads back as 0,
/// so leaving 0 undefined means an uninitialised altitude reports a reference that cannot be
/// mistaken for a real one -- rather than silently claiming <see cref="Msl"/>, which is what
/// an enum starting at 0 would do. <see cref="Altitude"/> uses that 0 as its initialisation
/// sentinel; do not add a member for it.
/// <para>
/// Converting between these references requires terrain elevation, which the station does not
/// hold. Until it does, a value must be consumed against the reference it was reported with.
/// </para>
/// </remarks>
public enum AltitudeReference
{
    /// <summary>Metres above mean sea level. What barometric altimeters and most flight plans use.</summary>
    Msl = 1,

    /// <summary>Metres above the ground directly beneath the vehicle. What matters for terrain clearance.</summary>
    Agl = 2,

    /// <summary>Metres above the WGS-84 ellipsoid. What raw GNSS reports before any geoid correction.</summary>
    Hae = 3,
}

/// <summary>
/// An altitude and the reference it was measured against, inseparably.
/// </summary>
/// <remarks>
/// MCS-004: the adapter interface shall reject any position report that does not declare an
/// altitude reference. Pairing the two in one type is how that requirement is met at every
/// call site at once -- a bare <c>double</c> altitude is a bug that comes due the day MSL/AGL
/// conversion arrives and there is no way to tell which values need converting.
/// <para>
/// Not a positional record on purpose. Positional records generate <c>init</c> accessors, and
/// a <c>with</c> expression assigns those directly without re-running the constructor -- so
/// constructor validation alone would leave <c>alt with { Reference = (AltitudeReference)0 }</c>
/// as an unguarded hole. Get-only properties make that a compile error instead.
/// </para>
/// <para>
/// The constructor is private and the factories are named for their unit. <c>new Altitude(120, Agl)</c>
/// puts the unit in a parameter name nobody reads at the call site, which is exactly how a
/// feet-valued sensor reading gets stored as metres; <see cref="FromFeet"/> makes the same
/// mistake unwriteable. It also means <see cref="FromMeters"/> and <see cref="FromFeet"/> are
/// the complete list of ways a valid instance comes into being.
/// </para>
/// <para>
/// No <see cref="IComparable{T}"/>, and this is deliberate rather than unfinished. Ordering is
/// undefined across references: 100 m AGL may be above or below 100 m MSL depending on terrain
/// nobody has loaded yet, so any <c>CompareTo</c> would either lie for mixed pairs or throw
/// from inside <c>Sort</c>, where the caller cannot see it coming. Once terrain and conversion
/// exist, comparison belongs on the service that holds them -- not on the value. Order
/// by <see cref="Meters"/> explicitly, after grouping by <see cref="Reference"/>.
/// </para>
/// <para>
/// Serialisation note for the telemetry API: the reference must reach the browser as a name
/// ("Msl"), not as the underlying number. Apply <c>JsonStringEnumConverter</c> at the DTO
/// layer -- a bare <c>1</c> on the wire is not self-describing, and renumbering the enum
/// would silently change the contract. Deserialising straight into this type will not work
/// (no public constructor, and the properties are get-only), which is intended: inbound JSON
/// lands in a DTO and passes through <see cref="FromMeters"/>, so the wire cannot mint an
/// unvalidated altitude.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// // Correct construction -- the factory names the unit at the call site.
/// var altitude = Altitude.FromMeters(1500.5, AltitudeReference.Msl);
/// var fromSensor = Altitude.FromFeet(4922.9, AltitudeReference.Msl); // ~1500.5 m
/// Console.WriteLine(altitude); // "1500.5 m Msl"
///
/// double val = altitude.Meters;                    // 1500.5
/// AltitudeReference refDatum = altitude.Reference; // AltitudeReference.Msl
///
/// // Rejected at construction -- ArgumentOutOfRangeException:
/// // Altitude.FromMeters(double.NaN, AltitudeReference.Agl);
/// // Altitude.FromMeters(100, (AltitudeReference)0);
///
/// // 'default' is the case the language will not let this type refuse. Constructing one
/// // never throws; reading a value off it does -- InvalidOperationException:
/// var uninitialised = default(Altitude);   // no exception here
/// Console.WriteLine(uninitialised);        // "Altitude(uninitialised)" -- safe for logs
/// double bad = uninitialised.Meters;       // throws
/// </code>
/// </remarks>
public readonly record struct Altitude : IFormattable
{
    private const string UninitialisedMessage =
        "Altitude was never initialised. Do not use 'default' or parameterless constructors.";

    private const string UninitialisedText = "Altitude(uninitialised)";

    /// <summary>Default numeric format: metres to two decimals, trailing zeros trimmed.</summary>
    private const string DefaultFormat = "0.##";

    /// <summary>Exact by the 1959 international definition of the foot; not an approximation.</summary>
    private const double MetresPerFoot = 0.3048;

    private readonly double _meters;
    private readonly AltitudeReference _reference;

    /// <summary>
    /// Validates and stores. Private: callers go through <see cref="FromMeters"/> or
    /// <see cref="FromFeet"/>, so the unit is stated at every call site.
    /// </summary>
    /// <param name="meters">The altitude in metres. Must be finite.</param>
    /// <param name="reference">The datum <paramref name="meters"/> is measured against.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not finite, or the reference is not a declared <see cref="AltitudeReference"/>.
    /// </exception>
    private Altitude(double meters, AltitudeReference reference)
    {
        // Non-finite values are rejected here because they are unrepresentable downstream, not
        // as a domain judgement: System.Text.Json throws on NaN and Infinity, so an unvalidated
        // NaN would surface as a failed response in the telemetry stream, far from its cause.
        if (!double.IsFinite(meters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(meters), meters, "Altitude must be a finite number of metres.");
        }

        // Catches both the uninitialised 0 and any out-of-band cast, e.g. (AltitudeReference)99.
        if (!Enum.IsDefined(reference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reference), reference, "Altitude must declare a reference (MCS-004).");
        }

        _meters = meters;
        _reference = reference;
    }

    /// <summary>
    /// Creates an altitude from a value already in metres.
    /// </summary>
    /// <param name="meters">The altitude in metres. Must be finite.</param>
    /// <param name="reference">The datum <paramref name="meters"/> is measured against.</param>
    /// <returns>A validated <see cref="Altitude"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="meters"/> is not finite, or <paramref name="reference"/> is not a
    /// declared <see cref="AltitudeReference"/>.
    /// </exception>
    public static Altitude FromMeters(double meters, AltitudeReference reference) =>
        new(meters, reference);

    /// <summary>
    /// Creates an altitude from a value in feet, converting to metres on the way in.
    /// </summary>
    /// <remarks>
    /// Feet are what a good deal of aviation equipment and airspace paperwork report, so the
    /// conversion belongs at the boundary rather than scattered through callers. The stored
    /// value is metres; there is no feet accessor, because a type that could hand back either
    /// unit would put the ambiguity straight back.
    /// </remarks>
    /// <param name="feet">The altitude in feet. Must be finite.</param>
    /// <param name="reference">The datum <paramref name="feet"/> is measured against.</param>
    /// <returns>A validated <see cref="Altitude"/> holding the equivalent in metres.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feet"/> is not finite, or <paramref name="reference"/> is not a
    /// declared <see cref="AltitudeReference"/>.
    /// </exception>
    public static Altitude FromFeet(double feet, AltitudeReference reference)
    {
        // Checked here rather than left to the constructor so the exception names the parameter
        // the caller actually passed: a NaN reported against "meters" sends the reader looking
        // for a metres-valued call site that does not exist. (Scaling by a factor below 1 cannot
        // overflow, so a finite input always yields a finite result.)
        if (!double.IsFinite(feet))
        {
            throw new ArgumentOutOfRangeException(
                nameof(feet), feet, "Altitude must be a finite number of feet.");
        }

        return new Altitude(feet * MetresPerFoot, reference);
    }

    /// <summary>Gets the altitude in metres, relative to <see cref="Reference"/>.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public double Meters => IsInitialised
        // A default instance reads back as 0 m, which is a plausible-looking altitude. That
        // plausibility is the hazard: it is the one value a caller would not question.
        ? _meters
        : throw new InvalidOperationException(UninitialisedMessage);

    /// <summary>Gets the datum <see cref="Meters"/> is measured against.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public AltitudeReference Reference => IsInitialised
        ? _reference
        : throw new InvalidOperationException(UninitialisedMessage);

    // The constructor rejects reference 0, so a zero reference can only mean the struct was
    // never constructed. Compared against 0 rather than 'default' because the test is for the
    // sentinel value specifically, not for whatever the type's default happens to be.
    private bool IsInitialised => _reference != 0;

    /// <summary>
    /// Formats as "1500.5 m Msl", in the invariant culture. Returns
    /// "Altitude(uninitialised)" rather than throwing for a <c>default</c> instance.
    /// </summary>
    /// <remarks>
    /// Describable rather than throwing, for the same reason as <c>VehicleId.ToString</c>: this
    /// is what log templates and debugger windows call, and an exception raised from a
    /// diagnostic is worse than the bad value it was trying to report.
    /// <para>
    /// Invariant by default because the default caller is a log or a debugger, where a container
    /// with a comma decimal separator must not change what the record says. Display code that
    /// wants the operator's culture, or a fixed number of decimals, calls
    /// <see cref="ToString(string?, IFormatProvider?)"/>.
    /// </para>
    /// </remarks>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the altitude with the given numeric format and culture.
    /// </summary>
    /// <param name="format">
    /// A standard or custom numeric format string applied to the metres value; <see langword="null"/>
    /// uses "0.##". The unit and the reference name are not affected by it.
    /// </param>
    /// <param name="formatProvider">
    /// Culture used for the number; <see langword="null"/> means the current culture, per the
    /// <see cref="IFormattable"/> convention. Note that this differs from
    /// <see cref="ToString()"/>, which is invariant -- pass a culture deliberately for display,
    /// and let logs take the parameterless overload.
    /// </param>
    /// <returns>
    /// The formatted altitude, or "Altitude(uninitialised)" for a <c>default</c> instance.
    /// </returns>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!IsInitialised)
        {
            return UninitialisedText;
        }

        // The reference is an enum name, so it is culture-independent by construction; the
        // provider only ever reaches the number.
        IFormatProvider provider = formatProvider ?? CultureInfo.CurrentCulture;
        return string.Create(
            provider, $"{_meters.ToString(format ?? DefaultFormat, provider)} m {_reference}");
    }
}
