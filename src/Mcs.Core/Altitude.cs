using System.Globalization;

namespace Mcs.Core;

/// <summary>
/// The datum an altitude is measured against.
/// </summary>
/// <remarks>
/// No zero member, deliberately. An unassigned struct field reads back as 0, so leaving 0 undefined
/// means an uninitialised altitude cannot silently claim <see cref="Msl"/>; <see cref="Altitude"/>
/// uses that 0 as its initialisation sentinel. Do not add a member for it.
/// <para>
/// Converting between these requires terrain elevation, which the station does not hold. Until it
/// does, a value must be consumed against the reference it was reported with.
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
/// An altitude and the reference it was measured against, inseparably (MCS-004).
/// </summary>
/// <remarks>
/// Pairing the two in one type meets MCS-004 -- reject any position report that does not declare an
/// altitude reference -- at every call site at once. A bare <c>double</c> altitude is a bug that
/// comes due the day MSL/AGL conversion arrives and there is no way to tell which values need it.
/// <para>
/// Not a positional record: those generate <c>init</c> accessors, and <c>with</c> assigns them
/// without re-running validation, leaving <c>alt with { Reference = (AltitudeReference)0 }</c> as a
/// hole. The factories are named for their unit because <c>new Altitude(120, Agl)</c> puts the unit
/// in a parameter name nobody reads at the call site -- which is how a feet-valued sensor reading
/// gets stored as metres.
/// </para>
/// <para>
/// No <see cref="IComparable{T}"/>: ordering is undefined across references, so any
/// <c>CompareTo</c> would either lie for mixed pairs or throw from inside <c>Sort</c>. Group by
/// <see cref="Reference"/>, then order by <see cref="Meters"/>.
/// </para>
/// <para>
/// Serialisation: apply <c>JsonStringEnumConverter</c> at the DTO layer so the reference reaches the
/// browser as a name, not a renumberable integer. Deserialising straight into this type will not
/// work, which is intended -- inbound JSON passes through <see cref="FromMeters"/>.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// var altitude = Altitude.FromMeters(1500.5, AltitudeReference.Msl);  // "1500.5 m Msl"
/// var fromSensor = Altitude.FromFeet(4922.9, AltitudeReference.Msl);  // ~1500.5 m
/// // Altitude.FromMeters(100, (AltitudeReference)0);  // ArgumentOutOfRangeException
///
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

    private Altitude(double meters, AltitudeReference reference)
    {
        //  Non-finite is unrepresentable downstream rather than merely wrong: System.Text.Json throws
        //  on NaN and Infinity, so an unvalidated one would surface as a failed response in the
        //  telemetry stream, far from its cause.
        if (!double.IsFinite(meters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(meters), meters, "Altitude must be a finite number of metres.");
        }

        //  Catches both the uninitialised 0 and any out-of-band cast, e.g. (AltitudeReference)99.
        if (!Enum.IsDefined(reference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reference), reference, "Altitude must declare a reference (MCS-004).");
        }

        _meters = meters;
        _reference = reference;
    }

    /// <summary>Creates an altitude from a value already in metres.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not finite, or the reference is undeclared.</exception>
    public static Altitude FromMeters(double meters, AltitudeReference reference) =>
        new(meters, reference);

    /// <summary>
    /// Creates an altitude from a value in feet, converting to metres on the way in. There is no
    /// feet accessor: a type that could hand back either unit would put the ambiguity straight back.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not finite, or the reference is undeclared.</exception>
    public static Altitude FromFeet(double feet, AltitudeReference reference)
    {
        //  Checked here rather than left to the constructor so the exception names the parameter the
        //  caller passed. (Scaling by a factor below 1 cannot overflow.)
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
        //  A default instance would read back as 0 m -- a plausible altitude, and that plausibility
        //  is the hazard: it is the one value a caller would not question.
        ? _meters
        : throw new InvalidOperationException(UninitialisedMessage);

    /// <summary>Gets the datum <see cref="Meters"/> is measured against.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public AltitudeReference Reference => IsInitialised
        ? _reference
        : throw new InvalidOperationException(UninitialisedMessage);

    //  The constructor rejects reference 0, so a zero reference can only mean "never constructed".
    private bool IsInitialised => _reference != 0;

    /// <summary>
    /// Formats as "1500.5 m Msl", invariant. Returns "Altitude(uninitialised)" rather than throwing,
    /// for the same reason as <see cref="VehicleId.ToString"/>. Invariant because the default caller
    /// is a log or a debugger, where a comma decimal separator must not change what the record says;
    /// display code calls <see cref="ToString(string?, IFormatProvider?)"/>.
    /// </summary>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <summary>Formats the altitude with the given numeric format and culture.</summary>
    /// <param name="format">Applied to the metres value; <see langword="null"/> uses "0.##".</param>
    /// <param name="formatProvider">
    /// <see langword="null"/> means the current culture, per <see cref="IFormattable"/> -- note this
    /// differs from <see cref="ToString()"/>, which is invariant.
    /// </param>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!IsInitialised)
        {
            return UninitialisedText;
        }

        //  The reference is an enum name, so the provider only ever reaches the number.
        IFormatProvider provider = formatProvider ?? CultureInfo.CurrentCulture;
        return string.Create(
            provider, $"{_meters.ToString(format ?? DefaultFormat, provider)} m {_reference}");
    }
}
