namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// What the decode layer did with the frames framing handed it, since it was started.
/// </summary>
/// <remarks>
/// The sibling of <see cref="MavlinkParserStatistics"/>, and for the same reason: nothing in this
/// layer logs per message either. A vehicle at 4 Hz across four message types is sixteen lines a
/// second before a second vehicle connects, which is how a log stops being read.
/// <para>
/// <b>The split between the two is the split between loud and quiet failures.</b> The parser's
/// counters describe bytes that were not frames; these describe frames that were not usable
/// telemetry. Keeping them apart is what lets a rise in one be diagnosed without the other: a bad
/// radio moves <see cref="MavlinkParserStatistics.BytesResynced"/> and leaves these alone, and a
/// misconfigured simulator sending positions with no VFR_HUD moves
/// <see cref="PositionsWithoutHud"/> and leaves the parser's numbers perfect.
/// </para>
/// <para>
/// Mutable and not thread-safe, matching the parser and the assemblers: one decoder per link,
/// driven by the one loop reading that socket. Copy them out with <see cref="Snapshot"/> if they
/// are going anywhere else.
/// </para>
/// </remarks>
public sealed class MavlinkDecoderStatistics
{
    /// <summary>
    /// Gets the number of frames whose fields were decoded and accepted. Ordinary traffic -- expect
    /// this to be the largest number here.
    /// </summary>
    /// <remarks>
    /// Accepted, not emitted. Most of what lands here folds into a vehicle's running state and
    /// produces no report at all -- a heartbeat, a battery reading, a HUD -- because only a position
    /// emits. A position that finds no VFR_HUD behind it counts here too, and goes out with speed
    /// and heading null rather than being withheld; <see cref="PositionsWithoutHud"/> is what
    /// records that, and the three counters overlap on it by design. So a wide gap between this and
    /// <see cref="TelemetryEmitted"/> is the ordinary case and not a symptom: reading this one alone
    /// as "the decoder is working" is safe; reading it as "telemetry is reaching the console" is
    /// not, and that is what <see cref="TelemetryEmitted"/> is for.
    /// </remarks>
    public long MessagesDecoded { get; internal set; }

    /// <summary>
    /// Gets the number of messages discarded for carrying a field value the station will not
    /// represent -- a latitude past the pole, a negative ground speed, a battery above 100%.
    /// </summary>
    /// <remarks>
    /// <b>Discarded, never clamped.</b> A clamped 200% battery renders as a believable 100% and the
    /// operator never learns the adapter is broken. The message is dropped, the vehicle keeps the
    /// last values it reported that were representable, and this number is the only evidence the
    /// drop happened -- so a sender with a broken field presents as a count climbing here rather
    /// than as a plausible picture.
    /// <para>
    /// The rejected alternative was to blank the affected field instead. It was worse: a missing
    /// SYS_STATUS already leaves the previous battery in place, so blanking on a <i>bad</i> one
    /// would make a corrupt message erase knowledge that a merely absent message does not.
    /// </para>
    /// </remarks>
    public long MessagesRejected { get; internal set; }

    /// <summary>Gets the number of <see cref="Mcs.Core.VehicleTelemetry"/> reports emitted.</summary>
    /// <remarks>
    /// Against <see cref="MessagesDecoded"/> this is the assembly ratio, and it is expected to be
    /// well below 1: several messages of different types fold into each emitted report, and only a
    /// position emits one. <b>This is the number that means telemetry is reaching the console</b>,
    /// and the only one that does: every other counter here can climb steadily on a link that has
    /// never produced a renderable report.
    /// </remarks>
    public long TelemetryEmitted { get; internal set; }

    /// <summary>
    /// Gets the number of positions emitted for a vehicle that had no VFR_HUD yet, and so carried
    /// no speed and no heading.
    /// </summary>
    /// <remarks>
    /// Emitted, not withheld: the report goes out with those fields null, because a vehicle whose
    /// position is known belongs on the map even when its heading is not. This counts how often
    /// that happened.
    /// <para>
    /// A handful per vehicle at startup is the ordinary case -- the two messages arrive on
    /// independent schedules and one of them is first. A number that keeps climbing is a sender not
    /// emitting VFR_HUD at all, which on the console looks like a fleet of vehicles that
    /// permanently show dashes, and which no other counter here distinguishes from a healthy link.
    /// </para>
    /// </remarks>
    public long PositionsWithoutHud { get; internal set; }

    /// <summary>Returns an independent copy, safe to hand to code running on another thread.</summary>
    public MavlinkDecoderStatistics Snapshot() => new()
    {
        MessagesDecoded = MessagesDecoded,
        MessagesRejected = MessagesRejected,
        TelemetryEmitted = TelemetryEmitted,
        PositionsWithoutHud = PositionsWithoutHud,
    };

    public override string ToString() =>
        $"decoded={MessagesDecoded}, rejected={MessagesRejected}, emitted={TelemetryEmitted}, "
        + $"positionsWithoutHud={PositionsWithoutHud}";
}
