using Mcs.Adapters.Mavlink;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The streaming behaviour: buffering across reads, resync, and the traffic that must be stepped
/// over rather than decoded.
/// </summary>
/// <remarks>
/// Every case here drives the same committed stream fixtures, so what is asserted is that the
/// parser recovers the frames pymavlink put into a buffer -- not that it agrees with a buffer this
/// suite built for it.
/// <para>
/// The property under test throughout is that <b>a frame the station received in full is never
/// lost to something that happened around it</b>. Corruption before it, unknown traffic before it,
/// a read boundary through the middle of it: none may cost the frame. That is HAZ-01 at the
/// framing layer -- a console showing an older position than the one the station actually had.
/// </para>
/// </remarks>
public class MavlinkFrameParserTests
{
    // --- Delivery boundaries must not matter ---------------------------------------------------

    [Fact]
    public void BackToBackFrames_InOneBuffer_BothArrive()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("back_to_back");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        AssertFrames(parser, stream.Expect);
        Assert.Equal(0, parser.BufferedByteCount);
    }

    /// <summary>
    /// The same two frames split inside the second frame's payload yield the same result.
    /// </summary>
    /// <remarks>
    /// Split mid-payload on purpose. A parser that buffers whole headers but not partial payloads
    /// passes a split on a frame boundary and fails this one, so the easy case is the one that
    /// proves nothing.
    /// </remarks>
    [Fact]
    public void SplitAcrossTwoReads_YieldsTheSameFramesAsOneBuffer()
    {
        MavlinkStream whole = MavlinkVectors.StreamNamed("back_to_back");
        MavlinkStream split = MavlinkVectors.StreamNamed("split_mid_payload");

        MavlinkFrameParser wholeParser = new();
        wholeParser.Append(whole.Bytes);
        List<MavlinkFrame> fromWhole = Drain(wholeParser);

        MavlinkFrameParser splitParser = new();
        List<MavlinkFrame> fromSplit = [];
        foreach (byte[] chunk in split.ChunkBytes)
        {
            splitParser.Append(chunk);
            fromSplit.AddRange(Drain(splitParser));
        }

        //  Compared as frames rather than counts: equal counts would pass even if the split run had
        //  produced two copies of the first frame and dropped the second.
        Assert.Equal(fromWhole, fromSplit);
        Assert.Equal(split.Expect.Count, fromSplit.Count);
    }

    /// <summary>A partial frame is held, not discarded, and completes when the rest arrives.</summary>
    [Fact]
    public void PartialFrame_IsHeldUntilTheRestArrives()
    {
        MavlinkVector vector = MavlinkVectors.Named("global_position_int");
        byte[] bytes = vector.Bytes;

        MavlinkFrameParser parser = new();
        parser.Append(bytes.AsSpan(0, bytes.Length - 1));

        Assert.False(parser.TryReadFrame(out MavlinkFrame? _));
        Assert.Equal(bytes.Length - 1, parser.BufferedByteCount);

        //  Nothing was counted as resynced: the bytes are not garbage, they are simply not all here
        //  yet, and a parser that charged them to the resync counter would make an ordinary read
        //  boundary look like link noise.
        Assert.Equal(0, parser.Statistics.BytesResynced);

        parser.Append(bytes.AsSpan(bytes.Length - 1));

        Assert.True(parser.TryReadFrame(out MavlinkFrame? frame));
        Assert.Equal(vector.MessageId, frame.MessageId);
    }

    /// <summary>One byte at a time is the same stream as one buffer.</summary>
    /// <remarks>
    /// The degenerate case of the split test, and the one that catches a state machine that only
    /// works because its inputs happened to be large enough.
    /// </remarks>
    [Fact]
    public void ByteAtATime_YieldsTheSameFrames()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("back_to_back");

        MavlinkFrameParser parser = new();
        List<MavlinkFrame> frames = [];

        foreach (byte value in stream.Bytes)
        {
            parser.Append([value]);
            frames.AddRange(Drain(parser));
        }

        Assert.Equal(stream.Expect.Count, frames.Count);
        Assert.Equal(ExpectedMessageIds(stream.Expect), frames.Select(f => f.MessageId));
    }

    // --- Resync -------------------------------------------------------------------------------

    /// <summary>
    /// A corrupt frame is rejected and the good frame behind it still arrives.
    /// </summary>
    /// <remarks>
    /// This is the case that justifies discarding one byte rather than the buffer. Both policies
    /// reject the corrupt frame and both look correct in isolation; only this one keeps the frame
    /// that followed it, which the station had already received in full.
    /// </remarks>
    [Fact]
    public void CorruptChecksum_IsRejectedAndTheNextFrameSurvives()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("corrupt_crc_then_good");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        AssertFrames(parser, stream.Expect);

        Assert.Equal(1, parser.Statistics.ChecksumFailures);
        Assert.Equal(1, parser.Statistics.FramesParsed);

        //  Resync cost: the rejected frame's start byte, then the rest of its bytes scanned through
        //  in the hunt for the next start. Asserted as "more than one" rather than an exact count,
        //  which would pin an implementation detail of the scan without saying anything more.
        Assert.True(parser.Statistics.BytesResynced > 1);
    }

    [Fact]
    public void LeadingGarbage_IsCountedAndTheFrameBehindItArrives()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("leading_garbage");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        AssertFrames(parser, stream.Expect);
        Assert.True(parser.Statistics.BytesResynced >= 16);
    }

    // --- Traffic that is stepped over ----------------------------------------------------------

    /// <summary>
    /// A message with no definition is skipped and counted, and the stream stays in sync.
    /// </summary>
    /// <remarks>
    /// Skipped by its length field, not resynced through: a parser that scanned an unknown payload
    /// byte by byte would spend a resync on each one and could find a false start byte inside it.
    /// The assertion that no bytes were resynced is what distinguishes the two.
    /// </remarks>
    [Fact]
    public void UnknownMessageId_IsSkippedByLengthAndCounted()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("unknown_message_id");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        AssertFrames(parser, stream.Expect);

        Assert.Equal(1, parser.Statistics.UnknownMessagesSkipped);
        Assert.Equal(0, parser.Statistics.BytesResynced);
        Assert.Equal(0, parser.Statistics.ChecksumFailures);
    }

    [Fact]
    public void V1Frame_IsSkippedAsAUnitAndTheV2FrameBehindItArrives()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("v1_frame_then_v2");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        AssertFrames(parser, stream.Expect);

        Assert.Equal(1, parser.Statistics.V1FramesSkipped);
        Assert.Equal(0, parser.Statistics.BytesResynced);
    }

    /// <summary>
    /// A signed frame is rejected, signature block included.
    /// </summary>
    /// <remarks>
    /// Signing is not implemented and is not planned. What matters here is that rejection steps
    /// over all thirteen signature bytes: they sit between this frame's checksum and whatever comes
    /// next, and a parser that resynced instead would scan an HMAC for a start byte -- bytes as
    /// close to random as bytes get, so it would find one eventually and decode noise.
    /// </remarks>
    [Fact]
    public void SignedFrame_IsRejectedAndItsSignatureIsConsumed()
    {
        MavlinkStream stream = MavlinkVectors.StreamNamed("signed_frame");

        MavlinkFrameParser parser = new();
        parser.Append(stream.Bytes);

        Assert.Empty(Drain(parser));
        Assert.Equal(1, parser.Statistics.SignedFramesRejected);
        Assert.Equal(0, parser.Statistics.FramesParsed);

        //  Nothing left over and nothing resynced: the whole frame, signature included, was
        //  identified and consumed rather than picked apart.
        Assert.Equal(0, parser.BufferedByteCount);
        Assert.Equal(0, parser.Statistics.BytesResynced);
    }

    /// <summary>
    /// An incompatibility flag this parser does not understand costs the frame.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than taken from a vector, because it asserts a policy rather than a wire
    /// format: the specification requires a receiver to discard frames carrying incompatible flags
    /// it does not implement, and this is the check that the requirement is honoured instead of the
    /// flag being read and ignored. The good frame behind it must still arrive.
    /// </remarks>
    [Fact]
    public void UnknownIncompatibleFlag_CostsTheFrameButNotTheNextOne()
    {
        MavlinkVector heartbeat = MavlinkVectors.Named("heartbeat");
        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        byte[] flagged = heartbeat.Bytes;
        flagged[2] = 0x02;  // not the signing bit, and not a flag this codec implements

        MavlinkFrameParser parser = new();
        parser.Append(flagged);
        parser.Append(position.Bytes);

        List<MavlinkFrame> frames = Drain(parser);

        MavlinkFrame survivor = Assert.Single(frames);
        Assert.Equal(position.MessageId, survivor.MessageId);
    }

    // --- Extension fields ----------------------------------------------------------------------

    /// <summary>
    /// A frame longer than the station's definition is accepted, not rejected as corruption.
    /// </summary>
    /// <remarks>
    /// <b>Why this is hand-assembled rather than a vector.</b> It needs a sender whose definition of
    /// a message is newer than this station's, and the pinned pymavlink cannot be both ends of that.
    /// The mechanism is not in doubt: extension fields are excluded from the <c>CRC_EXTRA</c> seed
    /// precisely so an older receiver still validates the frame, and in the pinned dialect
    /// <c>GPS_RAW_INT</c> is 30 bytes before its extensions and 52 after with one seed covering both.
    /// <c>SYS_STATUS</c> -- which this station does decode -- has since grown three extension fields,
    /// so this is what a current autopilot actually puts on the wire.
    /// <para>
    /// The failure it guards against is the quiet one: the checksum passes, so a parser that
    /// rejected the frame on its length would drop every <c>SYS_STATUS</c> and keep working perfectly
    /// on everything else. The operator sees position and heartbeats and never sees battery.
    /// </para>
    /// </remarks>
    [Fact]
    public void ExtendedPayload_IsAcceptedAndKeptWhole()
    {
        const int ExtensionBytes = 12;

        MavlinkVector sysStatus = MavlinkVectors.Named("sys_status");
        byte[] basePayload = sysStatus.FullPayload;

        byte[] extendedPayload = new byte[basePayload.Length + ExtensionBytes];
        basePayload.CopyTo(extendedPayload, 0);
        for (int i = 0; i < ExtensionBytes; i++)
        {
            //  Non-zero, or truncation would strip them back off and the case would evaporate.
            extendedPayload[basePayload.Length + i] = (byte)(0xA0 + i);
        }

        byte[] frameBytes = BuildFrame(
            sysStatus.MessageId, extendedPayload, sysStatus.CrcExtra);

        MavlinkFrameParser parser = new();
        parser.Append(frameBytes);

        Assert.True(
            parser.TryReadFrame(out MavlinkFrame? frame),
            "A frame carrying extension fields validates against the base seed and must be "
            + "accepted; rejecting it silently drops one message type.");

        Assert.Equal(extendedPayload, frame.Payload.ToArray());
        Assert.Equal(extendedPayload.Length, frame.WireLength);
        Assert.Equal(0, parser.Statistics.ChecksumFailures);

        //  The bytes the station's own definition covers are untouched, so a decoder reading the
        //  declared fields from the front is unaffected by the extension.
        Assert.Equal(basePayload, frame.Payload.ToArray()[..basePayload.Length]);
    }

    // --- Progress under noise ------------------------------------------------------------------

    /// <summary>
    /// A false start byte the parser can already reject must not hold up the frames behind it.
    /// </summary>
    /// <remarks>
    /// The verdict on an unknown incompatibility flag needs only byte 2, but the flag byte sits
    /// beside a length byte that can claim 255 more. If the flag test waits for the declared body to
    /// arrive, one noise byte stalls every complete, checksum-valid frame already in the buffer --
    /// at a 1 Hz heartbeat, potentially for many seconds. Losing time on a frame the station already
    /// holds in full is the same hazard as losing the frame.
    /// </remarks>
    [Fact]
    public void FalseStartWithAnUnknownFlag_DoesNotStallTheFrameBehindIt()
    {
        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        //  A false start claiming the largest possible payload, with an incompatibility flag this
        //  parser does not implement. Only three bytes of it are present, and none of the 255 it
        //  claims will ever arrive.
        byte[] noise = [0xFD, 0xFF, 0x02];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        parser.Append(position.Bytes);

        MavlinkFrame frame = Assert.Single(Drain(parser));
        Assert.Equal(position.MessageId, frame.MessageId);
    }

    /// <summary>
    /// A bare 0xFE is not taken at its word, so it cannot swallow the frame behind it.
    /// </summary>
    /// <remarks>
    /// v1's header is six bytes and its length field can claim 255 more, so skipping on the strength
    /// of the start byte alone discards up to 263 bytes unverified. On a noisy link roughly one byte
    /// in 256 is 0xFE, and the resync scan walks through every discarded region -- so without a
    /// checksum gate a spurious byte silently consumes a complete position report. The v2 path has
    /// always been protected from its own false starts by the checksum; this is the same guard.
    /// </remarks>
    [Fact]
    public void FalseV1StartByte_WithACompleteClaim_IsRejectedByItsChecksum()
    {
        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        //  A claimed length of 32 puts the end of this "v1 frame" 34 bytes into the good frame
        //  behind it, and the buffer is long enough to satisfy the claim -- so nothing but checksum
        //  verification stands between the noise and a destroyed position report. The message id at
        //  offset 5 is HEARTBEAT, one this station has a seed for, so the check can actually run.
        byte[] noise = [0xFE, 0x20, 0x00, 0x01, 0x01, 0x00];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        parser.Append(position.Bytes);

        MavlinkFrame frame = Assert.Single(Drain(parser));
        Assert.Equal(position.MessageId, frame.MessageId);

        //  Counted as noise, not as v1 traffic. The distinction matters to whoever reads the
        //  counters: "something is speaking v1" is a different diagnosis from "this link is noisy".
        Assert.Equal(0, parser.Statistics.V1FramesSkipped);
        Assert.True(parser.Statistics.BytesResynced > 0);
    }

    /// <summary>
    /// A <c>0xFE</c> claiming more bytes than have arrived is noise, not something to wait for.
    /// </summary>
    /// <remarks>
    /// The v2 path has to wait for a declared body before it can judge, because the checksum is its
    /// only test. v1 gets no such benefit of the doubt: nothing here decodes v1, so refusing to wait
    /// costs nothing and means a stray start byte claiming 255 bytes cannot hold up the complete
    /// frames queued behind it.
    /// </remarks>
    [Fact]
    public void FalseV1StartByte_WithAnIncompleteClaim_DoesNotStallTheFrameBehindIt()
    {
        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        //  Claims 64 payload bytes, so 72 in total; only 46 will ever arrive.
        byte[] noise = [0xFE, 0x40, 0x00, 0x01, 0x01, 0x00];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        parser.Append(position.Bytes);

        MavlinkFrame frame = Assert.Single(Drain(parser));
        Assert.Equal(position.MessageId, frame.MessageId);
        Assert.Equal(0, parser.Statistics.V1FramesSkipped);
    }

    /// <summary>
    /// A false start byte claiming an unknown message must not swallow the frames behind it.
    /// </summary>
    /// <remarks>
    /// An unknown message id cannot be checksum-verified -- the seed is an input to the checksum --
    /// so its length byte is the only claim about where it ends, and unknown traffic is the common
    /// case on a real link. Before the length was corroborated against the following start byte,
    /// this exact input delivered <b>one</b> of eight valid frames and destroyed seven, recording
    /// the loss as a single <c>UnknownMessagesSkipped</c>: a counter documented as ordinary traffic.
    /// No counter showed the loss, which is what made it worth a guard rather than a comment.
    /// </remarks>
    [Fact]
    public void FalseStartClaimingAnUnknownMessage_DoesNotSwallowTheFramesBehindIt()
    {
        const int FrameCount = 8;

        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        //  Length 255 and message id 31, which this station has no decoder for. The claimed frame
        //  would run 267 bytes into six and a half of the good frames that follow.
        byte[] noise = [0xFD, 0xFF, 0x00, 0x00, 0x00, 0x01, 0x01, 0x1F, 0x00, 0x00];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        for (int i = 0; i < FrameCount; i++)
        {
            parser.Append(position.Bytes);
        }

        List<MavlinkFrame> frames = Drain(parser);

        Assert.Equal(FrameCount, frames.Count);
        Assert.All(frames, frame => Assert.Equal(position.MessageId, frame.MessageId));

        //  Booked as noise, which is what it was. Counting it as unknown traffic would have hidden
        //  the event behind a counter whose documented meaning is "nothing is wrong".
        Assert.Equal(0, parser.Statistics.UnknownMessagesSkipped);
        Assert.Equal(noise.Length, parser.Statistics.BytesResynced);
    }

    /// <summary>
    /// A claim ending exactly at the buffer's end is not accepted on trust; it waits for a byte to
    /// check against.
    /// </summary>
    /// <remarks>
    /// This is the one remaining way a bogus length could swallow good frames: with nothing past the
    /// claimed frame there is no start byte to corroborate it against. Accepting would be free of
    /// consequence only if nothing were queued behind, which is exactly what cannot be known yet --
    /// so the decision waits, and the frames survive to be delivered once a byte arrives that
    /// disproves the claim.
    /// <para>
    /// The lengths here are chosen so the claimed frame ends precisely on the last buffered byte:
    /// three 40-byte frames behind a 10-byte false header, against a declared payload of 118.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnknownClaimEndingAtTheBufferEnd_WaitsRatherThanSwallowing()
    {
        const int FrameCount = 3;
        const int ClaimedPayload = (FrameCount * 40) - 2;

        MavlinkVector position = MavlinkVectors.Named("global_position_int");
        Assert.Equal(40, position.Bytes.Length);

        byte[] noise = [0xFD, ClaimedPayload, 0x00, 0x00, 0x00, 0x01, 0x01, 0x1F, 0x00, 0x00];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        for (int i = 0; i < FrameCount; i++)
        {
            parser.Append(position.Bytes);
        }

        //  The claim ends on the final byte, so nothing can be concluded and nothing may be thrown
        //  away. Both halves matter: no frame delivered yet, and the whole buffer still held.
        Assert.Empty(Drain(parser));
        Assert.Equal(noise.Length + (FrameCount * 40), parser.BufferedByteCount);

        //  One byte that is not a start byte disproves the claim, and the frames come back.
        parser.Append([0x00]);

        List<MavlinkFrame> frames = Drain(parser);

        Assert.Equal(FrameCount, frames.Count);
        Assert.All(frames, frame => Assert.Equal(position.MessageId, frame.MessageId));
        Assert.Equal(0, parser.Statistics.UnknownMessagesSkipped);
    }

    /// <summary>
    /// An incomplete candidate delays the frames behind it but must never lose them.
    /// </summary>
    /// <remarks>
    /// <b>The delay is inherent and the loss is not.</b> A start byte whose declared body has not
    /// arrived cannot be told apart from a genuine frame split across two reads -- and genuine
    /// splits are ordinary, so resyncing past it immediately would destroy real frames. The parser
    /// therefore waits, and a false start claiming 255 bytes holds the buffer until that many
    /// arrive: at a 1 Hz frame rate, several seconds of telemetry queued behind three bytes of
    /// noise.
    /// <para>
    /// What must hold is that the queue is delivered rather than discarded when the candidate
    /// finally resolves. This pins that, and it is the assertion that would have failed before the
    /// unknown-message length claim was corroborated -- that combination lost every frame and
    /// reported nothing.
    /// </para>
    /// <para>
    /// Removing the delay as well needs the parser to look past an unresolved candidate and deliver
    /// verified frames from behind it, which is a larger change than framing needs and would deliver
    /// out of order. Left undone deliberately; the exposure is latency, bounded by the claimed
    /// length, with no silent loss.
    /// </para>
    /// </remarks>
    [Fact]
    public void IncompleteCandidate_DelaysTheFramesBehindItButDoesNotLoseThem()
    {
        const int FrameCount = 7;

        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        MavlinkFrameParser parser = new();

        //  Three bytes: a start byte, a length of 255, and no flags. Nothing more of it will arrive.
        parser.Append([0xFD, 0xFF, 0x00]);

        List<MavlinkFrame> frames = [];
        for (int i = 0; i < FrameCount; i++)
        {
            parser.Append(position.Bytes);
            frames.AddRange(Drain(parser));
        }

        Assert.Equal(FrameCount, frames.Count);
        Assert.All(frames, frame => Assert.Equal(position.MessageId, frame.MessageId));
        Assert.Equal(0, parser.BufferedByteCount);
        Assert.Equal(3, parser.Statistics.BytesResynced);
    }

    /// <summary>
    /// A false start byte whose flags happen to read as "signed" is verified before it is skipped.
    /// </summary>
    /// <remarks>
    /// The signature block sits after the checksum, so a signed frame's checksum span is exactly the
    /// same computation and the seed is in hand for any known message id. Skipping first would let a
    /// noise byte whose flags byte is 0x01 consume up to 280 bytes unverified and book them as
    /// signed frames -- both a lost frame and a wrong diagnosis, and both free to avoid.
    /// </remarks>
    [Fact]
    public void FalseSignedStartByte_IsCheckedBeforeItsFrameIsSkipped()
    {
        MavlinkVector position = MavlinkVectors.Named("global_position_int");

        //  Flags say signed, message id is HEARTBEAT, and the checksum is wrong because these bytes
        //  are noise. The declared length covers most of the good frame behind it -- so skipping
        //  unverified would consume 10 bytes of noise plus 31 of the frame -- but is small enough
        //  that the buffer can reach a verdict, which a v2 frame needs its whole body to do.
        byte[] noise = [0xFD, 0x10, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00];

        MavlinkFrameParser parser = new();
        parser.Append(noise);
        parser.Append(position.Bytes);

        MavlinkFrame frame = Assert.Single(Drain(parser));
        Assert.Equal(position.MessageId, frame.MessageId);

        Assert.Equal(0, parser.Statistics.SignedFramesRejected);
        Assert.Equal(1, parser.Statistics.ChecksumFailures);
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Assembles a v2 frame around an arbitrary payload, without the writer's insistence that the
    /// payload match a declared length. Used only for frames a newer sender would produce.
    /// </summary>
    private static byte[] BuildFrame(uint messageId, byte[] payload, byte crcExtra)
    {
        byte[] frame = new byte[10 + payload.Length + 2];

        frame[0] = 0xFD;
        frame[1] = (byte)payload.Length;
        frame[4] = 0;
        frame[5] = MavlinkVectorConstants.SourceSystem;
        frame[6] = MavlinkVectorConstants.SourceComponent;
        frame[7] = (byte)messageId;
        frame[8] = (byte)(messageId >> 8);
        frame[9] = (byte)(messageId >> 16);
        payload.CopyTo(frame, 10);

        ushort checksum = Crc(frame.AsSpan(1, 9 + payload.Length), crcExtra);
        frame[^2] = (byte)checksum;
        frame[^1] = (byte)(checksum >> 8);

        return frame;
    }

    /// <summary>
    /// CRC-16/MCRF4XX, restated here rather than called from the codec.
    /// </summary>
    /// <remarks>
    /// A test that built its expected frame with the same function it is testing would agree with
    /// the codec by construction. This is transcribed independently from the specification, so the
    /// hand-assembled frames above are an outside opinion about what the bytes should be.
    /// </remarks>
    private static ushort Crc(ReadOnlySpan<byte> bytes, byte crcExtra)
    {
        ushort accumulator = 0xFFFF;

        foreach (byte value in bytes)
        {
            accumulator = Fold(accumulator, value);
        }

        return Fold(accumulator, crcExtra);

        static ushort Fold(ushort accumulator, byte value)
        {
            int scratch = value ^ (accumulator & 0xFF);
            scratch = (scratch ^ (scratch << 4)) & 0xFF;

            return (ushort)((accumulator >> 8) ^ (scratch << 8) ^ (scratch << 3) ^ (scratch >> 4));
        }
    }

    private static List<MavlinkFrame> Drain(MavlinkFrameParser parser)
    {
        List<MavlinkFrame> frames = [];

        while (parser.TryReadFrame(out MavlinkFrame? frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Asserts the parser yields exactly the frames a stream case names, in order.</summary>
    private static void AssertFrames(MavlinkFrameParser parser, IReadOnlyList<string> expected)
    {
        List<MavlinkFrame> frames = Drain(parser);

        Assert.Equal(expected.Count, frames.Count);
        Assert.Equal(ExpectedMessageIds(expected), frames.Select(frame => frame.MessageId));

        //  The payloads too, not just the ids: a parser that found the right frame boundaries but
        //  sliced the payload one byte off would pass an id-only comparison.
        foreach ((string name, MavlinkFrame frame) in expected.Zip(frames))
        {
            Assert.Equal(MavlinkVectors.Named(name).FullPayload, frame.Payload.ToArray());
        }
    }

    private static IEnumerable<uint> ExpectedMessageIds(IReadOnlyList<string> expected) =>
        expected.Select(name => MavlinkVectors.Named(name).MessageId);
}
