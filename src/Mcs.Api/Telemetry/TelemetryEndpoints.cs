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
/// <para>
/// <b>Every payload here is a list of vehicles</b>, whether it holds one of them or twelve. Two
/// shapes on one stream would mean either an untyped result or a discriminator inside the JSON, and
/// the event name is already the discriminator SSE provides. It also makes the snapshot and the
/// fleet tick literally the same bytes, which is what stops a console from having two code paths
/// that can disagree about a vehicle's state.
/// </para>
/// </remarks>
public static class TelemetryEndpoints
{
    /// <summary>The latest frame per known vehicle.</summary>
    public const string SnapshotPath = "/api/vehicles";

    /// <summary>Frames as they arrive, as server-sent events.</summary>
    public const string StreamPath = "/api/telemetry/stream";

    /// <summary>The SSE event type carrying the one vehicle that has just reported.</summary>
    /// <remarks>
    /// Named rather than the default <c>message</c>, so later event kinds -- alerts, command status
    /// -- can share this stream without a discriminator inside the payload.
    /// </remarks>
    public const string TelemetryEventType = "telemetry";

    /// <summary>
    /// The SSE event type carrying the whole fleet, re-evaluated. See <see cref="TelemetrySseStream"/>.
    /// </summary>
    /// <remarks>
    /// This was a <c>heartbeat</c> with an empty payload, and it changed name when it stopped being
    /// one. It still keeps the connection alive through an idle proxy, but that is now a side effect
    /// of its actual job: a vehicle that has gone quiet sends nothing, so without a scheduled event
    /// carrying the station's answer, the console would go on showing the last state it was told
    /// about -- for a vehicle that has stopped reporting, forever. Silence cannot be delivered by
    /// the silent party.
    /// </remarks>
    public const string FleetEventType = "fleet";

    /// <summary>How often the stream re-states the whole fleet, whether or not anything reported.</summary>
    /// <remarks>
    /// A third of <see cref="TelemetryCurrency.StaleAfter"/>, derived rather than picked so it
    /// follows the threshold it exists to serve: a vehicle crossing into stale is on the wire within
    /// a third of the window that defines the crossing. Deriving it also removes a way for the two
    /// to drift apart, which is how a console ends up reporting a three-second rule ten seconds late.
    /// <para>
    /// The old 15 s heartbeat was sized against the 30-60 s idle timeout proxies default to. That
    /// constraint is still met, by an order of magnitude, and is no longer what sets the number.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan FleetTickPeriod = TelemetryCurrency.StaleAfter / 3;

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
    /// Serves the current state of the fleet: one frame per vehicle, in no particular order, each
    /// with its age and state as of this request (MCS-002).
    /// </summary>
    private static Ok<IReadOnlyList<VehicleFrameResponse>> GetVehicles(
        ITelemetryStore store, TimeProvider timeProvider)
    {
        //  An empty fleet is 200 and [], never 404 -- "nothing is flying" is a valid answer to
        //  this question and the console has to render it. A vehicle the station has never heard
        //  from is absent for the same reason it has no state: there is nothing to be current or
        //  stale about, and inventing an entry for it would be the console's first lie.
        return TypedResults.Ok(
            VehicleFrameResponse.Fleet(store.GetLatestSnapshot(), timeProvider));
    }

    /// <summary>
    /// Opens a live stream of frames for one client, until it disconnects.
    /// </summary>
    /// <remarks>
    /// Backpressure is the store's: a subscriber queue is bounded at
    /// <see cref="ITelemetryStore.SubscriberBufferCapacity"/> and drops its <i>oldest</i> frames, so
    /// a slow client is shown the present rather than a smooth, complete and stale replay.
    /// </remarks>
    private static ServerSentEventsResult<IReadOnlyList<VehicleFrameResponse>> StreamTelemetry(
        ITelemetryStore store,
        TimeProvider timeProvider,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        //  nginx buffers a proxied response by default, and a buffered SSE stream arrives in bursts
        //  -- which reads as a broken feed and never gets diagnosed as a proxy setting.
        //  ServerSentEventsResult sets the rest of the headers; this one is nginx-specific.
        response.Headers["X-Accel-Buffering"] = "no";

        return TypedResults.ServerSentEvents(
            TelemetrySseStream.WithFleetTicks(
                store.Subscribe(cancellationToken),

                //  The store is read at each tick rather than captured once: the tick's whole
                //  purpose is to say what is true now, and a snapshot taken when the subscription
                //  opened would age with the connection instead of with the fleet.
                () => VehicleFrameResponse.Fleet(store.GetLatestSnapshot(), timeProvider),
                FleetTickPeriod,
                timeProvider,
                cancellationToken));
    }
}
