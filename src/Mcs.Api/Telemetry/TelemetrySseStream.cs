using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

using Mcs.Core;

namespace Mcs.Api.Telemetry;

/// <summary>
/// Turns the store's frame subscription into the event sequence
/// <see cref="TelemetryEndpoints.StreamPath"/> writes, interleaving a periodic statement of the
/// whole fleet.
/// </summary>
/// <remarks>
/// <b>The tick is what makes staleness reach the console at all.</b> A vehicle that has gone quiet
/// produces no frames by definition, so a stream carrying only frames can never say that it went
/// quiet -- the console would hold the last state it was told about, indefinitely, for exactly the
/// vehicle the operator most needs to know about. The tick re-reads the store and re-evaluates every
/// vehicle against the station clock, so an age climbs on screen with nothing arriving from the air.
/// <para>
/// It fires on a schedule rather than after a silence, which is the other half of the same point: a
/// fleet of twelve where one has stopped is never silent, and an idle-triggered event would never
/// fire in the case it was needed for. The old behaviour -- fifteen seconds of nothing, then an
/// empty <c>heartbeat</c> -- kept a proxy from dropping an idle connection and said nothing else;
/// this keeps doing that as a side effect.
/// </para>
/// <para>
/// Both event types carry a list of vehicles: one element for a report, the whole fleet (possibly
/// empty) for a tick. <see cref="SseItem{T}"/> is generic over a single payload type, and the
/// alternative -- <c>object</c>, serialised by runtime type -- would trade a typed contract and an
/// OpenAPI shape for the ability to omit one pair of brackets.
/// </para>
/// </remarks>
internal static class TelemetrySseStream
{
    /// <summary>
    /// Yields each frame as a <c>telemetry</c> event and the whole fleet as a <c>fleet</c> event
    /// every <paramref name="tickPeriod"/>.
    /// </summary>
    /// <param name="frames">A live subscription from <see cref="ITelemetryStore.Subscribe"/>.</param>
    /// <param name="fleet">
    /// Reads the current fleet, already projected and dated. Called on the tick, so it must be cheap
    /// and must not block: it runs on the thread writing the response.
    /// </param>
    /// <param name="tickPeriod">How often the fleet is re-stated regardless of traffic.</param>
    /// <param name="timeProvider">The station clock, so both the period and the ages are drivable by a test.</param>
    /// <param name="cancellationToken">The request's, so a closed tab ends the subscription.</param>
    public static async IAsyncEnumerable<SseItem<IReadOnlyList<VehicleFrameResponse>>> WithFleetTicks(
        IAsyncEnumerable<TelemetryFrame> frames,
        Func<IReadOnlyList<VehicleFrameResponse>> fleet,
        TimeSpan tickPeriod,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(timeProvider);

        //  The reads get their own token rather than the request's, so the teardown below can end
        //  one that is still outstanding. The store links this with the token Subscribe was given,
        //  so cancelling either still ends the subscription.
        using CancellationTokenSource reading =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IAsyncEnumerator<TelemetryFrame> enumerator = frames.GetAsyncEnumerator(reading.Token);

        //  Survives across iterations. A read that loses the race is still running: a second
        //  MoveNextAsync on the same enumerator is undefined behaviour, and letting go of this
        //  reference loses the frame it was about to produce.
        Task<bool>? pendingRead = null;

        //  Rearmed only where it fires, never where a frame arrives. Resetting it on traffic is what
        //  made the old heartbeat idle-triggered, and it would leave a busy fleet's one quiet
        //  vehicle unreported for as long as the other eleven kept talking. One delay outstanding at
        //  a time, so there is also nothing to pile up.
        Task tickElapsed = Task.Delay(tickPeriod, timeProvider, cancellationToken);

        try
        {
            while (true)
            {
                pendingRead ??= enumerator.MoveNextAsync().AsTask();

                await Task.WhenAny(pendingRead, tickElapsed).ConfigureAwait(false);

                //  Which one fired is asked of the tick itself rather than read off WhenAny's
                //  result. WhenAny returns the first *argument* that is already complete, and a
                //  subscriber queue with anything buffered completes a read synchronously -- so
                //  comparing the result against tickElapsed hands every iteration to the frame while
                //  a client is behind, and the fleet is not re-stated until its backlog clears. That
                //  is precisely the connection where a vehicle's age most needs to keep climbing.
                //  Swapping the arguments instead would only move the starvation onto the frames;
                //  this way a fired tick is taken once and rearmed, so the next iteration is the
                //  read's.
                bool ticked = tickElapsed.IsCompleted;

                //  The second half of the condition is not redundant. Cancelling the request
                //  completes tickElapsed as well, and without this the loop spins on an
                //  already-cancelled delay, writing ticks as fast as the socket accepts them.
                if (ticked && !cancellationToken.IsCancellationRequested)
                {
                    tickElapsed = Task.Delay(tickPeriod, timeProvider, cancellationToken);

                    //  Rearmed before the yield, not after: the consumer is a network write and may
                    //  take as long as it takes, and restarting the clock afterwards would stretch
                    //  the period by the write on every tick.
                    yield return new SseItem<IReadOnlyList<VehicleFrameResponse>>(
                        fleet(), TelemetryEndpoints.FleetEventType);

                    continue;
                }

                //  Awaited even once the token has fired, because this is the call the store throws
                //  OperationCanceledException from, and that throw is what unregisters the
                //  subscriber.
                if (!await pendingRead.ConfigureAwait(false))
                {
                    yield break;
                }

                TelemetryFrame frame = enumerator.Current;

                //  Dated here rather than at arrival, so the age carries whatever this frame spent
                //  in the subscriber queue. A frame that waited two seconds behind a slow client is
                //  two seconds old, and saying otherwise would be the console's own latency
                //  reported as freshness.
                yield return new SseItem<IReadOnlyList<VehicleFrameResponse>>(
                    [VehicleFrameResponse.From(frame, TelemetryCurrency.Of(frame, timeProvider))],
                    TelemetryEndpoints.TelemetryEventType);

                pendingRead = null;
            }
        }
        finally
        {
            //  A consumer can stop enumerating between two events -- a closed tab, an aborted
            //  response -- and it does so while a read is still outstanding, because the tick
            //  branch yields without waiting for one. DisposeAsync on a compiler-generated async
            //  iterator whose MoveNextAsync has not returned throws NotSupportedException, out of
            //  the teardown path, where it replaces whatever was actually going on. So: end the
            //  read, wait for it, then dispose.
            await reading.CancelAsync().ConfigureAwait(false);

            if (pendingRead is not null)
            {
                try
                {
                    await pendingRead.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    //  Swallowed only here. Every read the loop itself consumes is awaited above,
                    //  so the only fault this can see is the cancellation just requested -- and
                    //  rethrowing from a finally would replace the reason we are unwinding.
                }
            }

            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
