namespace Mcs.Adapters.Mavlink;

/// <summary>
/// What the parser discarded and why, since it was started.
/// </summary>
/// <remarks>
/// <b>These counters are the reason nothing here logs per frame.</b> A ground station sees dozens
/// of message types it has no decoder for, continuously, from every component on the vehicle. A log
/// line each would push the interesting output off the screen within seconds and train whoever is
/// watching to ignore the stream -- so the parser says nothing per frame and keeps a count instead,
/// and something above it decides when a number has become worth reporting.
/// <para>
/// A rising count is also the only local evidence of several faults that are otherwise invisible.
/// A link degrading into noise shows up here as <see cref="BytesResynced"/> climbing while frames
/// keep arriving; a dialect mismatch shows up as <see cref="ChecksumFailures"/> against exactly one
/// message type. Neither is visible from the frames that do arrive, which is why these are recorded
/// now rather than added when something needs them.
/// </para>
/// <para>
/// Mutable and not thread-safe, matching the parser: one parser per link, driven by the one loop
/// reading that socket. Read them from that loop, or copy them out with
/// <see cref="Snapshot"/> if they are going somewhere else.
/// </para>
/// </remarks>
public sealed class MavlinkParserStatistics
{
    /// <summary>Gets the number of frames that passed their checksum and were handed to the caller.</summary>
    public long FramesParsed { get; internal set; }

    /// <summary>
    /// Gets the number of frames whose checksum did not match. Corruption, or a dialect
    /// disagreement about a message this station believes it knows -- and the second is the one
    /// worth suspecting when the count rises for one message id and no other.
    /// </summary>
    public long ChecksumFailures { get; internal set; }

    /// <summary>
    /// Gets the number of bytes thrown away hunting for a frame start: leading noise, and the
    /// single byte dropped after each failed frame.
    /// </summary>
    /// <remarks>
    /// The useful ratio is this against <see cref="FramesParsed"/>. A handful at startup is a
    /// parser that attached mid-stream and is expected; a number that keeps pace with the frame
    /// count is a link that is half noise, which no individual frame reports.
    /// </remarks>
    public long BytesResynced { get; internal set; }

    /// <summary>
    /// Gets the number of frames carrying a message id the station has no decoder for, whose length
    /// claim was corroborated by the frame that followed. Ordinary traffic, not a fault -- expect
    /// this to be the largest number here.
    /// </summary>
    /// <remarks>
    /// Reading this as "nothing is wrong" is only safe because of that corroboration. An unknown
    /// message cannot be checksum-verified, so before the parser required the byte past the claimed
    /// frame to be a start byte, a corrupt length could consume several good frames and land here as
    /// a single increment -- a real loss reported by the one counter that means everything is fine.
    /// Uncorroborated claims now go to <see cref="BytesResynced"/> instead, which is where a reader
    /// looks for trouble.
    /// </remarks>
    public long UnknownMessagesSkipped { get; internal set; }

    /// <summary>
    /// Gets the number of MAVLink v1 frames recognised and stepped over. A non-zero count means
    /// something on the link is speaking v1, which this station does not support and does not
    /// intend to.
    /// </summary>
    public long V1FramesSkipped { get; internal set; }

    /// <summary>
    /// Gets the number of frames rejected for carrying the signing flag.
    /// </summary>
    /// <remarks>
    /// Signing is not implemented and is not planned. It is a substantial sub-feature -- key
    /// management, timestamp windows, replay rejection -- that nothing in this station needs, and a
    /// half-implementation of an authentication mechanism is worse than none, because it invites
    /// the assumption that frames were authenticated. Counted rather than ignored so that a link
    /// configured to require signing presents as a number climbing here rather than as a station
    /// that mysteriously sees no traffic.
    /// <para>
    /// Reading it as that configuration mismatch -- rather than as lost frames -- is only safe because
    /// a signed frame is verified before it is stepped over: by its checksum where the message id is
    /// known, and otherwise by the same length corroboration
    /// <see cref="UnknownMessagesSkipped"/> describes. Without it a corrupt length byte with the
    /// signing bit set consumed up to 280 bytes of good frames and landed here as one increment, and
    /// on a signed link every frame takes that path.
    /// </para>
    /// </remarks>
    public long SignedFramesRejected { get; internal set; }

    /// <summary>
    /// Gets the number of frame candidates discarded for declaring an incompatibility flag this
    /// parser does not implement.
    /// </summary>
    /// <remarks>
    /// The specification's rule for an unimplemented incompatibility flag is to discard the frame --
    /// that is what makes the flags <i>incompatible</i> rather than merely unknown -- so the discard
    /// is correct and this counter is the only way to tell it happened. Its absence was the same
    /// reporting gap <see cref="SignedFramesRejected"/> exists to close: firmware that starts setting
    /// a flag defined after this parser was written would present as a station seeing no frames on a
    /// link whose only rising number is <see cref="BytesResynced"/> -- indistinguishable from a bad
    /// radio, and the wrong thing to go and check.
    /// <para>
    /// Frame <i>candidates</i>, not frames: an undefined flag is exactly the mechanism a future
    /// revision would use to append data, so the length field cannot be trusted to say where such a
    /// frame ends and the discard is one byte with the remainder rescanned. That rescan can find a
    /// second candidate inside the same frame and land here again. Read it as evidence of a feature
    /// mismatch, not as a count of frames lost.
    /// </para>
    /// </remarks>
    public long IncompatibleFlagsRejected { get; internal set; }

    /// <summary>Returns an independent copy, safe to hand to code running on another thread.</summary>
    public MavlinkParserStatistics Snapshot() => new()
    {
        FramesParsed = FramesParsed,
        ChecksumFailures = ChecksumFailures,
        BytesResynced = BytesResynced,
        UnknownMessagesSkipped = UnknownMessagesSkipped,
        V1FramesSkipped = V1FramesSkipped,
        SignedFramesRejected = SignedFramesRejected,
        IncompatibleFlagsRejected = IncompatibleFlagsRejected,
    };

    public override string ToString() =>
        $"parsed={FramesParsed}, crcFailures={ChecksumFailures}, resyncedBytes={BytesResynced}, "
        + $"unknown={UnknownMessagesSkipped}, v1={V1FramesSkipped}, signed={SignedFramesRejected}, "
        + $"incompatFlags={IncompatibleFlagsRejected}";
}
