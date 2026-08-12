using System.Diagnostics.CodeAnalysis;

namespace Mcs.Adapters.Mavlink;

/// <summary>
/// Turns a byte stream into MAVLink v2 frames, recovering from corruption without losing the frames
/// either side of it.
/// </summary>
/// <remarks>
/// <b>Streaming, not <c>byte[] -&gt; Frame</c>.</b> Today the link is UDP and every datagram holds
/// whole frames, so a function taking a complete frame would work and would be simpler. It was
/// rejected anyway: the state machine for a serial link is the same one, and the difference only
/// shows up as a rewrite on the day a radio is plugged in. More immediately, a parser that can only
/// accept a whole frame at once is a parser that cannot resync -- it has nowhere to keep the bytes
/// it has not made sense of yet, so its only response to a corrupt byte is to discard everything it
/// was given, taking the good frames in the same buffer with it.
/// <para>
/// <b>Resync discards exactly one byte.</b> On a checksum failure the parser drops the start byte
/// and rescans from the next one. Dropping the whole buffer is the obvious alternative and is
/// wrong: a corrupted frame is very often followed immediately by a good one, and the loss of a
/// position report the station had already received in full is HAZ-01 -- a console showing an older
/// picture than it was given. Dropping one byte also handles the subtler case, a false start byte
/// inside a payload, which resolves into the real frame that was there all along.
/// </para>
/// <para>
/// <b>Nothing here logs.</b> Every discard increments a counter on <see cref="Statistics"/> instead.
/// The reasoning is on <see cref="MavlinkParserStatistics"/>, and it is the difference between a
/// station whose logs are readable in flight and one whose are not.
/// </para>
/// <para>
/// <b>Not thread-safe, by design.</b> One parser belongs to one link and is driven by the single
/// loop reading that socket. Two links get two parsers; sharing one would interleave two byte
/// streams into a buffer that assumes it holds one.
/// </para>
///
/// <b>Usage:</b>
/// <code>
/// parser.Append(datagram);
/// while (parser.TryReadFrame(out MavlinkFrame? frame))
/// {
///     Dispatch(frame);
/// }
/// </code>
/// </remarks>
public sealed class MavlinkFrameParser
{
    /// <summary>MAVLink v2's start-of-frame byte.</summary>
    private const byte StxV2 = 0xFD;

    /// <summary>
    /// MAVLink v1's start-of-frame byte. Recognised so v1 traffic can be stepped over as a unit;
    /// v1 is not supported and will not be.
    /// </summary>
    private const byte StxV1 = 0xFE;

    /// <summary>STX, length, incompat, compat, sequence, system, component, and a 24-bit message id.</summary>
    private const int V2HeaderLength = 10;

    /// <summary>STX, length, sequence, system, component, and an 8-bit message id.</summary>
    private const int V1HeaderLength = 6;

    private const int ChecksumLength = 2;

    /// <summary>Link id, a six-byte timestamp, and a six-byte truncated HMAC.</summary>
    private const int SignatureLength = 13;

    /// <summary>Bit 0 of the incompatibility flags: the frame carries a signature block.</summary>
    private const byte IncompatibleFlagSigned = 0x01;

    /// <summary>
    /// The largest frame v2 can express, and therefore the most the buffer must hold to decide
    /// about one frame. Only the caller can push the buffer past this, by appending faster than it
    /// drains.
    /// </summary>
    private const int MaxFrameLength =
        V2HeaderLength + MavlinkFrame.MaxPayloadLength + ChecksumLength + SignatureLength;

    //  Sized to hold one maximal frame plus a typical datagram without a first-read resize. It
    //  grows if a caller appends more than it drains and never shrinks, which is the right trade
    //  for a buffer whose steady-state size is set by the link's MTU within the first second.
    private byte[] _buffer = new byte[MaxFrameLength * 2];

    private int _count;

    /// <summary>Gets what this parser has discarded, and why. See <see cref="MavlinkParserStatistics"/>.</summary>
    public MavlinkParserStatistics Statistics { get; } = new();

    /// <summary>
    /// Gets the bytes held back awaiting more input -- a partial frame, or a start byte whose frame
    /// has not fully arrived. Exposed for diagnostics: a value that stops changing while bytes are
    /// still arriving is a stalled parser, which no counter reports.
    /// </summary>
    public int BufferedByteCount => _count;

    /// <summary>Adds received bytes to the parse buffer. Does not itself parse.</summary>
    /// <remarks>
    /// Separate from <see cref="TryReadFrame"/> so that one read producing three frames is three
    /// calls to the reader and one to this, rather than an API that has to return a collection and
    /// allocate for the common case of returning one frame or none.
    /// </remarks>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_count + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;
    }

    /// <summary>
    /// Takes the next complete, checksum-verified frame out of the buffer.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the buffer holds no further complete frame -- which means
    /// "append more bytes", never "this stream is broken". Everything broken is consumed and
    /// counted before this returns.
    /// </returns>
    public bool TryReadFrame([NotNullWhen(true)] out MavlinkFrame? frame)
    {
        //  Every path that does not produce a frame either consumes bytes and loops, or returns
        //  false because more input is genuinely needed. Anything that consumed nothing and looped
        //  would spin here forever, so each `continue` below is preceded by a Discard.
        while (true)
        {
            if (!TrySynchronise())
            {
                frame = null;
                return false;
            }

            bool consumed;

            //  An if/else rather than a ternary over the two readers: the nullable analysis does
            //  not carry [NotNullWhen] through a conditional expression's out parameters, so the
            //  ternary compiles to a warning about `frame` that this form does not.
            if (_buffer[0] == StxV2)
            {
                if (TryReadV2Frame(out frame, out consumed))
                {
                    return true;
                }
            }
            else
            {
                frame = null;
                consumed = SkipV1Frame();
            }

            //  Nothing consumed means the frame is real so far but has not fully arrived. Keep the
            //  bytes and wait -- discarding them here is how a parser loses every frame that
            //  happens to straddle two reads.
            if (!consumed)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Drops leading bytes until the buffer starts on a start byte, counting them as resynced.
    /// </summary>
    /// <returns><see langword="false"/> if no start byte is in the buffer, which empties it.</returns>
    private bool TrySynchronise()
    {
        int start = 0;
        while (start < _count && _buffer[start] != StxV2 && _buffer[start] != StxV1)
        {
            start++;
        }

        if (start > 0)
        {
            Statistics.BytesResynced += start;
            Discard(start);
        }

        return _count > 0;
    }

    /// <summary>Reads the v2 frame at the head of the buffer, if all of it has arrived.</summary>
    /// <param name="consumed">
    /// Whether bytes were removed. False with no frame means "incomplete, wait"; true with no frame
    /// means "discarded, try again from the new head".
    /// </param>
    private bool TryReadV2Frame([NotNullWhen(true)] out MavlinkFrame? frame, out bool consumed)
    {
        frame = null;
        consumed = false;

        if (_count < V2HeaderLength)
        {
            return false;
        }

        int payloadLength = _buffer[1];
        byte incompatibleFlags = _buffer[2];

        //  Tested here, from the header alone, and deliberately *before* the wait for the body
        //  below. Any incompatibility flag other than signing is one this parser does not
        //  understand, and the specification's rule for those is to discard the frame -- that is
        //  what makes them *incompatible* rather than merely unknown. Since the verdict needs only
        //  byte 2, waiting for a declared 255-byte payload first would let a noise byte hold up
        //  every complete frame already sitting behind it in the buffer, for as long as it takes
        //  those bytes to arrive. Discarded by resync rather than by frame length, because an
        //  undefined flag is exactly the mechanism a future revision would use to append data, so
        //  the length field cannot be trusted to describe the whole frame.
        if ((incompatibleFlags & ~IncompatibleFlagSigned) != 0)
        {
            //  Both counters, as on the checksum path: the byte is resynced, and the reason is
            //  recorded separately because it is the one thing resync alone cannot say. Without it an
            //  unsupported protocol feature and a failing radio are the same reading.
            Statistics.IncompatibleFlagsRejected++;
            Statistics.BytesResynced++;
            Discard(1);
            consumed = true;
            return false;
        }

        bool signed = (incompatibleFlags & IncompatibleFlagSigned) != 0;

        int frameLength = V2HeaderLength + payloadLength + ChecksumLength
            + (signed ? SignatureLength : 0);

        if (_count < frameLength)
        {
            return false;
        }

        uint messageId = (uint)(_buffer[7] | (_buffer[8] << 8) | (_buffer[9] << 16));

        bool known = MavlinkMessageId.TryGetDefinition(
            messageId, out byte crcExtra, out int declaredLength);

        //  Verified first, for anything verifiable, so that every disposition below acts on a frame
        //  already known to be real. The signature block sits *after* the checksum, so a signed
        //  frame's checksum span is byte for byte the same computation -- which means a signed frame
        //  carrying a known message id gets validated too, rather than being taken on trust.
        if (known && !ChecksumMatches(payloadLength, crcExtra))
        {
            //  One byte, not the frame. See the resync note on the type.
            Statistics.ChecksumFailures++;
            Statistics.BytesResynced++;
            Discard(1);
            consumed = true;
            return false;
        }

        if (signed)
        {
            //  Skipped whole, signature included. The signature is not optional to step over: those
            //  thirteen bytes sit between this frame's checksum and the next start byte, and a
            //  parser that resynced instead would rescan them and could find a false start byte in
            //  an HMAC, which is as close to random as bytes get.
            //
            //  An unknown message id has no seed, so the checksum above did not run and the length
            //  claim arrives here unverified -- and on a signed link that is the *ordinary* case,
            //  since every frame carries the flag and most carry ids outside the seed table. So the
            //  claim earns the same corroboration an unsigned unknown gets, before 280 bytes are
            //  discarded on the word of byte 1.
            if (!known && !LengthClaimIsCorroborated(frameLength, out consumed))
            {
                return false;
            }

            //  Counted as signed even where it was the unknown id that prevented verification. Both
            //  facts are true of such a frame, and signing is the more actionable one: a link
            //  configured to require it presents as this number climbing, where reporting it as
            //  unknown traffic would leave an operator with no way to learn why nothing decodes.
            Statistics.SignedFramesRejected++;
            Discard(frameLength);
            consumed = true;
            return false;
        }

        if (!known)
        {
            if (!LengthClaimIsCorroborated(frameLength, out consumed))
            {
                return false;
            }

            Statistics.UnknownMessagesSkipped++;
            Discard(frameLength);
            consumed = true;
            return false;
        }

        frame = MavlinkFrame.Create(
            sequence: _buffer[4],
            systemId: _buffer[5],
            componentId: _buffer[6],
            messageId: messageId,
            incompatibleFlags: incompatibleFlags,
            compatibleFlags: _buffer[3],
            payload: _buffer.AsSpan(V2HeaderLength, payloadLength),
            declaredLength: declaredLength);

        Statistics.FramesParsed++;
        Discard(frameLength);
        consumed = true;
        return true;
    }

    /// <summary>
    /// Whether the frame at the head may be stepped over as a unit, for a frame whose checksum could
    /// not be computed and whose length claim is therefore all there is to go on.
    /// </summary>
    /// <param name="consumed">
    /// Meaningful only when this returns <see langword="false"/>: true where the claim was disproved
    /// and the head discarded as noise, false where the verdict is waiting on more input. A
    /// <see langword="true"/> return leaves it alone -- the caller consumes the frame itself, so that
    /// the counter it books belongs to the caller's disposition rather than to this check.
    /// </param>
    /// <remarks>
    /// <b>The problem.</b> The <c>CRC_EXTRA</c> seed is an input to the checksum, so a message with
    /// no definition here cannot be verified at all. Its length byte is the only thing saying where
    /// it ends, and that byte is exactly what corruption damages. Trusting it outright means a false
    /// start byte declaring 255 bytes consumes 267 of them -- and unknown traffic is the <i>common</i>
    /// case on a real link, so this path runs constantly. Measured before this guard existed: ten
    /// bytes of noise ahead of eight valid position reports delivered one of them and destroyed
    /// seven, recorded as a single <see cref="MavlinkParserStatistics.UnknownMessagesSkipped"/> --
    /// a counter documented as ordinary traffic. Nothing reported the loss. That is HAZ-01 with no
    /// symptom, and it is the same hazard <see cref="SkipV1Frame"/> guards against; leaving it open
    /// here while closing it there was indefensible.
    /// <para>
    /// <b>The corroboration.</b> MAVLink frames run back to back, so an honest length claim ends
    /// where another frame begins. If the byte just past the claimed frame is not a start byte, the
    /// claim is not corroborated and the head is treated as one byte of noise instead. That costs
    /// nothing in the ordinary case -- a genuine unknown frame is followed by a start byte or by
    /// nothing -- and it turns the measurement above into all eight frames delivered.
    /// </para>
    /// <para>
    /// Where the claimed frame ends exactly at the buffer's end there is no byte to check, so the
    /// decision waits for one. Waiting is free here: by definition nothing is queued behind it, and
    /// accepting instead would be the one remaining way for a bogus length to swallow good frames.
    /// </para>
    /// <para>
    /// <b>Shared with the signed path rather than living on the unknown one.</b> It was a private
    /// step of the unknown-message skip first, which left the identical exposure open next door: a
    /// signed frame with an unknown id also reaches its disposition unverified, and discarded
    /// <see cref="SignatureLength"/> further on the same untrustworthy byte -- booking the loss to
    /// <see cref="MavlinkParserStatistics.SignedFramesRejected"/>, a counter whose documented meaning
    /// is a configuration mismatch rather than a lost frame. Two callers with one rule is what stops
    /// the next disposition added above from being a third such hole.
    /// </para>
    /// </remarks>
    private bool LengthClaimIsCorroborated(int frameLength, out bool consumed)
    {
        consumed = false;

        if (_count == frameLength)
        {
            return false;
        }

        if (_buffer[frameLength] is not (StxV2 or StxV1))
        {
            Statistics.BytesResynced++;
            Discard(1);
            consumed = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies the checksum of the frame at the head of the buffer against
    /// <paramref name="crcExtra"/>. Covers the length byte through the last payload byte -- the
    /// start byte is excluded, being the marker the resync scan looks for rather than part of the
    /// message.
    /// </summary>
    private bool ChecksumMatches(int payloadLength, byte crcExtra, int headerLength = V2HeaderLength)
    {
        int checksumOffset = headerLength + payloadLength;

        ushort expected = MavlinkCrc.Compute(_buffer.AsSpan(1, checksumOffset - 1), crcExtra);
        ushort actual = (ushort)(_buffer[checksumOffset] | (_buffer[checksumOffset + 1] << 8));

        return expected == actual;
    }

    /// <summary>
    /// Steps over the v1 frame at the head of the buffer, if all of it has arrived. Returns whether
    /// bytes were consumed; it never yields a frame, which is why it is not a <c>TryRead</c>.
    /// </summary>
    /// <remarks>
    /// Recognised so that a real v1 frame is skipped as a unit instead of being scanned through byte
    /// by byte, which would spend a resync on every byte of it and could find a false start inside
    /// its payload. Note the limit of that: only the four ids in <see cref="MavlinkMessageId"/> have
    /// a seed, so v1 traffic of any other type cannot be verified and is scanned rather than skipped
    /// -- and does not reach <see cref="MavlinkParserStatistics.V1FramesSkipped"/>, so that counter
    /// under-reports a v1 link carrying messages this station does not decode. Accepted: the counter
    /// exists to notice v1 on the link at all, and a station that decodes four messages will see
    /// those four.
    /// <para>
    /// <b>But only once its checksum has passed.</b> Skipping on the strength of a bare
    /// <c>0xFE</c> would discard up to 263 bytes on the word of a single byte, and on any link with
    /// noise roughly one byte in 256 is <c>0xFE</c> -- so a spurious start byte would silently
    /// swallow whatever followed it, including a complete position report the station had already
    /// received. The v2 path is protected from its own false starts by the checksum; this is the
    /// equivalent guard, and verification is the only thing that distinguishes a real v1 frame from
    /// noise. It would also have mislabelled the noise as v1 traffic on a link that has none.
    /// </para>
    /// <para>
    /// So <c>0xFE</c> is trusted only when the whole frame it claims is present <i>and</i> its
    /// checksum passes. Anything else -- an unknown message id, a failed checksum, or a claimed
    /// length the buffer does not yet hold -- is one byte of noise. Note the last of those: the v2
    /// path has to wait for a declared body before it can judge, because the checksum is its only
    /// test, but v1 gets no such benefit of the doubt. Nothing here decodes v1, so there is no cost
    /// to refusing to wait for it, and refusing means a stray <c>0xFE</c> claiming 255 bytes can
    /// never hold up the frames queued behind it. The price is a resync scan through genuine v1
    /// traffic, which was being discarded anyway.
    /// </para>
    /// </remarks>
    private bool SkipV1Frame()
    {
        if (_count < V1HeaderLength)
        {
            return false;
        }

        int payloadLength = _buffer[1];
        int frameLength = V1HeaderLength + payloadLength + ChecksumLength;

        if (_count >= frameLength
            //  v1 carries an 8-bit message id at offset 5, where v2 has 24 bits at 7. The checksum
            //  is otherwise the same construction over the same span, seeded with the same CRC_EXTRA.
            && MavlinkMessageId.TryGetDefinition(_buffer[5], out byte crcExtra, out _)
            && ChecksumMatches(payloadLength, crcExtra, V1HeaderLength))
        {
            Statistics.V1FramesSkipped++;
            Discard(frameLength);
            return true;
        }

        Statistics.BytesResynced++;
        Discard(1);
        return true;
    }

    /// <summary>Removes <paramref name="count"/> bytes from the head of the buffer.</summary>
    /// <remarks>
    /// Compacts by copying rather than carrying a read offset. A read offset avoids the copy and
    /// was rejected as the wrong trade here: it needs a compaction policy anyway once the offset
    /// walks to the end, and it puts an index on every buffer access in the parse path above --
    /// where an omitted one is an off-by-a-frame bug that a checksum does not catch. The copy moves
    /// at most a few hundred bytes and happens once per frame.
    /// </remarks>
    private void Discard(int count)
    {
        _count -= count;

        if (_count > 0)
        {
            Array.Copy(_buffer, count, _buffer, 0, _count);
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        //  Doubling, floored at what is required. Parsing alone cannot get here -- a frame is
        //  bounded and every path consumes or completes one -- so growth means the caller is
        //  appending faster than it reads, and the buffer sizes itself to that caller's appetite
        //  once rather than reallocating per read.
        int capacity = Math.Max(required, _buffer.Length * 2);
        Array.Resize(ref _buffer, capacity);
    }
}
