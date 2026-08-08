namespace Mcs.Core;

/// <summary>
/// Strongly-typed identifier for a vehicle.
/// </summary>
/// <remarks>
/// A readonly record struct so it can key the telemetry store without boxing, using the
/// smart-constructor pattern: <see cref="From(string)"/> is the only way in, so no instance
/// reaches the domain unvalidated.
/// <para>
/// Comparison is ordinal and case-sensitive: "uav-01" and "UAV-01" are two different vehicles.
/// That is the intended behaviour for ids the station assigns itself. M1's MAVLink adapter
/// derives ids from system IDs and must normalise casing at that boundary, or one vehicle
/// will render as two tracks.
/// </para>
/// <para>
/// Surrounding whitespace, on the other hand, <i>is</i> normalised: <see cref="From"/> trims
/// before storing, so " UAV-01 " and "UAV-01" are one vehicle. The asymmetry with casing is
/// deliberate. Case can be meaningful in an id somebody chose; padding never is -- it is an
/// artefact of a CSV column, a query string, or a hand-edited config, and storing it would
/// produce a third track that renders identically to the other two and compares equal to
/// neither. Callers needing the id byte-exact must not use this type.
/// </para>
/// <para>
/// Accepted shape after trimming: 1 to <see cref="MaxLength"/> characters, each an ASCII
/// letter, digit, '-' or '_'. The cap bounds what a single ingest message can commit in a
/// store that is bounded by vehicle count but not by key size. The allowlist covers the two
/// places an id travels unescaped -- a URL path segment and a log line -- so a control
/// character, a quote, or an angle bracket cannot ride an id into the browser or split a log
/// record in two.
/// </para>
/// <para>
/// Serialisation: this must cross the wire as a bare string ("UAV-01"). Left alone,
/// System.Text.Json would serialise the public surface and emit <c>{"Value":"UAV-01"}</c>, and
/// would then fail to read it back, because <see cref="From"/> is the only constructor.
/// Register a <c>JsonConverter&lt;VehicleId&gt;</c> at the DTO layer that writes
/// <see cref="Value"/> and reads through <see cref="From"/>, so inbound JSON is validated
/// rather than trusted. The same converter covers the id's use as a JSON property name and as
/// a route parameter -- both of which are safe precisely because of the allowlist above.
/// </para>
///
/// <b>Example:</b>
/// <code>
/// // Valid instantiation:
/// VehicleId id = VehicleId.From("UAV-01");
/// VehicleId padded = VehicleId.From("  UAV-01  "); // trimmed: equal to id
///
/// // Rejected -- ArgumentException:
/// // VehicleId.From("");           // and null, and all-whitespace
/// // VehicleId.From("UAV 01");     // space is not in the allowlist
/// // VehicleId.From(new string('x', 65)); // ArgumentOutOfRangeException
///
/// // Bypassing validation throws on read:
/// VehicleId badId = default;
/// Console.WriteLine(badId.ToString()); // Safe for logs: "VehicleId(uninitialised)"
/// string val = badId.Value;            // Throws InvalidOperationException!
/// </code>
/// </remarks>
public readonly record struct VehicleId
{
    /// <summary>
    /// Maximum length of an id, in characters, after trimming. Public so the ingest boundary
    /// and any UI-side check state the same limit instead of drifting apart.
    /// </summary>
    /// <remarks>
    /// Long enough for anything the domain actually produces -- a MAVLink-derived id, a tail
    /// number, a mission callsign -- and short enough that a hostile or broken adapter cannot
    /// hand the store a multi-megabyte key. The number is a budget, not a measurement; widen
    /// it if a real id needs it.
    /// </remarks>
    public const int MaxLength = 64;

    private const string UninitialisedMessage =
        "VehicleId was never initialised. Do not use 'default' or parameterless constructors.";

    private const string UninitialisedText = "VehicleId(uninitialised)";

    // Structs in C# can always be created via `default` or `new()`, which bypasses custom constructors.
    // We use a nullable backing field so we can detect if this instance was created without validation.
    private readonly string? _value;

    // Private constructor forces consumers to use the From() factory method,
    // ensuring validation cannot be bypassed.
    private VehicleId(string value) => _value = value;

    /// <summary>
    /// Gets the underlying string value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the struct was uninitialized (e.g., via `default`).</exception>
    public string Value => _value
        // Fail-fast defence: an uninitialised id must not propagate into the domain, where it
        // would silently occupy one of the store's bounded vehicle slots. Note this fires on
        // read, not on use -- a default id can still be used as a dictionary key without
        // touching Value, so the store's write path validates as well. The type narrows the
        // mistake; the ingest boundary is what closes it.
        ?? throw new InvalidOperationException(UninitialisedMessage);

    // Mirrors Altitude.IsInitialised. Only ToString needs it -- Value reads better with the
    // null-coalescing throw -- but the two types should answer "was this ever constructed?"
    // the same way, so the next member added to either does not have to invent it again.
    private bool IsInitialised => _value is not null;

    /// <summary>
    /// Creates a valid VehicleId. This is the only permitted way to instantiate this type.
    /// </summary>
    /// <param name="value">
    /// The raw id. Surrounding whitespace is trimmed; after trimming it must be 1 to
    /// <see cref="MaxLength"/> characters of ASCII letters, digits, '-' or '_'.
    /// </param>
    /// <returns>A validated <see cref="VehicleId"/> holding the trimmed value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or whitespace, or contains a character outside the
    /// allowlist.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is longer than <see cref="MaxLength"/> after trimming.
    /// </exception>
    public static VehicleId From(string value)
    {
        // Centralized validation: Guarantees every ID in the system is valid at the point of creation.
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // Trim before every other check, so the length cap and the allowlist judge the string
        // that will actually be stored rather than its transport packaging.
        string trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                trimmed.Length,
                $"Vehicle id must be at most {MaxLength} characters; got {trimmed.Length}.");
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                continue;
            }

            // Reports the code point, never the character itself. Echoing a rejected control
            // character into an exception message would put it in the log line that records
            // the rejection -- reintroducing, in the diagnostic, exactly what the allowlist
            // exists to keep out.
            throw new ArgumentException(
                $"Vehicle id may contain only ASCII letters, digits, '-' and '_'; found U+{(int)c:X4} at index {i}.",
                nameof(value));
        }

        return new VehicleId(trimmed);
    }

    /// <summary>
    /// Returns the id, or "VehicleId(uninitialised)" for a <c>default</c> instance. Never throws.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT route through <see cref="Value"/>. ToString is what diagnostics call
    /// -- log templates, string interpolation, debugger watch windows -- so a throwing ToString
    /// turns a logged bad id into an exception raised by the log statement itself, which in a
    /// catch block would replace the original exception. <see cref="Value"/> already fails fast
    /// for every non-diagnostic use; this stays describable.
    /// <para>
    /// The fallback covers exactly one case, and it is not a judgement call: <see cref="From"/>
    /// rejects null, empty, whitespace-only and over-long input, so the field is either null
    /// (never constructed) or a valid non-empty id. There is no path that renders an empty log
    /// line, and no culture-dependent formatting to get wrong.
    /// </para>
    /// </remarks>
    public override string ToString() => IsInitialised ? _value! : UninitialisedText;
}
