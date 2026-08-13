using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// The link out: a UDP socket pointed at the station, and the counters for what it managed to send.
/// </summary>
/// <remarks>
/// <b>A resolve failure is fatal and names the setting; a send failure is not fatal at all.</b>
/// The asymmetry is the whole of this type's policy and it is the mirror of the station adapter's.
/// A target that cannot be resolved is a configuration fault, and a simulator transmitting into
/// nowhere is indistinguishable from a healthy one -- it runs, it logs, it reports no error, and
/// the map stays empty. A send that fails is a statement about the far end, which a vehicle has no
/// business acting on: a real aircraft keeps flying and keeps transmitting when its ground station
/// goes down, and one that gave up would be modelling the wrong failure.
///
/// <para>
/// <b>Re-resolving after a failure is the container case.</b> On a Compose network a restarted API
/// keeps its name and takes a new address, so an address resolved once at startup can be stale
/// while the name is fine. Re-resolving only after a send has already failed keeps the ordinary
/// path free of a name lookup per frame, which at these rates would be thousands of pointless
/// queries an hour -- but a station that is down fails <i>every</i> send, so "only after a failure"
/// is the ordinary path once it is, and the lookup has to be rationed on its own account. Hence
/// <see cref="ReresolveInterval"/>, and hence a configured address literal skipping the lookup
/// entirely: an address cannot move to a different address, so there is nothing there to re-read.
/// </para>
///
/// <para>
/// <b>One frame per datagram.</b> That is what firmware does. Coalescing several frames into one
/// datagram is a router's behaviour, and the station's adapter is already proved against it by a
/// test that sends exactly that -- so a simulator pretending to be a router would be duplicating a
/// covered case while making its own output less like a vehicle's.
/// </para>
///
/// <para><b>Not thread-safe.</b> One transmitter per vehicle, driven by the one loop flying it.</para>
/// </remarks>
internal sealed class MavlinkTransmitter : IDisposable
{
    /// <summary>
    /// The shortest gap between two name lookups on the failure path.
    /// </summary>
    /// <remarks>
    /// Ten seconds, against a link sending roughly eight and a half frames a second: without a
    /// floor, a station that is down turns every one of those into a DNS query, and the recovery
    /// this is here for costs more than the outage it recovers from. It is long enough that the
    /// queries are negligible and short enough that a restarted station is picked up within one
    /// report interval, which is the granularity anyone reading the log has anyway.
    /// </remarks>
    private static readonly TimeSpan ReresolveInterval = TimeSpan.FromSeconds(10);

    private readonly string _host;
    private readonly int _port;
    private readonly bool _hostIsAddressLiteral;
    private readonly SimulatorStatistics _statistics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Socket _socket;

    private IPEndPoint _target;

    //  Null until the first re-resolution, so the first send failure looks the target up straight
    //  away rather than waiting out an interval that has not started.
    private long? _lastReresolveTimestamp;

    /// <summary>
    /// Resolves the target and opens the socket, failing here rather than at the first send.
    /// </summary>
    /// <param name="host">The station's host name or address. Resolved now; see the remarks.</param>
    /// <param name="port">The station's MAVLink port.</param>
    /// <param name="statistics">The counters this transmitter fills in.</param>
    /// <param name="timeProvider">Times the gap between re-resolutions; no wall clock is read here.</param>
    /// <param name="logger">Per-link events only; nothing here logs per frame.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="host"/> could not be resolved. Fatal, and it names the setting.
    /// </exception>
    internal MavlinkTransmitter(
        string host,
        int port,
        SimulatorStatistics statistics,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _port = port;
        _hostIsAddressLiteral = IPAddress.TryParse(host, out _);
        _statistics = statistics;
        _timeProvider = timeProvider;
        _logger = logger;

        _target = Resolve(host, port);

        //  The address family follows the resolved target rather than being fixed, so a station
        //  reachable only over IPv6 gets an IPv6 socket instead of a mismatch nobody chose.
        _socket = new Socket(_target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        SuppressConnectionResetReporting(_socket);

        _logger.LogInformation(
            "Transmitting MAVLink to {Target}, resolved from {Host}:{Port}.", _target, host, port);
    }

    /// <summary>Gets the endpoint frames are currently being sent to.</summary>
    internal IPEndPoint Target => _target;

    /// <summary>
    /// Sends one frame as one datagram, counting a failure rather than propagating it.
    /// </summary>
    /// <param name="frame">A complete MAVLink v2 frame.</param>
    /// <param name="cancellationToken">Cancelled on shutdown; the exception is the caller's to handle.</param>
    internal async ValueTask SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        try
        {
            int sent = await _socket
                .SendToAsync(frame, SocketFlags.None, _target, cancellationToken)
                .ConfigureAwait(false);

            _statistics.DatagramsSent++;
            _statistics.BytesSent += sent;
        }
        catch (SocketException exception)
        {
            _statistics.SendErrors++;

            //  The first one only. A station that is down produces one of these per frame, and a
            //  warning each would bury the periodic summary that carries the rest.
            if (_statistics.SendErrors == 1)
            {
                _logger.LogWarning(
                    exception,
                    "Sending to {Target} failed. The aircraft keeps flying and keeps transmitting; "
                    + "further failures are counted rather than logged.",
                    _target);
            }

            await ReresolveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();

    /// <summary>Resolves a host to an endpoint, or fails naming the settings that produced it.</summary>
    private static IPEndPoint Resolve(string host, int port)
    {
        //  Parse before resolve: an address literal is not a name, and handing one to the resolver
        //  works by accident on most platforms rather than by contract.
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return new IPEndPoint(literal, port);
        }

        IPAddress[] addresses;

        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(BuildResolveFailureMessage(host, port), exception);
        }

        if (addresses.Length == 0)
        {
            throw new InvalidOperationException(BuildResolveFailureMessage(host, port));
        }

        //  The first record. A name with several addresses is a load-balanced station, which is not
        //  a thing this deployment has, and picking among them would be a policy invented here.
        return new IPEndPoint(addresses[0], port);
    }

    private static string BuildResolveFailureMessage(string host, int port) =>
        $"The simulator could not resolve '{host}' to send MAVLink to. Check "
        + $"{SimulatorOptions.SectionName}:{nameof(SimulatorOptions.TargetHost)} and "
        + $"{SimulatorOptions.SectionName}:{nameof(SimulatorOptions.TargetPort)} (port {port}); "
        + "under Compose the host is the API service's name on the shared network.";

    /// <summary>
    /// Looks the target up again after a send failure, at most once per
    /// <see cref="ReresolveInterval"/>, keeping the current target if the answer is unusable.
    /// </summary>
    /// <remarks>
    /// Failures here are swallowed, unlike at construction. By this point the process is flying and
    /// has a target that worked at least once, so a name lookup that fails now is far more likely
    /// to be a DNS blip than a configuration fault -- and stopping the aircraft over it would turn a
    /// recoverable outage into an outage plus a dead simulator. The address family must match: the
    /// socket was created for one, and sending to the other throws for every frame thereafter.
    /// <para>
    /// Asynchronous where the constructor's resolve is not, and for a reason that only applies
    /// here: this runs inside the flight loop, so a blocking lookup does not merely cost time, it
    /// costs it out of the aircraft's next step. At startup there is no aircraft yet to delay.
    /// </para>
    /// </remarks>
    private async ValueTask ReresolveAsync(CancellationToken cancellationToken)
    {
        //  A literal cannot have moved, so there is nothing to look up and nothing to count. This
        //  covers the default target of 127.0.0.1, which is also the case that fails every send
        //  most often -- a developer running the simulator with no station up.
        if (_hostIsAddressLiteral)
        {
            return;
        }

        if (_lastReresolveTimestamp is { } previous
            && _timeProvider.GetElapsedTime(previous) < ReresolveInterval)
        {
            return;
        }

        _lastReresolveTimestamp = _timeProvider.GetTimestamp();
        _statistics.TargetReresolutions++;

        IPAddress[] addresses;

        try
        {
            addresses = await Dns
                .GetHostAddressesAsync(_host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            //  Keep transmitting at the last address that worked. See the remarks.
            return;
        }

        if (addresses.Length == 0)
        {
            return;
        }

        //  The first record, on the same reasoning as Resolve's: picking among several would be a
        //  policy invented here, and inventing a different one on the recovery path than on the
        //  startup path would make a restarted station land somewhere the log line cannot explain.
        IPEndPoint resolved = new(addresses[0], _port);

        if (resolved.AddressFamily == _socket.AddressFamily && !resolved.Equals(_target))
        {
            _logger.LogInformation(
                "The target moved from {Previous} to {Current}; transmitting there instead.",
                _target,
                resolved);

            _target = resolved;
        }
    }

    /// <summary>
    /// Stops Windows reporting an ICMP port-unreachable from the station as a failure of
    /// <i>this</i> socket's next send.
    /// </summary>
    /// <remarks>
    /// The same Windows behaviour the station's adapter clears, from the other end of the link:
    /// sending to a port nothing is listening on gets the resulting ICMP rejection surfaced as
    /// <c>WSAECONNRESET</c> on a later call. The loop survives it either way, but it would be
    /// counted as a send failure every time the station restarts, which makes
    /// <see cref="SimulatorStatistics.SendErrors"/> report the platform rather than the link.
    /// </remarks>
    private static void SuppressConnectionResetReporting(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        //  winsock2.h's SIO_UDP_CONNRESET, which .NET's IOControlCode enumeration does not name.
        //  Four zero bytes is FALSE -- stop reporting it.
        const int SioUdpConnectionReset = unchecked((int)0x9800000C);

        socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
    }
}
