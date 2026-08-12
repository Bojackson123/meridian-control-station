using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Mcs.Core;

namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// Turns the frames a <see cref="MavlinkFrameParser"/> produces into <see cref="VehicleTelemetry"/>,
/// keeping one running state per sender.
/// </summary>
/// <remarks>
/// The layer above framing and below the socket. Framing decides what is a frame; this decides what
/// a frame means and which vehicle it belongs to. Nothing here reads a clock or writes to a store --
/// the adapter that owns the socket does both, so that the receipt timestamp is taken at arrival and
/// the decode that happens in here is measured rather than hidden (MCS-005).
///
/// <para>
/// <b>Senders are keyed by system <i>and</i> component.</b> One system emits from several
/// components -- autopilot, gimbal, companion computer -- and every one of them heartbeats, so
/// keying on the system alone would let a gimbal's messages fold into the aircraft's state and a
/// companion computer's SYS_STATUS overwrite the autopilot's battery. The obvious alternative,
/// accepting only component 1 (<c>MAV_COMP_ID_AUTOPILOT1</c>), is wrong twice: that number is a
/// convention rather than a rule, so hard-coding it makes the station silently blind to a legitimate
/// sender, and the reference vectors this codec is proved against are themselves packed as component
/// 190. Keying on the pair needs no such rule: a component that does not send positions gets its own
/// assembler and never emits from it, because only a position emits.
/// </para>
///
/// <para>
/// <b>What that does not cover, stated rather than implied.</b> A component that <i>does</i> send
/// positions -- a companion computer running mavros, or a router forwarding without rewriting the
/// component id -- gets an assembler that emits, under the same vehicle id as the autopilot's, since
/// the id comes from the system alone. The store then receives interleaved reports from two
/// independent states, which can disagree about heading and battery: one marker at double the
/// expected rate, alternating between two answers. Nothing here detects it, and
/// <see cref="SenderCount"/> exceeding the fleet size is the only visible sign.
/// </para>
///
/// <para>
/// Left open on purpose. Closing it means a policy about which component owns a vehicle -- first
/// emitter wins, or an id the operator configures -- and choosing one blind risks discarding the
/// autopilot's own reports in favour of whatever spoke first. That policy belongs with the adapter
/// that owns the link and knows what is on it, not with the layer that decodes bytes.
/// </para>
///
/// <para>
/// <b>The vehicle id comes from the system id alone</b>, because that is what identifies the
/// airframe; two components of one system are two views of one vehicle, not two vehicles. Nothing
/// here bounds how many of those there may be -- an id is minted for every distinct system id seen
/// -- because the store already caps the fleet and rejects the thirteenth, so a link carrying more
/// than that presents as a rejection there rather than as a quietly growing map.
/// </para>
///
/// <para>
/// <b>Which the caller has to catch</b>, and is why the sample below does.
/// <see cref="TelemetryStoreCapacityExceededException"/> is raised per write, on a thirteenth system
/// a link may be carrying by accident -- a router forwarding a neighbour's traffic, a bench rig left
/// powered -- and letting it out of the read loop ends the loop, taking the twelve vehicles that
/// <i>did</i> fit off the console in order to report the one that did not. Twelve tracks lost to the
/// arrival of a thirteenth is HAZ-01 with the fleet cap as its cause; the rejection is meant to be
/// counted, not fatal.
/// </para>
///
/// <para>
/// <b>The assembler table needs no eviction policy</b>, which is worth stating rather than leaving
/// to be noticed: the key is two bytes, so it is bounded at 65,536 entries by the type of the field
/// and not by anything this class does. A link spraying random headers would cost a few megabytes
/// and stop, where an unbounded key would not stop.
/// </para>
///
/// <para>
/// <b>Not thread-safe.</b> One decoder per link, driven by the single loop reading that socket, like
/// the parser it is fed from.
/// </para>
///
/// <b>Usage:</b>
/// <code>
/// parser.Append(datagram);
/// while (parser.TryReadFrame(out MavlinkFrame? frame))
/// {
///     TelemetryReceipt receipt = ingest.BeginReceive();
///     if (decoder.TryDecode(frame, out VehicleTelemetry? telemetry))
///     {
///         try
///         {
///             store.Write(receipt.Complete(telemetry));
///         }
///         catch (TelemetryStoreCapacityExceededException)
///         {
///             //  The thirteenth vehicle is counted and the link keeps running. See above.
///         }
///     }
/// }
/// </code>
/// <para>
/// <b>One receipt per frame, inside the loop, and not one per read.</b> A receipt is exchangeable
/// exactly once -- that is what stops one arrival minting two frames with the same stamp -- so
/// hoisting <c>BeginReceive</c> above the loop throws on the second frame in a datagram that
/// carries two, taking the rest of the buffer with it. A router multiplexing two vehicles onto one
/// port produces exactly that datagram, and a serial link produces it whenever a read spans a frame
/// boundary, so it is not a rare shape.
/// </para>
/// <para>
/// The cost of the correct form is that the clock is read after <c>TryReadFrame</c> rather than the
/// instant the bytes landed, so a frame's stamp includes the framing of the frames ahead of it in
/// the same buffer. That is microseconds against a one-second budget, and it errs in the safe
/// direction -- data recorded very slightly older than it is, never younger. An adapter that wants
/// the tighter answer takes the reading once at the socket and mints a receipt per frame from it,
/// which is a change to <c>Mcs.Core</c>'s ingest boundary and is not made on speculation.
/// </para>
/// </remarks>
public sealed class MavlinkTelemetryDecoder
{
    /// <summary>
    /// How a MAVLink system id becomes a <see cref="VehicleId"/>: "MAV-007" for system 7.
    /// </summary>
    /// <remarks>
    /// Prefixed and zero-padded rather than the bare number. The prefix says where the id came from,
    /// which matters as soon as a second adapter with its own numbering shares a fleet with this
    /// one; the padding keeps ids sorting lexicographically, so "MAV-009" does not follow
    /// "MAV-010" in a vehicle list.
    /// </remarks>
    private const string VehicleIdFormat = "MAV-{0:000}";

    //  Keyed by system and component packed into one value rather than by a tuple: the key is two
    //  bytes, and a ushort keys a dictionary without the equality comparer a ValueTuple brings.
    private readonly Dictionary<ushort, MavlinkTelemetryAssembler> _assemblers = [];

    /// <summary>Gets what this decoder did with the frames it was given. See <see cref="MavlinkDecoderStatistics"/>.</summary>
    public MavlinkDecoderStatistics Statistics { get; } = new();

    /// <summary>Gets the number of distinct senders seen, which is at least the number of vehicles.</summary>
    /// <remarks>
    /// At least, not equal: a vehicle with a gimbal is two senders and one vehicle. Exposed for
    /// diagnostics, where the gap between this and the fleet size on screen is the first sign that
    /// traffic is arriving from something other than an autopilot.
    /// </remarks>
    public int SenderCount => _assemblers.Count;

    /// <summary>
    /// Folds one frame into its sender's state, and emits telemetry if the frame completed a
    /// renderable picture.
    /// </summary>
    /// <param name="frame">A checksum-verified frame from <see cref="MavlinkFrameParser"/>.</param>
    /// <param name="telemetry">The report, when this returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> on any GLOBAL_POSITION_INT the station will represent, with or without
    /// a VFR_HUD behind it -- see <see cref="MavlinkTelemetryAssembler"/> for why position alone is
    /// the emit rule. Everything else is folded, or counted and dropped, and returns
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public bool TryDecode(MavlinkFrame frame, [NotNullWhen(true)] out VehicleTelemetry? telemetry)
    {
        ArgumentNullException.ThrowIfNull(frame);

        ushort sender = (ushort)((frame.SystemId << 8) | frame.ComponentId);

        if (!_assemblers.TryGetValue(sender, out MavlinkTelemetryAssembler? assembler))
        {
            assembler = new MavlinkTelemetryAssembler(
                VehicleId.From(
                    string.Format(CultureInfo.InvariantCulture, VehicleIdFormat, frame.SystemId)),
                Statistics);

            _assemblers[sender] = assembler;
        }

        return assembler.TryAdd(frame, out telemetry);
    }
}
