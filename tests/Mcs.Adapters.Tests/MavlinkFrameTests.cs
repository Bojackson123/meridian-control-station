using Mcs.Adapters.Mavlink;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The frame type's own behaviour: zero-extension, and the equality it had to be given by hand.
/// </summary>
public class MavlinkFrameTests
{
    /// <summary>
    /// Frames decoded from the same bytes are equal, payload included.
    /// </summary>
    /// <remarks>
    /// The record's synthesized equality compares the backing array by reference and gets this
    /// wrong, while printing both frames identically in the failure message. Pinned here because
    /// the override that fixes it is hand-written and therefore deletable -- and because comparing
    /// two frames is the obvious thing for a caller to do.
    /// </remarks>
    [Fact]
    public void Equality_ComparesPayloadContentsNotReferences()
    {
        MavlinkVector vector = MavlinkVectors.Named("global_position_int");

        MavlinkFrame first = ParseOne(vector.Bytes);
        MavlinkFrame second = ParseOne(vector.Bytes);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Equality_SeparatesFramesWithDifferentPayloads()
    {
        byte[] payload = new byte[28];
        payload[0] = 1;

        MavlinkFrame first = ParseOne(Write(payload));

        payload[0] = 2;
        MavlinkFrame second = ParseOne(Write(payload));

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Two frames whose payloads differ only in bytes that truncation removed are not equal.
    /// </summary>
    /// <remarks>
    /// Both carry the same 28 zero-extended bytes, so a comparison of payloads alone would call
    /// them equal. They are not the same frame: one reported nine fewer bytes on the wire. That
    /// distinction is the whole reason <see cref="MavlinkFrame.WireLength"/> exists, and this is
    /// the test that keeps it in the equality.
    /// </remarks>
    [Fact]
    public void Equality_SeparatesATruncatedFrameFromAnUntruncatedOne()
    {
        //  Same values either way: an all-zero payload, sent truncated to one byte by the writer,
        //  against the same payload delivered in full.
        MavlinkFrame truncated = ParseOne(Write(new byte[28]));
        MavlinkFrame full = MavlinkFrame.Create(
            sequence: 0,
            systemId: 255,
            componentId: 190,
            messageId: MavlinkMessageId.GlobalPositionInt,
            incompatibleFlags: 0,
            compatibleFlags: 0,
            payload: new byte[28],
            declaredLength: 28);

        Assert.Equal(truncated.Payload.ToArray(), full.Payload.ToArray());
        Assert.Equal(1, truncated.WireLength);
        Assert.Equal(28, full.WireLength);
        Assert.NotEqual(truncated, full);
    }

    /// <summary>
    /// A payload longer than the definition is kept whole, because that is what an extension field
    /// looks like from here.
    /// </summary>
    /// <remarks>
    /// Extension fields are excluded from the <c>CRC_EXTRA</c> seed by design, so a newer sender's
    /// frame validates against an older receiver's seed and arrives longer than declared. Rejecting
    /// it -- which this type used to do, on the reasoning that truncation only removes bytes -- would
    /// discard every frame of one message type against current firmware while leaving the others
    /// working.
    /// </remarks>
    [Fact]
    public void Create_KeepsAPayloadLongerThanTheDeclaredLength()
    {
        byte[] extended = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();

        MavlinkFrame frame = MavlinkFrame.Create(
            sequence: 0,
            systemId: 1,
            componentId: 1,
            messageId: MavlinkMessageId.Heartbeat,
            incompatibleFlags: 0,
            compatibleFlags: 0,
            payload: extended,
            declaredLength: 9);

        Assert.Equal(extended, frame.Payload.ToArray());
        Assert.Equal(12, frame.WireLength);
    }

    [Fact]
    public void Create_RejectsADeclaredLengthBeyondWhatTheFormatCanExpress()
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MavlinkFrame.Create(
                sequence: 0,
                systemId: 1,
                componentId: 1,
                messageId: MavlinkMessageId.Heartbeat,
                incompatibleFlags: 0,
                compatibleFlags: 0,
                payload: [],
                declaredLength: 256));

        Assert.Equal("declaredLength", ex.ParamName);
    }

    private static byte[] Write(byte[] payload) => MavlinkFrameWriter.Write(
        MavlinkMessageId.GlobalPositionInt,
        payload,
        sequence: 0,
        systemId: 255,
        componentId: 190);

    private static MavlinkFrame ParseOne(byte[] bytes)
    {
        MavlinkFrameParser parser = new();
        parser.Append(bytes);

        Assert.True(parser.TryReadFrame(out MavlinkFrame? frame));
        return frame;
    }
}
