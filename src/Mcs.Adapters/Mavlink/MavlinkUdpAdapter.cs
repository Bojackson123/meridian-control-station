using System.Net;
using System.Net.Sockets;

using Mcs.Adapters.Mavlink.Messages;
using Mcs.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mcs.Adapters.Mavlink;

/// <summary>
/// A MAVLink link over UDP: a bound socket, the streaming parser, the decoder, and the station's
/// ingest boundary, arranged so that bytes arriving on a port become frames in the store.
/// </summary>
/// <remarks>
/// <b>Datagram boundaries are not frame boundaries, and this is the class that must not assume they
/// are.</b> In practice a sender emits one frame per datagram almost every time, which is exactly
/// what makes the assumption attractive and what makes it dangerous: the exceptions -- a sender that
/// batches, a router that coalesces, a frame split across two datagrams -- are rare enough to survive
/// testing and common enough to happen in flight. Every datagram is appended to the parser and the
/// parser is drained; the state that spans reads lives in it, where a serial link will want it too.
///
/// <para>
/// <b>The ingest boundary is used per frame, not per datagram.</b> A receipt is exchangeable exactly
/// once, so reading the clock once per datagram would throw on the second frame of a datagram
/// carrying two -- taking the rest of that buffer with it -- and that is precisely the datagram this
/// class exists to handle correctly. The cost, spelled out on <see cref="MavlinkTelemetryDecoder"/>,
/// is that a frame's stamp includes the framing of the frames ahead of it in the same buffer:
/// microseconds, and in the safe direction, because data recorded slightly older than it is can only
/// make the console more cautious.
/// </para>
///
/// <para>
/// <b>Nothing here decides a vehicle is gone.</b> UDP will not report a simulator that stopped, a
/// radio that failed, or an aircraft that landed -- and it is not this class's job to infer it.
/// Silence is staleness, staleness is measured against the station clock, and two mechanisms
/// deciding a vehicle has been lost will eventually disagree in front of an operator. What the link
/// owes instead is that it does not die when nothing arrives, does not die on a malformed datagram,
/// and counts what it dropped.
/// </para>
///
/// <para>
/// <b>This layer logs where the ones below it do not.</b> The parser and decoder say nothing per
/// message, because a ground station sees dozens a second and a line each is how a log stops being
/// read. The events here are per <i>link</i> -- bound, first traffic, stopped, and a periodic
/// summary -- and there are a handful of them for the life of the process. The one thing worse than
/// a noisy log is a socket that binds to the wrong interface and reports nothing at all.
/// </para>
///
/// <para>
/// <b>Not thread-safe, and it never needs to be.</b> One adapter owns one socket, one parser and one
/// decoder, all driven by the single loop in <see cref="RunAsync"/> -- which is the contract those
/// two types are documented to require.
/// </para>
/// </remarks>
public sealed class MavlinkUdpAdapter : IVehicleAdapter
{
    /// <summary>
    /// The largest payload a UDP datagram can carry: 65,535 less the IP and UDP headers.
    /// </summary>
    /// <remarks>
    /// Sized to the maximum rather than to the ~280 bytes a MAVLink frame needs, because a datagram
    /// larger than the buffer is not an error the socket reports -- it is silently truncated, and a
    /// truncated datagram is a corrupted byte stream handed to a parser that would then resync
    /// through the wreckage, losing the frames behind it and booking the loss as noise. One 64 KB
    /// array per link, allocated once, is cheaper than that failure by every measure.
    /// </remarks>
    private const int MaxDatagramBytes = 65_507;

    /// <summary>
    /// How many receives may fail in a row before the link is treated as broken rather than as a
    /// peer misbehaving.
    /// </summary>
    /// <remarks>
    /// A single receive failure is ordinary -- see <see cref="MavlinkAdapterStatistics.ReceiveErrors"/>
    /// -- and the loop absorbs it. What the loop must not do is absorb it forever: a socket that has
    /// entered a permanently failing state returns instantly, and "count it and continue" becomes a
    /// spin at full CPU that reports nothing but a rising counter. Consecutive, and reset by any
    /// successful receive, so a peer restarting once an hour never approaches it.
    /// </remarks>
    private const int MaxConsecutiveReceiveErrors = 64;

    /// <summary>How often the link's counters are summarised into the log, while traffic flows.</summary>
    /// <remarks>
    /// Long enough to be background noise over a flight, short enough that a degrading link is
    /// visible in the log before someone thinks to ask. Not configurable: an operator who wants the
    /// numbers sooner is better served by an endpoint than by a knob that has to be set before the
    /// event.
    /// </remarks>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    private readonly MavlinkAdapterOptions _settings;
    private readonly ITelemetryStore _store;
    private readonly TelemetryIngest _ingest;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MavlinkUdpAdapter> _logger;

    private readonly MavlinkFrameParser _parser = new();
    private readonly MavlinkTelemetryDecoder _decoder = new();

    //  Completed when the socket is bound, faulted if binding fails. See the property.
    private readonly TaskCompletionSource<IPEndPoint> _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Builds the adapter. No socket is opened until <see cref="RunAsync"/>.</summary>
    /// <remarks>
    /// Binding in the constructor was the alternative, and it would make
    /// <see cref="Listening"/> unnecessary. It was rejected because it makes the port a resource
    /// held for the lifetime of the object rather than for the lifetime of the run: a stopped
    /// adapter would still own the port, so nothing else in the process could take it, and
    /// constructing one in a test that never intends to run it would bind anyway.
    /// </remarks>
    public MavlinkUdpAdapter(
        IOptions<MavlinkAdapterOptions> options,
        ITelemetryStore store,
        TelemetryIngest ingest,
        TimeProvider timeProvider,
        ILogger<MavlinkUdpAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _settings = options.Value;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "mavlink-udp";

    /// <summary>
    /// Gets a task that completes with the endpoint actually bound, once the socket is listening,
    /// and faults with the bind failure if it never does.
    /// </summary>
    /// <remarks>
    /// "Listening" is otherwise unobservable, and the two things that want to observe it both have a
    /// real question behind them. A configured port of 0 means the bound port is not knowable from
    /// configuration at all, so this is the only way to learn it. And "is the link up?" is a
    /// question a readiness check will eventually ask, which a nullable property read at the wrong
    /// moment answers wrongly -- a task cannot be sampled before it has an answer.
    /// </remarks>
    public Task<IPEndPoint> Listening => _listening.Task;

    /// <summary>Gets what the link did: see <see cref="MavlinkAdapterStatistics"/>.</summary>
    public MavlinkAdapterStatistics Statistics { get; } = new();

    /// <summary>Gets what framing discarded, and why.</summary>
    public MavlinkParserStatistics ParserStatistics => _parser.Statistics;

    /// <summary>Gets what the decode layer made of the frames framing produced.</summary>
    public MavlinkDecoderStatistics DecoderStatistics => _decoder.Statistics;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        IPEndPoint endPoint = _settings.ResolveEndPoint();

        //  The address family follows the configured address rather than being fixed, so an IPv6
        //  literal binds an IPv6 socket instead of failing at a mismatch nobody chose.
        using Socket socket = new(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            SuppressConnectionResetReporting(socket);
            socket.Bind(endPoint);
        }
        catch (SocketException exception)
        {
            //  Fatal, and named. A port already in use or an address on no local interface is a
            //  configuration fault, and the alternative to stopping is a station that starts
            //  cleanly, reports itself healthy and shows an empty map forever.
            _listening.TrySetException(exception);

            throw new InvalidOperationException(
                $"The MAVLink adapter could not bind {endPoint}. Check "
                + $"{MavlinkAdapterOptions.SectionName}:{nameof(MavlinkAdapterOptions.ListenAddress)} "
                + $"and {MavlinkAdapterOptions.SectionName}:{nameof(MavlinkAdapterOptions.Port)}.",
                exception);
        }

        //  What the socket bound, not what was asked for: with a configured port of 0 those differ,
        //  and the one worth logging is the one a sender has to be pointed at.
        IPEndPoint bound = (IPEndPoint)socket.LocalEndPoint!;
        _listening.TrySetResult(bound);

        _logger.LogInformation("MAVLink UDP adapter listening on {EndPoint}.", bound);

        await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);

        _logger.LogInformation(
            "MAVLink UDP adapter stopped. Link: {Link}. Framing: {Framing}. Decode: {Decode}.",
            Statistics,
            ParserStatistics,
            DecoderStatistics);
    }

    /// <summary>
    /// Reads datagrams until cancelled, feeding each one to the parser and draining the frames it
    /// completes.
    /// </summary>
    private async Task ReceiveLoopAsync(Socket socket, CancellationToken stoppingToken)
    {
        byte[] buffer = new byte[MaxDatagramBytes];

        //  Any address: this is an unconnected socket and the sender is whoever is transmitting.
        //  Reassigned by each receive to the actual peer.
        EndPoint sender = new IPEndPoint(
            socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
            0);

        long lastReportTimestamp = _timeProvider.GetTimestamp();
        int consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;

            try
            {
                received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, sender, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                //  Ordinary shutdown, and swallowed here rather than propagated: an adapter that
                //  throws on a clean stop is reported by the host as a crashed background service,
                //  every time, which is how the one line that matters gets ignored.
                break;
            }
            catch (SocketException exception)
            {
                Statistics.ReceiveErrors++;

                if (++consecutiveErrors >= MaxConsecutiveReceiveErrors)
                {
                    throw new InvalidOperationException(
                        $"The MAVLink adapter's socket on {socket.LocalEndPoint} failed "
                        + $"{consecutiveErrors} receives in a row, so the link is not recoverable "
                        + "by retrying. The station is stopping rather than spinning on it.",
                        exception);
                }

                //  Absorbed. The usual cause is an ICMP rejection from a peer that has restarted,
                //  which says nothing about this station's ability to receive the next datagram.
                continue;
            }

            consecutiveErrors = 0;

            Statistics.DatagramsReceived++;
            Statistics.BytesReceived += received.ReceivedBytes;

            if (Statistics.DatagramsReceived == 1)
            {
                //  Once per run, and worth an Information line: it is the difference between "the
                //  vehicle is not talking to us" and "something arrived and was unusable", which no
                //  counter distinguishes at a glance and which are investigated in different places.
                _logger.LogInformation(
                    "MAVLink UDP adapter received its first datagram, from {Sender}.",
                    received.RemoteEndPoint);
            }

            _parser.Append(buffer.AsSpan(0, received.ReceivedBytes));

            DrainFrames();

            if (_timeProvider.GetElapsedTime(lastReportTimestamp) >= ReportInterval)
            {
                _logger.LogInformation(
                    "MAVLink link: {Link}. Framing: {Framing}. Decode: {Decode}.",
                    Statistics,
                    ParserStatistics,
                    DecoderStatistics);

                lastReportTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Takes every complete frame the parser now holds and writes the telemetry they compose.
    /// </summary>
    /// <remarks>
    /// A loop rather than a single read, because one datagram can complete more than one frame --
    /// and because a datagram that completes a frame started by the previous one leaves the parser
    /// with nothing to give, which the loop handles as the same case.
    /// </remarks>
    private void DrainFrames()
    {
        while (_parser.TryReadFrame(out MavlinkFrame? frame))
        {
            //  The station's own reading of when this message arrived, taken before the decode it
            //  pays for. Per frame, never hoisted out of this loop: see the remarks on the type.
            TelemetryReceipt receipt = _ingest.BeginReceive();

            if (!_decoder.TryDecode(frame, out VehicleTelemetry? telemetry))
            {
                //  Folded into a vehicle's state, or rejected and counted by the decoder. Either
                //  way there is nothing to write and the receipt is abandoned unused.
                continue;
            }

            TelemetryFrame stamped = receipt.Complete(telemetry);

            if (receipt.IngestDelay > TelemetryIngest.RecommendedIngestBudget)
            {
                Statistics.IngestBudgetExceeded++;

                //  The first one only. A machine slow enough to blow the budget once will blow it
                //  continuously, and the counter carries the rest into the periodic summary; a
                //  warning per frame would bury the summary it is meant to send you to.
                if (Statistics.IngestBudgetExceeded == 1)
                {
                    _logger.LogWarning(
                        "Decoding a MAVLink frame took {IngestDelay}, past the {Budget} ingest "
                        + "budget. The frame was written with the age it actually has; further "
                        + "occurrences are counted rather than logged.",
                        receipt.IngestDelay,
                        TelemetryIngest.RecommendedIngestBudget);
                }
            }

            try
            {
                _store.Write(stamped);
                Statistics.TelemetryWritten++;
            }
            catch (TelemetryStoreCapacityExceededException exception)
            {
                //  Counted, and the loop continues. Ending the link over a thirteenth vehicle would
                //  take the twelve that fit off the console in order to report the one that did not.
                Statistics.VehiclesRejected++;

                _logger.LogWarning(
                    exception,
                    "The MAVLink adapter could not record a frame for {VehicleId}.",
                    exception.RejectedId);
            }
        }
    }

    /// <summary>
    /// Stops Windows reporting an ICMP port-unreachable from a peer as a failure of <i>this</i>
    /// socket's next receive.
    /// </summary>
    /// <remarks>
    /// Windows-only behaviour, and a genuinely surprising one: on Windows an unconnected UDP socket
    /// that has sent to a port nothing is listening on gets the resulting ICMP rejection surfaced as
    /// <c>WSAECONNRESET</c> from a subsequent <c>ReceiveFrom</c> -- an error about a different peer,
    /// delivered to a call that was doing nothing wrong. The receive loop survives it either way,
    /// but it would be counted as a link fault every time a simulator restarts, which makes
    /// <see cref="MavlinkAdapterStatistics.ReceiveErrors"/> report the platform rather than the link.
    /// Clearing <c>SIO_UDP_CONNRESET</c> is the documented way to switch it off.
    /// </remarks>
    private static void SuppressConnectionResetReporting(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        //  The raw control code: winsock2.h's SIO_UDP_CONNRESET, which .NET's IOControlCode
        //  enumeration does not name. Four zero bytes is FALSE -- stop reporting it.
        const int SioUdpConnectionReset = unchecked((int)0x9800000C);

        socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
    }
}
