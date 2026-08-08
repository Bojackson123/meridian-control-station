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
/// </remarks>
public readonly record struct VehicleId
{
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
        ?? throw new InvalidOperationException("VehicleId was never initialised. Do not use 'default' or parameterless constructors.");

    /// <summary>
    /// Creates a valid VehicleId. This is the only permitted way to instantiate this type.
    /// </summary>
    public static VehicleId From(string value)
    {
        // Centralized validation: Guarantees every ID in the system is valid at the point of creation.
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new VehicleId(value);
    }

    // Deliberately does NOT route through Value. ToString is what diagnostics call -- log
    // templates, string interpolation, debugger watch windows -- so a throwing ToString turns
    // a logged bad id into an exception raised by the log statement itself, which in a catch
    // block would replace the original exception. Value already fails fast for every
    // non-diagnostic use; this stays describable.
    public override string ToString() => _value ?? "VehicleId(uninitialised)";
}