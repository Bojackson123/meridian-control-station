namespace Mcs.Adapters.Mavlink;

/// <summary>
/// CRC-16/MCRF4XX over a MAVLink frame, seeded with the message's <c>CRC_EXTRA</c> byte.
/// </summary>
/// <remarks>
/// The checksum covers the frame from the <i>length</i> byte through the end of the payload -- the
/// start-of-frame byte is excluded, because it is the thing being searched for during resync and a
/// checksum that covered it could not be computed until after the frame was found anyway.
/// <para>
/// <b>The <c>CRC_EXTRA</c> byte is the load-bearing part.</b> It is accumulated last, after the
/// payload, and is derived from the message's field names and types -- so two messages whose bytes
/// are otherwise identical produce different checksums. That is what stops a station from decoding
/// a frame against the wrong message definition when a dialect has drifted between the two ends of
/// the link. It also means a wrong seed is not a uniform failure: get it right for one message and
/// wrong for another and the parser works perfectly on heartbeats while silently discarding every
/// position report, which is HAZ-01 wearing a checksum. The committed byte vectors exist mostly to
/// make that specific mistake impossible to ship.
/// </para>
/// <para>
/// Implemented as the reference bit-twiddle rather than a 256-entry lookup table. The table is
/// roughly four times faster and entirely unnecessary here: a frame is at most 280 bytes and the
/// link delivers a few hundred a second, so this costs microseconds against a one-second
/// receipt-to-screen budget, and the loop below can be read against the specification line by line
/// where a table of magic constants cannot.
/// </para>
/// </remarks>
internal static class MavlinkCrc
{
    /// <summary>
    /// The seed both MAVLink versions start from. Not zero, so that a run of leading zero bytes
    /// changes the checksum rather than being absorbed by it.
    /// </summary>
    private const ushort InitialValue = 0xFFFF;

    /// <summary>
    /// Computes the checksum over <paramref name="frameWithoutStx"/> -- the length byte through the
    /// last payload byte -- finishing with <paramref name="crcExtra"/>.
    /// </summary>
    internal static ushort Compute(ReadOnlySpan<byte> frameWithoutStx, byte crcExtra)
    {
        ushort accumulator = InitialValue;

        foreach (byte value in frameWithoutStx)
        {
            accumulator = Accumulate(accumulator, value);
        }

        return Accumulate(accumulator, crcExtra);
    }

    /// <summary>Folds one byte into the running checksum.</summary>
    /// <remarks>
    /// A transcription of <c>crc_accumulate</c> from the reference implementation. The intermediate
    /// masks are not optional: <c>tmp</c> must be truncated to eight bits before it is shifted back
    /// up, or the high bits of <c>tmp &lt;&lt; 4</c> survive into the result and every checksum past
    /// the first byte is wrong.
    /// <para>
    /// The arithmetic is done in <see cref="int"/> and narrowed once at the end, deliberately. C#
    /// promotes <see cref="ushort"/> operands to <see cref="int"/> for every shift and xor anyway,
    /// so writing it this way makes the single narrowing point visible instead of leaving a reader
    /// to wonder where the value gets truncated.
    /// </para>
    /// </remarks>
    private static ushort Accumulate(ushort accumulator, byte value)
    {
        int scratch = value ^ (accumulator & 0xFF);
        scratch = (scratch ^ (scratch << 4)) & 0xFF;

        return (ushort)((accumulator >> 8) ^ (scratch << 8) ^ (scratch << 3) ^ (scratch >> 4));
    }
}
