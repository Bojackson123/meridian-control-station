using System.Net;
using System.Net.Sockets;

using Mcs.Adapters.Mavlink;
using Mcs.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mcs.Adapters.Tests;

/// <summary>
/// A running <see cref="MavlinkUdpAdapter"/> on a loopback port, with a socket pointed at it.
/// </summary>
/// <remarks>
/// <b>A real socket, deliberately.</b> The alternative is a seam that hands the adapter byte arrays
/// directly, which would test everything except the two things this class is: that a datagram
/// boundary is not a frame boundary, and that the receive loop survives what a link does to it.
/// Both are properties of the socket path, and a fake datagram source is a fake of exactly the part
/// that has to be right. The cost is one loopback port per test, which needs no Docker, no network
/// and no fixed port number.
/// <para>
/// Bound to 127.0.0.1 with a port of 0: loopback so a test run never accepts traffic from the
/// network, and an ephemeral port so tests run in parallel -- and beside a developer's own station
/// on 14550 -- without arranging port numbers between them.
/// </para>
/// </remarks>
internal sealed class MavlinkAdapterHarness : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    private readonly Socket _sender =
        new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private readonly CancellationTokenSource _stopping;

    private MavlinkAdapterHarness(
        MavlinkUdpAdapter adapter,
        RecordingTelemetryStore store,
        TimeProvider clock,
        Task run,
        IPEndPoint listening,
        CancellationTokenSource stopping)
    {
        Adapter = adapter;
        Store = store;
        Clock = clock;
        Run = run;
        Listening = listening;
        _stopping = stopping;
    }

    /// <summary>Gets the adapter under test.</summary>
    internal MavlinkUdpAdapter Adapter { get; }

    /// <summary>Gets the store it writes to.</summary>
    internal RecordingTelemetryStore Store { get; }

    /// <summary>Gets the clock the adapter and store share.</summary>
    internal TimeProvider Clock { get; }

    /// <summary>
    /// Gets the adapter's run task, so a test can assert the link is still up -- which is most of
    /// what "counted, not fatal" means.
    /// </summary>
    internal Task Run { get; }

    /// <summary>Gets the endpoint the adapter bound.</summary>
    internal IPEndPoint Listening { get; }

    /// <summary>Starts an adapter and waits until it is listening.</summary>
    /// <param name="clock">
    /// The station clock. Defaults to a frozen <see cref="FakeClock"/>; pass a
    /// <see cref="SteppingClock"/> where the test is about how long something took.
    /// </param>
    internal static async Task<MavlinkAdapterHarness> StartAsync(TimeProvider? clock = null)
    {
        TimeProvider timeProvider = clock ?? new FakeClock();
        RecordingTelemetryStore store = new(timeProvider);

        MavlinkUdpAdapter adapter = Create(
            new MavlinkAdapterOptions { ListenAddress = "127.0.0.1", Port = 0 },
            timeProvider,
            store);

        CancellationTokenSource stopping = new();

        Task run = adapter.RunAsync(stopping.Token);

        try
        {
            //  Not a poll: the adapter publishes the endpoint it actually bound, and a datagram
            //  sent before the bind would be dropped by the operating system with nothing to retry
            //  against.
            IPEndPoint listening = await adapter.Listening.WaitAsync(StopTimeout);

            return new MavlinkAdapterHarness(adapter, store, timeProvider, run, listening, stopping);
        }
        catch
        {
            //  A start that fails half way returns no harness, so nothing else will ever dispose
            //  what it opened -- and a socket left bound with a loop still on the thread pool turns
            //  one failing test into a run of unrelated failures after it. The run task is observed
            //  as well, or a bind failure surfaces later as an unobserved exception attributed to
            //  whichever test happened to be running when the finalizer got to it.
            await stopping.CancelAsync();

            try
            {
                await run.WaitAsync(StopTimeout);
            }
            catch
            {
                //  Whatever ended the run is not the failure being reported; the one below is.
            }

            stopping.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Runs an adapter against settings expected to fail, and returns the task that carries the
    /// failure. Nothing is started in the background, so there is no harness to dispose.
    /// </summary>
    internal static Task RunWithAsync(MavlinkAdapterOptions settings)
    {
        TimeProvider clock = new FakeClock();

        return Create(settings, clock, new RecordingTelemetryStore(clock))
            .RunAsync(CancellationToken.None);
    }

    private static MavlinkUdpAdapter Create(
        MavlinkAdapterOptions settings, TimeProvider clock, RecordingTelemetryStore store) =>
        new(
            Options.Create(settings),
            store,
            new TelemetryIngest(clock),
            clock,

            //  Null rather than a capturing logger: nothing asserted here is a log line, and the
            //  counters are what the adapter is documented to report through.
            NullLogger<MavlinkUdpAdapter>.Instance);

    /// <summary>Sends one datagram to the adapter, exactly as given.</summary>
    internal void Send(ReadOnlySpan<byte> datagram) => _sender.SendTo(datagram, Listening);

    /// <summary>
    /// Waits until the adapter's counters say what the test is waiting for, failing with
    /// <paramref name="because"/> if they never do.
    /// </summary>
    /// <remarks>
    /// <b>A poll, and it has to be.</b> The obvious alternative -- have the fake store signal the
    /// test as it records a write -- wakes the test one statement too early: the adapter increments
    /// its own counters <i>after</i> <c>Write</c> returns, and after it catches a refusal, so an
    /// assertion made on that signal races the increment it is about. That race is nanoseconds wide
    /// and would fail somewhere else, on a loaded machine, months from now.
    /// <para>
    /// This is also the one place in the suite that reads the statistics from a thread other than
    /// the receive loop they are documented to belong to. Safe here because each counter is an
    /// aligned <c>long</c> that only ever increases, and because the awaits between reads are what
    /// make a newer value observable at all -- a single read of a stale value is exactly what the
    /// loop retries past.
    /// </para>
    /// </remarks>
    internal static async Task WaitUntilAsync(Func<bool> satisfied, string because)
    {
        using CancellationTokenSource deadline = new(StopTimeout);

        while (!satisfied())
        {
            if (deadline.IsCancellationRequested)
            {
                Assert.Fail($"Waited {StopTimeout.TotalSeconds:0} s and {because}.");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Stops the adapter and asserts it stopped cleanly.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        //  Awaited rather than abandoned, and with a timeout: a receive loop that ignores its
        //  cancellation token would otherwise leave a socket bound and a thread-pool task running
        //  for the rest of the test run, failing something unrelated later.
        await Run.WaitAsync(StopTimeout);

        _sender.Dispose();
        _stopping.Dispose();
    }
}
