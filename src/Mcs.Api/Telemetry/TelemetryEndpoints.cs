using Microsoft.AspNetCore.Http.HttpResults;

using Mcs.Core;

namespace Mcs.Api.Telemetry;

/// <summary>
/// The station's two telemetry endpoints: <c>GET /api/vehicles</c> for where everything is right
/// now, and <c>GET /api/telemetry/stream</c> for everything that happens next.
/// </summary>
/// <remarks>
/// Both are thin -- read the store, map to a DTO, return.
/// <para>
/// Snapshot <i>and</i> stream, because a client that only subscribed would show a blank map until
/// the next frame. SSE rather than a socket, because the traffic is one-directional and
/// <c>EventSource</c> reconnects on its own.
/// </para>
/// </remarks>
public static class TelemetryEndpoints
{
    /// <summary>The latest frame per known vehicle.</summary>
    public const string SnapshotPath = "/api/vehicles";

    /// <summary>Frames as they arrive, as server-sent events.</summary>
    public const string StreamPath = "/api/telemetry/stream";

    /// <summary>The SSE event type carrying a <see cref="VehicleFrameResponse"/>.</summary>
    /// <remarks>
    /// Named rather than the default <c>message</c>, so later event kinds -- alerts, command status
    /// -- can share this stream without a discriminator inside the payload.
    /// </remarks>
    public const string TelemetryEventType = "telemetry";

    /// <summary>The SSE event type that only keeps the connection open. See <see cref="TelemetrySseStream"/>.</summary>
    public const string HeartbeatEventType = "heartbeat";

    /// <summary>How long the stream may go quiet before it sends a heartbeat.</summary>
    /// <remarks>Comfortably inside the 30-60 s idle timeout proxies and load balancers default to.</remarks>
    public static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maps both endpoints. No <c>MapGroup</c>: two routes with nothing shared to hang on a group,
    /// and the paths carry the <c>/api</c> prefix nginx proxies on their own.
    /// </summary>
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(SnapshotPath, GetVehicles)
            .WithName("GetVehicles")
            .WithSummary("The latest frame from every vehicle the station knows about.");

        endpoints.MapGet(StreamPath, StreamTelemetry)
            .WithName("StreamTelemetry")
            .WithSummary("Telemetry frames as they arrive, as server-sent events.");

        return endpoints;
    }

    /// <summary>
    /// Serves the current state of the fleet: one frame per vehicle, in no particular order.
    /// </summary>
    private static Ok<IReadOnlyList<VehicleFrameResponse>> GetVehicles(ITelemetryStore store)
    {
        IReadOnlyList<TelemetryFrame> snapshot = store.GetLatestSnapshot();

        //  An empty fleet is 200 and [], never 404 -- "nothing is flying" is a valid answer to
        //  this question and the console has to render it.
        return TypedResults.Ok<IReadOnlyList<VehicleFrameResponse>>(
            [.. snapshot.Select(VehicleFrameResponse.From)]);
    }

    /// <summary>
    /// Opens a live stream of frames for one client, until it disconnects.
    /// </summary>
    /// <remarks>
    /// Backpressure is the store's: a subscriber queue is bounded at
    /// <see cref="ITelemetryStore.SubscriberBufferCapacity"/> and drops its <i>oldest</i> frames, so
    /// a slow client is shown the present rather than a smooth, complete and stale replay.
    /// </remarks>
    private static ServerSentEventsResult<VehicleFrameResponse?> StreamTelemetry(
        ITelemetryStore store,
        TimeProvider timeProvider,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        //  nginx buffers a proxied response by default, and a buffered SSE stream arrives in bursts
        //  -- which reads as a broken feed and never gets diagnosed as a proxy setting.
        //  ServerSentEventsResult sets the rest of the headers; this one is nginx-specific.
        response.Headers["X-Accel-Buffering"] = "no";

        return TypedResults.ServerSentEvents<VehicleFrameResponse?>(
            TelemetrySseStream.WithHeartbeat(
                store.Subscribe(cancellationToken),
                HeartbeatPeriod,
                timeProvider,
                cancellationToken));
    }
}
