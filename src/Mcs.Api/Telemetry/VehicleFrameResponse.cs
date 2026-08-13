using Mcs.Core;

namespace Mcs.Api.Telemetry;

/// <summary>
/// One vehicle's latest frame as it goes on the wire, together with the station's judgement of how
/// current it is: an element of <c>GET /api/vehicles</c> and of both event types on the stream.
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

    //  The station's own three, and the reason this DTO is flat: everything above is a claim by the
    //  vehicle, everything from here down is what the station observed about it. MCS-002's answer
    //  travels with the data it is about, so a consumer cannot render one without the other.
    VehicleState State,
    long AgeMilliseconds,
    DateTimeOffset ReceivedAtUtc)
{
    /// <summary>Projects a stored frame onto the wire shape, with how current it is.</summary>
    /// <remarks>
    /// The currency is a parameter rather than something computed here, so that a fleet can be
    /// projected against one clock reading -- and so that there is no overload which quietly omits
    /// it. A frame reaching the console without an age attached is the state this whole ticket
    /// exists to remove.
    /// </remarks>
    public static VehicleFrameResponse From(TelemetryFrame frame, TelemetryCurrency currency)
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
            currency.State,

            //  Whole milliseconds. The console formats seconds and minutes, so the fraction is
            //  noise, and an integer keeps the JSON free of the "2999.9999999999995" a double
            //  round-trip produces -- a number that reads as a precision claim nobody is making.
            (long)currency.Age.TotalMilliseconds,
            frame.ReceivedAtUtc);
    }

    /// <summary>
    /// Projects a whole fleet, every vehicle of it against a single reading of the station clock.
    /// </summary>
    /// <remarks>
    /// <b>The one projection.</b> The snapshot endpoint and the stream's fleet tick both come
    /// through here, so the map and the vehicle panel cannot end up deriving a vehicle's state two
    /// different ways and disagreeing at the boundary (MCS-003).
    /// </remarks>
    /// <param name="frames">The latest frame per vehicle, from <see cref="ITelemetryStore"/>.</param>
    /// <param name="clock">The station clock -- the provider the frames were stamped by.</param>
    public static IReadOnlyList<VehicleFrameResponse> Fleet(
        IReadOnlyList<TelemetryFrame> frames, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(clock);

        //  Read once, outside the loop. Twelve reads would differ by microseconds and nothing would
        //  render differently -- but a fleet view is one answer to one question, and it costs
        //  nothing to have it be exactly that.
        long now = clock.GetTimestamp();

        return [.. frames.Select(frame => From(frame, TelemetryCurrency.Of(frame, clock, now)))];
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
