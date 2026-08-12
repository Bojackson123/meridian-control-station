using Mcs.Adapters.Mavlink;

namespace Mcs.Adapters.Tests;

/// <summary>
/// Frames for the decode and assembly suites, produced the way the station produces them.
/// </summary>
/// <remarks>
/// Everything here goes through the real <see cref="MavlinkFrameParser"/> rather than calling
/// <c>MavlinkFrame.Create</c> directly, so the payload a decoder is handed is one that survived a
/// checksum and had its truncation undone -- which is the payload it will be handed in flight, and
/// the reason a truncated vector is a meaningful input to a field test at all.
/// <para>
/// <b>Two sources, and the difference matters.</b> <see cref="FromVector"/> starts from pymavlink's
/// committed bytes and is what the field-decoding assertions run on. <see cref="FromPayload"/>
/// builds a frame with this codec's own writer, and is only ever used for cases the fixture cannot
/// express -- a second system id, or a latitude past the pole. Those tests are about routing and
/// range checks, which the payload's provenance has no bearing on; no assertion about <i>what a
/// field means</i> is ever allowed to rest on bytes this codec produced itself.
/// </para>
/// </remarks>
internal static class MavlinkFrames
{
    /// <summary>Parses a named vector, optionally re-addressed to a different sender.</summary>
    /// <remarks>
    /// Re-addressing repacks the vector's untruncated payload through the writer, which the
    /// agreement suite has already pinned to reproduce pymavlink's bytes exactly for these very
    /// vectors -- so a re-addressed frame differs from the committed one only in the header fields
    /// that were changed on purpose.
    /// </remarks>
    internal static MavlinkFrame FromVector(
        string vectorName,
        byte systemId = MavlinkVectorConstants.SourceSystem,
        byte componentId = MavlinkVectorConstants.SourceComponent)
    {
        MavlinkVector vector = MavlinkVectors.Named(vectorName);

        return systemId == MavlinkVectorConstants.SourceSystem
            && componentId == MavlinkVectorConstants.SourceComponent
                ? Parse(vector.Bytes, vectorName)
                : FromPayload(vector.MessageId, vector.FullPayload, systemId, componentId);
    }

    /// <summary>Builds a frame around an arbitrary payload. See the remarks on this type first.</summary>
    internal static MavlinkFrame FromPayload(
        uint messageId,
        ReadOnlySpan<byte> payload,
        byte systemId = MavlinkVectorConstants.SourceSystem,
        byte componentId = MavlinkVectorConstants.SourceComponent)
    {
        byte[] written = MavlinkFrameWriter.Write(
            messageId, payload, sequence: 0, systemId, componentId);

        return Parse(written, $"message id {messageId}");
    }

    private static MavlinkFrame Parse(byte[] bytes, string description)
    {
        MavlinkFrameParser parser = new();
        parser.Append(bytes);

        Assert.True(
            parser.TryReadFrame(out MavlinkFrame? frame),
            $"The parser produced no frame from {description}, so the test below it never ran "
            + $"against anything. Parser statistics: {parser.Statistics}.");

        return frame;
    }
}
