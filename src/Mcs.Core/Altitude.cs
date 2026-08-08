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
/// Converting between these references (MSL to AGL requires terrain elevation) is M3's work.
/// Until then a value must be consumed against the reference it was reported with.
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
/// call site at once -- a bare <c>double</c> altitude is a bug that comes due at M3, when
/// MSL/AGL conversion arrives and there is no way to tell which values need converting.
/// <para>
/// Not a positional record on purpose. Positional records generate <c>init</c> accessors, and
/// a <c>with</c> expression assigns those directly without re-running the constructor -- so
/// constructor validation alone would leave <c>alt with { Reference = (AltitudeReference)0 }</c>
/// as an unguarded hole. Get-only properties make that a compile error instead.
/// </para>
/// <para>
/// Serialisation note for the telemetry API: the reference must reach the browser as a name
/// ("Msl"), not as the underlying number. Apply <c>JsonStringEnumConverter</c> at the DTO
/// layer -- a bare <c>1</c> on the wire is not self-describing, and renumbering the enum
/// would silently change the contract.
/// </para>
/// </remarks>
public readonly record struct Altitude
{
    private readonly double _meters;
    private readonly AltitudeReference _reference;

    /// <summary>
    /// Creates an altitude. This is the only way to obtain a valid instance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not finite, or the reference is not a declared <see cref="AltitudeReference"/>.
    /// </exception>
    public Altitude(double meters, AltitudeReference reference)
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
    // never constructed. No separate initialisation flag needed.
    private bool IsInitialised => _reference != default;

    private const string UninitialisedMessage =
        "Altitude was never initialised. Do not use 'default' or parameterless constructors.";

    // Describable rather than throwing, for the same reason as VehicleId.ToString: this is what
    // log templates and debugger windows call, and an exception raised from a diagnostic is
    // worse than the bad value it was trying to report. Invariant culture so a container with a
    // comma decimal separator does not change what the logs say.
    public override string ToString() => IsInitialised
        ? string.Create(CultureInfo.InvariantCulture, $"{_meters:0.##} m {_reference}")
        : "Altitude(uninitialised)";
}
