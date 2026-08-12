namespace Mcs.Adapters.Mavlink.Messages;

/// <summary>
/// The one check every message decoder makes before reading a field.
/// </summary>
/// <remarks>
/// Shared rather than repeated four times, and a guard rather than a reliance on
/// <c>BinaryPrimitives</c>'s own bounds check: that one throws
/// <see cref="ArgumentOutOfRangeException"/> naming a span, from whichever field happened to be read
/// first, which says nothing about which message was short or by how much.
/// <para>
/// Reaching it at all means <see cref="MavlinkFrame"/>'s zero-extension did not happen -- a v2
/// sender cannot emit a payload shorter than its definition and still pass a checksum, because
/// truncation is undone from the declared length in the framing layer. So this is an assertion about
/// this codebase, not about the vehicle, and it throws rather than counting: a broken internal
/// invariant is exactly the loud failure the framing/semantics split exists to keep separate from
/// the quiet ones.
/// </para>
/// </remarks>
internal static class MavlinkPayload
{
    internal static void EnsureLength(ReadOnlySpan<byte> payload, int required, string messageName)
    {
        //  Longer is normal and correct: v2 extension fields are excluded from CRC_EXTRA, so a
        //  newer sender's frame validates against this station's seed and arrives with bytes past
        //  the definition. Read what the definition names and ignore the rest.
        if (payload.Length < required)
        {
            throw new ArgumentException(
                $"{messageName} needs {required} payload bytes and was given {payload.Length}. "
                + "A frame reaching a decoder has already been zero-extended to its declared "
                + "length, so this is a framing-layer bug rather than malformed input.",
                nameof(payload));
        }
    }
}
