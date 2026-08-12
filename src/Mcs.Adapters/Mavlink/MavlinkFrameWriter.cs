namespace Mcs.Adapters.Mavlink;

/// <summary>
/// Serialises a MAVLink v2 frame to bytes, applying the payload truncation the wire format
/// requires.
/// </summary>
/// <remarks>
/// <b>Why encode at all, when the station only receives?</b> Two reasons, and neither is
/// speculative. The simulator has to emit frames a real ground station would accept, and it is the
/// only thing in this repository that will. And a codec with only a decoder cannot be checked
/// against a reference implementation in the direction that matters: decoding pymavlink's bytes
/// proves this code reads the format, but reproducing them byte for byte from the same inputs is
/// what proves it agrees about the format -- including the truncation rule and the checksum span,
/// neither of which a decode-only test can get wrong in a way that shows.
/// <para>
/// <b>Round-tripping is not the test.</b> A parser fed its own writer's output agrees with itself
/// no matter how wrong both halves are: a transposed CRC seed, a checksum computed over the wrong
/// span, or a truncation rule off by one all cancel exactly. The committed byte vectors are what
/// make these two useful, and this type exists to be pointed at them.
/// </para>
/// <para>
/// Sequence numbers are the caller's business. This type would have to hold mutable state to
/// generate them, which would make it a connection rather than a serialiser and would put a
/// counter behind an API that otherwise has no reason to be shared carefully.
/// </para>
/// </remarks>
public static class MavlinkFrameWriter
{
    private const byte StxV2 = 0xFD;

    private const int HeaderLength = 10;

    private const int ChecksumLength = 2;

    /// <summary>
    /// Serialises one frame.
    /// </summary>
    /// <param name="messageId">Must be a message this station has a definition for.</param>
    /// <param name="payload">
    /// The full, untruncated payload, exactly as long as the message definition declares.
    /// Truncation is applied here -- passing an already-truncated payload produces a frame that is
    /// still valid but is not the frame the reference implementation would have produced for those
    /// field values, which is the one thing this type exists to match.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="messageId"/> has no definition, or <paramref name="payload"/> is not the
    /// declared length for it.
    /// </exception>
    public static byte[] Write(
        uint messageId,
        ReadOnlySpan<byte> payload,
        byte sequence,
        byte systemId,
        byte componentId,
        byte compatibleFlags = 0)
    {
        if (!MavlinkMessageId.TryGetDefinition(messageId, out byte crcExtra, out int declaredLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(messageId),
                messageId,
                "This station has no definition for that message id, so it can neither seed the "
                + "checksum nor know the payload length. Add it to MavlinkMessageId first.");
        }

        //  Exact, not "at most". A short payload here would be indistinguishable from one the
        //  caller truncated themselves, and this method would then re-truncate it and emit a frame
        //  whose length says something the sender did not mean.
        if (payload.Length != declaredLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Message {messageId} declares a {declaredLength}-byte payload; pass it in full and "
                + "let this method truncate.");
        }

        int truncatedLength = TruncatedLength(payload);
        byte[] frame = new byte[HeaderLength + truncatedLength + ChecksumLength];

        frame[0] = StxV2;
        frame[1] = (byte)truncatedLength;

        //  Incompatibility flags are hardcoded to zero rather than offered as a parameter: the only
        //  flag defined is signing, this codec does not sign, and a parameter would be an invitation
        //  to set a bit whose obligations nothing here meets.
        frame[2] = 0;
        frame[3] = compatibleFlags;
        frame[4] = sequence;
        frame[5] = systemId;
        frame[6] = componentId;
        frame[7] = (byte)messageId;
        frame[8] = (byte)(messageId >> 8);
        frame[9] = (byte)(messageId >> 16);

        payload[..truncatedLength].CopyTo(frame.AsSpan(HeaderLength));

        //  Over the truncated frame, from the length byte onward -- so the checksum covers the
        //  bytes that were sent, not the bytes that were meant. Computing it before truncation is
        //  the mistake this ordering exists to prevent, and it produces a frame every receiver
        //  rejects.
        int checksumOffset = HeaderLength + truncatedLength;
        ushort checksum = MavlinkCrc.Compute(frame.AsSpan(1, checksumOffset - 1), crcExtra);

        frame[checksumOffset] = (byte)checksum;
        frame[checksumOffset + 1] = (byte)(checksum >> 8);

        return frame;
    }

    /// <summary>
    /// Returns the payload length after v2's trailing-zero truncation.
    /// </summary>
    /// <remarks>
    /// <b>The floor is one byte, not zero.</b> An all-zero payload truncates to a single zero byte
    /// and the format never carries a zero-length payload -- which is not obvious from the rule as
    /// written, is the boundary a plain "while the last byte is zero" loop walks straight off, and
    /// is settled here by a committed vector rather than by anybody's recollection.
    /// <para>
    /// Truncation is blind to field boundaries and that is deliberate on the format's part: it
    /// counts zero bytes, so a cut lands wherever it lands, frequently in the middle of a multi-byte
    /// field. There is nothing to align to here, and code that tried would disagree with every other
    /// implementation.
    /// </para>
    /// </remarks>
    private static int TruncatedLength(ReadOnlySpan<byte> payload)
    {
        int length = payload.Length;

        while (length > 1 && payload[length - 1] == 0)
        {
            length--;
        }

        return length;
    }
}
