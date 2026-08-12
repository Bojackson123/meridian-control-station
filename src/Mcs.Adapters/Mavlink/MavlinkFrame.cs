namespace Mcs.Adapters.Mavlink;

/// <summary>
/// One decoded MAVLink v2 frame: its header fields and its payload, checksum already verified.
/// </summary>
/// <remarks>
/// <b>Framing only.</b> The payload is bytes. Nothing here knows that message id 33 means a
/// position report or that its first four bytes are a millisecond counter -- that is the decoder's
/// job, one layer up, and keeping the split means a framing bug and a field-mapping bug can never
/// present as each other. Framing fails loudly, semantics fail quietly, and mixing them puts the
/// quiet failures where the loud ones are.
/// <para>
/// A frame reaching a caller has passed its checksum, which is the only invariant this type
/// carries. It says nothing about whether the payload makes sense.
/// </para>
/// <para>
/// <b>The payload is zero-extended to its declared length.</b> v2 strips trailing zero bytes on the
/// wire, so what arrives is frequently shorter than the message definition -- and how much shorter
/// depends on the values, not the message type. A vehicle sitting at exactly zero altitude sends a
/// shorter position report than the same vehicle at 120 m. Restoring the zeros at this boundary is
/// what keeps that from being every downstream reader's problem, and a decoder that skipped it
/// would work perfectly until the first flight that touched zero.
/// </para>
/// <para>
/// <b>It can also be longer, and that is not corruption.</b> v2 <i>extension fields</i> append to a
/// message definition and are deliberately excluded from the <c>CRC_EXTRA</c> seed, precisely so
/// that a receiver holding an older definition still validates the frame; the format's instruction
/// to that receiver is to read the fields it knows and ignore the trailing bytes it does not. So a
/// newer sender's frame arrives longer than the declared length with a checksum that <i>passes</i>,
/// and it is kept whole here rather than rejected. This is not hypothetical: in the dialect the
/// vectors were generated from, <c>GPS_RAW_INT</c> is 30 bytes before its extensions and 52 after,
/// with one seed covering both -- and <c>SYS_STATUS</c>, which this station does decode, has since
/// grown three extension fields of its own. Rejecting the longer form would have broken exactly one
/// message type against current firmware, leaving the rest working: the quiet, per-message failure
/// this whole codec is arranged to prevent.
/// </para>
/// <para>
/// Payload as <see cref="ReadOnlyMemory{T}"/> over a private array rather than an exposed
/// <c>byte[]</c>: the parser reuses its receive buffer, so handing out a slice of it would give the
/// caller a window that changes under them on the next read. The array is copied once, here, which
/// is the one allocation per frame this design accepts in exchange for frames that stay valid.
/// </para>
/// </remarks>
public sealed record MavlinkFrame
{
    /// <summary>
    /// The largest payload v2 can express: the length field is one byte. Frames are therefore
    /// bounded at 280 bytes including the signature block, which is why the parser can buffer
    /// eagerly without a growth policy.
    /// </summary>
    public const int MaxPayloadLength = 255;

    private readonly byte[] _payload;

    private MavlinkFrame(
        byte sequence,
        byte systemId,
        byte componentId,
        uint messageId,
        byte incompatibleFlags,
        byte compatibleFlags,
        byte[] payload)
    {
        Sequence = sequence;
        SystemId = systemId;
        ComponentId = componentId;
        MessageId = messageId;
        IncompatibleFlags = incompatibleFlags;
        CompatibleFlags = compatibleFlags;
        _payload = payload;
    }

    /// <summary>
    /// Gets the sender's rolling frame counter, wrapping at 256. A gap means frames were lost
    /// between the vehicle and here -- which is not the same fact as staleness, and is the only
    /// evidence of loss that survives a link that is otherwise delivering.
    /// </summary>
    public byte Sequence { get; }

    /// <summary>Gets the sending system's id -- one vehicle, one id, by convention.</summary>
    public byte SystemId { get; }

    /// <summary>Gets the sending component's id. One system emits several: autopilot, gimbal, companion.</summary>
    public byte ComponentId { get; }

    /// <summary>
    /// Gets the message id, which selects the payload's meaning. 24 bits on the wire, so a
    /// <see cref="uint"/> rather than the <see cref="ushort"/> v1's 8-bit field would have allowed.
    /// </summary>
    public uint MessageId { get; }

    /// <summary>
    /// Gets the incompatibility flags. A receiver that does not understand a bit set here must
    /// <i>drop the frame</i> -- that is what makes them incompatible rather than merely unknown.
    /// Bit 0 is the signing flag; see <see cref="MavlinkParserStatistics.SignedFramesRejected"/>.
    /// </summary>
    public byte IncompatibleFlags { get; }

    /// <summary>
    /// Gets the compatibility flags, which a receiver may ignore wholesale and this one does.
    /// Carried rather than discarded so the frame is a faithful record of what arrived.
    /// </summary>
    public byte CompatibleFlags { get; }

    /// <summary>
    /// Gets the payload, at least as long as the message definition declares -- zero-extended if
    /// truncation shortened it, and longer than declared if the sender included extension fields.
    /// Read the fields the definition names from the front and ignore any excess.
    /// </summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>
    /// Gets the number of bytes actually on the wire, before zero-extension. Exposed because it is
    /// the only way to tell a field the vehicle reported as zero from one truncation removed --
    /// indistinguishable in <see cref="Payload"/>, and equal in value, but not in provenance.
    /// </summary>
    public int WireLength { get; private init; }

    /// <summary>
    /// Creates a frame, zero-extending <paramref name="payload"/> to
    /// <paramref name="declaredLength"/>.
    /// </summary>
    /// <param name="declaredLength">
    /// The length the message definition declares. Pass <paramref name="payload"/>'s own length
    /// where the definition is unknown -- there is then nothing to extend to, and no basis for
    /// inventing one. A <paramref name="payload"/> <i>longer</i> than this is kept whole; see the
    /// remarks on extension fields.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="declaredLength"/> is negative or exceeds <see cref="MaxPayloadLength"/>.
    /// </exception>
    internal static MavlinkFrame Create(
        byte sequence,
        byte systemId,
        byte componentId,
        uint messageId,
        byte incompatibleFlags,
        byte compatibleFlags,
        ReadOnlySpan<byte> payload,
        int declaredLength)
    {
        if (declaredLength is < 0 or > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredLength),
                declaredLength,
                $"A MAVLink payload is between 0 and {MaxPayloadLength} bytes.");
        }

        //  Allocated at whichever is longer and copied into: where the payload is short, the tail is
        //  left at zero, and that *is* the zero-extension -- no second pass to clear it. Where the
        //  payload is long, every byte is kept.
        byte[] extended = new byte[Math.Max(declaredLength, payload.Length)];
        payload.CopyTo(extended);

        return new MavlinkFrame(
            sequence,
            systemId,
            componentId,
            messageId,
            incompatibleFlags,
            compatibleFlags,
            extended)
        {
            WireLength = payload.Length,
        };
    }

    /// <summary>
    /// Compares header fields and payload <i>contents</i>.
    /// </summary>
    /// <remarks>
    /// Written out because the synthesized version does not do this. A record compares its fields
    /// with <see cref="object.Equals(object?)"/>, and for the backing <c>byte[]</c> that is
    /// reference equality -- so two frames decoded from identical bytes compare unequal, silently
    /// and while printing identically in any failure message. The first thing that noticed was a
    /// test asserting a split read yields the same frames as a whole one, which is exactly the
    /// comparison a caller would reach for.
    /// </remarks>
    public bool Equals(MavlinkFrame? other) =>
        other is not null
        && Sequence == other.Sequence
        && SystemId == other.SystemId
        && ComponentId == other.ComponentId
        && MessageId == other.MessageId
        && IncompatibleFlags == other.IncompatibleFlags
        && CompatibleFlags == other.CompatibleFlags
        && WireLength == other.WireLength
        && _payload.AsSpan().SequenceEqual(other._payload);

    /// <summary>Hashes the header and the payload length, not the payload.</summary>
    /// <remarks>
    /// Deliberately cheap and deliberately weaker than <see cref="Equals(MavlinkFrame?)"/>, which
    /// is the direction the contract allows: equal frames still hash equally. Hashing 280 payload
    /// bytes would cost more than the comparison it is meant to avoid, and nothing here keys a
    /// dictionary by a frame -- the sequence number and message id already spread the values that
    /// do get compared.
    /// </remarks>
    public override int GetHashCode() => HashCode.Combine(
        Sequence, SystemId, ComponentId, MessageId, IncompatibleFlags, CompatibleFlags, WireLength);
}
