using Mcs.Adapters.Mavlink;

namespace Mcs.Adapters.Tests;

/// <summary>
/// The tests that check this codec against pymavlink rather than against itself.
/// </summary>
/// <remarks>
/// Every case here is driven by the committed byte vectors, and the two directions are asserted
/// separately and deliberately:
/// <list type="bullet">
/// <item><b>Decode</b> -- pymavlink's bytes produce the expected header and payload.</item>
/// <item><b>Encode</b> -- the same field values reproduce pymavlink's bytes exactly.</item>
/// </list>
/// <para>
/// <b>Round-tripping is deliberately not among them.</b> A parser fed its own writer's output
/// agrees with itself however wrong both halves are -- a transposed CRC seed, a checksum computed
/// over the wrong span and an off-by-one truncation rule each cancel exactly, and the round-trip
/// stays green through all three. The one round-trip test in this file exists only to pin the
/// property for message values the fixture does not cover, and it is explicitly not the evidence
/// that the codec is correct. These are.
/// </para>
/// </remarks>
public class MavlinkCodecAgreementTests
{
    // --- The seed table, checked against the reference ----------------------------------------

    /// <summary>
    /// The single most valuable assertion in the suite.
    /// </summary>
    /// <remarks>
    /// A wrong <c>CRC_EXTRA</c> seed does not fail loudly or uniformly -- it fails every frame of
    /// one message type while every other message type keeps working perfectly. A station that
    /// shows heartbeats and never shows position is the same class of failure as HAZ-01 and much
    /// harder to see than a parser that rejects everything. Since the table is hand-transcribed,
    /// this is what stands between a typo and that outcome.
    /// </remarks>
    [Theory]
    [AllVectors]
    internal void MessageDefinition_AgreesWithPymavlink(MavlinkVector vector)
    {
        Assert.True(
            MavlinkMessageId.TryGetDefinition(
                vector.MessageId, out byte crcExtra, out int declaredLength),
            $"{vector.Message} (id {vector.MessageId}) is in the fixture but has no definition in "
            + "MavlinkMessageId, so the parser would skip it as unknown.");

        Assert.Equal(vector.CrcExtra, crcExtra);
        Assert.Equal(vector.DeclaredPayloadLength, declaredLength);
    }

    // --- Decode -------------------------------------------------------------------------------

    [Theory]
    [AllVectors]
    internal void Decode_ReproducesTheHeaderAndPayload(MavlinkVector vector)
    {
        MavlinkFrameParser parser = new();
        parser.Append(vector.Bytes);

        Assert.True(parser.TryReadFrame(out MavlinkFrame? frame), $"{vector.Name}: {vector.Note}");

        Assert.Equal(vector.MessageId, frame.MessageId);
        Assert.Equal(MavlinkVectorConstants.SourceSystem, frame.SystemId);
        Assert.Equal(MavlinkVectorConstants.SourceComponent, frame.ComponentId);
        Assert.Equal(0, frame.IncompatibleFlags);
        Assert.Equal(vector.PayloadLength, frame.WireLength);

        //  Against the zero-extended payload, not the wire payload: undoing truncation is part of
        //  decoding a v2 frame, and a decoder that handed back the short buffer would leave every
        //  reader above it to guess how many zeros to add.
        Assert.Equal(vector.FullPayload, frame.Payload.ToArray());

        Assert.Equal(1, parser.Statistics.FramesParsed);
        Assert.Equal(0, parser.Statistics.BytesResynced);
        Assert.Equal(0, parser.BufferedByteCount);
    }

    // --- Encode -------------------------------------------------------------------------------

    /// <summary>
    /// Encode reproduces pymavlink's frame byte for byte, from the untruncated payload.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte rather than field-by-field, because the things most likely to be wrong are not
    /// fields: the span the checksum covers, the byte order of the 24-bit message id, and where
    /// truncation stops. Every one of those is invisible to an assertion that compares decoded
    /// values and unmissable in a comparison of bytes.
    /// </remarks>
    [Theory]
    [AllVectors]
    internal void Encode_ReproducesPymavlinkBytesExactly(MavlinkVector vector)
    {
        byte[] written = MavlinkFrameWriter.Write(
            vector.MessageId,
            vector.FullPayload,
            sequence: 0,
            systemId: MavlinkVectorConstants.SourceSystem,
            componentId: MavlinkVectorConstants.SourceComponent);

        Assert.Equal(Convert.ToHexString(vector.Bytes), Convert.ToHexString(written));
    }

    /// <summary>
    /// The writer truncates; it does not accept something already truncated.
    /// </summary>
    /// <remarks>
    /// Without this, a caller passing the wire payload back in would get a frame that is internally
    /// consistent -- correct length byte, correct checksum -- and shorter than the one the
    /// reference implementation produces for those values. Valid, accepted by any receiver, and
    /// not the bytes this codec claims to produce.
    /// </remarks>
    [Fact]
    public void Encode_RejectsAnAlreadyTruncatedPayload()
    {
        MavlinkVector vector = MavlinkVectors.Named("global_position_int_truncated");

        //  The vector is chosen because its wire payload really is shorter than its declared one,
        //  which is what makes this a meaningful call rather than a tautology.
        Assert.True(vector.PayloadLength < vector.DeclaredPayloadLength);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MavlinkFrameWriter.Write(
                vector.MessageId, vector.WirePayload, sequence: 0, systemId: 255, componentId: 190));

        Assert.Equal("payload", ex.ParamName);
    }

    [Fact]
    public void Encode_RejectsAMessageWithNoDefinition()
    {
        //  ATTITUDE_QUATERNION: real, and deliberately not in the station's set.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MavlinkFrameWriter.Write(
                messageId: 31, new byte[32], sequence: 0, systemId: 255, componentId: 190));

        Assert.Equal("messageId", ex.ParamName);
    }

    // --- Truncation ---------------------------------------------------------------------------

    /// <summary>
    /// The truncation case that catches a decoder assuming a fixed payload length.
    /// </summary>
    /// <remarks>
    /// Nine bytes are trimmed here and the cut lands inside <c>relative_alt</c>, not on a field
    /// boundary -- truncation counts zero bytes and knows nothing about fields. A decoder that
    /// zero-extends reads it correctly; one that assumes 28 bytes reads past the buffer; one that
    /// only trims whole fields disagrees with pymavlink about the length byte.
    /// </remarks>
    [Fact]
    public void Truncation_CutsInsideAFieldAndIsUndoneOnDecode()
    {
        MavlinkVector vector = MavlinkVectors.Named("global_position_int_truncated");

        Assert.Equal(19, vector.PayloadLength);
        Assert.Equal(28, vector.DeclaredPayloadLength);

        MavlinkFrameParser parser = new();
        parser.Append(vector.Bytes);

        Assert.True(parser.TryReadFrame(out MavlinkFrame? frame));
        Assert.Equal(28, frame.Payload.Length);
        Assert.Equal(19, frame.WireLength);

        //  The nine restored bytes are zero, and the boundary is mid-field: byte 19 is the high
        //  byte of relative_alt, whose lower three bytes did survive.
        Assert.All(frame.Payload.ToArray()[19..], restored => Assert.Equal(0, restored));
    }

    /// <summary>
    /// An all-zero payload truncates to one byte, never to none.
    /// </summary>
    /// <remarks>
    /// This is the boundary a plain "while the last byte is zero" loop walks straight off, and the
    /// reason the vector set contains a message with every field zero: the answer is settled by
    /// what pymavlink emitted, not by anyone's reading of the rule.
    /// </remarks>
    [Fact]
    public void Truncation_StopsAtOneByteForAnAllZeroPayload()
    {
        MavlinkVector vector = MavlinkVectors.Named("heartbeat_all_zero");

        Assert.Equal(1, vector.PayloadLength);

        byte[] written = MavlinkFrameWriter.Write(
            vector.MessageId,
            new byte[vector.DeclaredPayloadLength],
            sequence: 0,
            systemId: MavlinkVectorConstants.SourceSystem,
            componentId: MavlinkVectorConstants.SourceComponent);

        Assert.Equal(Convert.ToHexString(vector.Bytes), Convert.ToHexString(written));
    }

    // --- Round-trip, and what it is worth ------------------------------------------------------

    /// <summary>
    /// Encode then decode returns the original values, for payloads the fixture does not cover.
    /// </summary>
    /// <remarks>
    /// Kept for the coverage it adds over arbitrary byte patterns, and worth exactly that much. It
    /// would pass with a wrong CRC seed, a wrong checksum span, and a wrong truncation floor, all
    /// three at once, because both halves share every one of those mistakes. The tests above are
    /// the ones that would fail.
    /// </remarks>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void RoundTrip_PreservesThePayload(byte fill)
    {
        byte[] payload = Enumerable.Repeat(fill, 28).ToArray();

        byte[] written = MavlinkFrameWriter.Write(
            MavlinkMessageId.GlobalPositionInt, payload, sequence: 7, systemId: 12, componentId: 34);

        MavlinkFrameParser parser = new();
        parser.Append(written);

        Assert.True(parser.TryReadFrame(out MavlinkFrame? frame));
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.Equal(7, frame.Sequence);
        Assert.Equal(12, frame.SystemId);
        Assert.Equal(34, frame.ComponentId);
    }
}

/// <summary>The header values <c>generate.py</c> packs every vector with.</summary>
/// <remarks>
/// 255/190 rather than 1/1, so a decoder that transposed the system and component bytes would be
/// caught. Restated here rather than read from the fixture because a test that reads its expected
/// value from the same file as its input asserts nothing about either.
/// </remarks>
internal static class MavlinkVectorConstants
{
    internal const byte SourceSystem = 255;

    internal const byte SourceComponent = 190;
}
