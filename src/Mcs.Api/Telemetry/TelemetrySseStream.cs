using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

using Mcs.Core;

namespace Mcs.Api.Telemetry;

/// <summary>
/// Turns the store's frame subscription into the event sequence
/// <see cref="TelemetryEndpoints.StreamPath"/> writes, inserting a heartbeat whenever the fleet goes
/// quiet.
/// </summary>
/// <remarks>
/// Heartbeats exist for the proxies between here and the browser, which drop an idle connection
/// without telling either end -- the client then reconnects in a loop and the operator watches a map
/// that keeps blinking. A dev machine at 1 Hz never sees it.
/// <para>
/// A named event rather than the SSE comment line the protocol offers for exactly this, because
/// <see cref="SseItem{T}"/> models only <c>event</c>, <c>data</c>, <c>id</c> and <c>retry</c>; a
/// comment means hand-writing every frame's bytes and owning the framing. <c>EventSource</c> does not
/// deliver an event nobody registered a listener for, so the cost is a few bytes on the wire.
/// </para>
/// </remarks>
internal static class TelemetrySseStream
{
    private static readonly SseItem<VehicleFrameResponse?> Heartbeat =
        new(null, TelemetryEndpoints.HeartbeatEventType);

    /// <summary>
    /// Yields each frame as a <c>telemetry</c> event, or a <c>heartbeat</c> when
    /// <paramref name="heartbeatPeriod"/> passes without one.
    /// </summary>
    /// <param name="frames">A live subscription from <see cref="ITelemetryStore.Subscribe"/>.</param>
    /// <param name="heartbeatPeriod">How long the stream may be silent before it says something.</param>
    /// <param name="timeProvider">The station clock, so the period is drivable by a test.</param>
    /// <param name="cancellationToken">The request's, so a closed tab ends the subscription.</param>
    public static async IAsyncEnumerable<SseItem<VehicleFrameResponse?>> WithHeartbeat(
        IAsyncEnumerable<TelemetryFrame> frames,
        TimeSpan heartbeatPeriod,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
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

        try
        {
            while (true)
            {
                pendingRead ??= enumerator.MoveNextAsync().AsTask();

                //  Cancelled at the end of each round rather than left to expire: at 1 Hz an
                //  abandoned 15-second delay leaves fifteen timers queued at all times for the one
                //  that fires.
                using CancellationTokenSource idle =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                Task idleElapsed = Task.Delay(heartbeatPeriod, timeProvider, idle.Token);

                bool wentQuiet = await Task.WhenAny(pendingRead, idleElapsed).ConfigureAwait(false)
                    == idleElapsed;

                //  The second half of the condition is not redundant. Cancelling the request
                //  completes idleElapsed as well, and without this the loop spins on an
                //  already-cancelled delay, writing heartbeats as fast as the socket accepts them.
                if (wentQuiet && !cancellationToken.IsCancellationRequested)
                {
                    yield return Heartbeat;
                    continue;
                }

                await idle.CancelAsync().ConfigureAwait(false);

                //  Awaited even once the token has fired, because this is the call the store throws
                //  OperationCanceledException from, and that throw is what unregisters the
                //  subscriber.
                if (!await pendingRead.ConfigureAwait(false))
                {
                    yield break;
                }

                yield return new SseItem<VehicleFrameResponse?>(
                    VehicleFrameResponse.From(enumerator.Current),
                    TelemetryEndpoints.TelemetryEventType);

                pendingRead = null;
            }
        }
        finally
        {
            //  A consumer can stop enumerating between two events -- a closed tab, an aborted
            //  response -- and it does so while a read is still outstanding, because the heartbeat
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
