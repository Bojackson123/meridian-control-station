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
    /// The SSE event name carrying the vehicle that just reported. There are no <c>id:</c> lines and
    /// no <c>:</c>-comment lines on this stream at all.
    /// </summary>
    public const string TelemetryEventType = "telemetry";

    /// <summary>
    /// The SSE event name carrying the whole fleet with its ages re-evaluated, sent on a schedule
    /// whether or not anything reported.
    /// </summary>
    public const string FleetEventType = "fleet";
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

    //  Nullable, matching the console's own types. This was widened in anticipation of a real
    //  adapter emitting a position before its speed and heading were known, back when the feed
    //  reported both on every frame and `double` would have passed. It is no longer hypothetical:
    //  the station decodes position and speed from two different MAVLink messages arriving at two
    //  different rates, and the adapter's own counters record positionsWithoutHud on an ordinary
    //  run. Declaring these non-nullable would throw against the stack this suite drives.
    double? GroundSpeedMetersPerSecond,
    double? HeadingDegrees,
    double? BatteryPercent,
    string LinkStatus,

    //  A string for the same reason LinkStatus is one: the host writes enum member names, and this
    //  is the field a console decides how much to trust a marker by. A renumbering or a rename has
    //  to fail an assertion here rather than be quietly re-parsed into whatever the compiled enum
    //  now says "2" means.
    string State,
    long AgeMilliseconds,
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
