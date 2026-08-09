namespace Mcs.Core;

/// <summary>
/// Strongly-typed identifier for a vehicle. <see cref="From(string)"/> is the only way in.
/// </summary>
/// <remarks>
/// A readonly record struct, so it keys the telemetry store without boxing. <c>default</c> is the
/// one case the language will not let a struct refuse, so <see cref="Value"/> rejects it on read.
/// <para>
/// Comparison is ordinal and case-sensitive, but surrounding whitespace is trimmed. Case can be
/// meaningful in an id somebody chose; padding never is, and storing it would produce a third track
/// rendering identically to the other two and equal to neither. Adapters deriving ids from MAVLink
/// system IDs must normalise casing themselves.
/// </para>
/// <para>
/// Accepted after trimming: 1 to <see cref="MaxLength"/> characters of ASCII letters, digits, '-' or
/// '_'. The allowlist covers the two places an id travels unescaped -- a URL path segment and a log
/// line -- so nothing can ride an id into the browser or split a log record in two.
/// </para>
/// <para>
/// Serialisation: must cross the wire as a bare string, so register a
/// <c>JsonConverter&lt;VehicleId&gt;</c> at the DTO layer that writes <see cref="Value"/> and reads
/// through <see cref="From"/>. Left alone, System.Text.Json emits <c>{"Value":"UAV-01"}</c> and
/// cannot read it back.
/// </para>
/// </remarks>
public readonly record struct VehicleId
{
    /// <summary>
    /// Maximum length of an id after trimming. Public so the ingest boundary and any UI-side check
    /// state the same limit. Long enough for a MAVLink-derived id, a tail number or a callsign;
    /// short enough that a broken adapter cannot hand the store a multi-megabyte key.
    /// </summary>
    public const int MaxLength = 64;

    private const string UninitialisedMessage =
        "VehicleId was never initialised. Do not use 'default' or parameterless constructors.";

    private const string UninitialisedText = "VehicleId(uninitialised)";

    //  Nullable, so an instance created via `default` or `new()` -- which bypass the constructor and
    //  cannot be prevented -- is detectable.
    private readonly string? _value;

    private VehicleId(string value) => _value = value;

    /// <summary>Gets the underlying string value.</summary>
    /// <exception cref="InvalidOperationException">The instance was never initialised.</exception>
    public string Value => _value
        //  Fires on read, not on use: a default id can still key a dictionary without touching
        //  Value, so the store's write path validates as well.
        ?? throw new InvalidOperationException(UninitialisedMessage);

    private bool IsInitialised => _value is not null;

    /// <summary>
    /// Creates a valid <see cref="VehicleId"/>. The only permitted way to instantiate this type.
    /// </summary>
    /// <param name="value">
    /// The raw id. Trimmed, then checked against <see cref="MaxLength"/> and the allowlist.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Empty, whitespace, or outside the allowlist.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Longer than <see cref="MaxLength"/>.</exception>
    public static VehicleId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        //  Trim first, so the cap and the allowlist judge the string that will actually be stored.
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

            //  Reports the code point, never the character: echoing a rejected control character
            //  would put it in the log line recording the rejection.
            throw new ArgumentException(
                $"Vehicle id may contain only ASCII letters, digits, '-' and '_'; found U+{(int)c:X4} at index {i}.",
                nameof(value));
        }

        return new VehicleId(trimmed);
    }

    /// <summary>
    /// Returns the id, or "VehicleId(uninitialised)" for a <c>default</c> instance. Never throws --
    /// this is what log templates and debuggers call, and an exception raised from a diagnostic
    /// inside a catch block would replace the original.
    /// </summary>
    public override string ToString() => IsInitialised ? _value! : UninitialisedText;
}
