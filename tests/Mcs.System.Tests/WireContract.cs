using System.Text.Json;

namespace Mcs.System.Tests;

/// <summary>
/// The paths this suite asks for, spelled out rather than imported.
/// </summary>
/// <remarks>
/// Taking these from <c>TelemetryEndpoints</c> would mean a route rename moved the test along with
/// the code, and the two would go on agreeing about a URL the console no longer calls. These are
/// the strings in <c>web/src/telemetry/client.ts</c> and <c>web/src/App.tsx</c>; if they stop
/// matching the API, something is broken and this is where it should show.
/// </remarks>
internal static class Routes
{
    public const string Liveness = "/health";

    public const string Readiness = "/health/db";

    public const string Snapshot = "/api/vehicles";

    public const string Stream = "/api/telemetry/stream";

    public const string WebRoot = "/";

    public const string BasemapStyle = "/basemap/style.json";

    /// <summary>
    /// The SSE event name carrying a frame. The stream also emits <c>heartbeat</c> events with a
    /// <c>null</c> payload, and no <c>id:</c> line or <c>:</c>-comment lines at all.
    /// </summary>
    public const string TelemetryEventType = "telemetry";
}

/// <summary>
/// How the station's JSON is read here: camelCase, which is what ASP.NET Core's web defaults write.
/// </summary>
/// <remarks>
/// Constructed rather than taken from the host, for the same reason the DTOs below are retyped --
/// a change to the server's serialiser options should fail a test rather than be silently agreed
/// with.
/// </remarks>
internal static class WireFormat
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// One vehicle's latest frame, as the console sees it.
/// </summary>
/// <remarks>
/// Mirrors <c>VehicleFrame</c> in <c>web/src/telemetry/types.ts</c>, not <c>VehicleFrameResponse</c>
/// on the API side. The enums are <see cref="string"/> here on purpose: the host writes enum member
/// names, and a renumbered or renamed member has to fail an assertion rather than be quietly
/// re-parsed into whatever the compiled enum now says that name means.
/// <para>
/// Extra properties the API grows are ignored, which is correct -- adding a field breaks no client.
/// A renamed or removed one leaves a default here and fails the assertion that reads it, which is
/// also correct, because that is exactly what it does to the browser.
/// </para>
/// </remarks>
internal sealed record VehicleFrame(
    string VehicleId,
    double LatitudeDegrees,
    double LongitudeDegrees,
    Altitude Altitude,
    double GroundSpeedMetersPerSecond,
    double HeadingDegrees,
    double? BatteryPercent,
    string LinkStatus,
    DateTimeOffset ReceivedAtUtc);

/// <summary>An altitude and the datum it was measured against, nested rather than flattened.</summary>
internal sealed record Altitude(double Meters, string Reference);

/// <summary>
/// A health endpoint's body: a status, plus whatever the checks that ran contributed.
/// </summary>
/// <remarks>
/// The version fields are nullable because liveness runs no checks and so reports neither, and
/// because readiness omits <see cref="SchemaVersion"/> when it could not read one. Absent and zero
/// are different answers and the type has to be able to tell them apart.
/// </remarks>
internal sealed record HealthReport(
    string Status,
    int? ExpectedSchemaVersion,
    int? SchemaVersion,
    string? Detail);
