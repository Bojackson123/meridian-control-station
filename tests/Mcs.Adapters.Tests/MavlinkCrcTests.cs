using Mcs.Adapters.Mavlink;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The checksum, tested directly rather than only through the parser.
/// </summary>
/// <remarks>
/// Reached through <c>InternalsVisibleTo</c> on purpose. Through the parser alone, every one of
/// these mistakes -- a wrong seed, the wrong span, a missing mask in the accumulator -- presents
/// identically, as a frame that failed to appear, and telling them apart means bisecting a state
/// machine to find out which. Testing the checksum against known values first means a parser
/// failure can be read as a parser failure.
/// </remarks>
public class MavlinkCrcTests
{
    /// <summary>
    /// The checksum matches the two bytes pymavlink appended, for every vector.
    /// </summary>
    /// <remarks>
    /// This is the same arithmetic the parser does, isolated: it takes the fixture's own frame
    /// bytes, computes the checksum over the span the format specifies, and compares against the
    /// trailer the reference implementation wrote. If this passes and decoding fails, the fault is
    /// in the framing, not the checksum.
    /// </remarks>
    [Theory]
    [AllVectors]
    internal void Compute_MatchesTheChecksumPymavlinkWrote(MavlinkVector vector)
    {
        byte[] frame = vector.Bytes;
        int checksumOffset = 10 + vector.PayloadLength;

        //  From the length byte through the last payload byte. The start byte is excluded, which is
        //  the detail most easily got wrong and is invisible in a round-trip test.
        ushort computed = MavlinkCrc.Compute(
            frame.AsSpan(1, checksumOffset - 1), vector.CrcExtra);

        ushort onTheWire = (ushort)(frame[checksumOffset] | (frame[checksumOffset + 1] << 8));

        Assert.Equal(onTheWire, computed);
    }

    /// <summary>
    /// The seed changes the result, which is the entire reason it exists.
    /// </summary>
    /// <remarks>
    /// <c>CRC_EXTRA</c> is what stops one message being decoded against another's definition when
    /// the two ends of a link disagree about a dialect. A checksum that ignored it would still be a
    /// valid checksum and would still catch corruption -- it would just quietly stop catching the
    /// failure it is there for, and no other test in the suite would notice.
    /// </remarks>
    [Fact]
    public void Compute_DependsOnTheSeed()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        Assert.NotEqual(
            MavlinkCrc.Compute(payload, crcExtra: 50),
            MavlinkCrc.Compute(payload, crcExtra: 104));
    }

    /// <summary>
    /// Leading zero bytes change the result.
    /// </summary>
    /// <remarks>
    /// The check on the initial value being 0xFFFF rather than zero. Seeded at zero, a run of
    /// leading zeros is absorbed and frames differing only in that run share a checksum -- which
    /// matters here because a MAVLink payload very often starts with a zeroed field.
    /// </remarks>
    [Fact]
    public void Compute_IsNotBlindToLeadingZeros()
    {
        Assert.NotEqual(
            MavlinkCrc.Compute([0x00, 0x01], crcExtra: 0),
            MavlinkCrc.Compute([0x01], crcExtra: 0));
    }

    /// <summary>
    /// A single flipped bit changes the checksum.
    /// </summary>
    /// <remarks>
    /// The corrupt-CRC vector proves the parser rejects a bad checksum; this proves the checksum
    /// would actually notice corruption in the payload, which that vector -- which corrupts the
    /// trailer -- does not cover.
    /// </remarks>
    [Fact]
    public void Compute_DetectsASingleFlippedBit()
    {
        byte[] original = [0x10, 0x20, 0x30, 0x40, 0x50];
        byte[] corrupted = [0x10, 0x20, 0x31, 0x40, 0x50];

        Assert.NotEqual(
            MavlinkCrc.Compute(original, crcExtra: 50),
            MavlinkCrc.Compute(corrupted, crcExtra: 50));
    }
}
