namespace Mcs.Adapters.Mavlink;

/// <summary>
/// The MAVLink message ids the station has a decoder for, and their <c>CRC_EXTRA</c> seeds.
/// </summary>
/// <remarks>
/// <b>Four messages, not two hundred and fifty.</b> The set is pinned to what the console displays
/// -- position, altitude, ground speed, heading, battery, link status -- and nothing else earns a
/// place here. The rejected alternative was to generate the whole common dialect's seed table from
/// the same script that emits the byte vectors: mechanical, free, and wrong for this codebase. A
/// generated table of 250 entries is a file nobody reads, it makes the repository's claim to a
/// hand-written parser materially weaker, and it buys only the ability to checksum messages the
/// station then discards. Transcribing four by hand is the exercise.
/// <para>
/// <b>What follows from a short table.</b> A frame whose message id is absent cannot be
/// checksum-verified at all -- the seed is an input to the checksum, so there is no way to validate
/// a message you do not know. Such a frame is stepped over using its declared length and counted;
/// see <see cref="MavlinkFrameParser"/>. That is a real limitation and not a hidden one: a corrupt
/// length byte on an unknown message desynchronises the stream until the next resync, where a full
/// table would have caught it immediately. Accepted, because the alternative costs the table above,
/// and because resync recovers within one frame.
/// </para>
/// <para>
/// The seeds are verified against pymavlink rather than trusted: the committed vectors carry the
/// <c>crc_extra</c> pymavlink used for every message, and the test suite asserts this table agrees
/// with them. A typo here is otherwise the quietest possible bug -- one message type stops arriving
/// and everything else keeps working.
/// </para>
/// </remarks>
internal static class MavlinkMessageId
{
    /// <summary>Vehicle presence, type, and mode. The link-status signal.</summary>
    internal const uint Heartbeat = 0;

    /// <summary>Battery remaining and voltage, plus the sensor health bitmasks.</summary>
    internal const uint SysStatus = 1;

    /// <summary>Latitude, longitude, both altitudes, and the velocity components.</summary>
    internal const uint GlobalPositionInt = 33;

    /// <summary>Ground speed and heading, already in the units the console wants.</summary>
    internal const uint VfrHud = 74;

    /// <summary>
    /// Gets the two facts framing needs about <paramref name="messageId"/>, or
    /// <see langword="false"/> if the station has no decoder for it.
    /// </summary>
    /// <param name="crcExtra">The seed the frame's checksum is finished with.</param>
    /// <param name="declaredLength">
    /// The payload length the message definition specifies, which is what a truncated payload is
    /// zero-extended back to. It belongs here rather than with the field decoding because
    /// truncation is a property of the v2 <i>frame</i>: undoing it is the last step of reading a
    /// frame, and a decoder handed a short payload has already lost the information needed to
    /// recover -- it cannot tell four missing bytes from a field that was genuinely zero.
    /// </param>
    /// <remarks>
    /// A switch rather than a dictionary: four cases compile to a jump the branch predictor
    /// handles, and a static dictionary would need a class constructor and an allocation to hold
    /// what fits in the instruction stream. It also puts the id, its seed and its length on one
    /// line, which is how they get checked against the message definitions.
    /// </remarks>
    internal static bool TryGetDefinition(uint messageId, out byte crcExtra, out int declaredLength)
    {
        (crcExtra, declaredLength) = messageId switch
        {
            Heartbeat => ((byte)50, 9),
            SysStatus => ((byte)124, 31),
            GlobalPositionInt => ((byte)104, 28),
            VfrHud => ((byte)20, 20),
            _ => ((byte)0, 0),
        };

        //  Zero is not a usable sentinel -- it is a legal CRC_EXTRA value, and a legal payload
        //  length for a message with no fields -- so the answer comes from the id being in the set
        //  above, never from either output being non-zero.
        return messageId is Heartbeat or SysStatus or GlobalPositionInt or VfrHud;
    }
}
