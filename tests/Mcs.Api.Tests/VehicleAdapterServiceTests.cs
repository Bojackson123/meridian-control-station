using Mcs.Api.Adapters;
using Mcs.Core;

using Microsoft.Extensions.Logging.Abstractions;

namespace Mcs.Api.Tests;

/// <summary>
/// The host's end of the adapter contract: that anything shaped like an
/// <see cref="IVehicleAdapter"/> gets run, and stopped.
/// </summary>
/// <remarks>
/// <b>This suite is the check that the interface is an abstraction.</b> An interface written while
/// looking at one implementation describes that implementation under a general name, and the
/// symptom does not appear until a second one is written -- in M3, against a different transport,
/// where the requirement is that it lands without touching <c>Mcs.Core</c>. The adapters here are
/// deliberately nothing like the MAVLink one: no socket, no bytes, no clock. If the interface has
/// quietly become a description of a UDP link, these will not compile.
/// </remarks>
public class VehicleAdapterServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every registered adapter is started, and every one is stopped by the host's token.
    /// </summary>
    /// <remarks>
    /// Two of them, because one would not distinguish "runs the adapters" from "runs an adapter" --
    /// and two is the configuration the station is actually in while a feed is being replaced.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_RunsEveryAdapterUntilTheHostStops()
    {
        StubVehicleAdapter first = new("first");
        StubVehicleAdapter second = new("second");

        using VehicleAdapterService service = new(
            [first, second], NullLogger<VehicleAdapterService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await first.Running.WaitAsync(Timeout);
        await second.Running.WaitAsync(Timeout);

        await service.StopAsync(CancellationToken.None);

        Assert.True(first.Stopped);
        Assert.True(second.Stopped);
    }

    /// <summary>
    /// One adapter failing surfaces the failure and stops the others, rather than leaving the
    /// station running on whatever sources are left.
    /// </summary>
    /// <remarks>
    /// The test the first version of this class did not have, and would have failed. Awaiting the
    /// adapters under a plain <c>WhenAll</c> is correct-looking and wrong: a task that never
    /// completes -- which is every healthy adapter, by contract -- means the group never completes
    /// either, so the fault is held inside it forever. The host stays up, the health checks stay
    /// green, and the dead link is invisible. Both assertions matter: the exception has to arrive,
    /// and the survivor has to have been told to stop.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_SurfacesAFaultAndStopsTheOtherAdapters()
    {
        FailingVehicleAdapter failing = new();
        StubVehicleAdapter survivor = new("survivor");

        using VehicleAdapterService service = new(
            [failing, survivor], NullLogger<VehicleAdapterService>.Instance);

        await service.StartAsync(CancellationToken.None);

        //  Bounded, because the bug this pins does not produce a wrong answer -- it produces no
        //  answer at all. Awaited bare, a regression here hangs the suite until the runner gives
        //  up; with the timeout it fails in five seconds saying it got a TimeoutException where an
        //  InvalidOperationException was due, which is the symptom described exactly.
        Task execute = service.ExecuteTask!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => execute.WaitAsync(Timeout));

        Assert.True(
            survivor.Stopped,
            "the healthy adapter was never cancelled, so the faulted one could not be reported.");

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// No adapters at all starts and stops without complaint.
    /// </summary>
    /// <remarks>
    /// The state the station passes through while one feed is being swapped for another, and there
    /// is a log line saying the console will show nothing. Throwing instead would make the swap a
    /// change that cannot be made in two steps, which is the opposite of how it is meant to be done.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_ToleratesNoAdaptersAtAll()
    {
        using VehicleAdapterService service = new([], NullLogger<VehicleAdapterService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// An adapter that fails the way a link that is not coming back fails -- a socket that could
    /// not bind, not a datagram it did not like.
    /// </summary>
    private sealed class FailingVehicleAdapter : IVehicleAdapter
    {
        public string Name => "failing";

        public Task RunAsync(CancellationToken stoppingToken) =>
            //  Faulted rather than thrown synchronously, which is what an async adapter does and is
            //  the harder case: a synchronous throw would surface from the very first await.
            Task.FromException(new InvalidOperationException("This link is not coming back."));
    }

    /// <summary>An adapter that does nothing but record that it was run and then stopped.</summary>
    private sealed class StubVehicleAdapter(string name) : IVehicleAdapter
    {
        private readonly TaskCompletionSource _running =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        /// <summary>Gets a task that completes once <see cref="RunAsync"/> has been entered.</summary>
        public Task Running => _running.Task;

        /// <summary>Gets whether the run ended through cancellation rather than any other way.</summary>
        public bool Stopped { get; private set; }

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            _running.TrySetResult();

            //  Swallowed, as the interface requires: a cancellation allowed out of here reaches the
            //  host as a crashed background service on every clean shutdown.
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Stopped = true;
            }
        }
    }
}
