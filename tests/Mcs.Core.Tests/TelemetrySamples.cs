namespace Mcs.Core.Tests;

/// <summary>
/// Builds a valid <see cref="VehicleTelemetry"/> that tests vary one field of at a time.
/// </summary>
/// <remarks>
/// <see cref="VehicleTelemetry.Create"/> takes eight arguments, seven of which are irrelevant to
/// any given validation case. Spelling all eight out at each call site would bury the one value
/// under test among seven that are only there to be valid -- so the defaults live here and a test
/// names, by keyword, exactly the field it is exercising.
/// <para>
/// The values are the ones from the type's own XML example, so what the tests assert against and
/// what the documentation claims stay the same thing.
/// </para>
/// </remarks>
internal static class TelemetrySamples
{
    /// <summary>The sample vehicle, as a raw string for tests that need to build the id themselves.</summary>
    public const string Id = "UAV-01";

    /// <summary>
    /// Creates a valid report, overriding any subset of its fields.
    /// </summary>
    /// <remarks>
    /// <paramref name="id"/> and <paramref name="altitude"/> are nullable only so "leave the
    /// default" is expressible; passing <c>default(VehicleId)</c> or <c>default(Altitude)</c>
    /// reaches <see cref="VehicleTelemetry.Create"/> untouched, which is what the
    /// uninitialised-struct tests depend on. <paramref name="batteryPercent"/> is different: it is
    /// genuinely nullable in the domain, so a <see langword="null"/> passed here means "not
    /// reported" and is forwarded as-is.
    /// </remarks>
    public static VehicleTelemetry Telemetry(
        VehicleId? id = null,
        double latitudeDegrees = 51.5074,
        double longitudeDegrees = -0.1278,
        Altitude? altitude = null,
        double groundSpeedMetersPerSecond = 14.2,
        double headingDegrees = 12.5,
        double? batteryPercent = 87.0,
        LinkStatus linkStatus = LinkStatus.Healthy) =>
        VehicleTelemetry.Create(
            id ?? VehicleId.From(Id),
            latitudeDegrees,
            longitudeDegrees,
            altitude ?? Altitude.FromMeters(120, AltitudeReference.Agl),
            groundSpeedMetersPerSecond,
            headingDegrees,
            batteryPercent,
            linkStatus);

    /// <summary>
    /// The exact text <see cref="object.ToString"/> produces for an unmodified
    /// <see cref="Telemetry"/>, pinned once so the frame's own formatting test can quote it.
    /// </summary>
    public const string TelemetryText =
        "VehicleTelemetry { Id = UAV-01, LatitudeDegrees = 51.5074, LongitudeDegrees = -0.1278, "
        + "Altitude = 120 m Agl, GroundSpeedMetersPerSecond = 14.2, HeadingDegrees = 12.5, "
        + "BatteryPercent = 87, LinkStatus = Healthy }";
}
