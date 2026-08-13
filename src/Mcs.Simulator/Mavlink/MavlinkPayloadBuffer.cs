namespace Mcs.Simulator.Mavlink;

/// <summary>The length check every payload writer starts with.</summary>
/// <remarks>
/// Exact, not "at least". A writer handed a longer span would leave the tail as whatever was in the
/// buffer before, and <c>MavlinkFrameWriter</c> truncates on trailing zeroes -- so stale bytes past
/// the last field would be sent as if they were part of the message, and only sometimes, depending
/// on what happened to be there. A shorter span is caught by the first write that runs off the end,
/// which reports the offset rather than the message.
/// </remarks>
internal static class MavlinkPayloadBuffer
{
    /// <summary>Throws unless <paramref name="destination"/> is exactly <paramref name="required"/> bytes.</summary>
    /// <param name="destination">The span the caller is about to write into.</param>
    /// <param name="required">The message definition's declared payload length.</param>
    /// <param name="messageName">Named in the exception, so the caller need not decode an offset.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    internal static void EnsureLength(Span<byte> destination, int required, string messageName)
    {
        if (destination.Length != required)
        {
            throw new ArgumentException(
                $"{messageName} declares a {required}-byte payload; the destination span is "
                + $"{destination.Length} bytes. MavlinkFrameWriter applies v2 truncation itself, so "
                + "it must be handed the payload at its full declared length.",
                nameof(destination));
        }
    }
}
