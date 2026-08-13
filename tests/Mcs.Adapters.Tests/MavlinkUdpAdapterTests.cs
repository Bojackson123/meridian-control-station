using System.Net;
using System.Net.Sockets;

using Mcs.Adapters.Mavlink;
using Mcs.Core;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The link: what a socket delivers, what survives the trip to the store, and what a bad datagram
/// costs.
/// </summary>
/// <remarks>
/// The codec suites already prove what a frame means. What is left to this one is everything the
/// socket adds -- that datagrams and frames are not the same unit, that the receipt is taken at
/// arrival rather than after the decode, and that nothing a link can do ends the loop. Each test
/// drives a real adapter on a loopback port; see <see cref="MavlinkAdapterHarness"/> for why that
/// is not faked.
/// <para>
/// The bytes are the committed pymavlink vectors, unmodified. Where a test needs two frames it
/// sends the same vector twice rather than building a second one, so no assertion here rests on
/// bytes this codec produced itself.
/// </para>
/// </remarks>
public class MavlinkUdpAdapterTests
{
    /// <summary>The system id the vectors are packed with, rendered the way the decoder renders it.</summary>
    private const string VectorVehicleId = "MAV-255";

    // --- Datagram boundaries are not frame boundaries ---------------------------------------------

    /// <summary>
    /// One datagram carrying two frames produces two writes.
    /// </summary>
    /// <remarks>
    /// The shape a router multiplexing two vehicles onto one port produces, and the one that breaks
    /// the tempting shortcut twice over: an adapter that treated the datagram as a frame would
    /// decode the first and discard the second, and one that took a single ingest receipt per
    /// datagram would throw on the second, losing it and everything behind it in the same buffer.
    /// </remarks>
    [Fact]
    public async Task Receive_DeliversBothFramesInOneDatagram()
    {
        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();

        byte[] position = MavlinkVectors.Named("global_position_int").Bytes;

        harness.Send([.. position, .. position]);

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.TelemetryWritten == 2,
            "the datagram's second frame never reached the store");

        Assert.Equal(1, harness.Adapter.Statistics.DatagramsReceived);

        //  Both frames stamped independently: two arrivals, two receipts, two frames. Same vehicle,
        //  because it is the same sender twice.
        Assert.All(
            harness.Store.Writes,
            write => Assert.Equal(VectorVehicleId, write.Frame.Telemetry.Id.ToString()));
    }

    /// <summary>
    /// A frame split across two datagrams is delivered once the second arrives, not discarded.
    /// </summary>
    /// <remarks>
    /// The fixture's own split case: a complete heartbeat and the start of a position in the first
    /// buffer, the rest of the position in the second. One write, because only a position emits --
    /// so this fails both if the halves are dropped and if the second half is mistaken for a frame
    /// of its own.
    /// </remarks>
    [Fact]
    public async Task Receive_DeliversAFrameSplitAcrossTwoDatagrams()
    {
        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();

        IReadOnlyList<byte[]> chunks = MavlinkVectors.StreamNamed("split_mid_payload").ChunkBytes;

        foreach (byte[] chunk in chunks)
        {
            harness.Send(chunk);
        }

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.TelemetryWritten == 1,
            "the frame split across the two datagrams was never delivered");

        Assert.Equal(chunks.Count, (int)harness.Adapter.Statistics.DatagramsReceived);

        //  Two frames were framed to produce one report: the heartbeat folded into the vehicle's
        //  state and emitted nothing, which is the decode layer's rule and is asserted here only to
        //  show the split frame was not double-counted.
        Assert.Equal(2, harness.Adapter.ParserStatistics.FramesParsed);
    }

    // --- What a bad datagram costs ----------------------------------------------------------------

    /// <summary>
    /// A datagram of noise is counted and the next good one still arrives.
    /// </summary>
    /// <remarks>
    /// The requirement is not that noise is understood -- it is that it costs nothing behind it. An
    /// adapter that threw out of its read loop, or that discarded the parse buffer on a byte it did
    /// not like, would pass a test that sent noise alone; the position sent afterwards is what makes
    /// this an assertion about survival.
    /// </remarks>
    [Fact]
    public async Task Receive_CountsAMalformedDatagramAndKeepsGoing()
    {
        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();

        //  No 0xFD or 0xFE anywhere, so every byte of it is discarded by the resync scan rather
        //  than being taken for the start of something.
        harness.Send([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        harness.Send(MavlinkVectors.Named("global_position_int").Bytes);

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.TelemetryWritten == 1,
            "the good datagram behind the noise was never delivered");

        Assert.Equal(8, harness.Adapter.ParserStatistics.BytesResynced);
        Assert.Equal(0, harness.Adapter.Statistics.ReceiveErrors);

        //  Still up. The counter above is only meaningful if the link survived to keep counting.
        Assert.False(harness.Run.IsCompleted);
    }

    /// <summary>
    /// A store that refuses the vehicle is counted, and the link keeps running.
    /// </summary>
    /// <remarks>
    /// The rejection is per vehicle -- a thirteenth system id on a link carrying twelve, which a
    /// router forwarding a neighbour's traffic produces without anything being wrong locally. Ending
    /// the read loop over it would take the twelve vehicles that did fit off the console in order to
    /// report the one that did not.
    /// </remarks>
    [Fact]
    public async Task Receive_CountsARejectedVehicleAndKeepsGoing()
    {
        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();

        harness.Store.RejectEveryVehicle = true;

        byte[] position = MavlinkVectors.Named("global_position_int").Bytes;

        harness.Send(position);
        harness.Send(position);

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.VehiclesRejected == 2,
            "the store's refusals were not both counted");

        Assert.Equal(0, harness.Adapter.Statistics.TelemetryWritten);
        Assert.False(harness.Run.IsCompleted);
    }

    // --- The ingest boundary ----------------------------------------------------------------------

    /// <summary>
    /// The frame carries the instant the datagram arrived, not the instant the decode finished.
    /// </summary>
    /// <remarks>
    /// MCS-005, and the reason ingest is two-phase at all. The clock moves by a step per
    /// measurement, so a frame stamped before the decode reaches the store carrying a time older
    /// than the clock's -- by however much the decode cost. An adapter that stamped at completion
    /// would hand over a frame whose stamp and the clock agreed exactly: data that looks fresher
    /// than it is, which is the one direction this must never fail in.
    /// <para>
    /// A lower bound rather than an exact figure. The precise gap is a function of how many
    /// timestamps the ingest boundary takes on the way through, which is <c>Mcs.Core</c>'s business
    /// and not something this suite should break over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Receive_StampsTheFrameAtArrivalRatherThanAfterTheDecode()
    {
        SteppingClock clock = new(TimeSpan.FromMilliseconds(60));

        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync(clock);

        harness.Send(MavlinkVectors.Named("global_position_int").Bytes);

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.TelemetryWritten == 1,
            "the position was never written");

        RecordedWrite write = Assert.Single(harness.Store.Writes);
        TimeSpan stampedBeforeTheWrite = write.ObservedUtcNow - write.Frame.ReceivedAtUtc;

        Assert.True(
            stampedBeforeTheWrite >= clock.Step,
            $"The frame reached the store stamped {stampedBeforeTheWrite} before the clock's own "
            + $"reading, which is less than the {clock.Step} the decode took. The receipt was "
            + "taken after the decode rather than at arrival, so the data is recorded younger "
            + "than it is.");
    }

    /// <summary>
    /// A decode past the ingest budget is counted, and the frame is still written.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The count is the only evidence that work has crept in behind the socket,
    /// since the frame itself looks perfect -- it carries the age it really has. And a late frame is
    /// still written, because a station that dropped data for being slow would answer a latency
    /// problem with a gap in the record.
    /// </remarks>
    [Fact]
    public async Task Receive_CountsADecodeThatOverrunsTheIngestBudget()
    {
        //  One step past the budget, so a single decode breaches it.
        SteppingClock clock = new(TelemetryIngest.RecommendedIngestBudget + TimeSpan.FromMilliseconds(10));

        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync(clock);

        harness.Send(MavlinkVectors.Named("global_position_int").Bytes);

        await MavlinkAdapterHarness.WaitUntilAsync(
            () => harness.Adapter.Statistics.TelemetryWritten == 1,
            "the slow decode was dropped rather than written");

        Assert.Equal(1, harness.Adapter.Statistics.IngestBudgetExceeded);
    }

    // --- Lifecycle --------------------------------------------------------------------------------

    /// <summary>
    /// The adapter reports the port it actually bound, which a configured zero does not name.
    /// </summary>
    [Fact]
    public async Task Listening_ReportsTheBoundEndpoint()
    {
        await using MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();

        Assert.NotEqual(0, harness.Listening.Port);
        Assert.Equal(IPAddress.Loopback, harness.Listening.Address);
    }

    /// <summary>
    /// Cancellation ends the run without faulting, and releases the port.
    /// </summary>
    /// <remarks>
    /// Not faulting is the load-bearing half: an <see cref="OperationCanceledException"/> allowed
    /// out of the run reaches the host as a crashed background service on every clean shutdown,
    /// which teaches whoever reads the log to ignore the line that would have mattered. The rebind
    /// afterwards is what proves the socket was closed rather than merely abandoned.
    /// </remarks>
    [Fact]
    public async Task RunAsync_StopsCleanlyAndReleasesThePort()
    {
        MavlinkAdapterHarness harness = await MavlinkAdapterHarness.StartAsync();
        IPEndPoint listening = harness.Listening;

        await harness.DisposeAsync();

        Assert.Equal(TaskStatus.RanToCompletion, harness.Run.Status);

        using Socket rebound = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rebound.Bind(listening);
    }

    /// <summary>
    /// An address that is on no local interface fails the run, naming the setting that chose it.
    /// </summary>
    /// <remarks>
    /// Fatal on purpose, and this is the case that most needs to be. A UDP socket that could not
    /// bind is indistinguishable from a healthy one nothing is sending to -- both report nothing
    /// forever -- so the alternative is a station that starts, passes its health checks and shows an
    /// empty map. The address is from TEST-NET-3, which is reserved for documentation and is
    /// therefore assigned to no interface on any machine this suite runs on.
    /// </remarks>
    [Fact]
    public async Task RunAsync_FailsWithTheSettingNamedWhenTheAddressIsNotLocal()
    {
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MavlinkAdapterHarness.RunWithAsync(
                new MavlinkAdapterOptions { ListenAddress = "203.0.113.1", Port = 0 }));

        Assert.Contains(
            nameof(MavlinkAdapterOptions.ListenAddress), failure.Message, StringComparison.Ordinal);

        Assert.IsType<SocketException>(failure.InnerException);
    }
}
