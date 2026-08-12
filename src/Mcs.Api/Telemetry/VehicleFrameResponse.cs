using Mcs.Core;

namespace Mcs.Api.Telemetry;

/// <summary>
/// One vehicle's latest frame as it goes on the wire: an element of <c>GET /api/vehicles</c> and the
/// payload of a <c>telemetry</c> event.
/// </summary>
/// <remarks>
/// A DTO rather than <see cref="TelemetryFrame"/> itself, so renaming a Core member is a refactor
/// rather than a breaking change for the console. Positional, where <c>Mcs.Core</c> refuses to be:
/// the objection there is that <c>with</c> assigns <c>init</c> accessors without re-validating, and
/// a wire record has no invariant for that to break.
/// </remarks>
public sealed record VehicleFrameResponse(
    string VehicleId,
    double LatitudeDegrees,
    double LongitudeDegrees,
    AltitudeResponse Altitude,

    //  Nullable on the wire, all three: System.Text.Json writes them as `null` rather than omitting
    //  them, so a console reading this cannot mistake "the vehicle did not report a heading" for a
    //  field the API forgot to send. A zero here would be indistinguishable from a real one.
    double? GroundSpeedMetersPerSecond,
    double? HeadingDegrees,
    double? BatteryPercent,
    LinkStatus LinkStatus,
    DateTimeOffset ReceivedAtUtc)
{
    /// <summary>Projects a stored frame onto the wire shape.</summary>
    public static VehicleFrameResponse From(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return new VehicleFrameResponse(
            frame.Telemetry.Id.Value,
            frame.Telemetry.LatitudeDegrees,
            frame.Telemetry.LongitudeDegrees,
            new AltitudeResponse(frame.Telemetry.Altitude.Meters, frame.Telemetry.Altitude.Reference),
            frame.Telemetry.GroundSpeedMetersPerSecond,
            frame.Telemetry.HeadingDegrees,
            frame.Telemetry.BatteryPercent,
            frame.Telemetry.LinkStatus,
            frame.ReceivedAtUtc);
    }
}

/// <summary>
/// An altitude on the wire: the value and the reference it was measured against, together.
/// </summary>
/// <remarks>
/// An object rather than a flat <c>altitudeMeters</c> because MCS-004's whole point is that the two
/// travel together, and a client that receives a number has no way to ask what it is above.
/// </remarks>
public sealed record AltitudeResponse(double Meters, AltitudeReference Reference);
