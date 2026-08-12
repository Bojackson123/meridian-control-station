#!/usr/bin/env python3
"""Emit the MAVLink v2 byte vectors the framing codec is tested against.

Run this, commit its output, and never hand-edit the result:

    py -3.12 -m venv .venv
    .venv/Scripts/pip install -r requirements.txt      # Scripts -> bin on Linux/macOS
    .venv/Scripts/python generate.py

Why both this script and its output live in the repository
----------------------------------------------------------
CI must not need pymavlink. A second language toolchain in the build is a cost every
contributor pays on every clone, forever, to regenerate a file that changes about twice a
year. So the fixture is committed and the tests read it as an embedded resource.

But a committed fixture with no generator is a wall of hex nobody can check or extend --
a magic number the size of a file. This script is the answer to "where did these bytes
come from", and it is how the vector set grows when new messages are added.

What the fixture is *for*
-------------------------
A parser tested only against its own encoder round-trips perfectly with a wrong CRC seed,
a wrong truncation rule, and a wrong byte order -- every error cancels itself out. These
bytes come from pymavlink, the implementation the rest of the world flies, so a test
against them is a test of agreement rather than of self-consistency.

Determinism is a requirement, not a nicety: the verification step for the ticket that
introduced this file is that rerunning it leaves `git diff` empty. Hence sorted keys, a
fixed message order, literal field values rather than anything random or clock-derived,
and an explicit "\\n" newline so a Windows checkout reproduces a Linux one.
"""

from __future__ import annotations

import json
import pathlib
import sys

from pymavlink.dialects.v20 import common as mavlink

#  Relative to this file, so the script works from any working directory. It writes into the
#  test project rather than next to itself because the fixture is consumed as an embedded
#  resource, and a resource that lives outside its project is a linked file nobody expects.
OUTPUT_PATH = (
    pathlib.Path(__file__).resolve().parents[2]
    / "tests"
    / "Mcs.Adapters.Tests"
    / "vectors"
    / "mavlink-v2.json"
)

#  A fixed pair, not the defaults. 255 is the conventional ground-station system id and 190
#  the conventional "mission planner" component id; using them means the header bytes in the
#  fixture are not all 1s, so a decoder that transposes sysid and compid is caught.
SRC_SYSTEM = 255
SRC_COMPONENT = 190

#  MAVLink v2's start byte. v1's 0xFE appears once below, in the frame the parser must skip.
STX_V2 = 0xFD
STX_V1 = 0xFE


def _mav() -> mavlink.MAVLink:
    """A packer whose sequence number starts from a known point.

    pymavlink increments `seq` on every pack, so the sequence a vector carries depends on how
    many messages were packed before it. Handing each vector its own instance makes every
    frame start at seq 0 and keeps the fixture stable under reordering -- which matters,
    because otherwise inserting a vector at the top silently rewrites every frame below it.
    """
    link = mavlink.MAVLink(None, srcSystem=SRC_SYSTEM, srcComponent=SRC_COMPONENT)
    link.robust_parsing = True
    return link


def _messages() -> dict[str, mavlink.MAVLink_message]:
    """The message instances the vectors are built from.

    Four message types, because those are the four the console needs (position, altitude,
    ground speed, heading, battery, link status) and no others. Values are chosen to be
    awkward rather than round: negative longitudes and velocities catch a decoder that
    treats a signed field as unsigned, and values above 127 in a single byte catch one that
    sign-extends where it should not.
    """
    return {
        #  mavlink_version is the last field and non-zero, so this one is *not* truncated --
        #  the control case against which the truncated vectors below mean something.
        "heartbeat": mavlink.MAVLink_heartbeat_message(
            type=mavlink.MAV_TYPE_FIXED_WING,
            autopilot=mavlink.MAV_AUTOPILOT_ARDUPILOTMEGA,
            base_mode=mavlink.MAV_MODE_FLAG_SAFETY_ARMED
            | mavlink.MAV_MODE_FLAG_CUSTOM_MODE_ENABLED,
            custom_mode=5,
            system_status=mavlink.MAV_STATE_ACTIVE,
            mavlink_version=3,
        ),
        #  Every field non-zero, so nothing is trimmed and the full 28-byte payload is
        #  exercised. Southern and western hemispheres: both coordinates are negative, and
        #  a decoder reading them unsigned lands them on the far side of the planet.
        "global_position_int": mavlink.MAVLink_global_position_int_message(
            time_boot_ms=3_600_000,
            lat=-337_213_400,
            lon=1_511_627_700,
            alt=1_250_500,
            relative_alt=118_300,
            vx=-1250,
            vy=430,
            vz=-75,
            hdg=27_150,
        ),
        #  The truncation case that actually bites. vx/vy/vz/hdg are zero (8 bytes) and
        #  relative_alt's high byte is zero too, so v2 trims *nine* bytes -- the cut lands in
        #  the middle of a multi-byte field. A decoder that zero-extends to the declared
        #  length reads it correctly; one that assumes a fixed length does not, and one that
        #  only trims whole fields produces a different byte count than pymavlink did.
        "global_position_int_truncated": mavlink.MAVLink_global_position_int_message(
            time_boot_ms=1000,
            lat=515_074_000,
            lon=-1_278_000,
            alt=120_000,
            relative_alt=100_000,
            vx=0,
            vy=0,
            vz=0,
            hdg=0,
        ),
        #  battery_remaining is int8 and -1 means "unmeasured" -- the value the station must
        #  carry through as "unreported" rather than as a battery about to run out.
        "sys_status": mavlink.MAVLink_sys_status_message(
            onboard_control_sensors_present=0x0F_FF_FF_FF,
            onboard_control_sensors_enabled=0x00_00_10_25,
            onboard_control_sensors_health=0x00_00_10_05,
            load=421,
            voltage_battery=12_600,
            current_battery=-1,
            battery_remaining=-1,
            drop_rate_comm=35,
            errors_comm=7,
            errors_count1=1,
            errors_count2=2,
            errors_count3=3,
            errors_count4=4,
        ),
        #  Floats, which is the point: four IEEE-754 values whose byte patterns a hand-written
        #  decoder has to get right, including a negative climb rate.
        "vfr_hud": mavlink.MAVLink_vfr_hud_message(
            airspeed=23.75,
            groundspeed=21.5,
            heading=142,
            throttle=67,
            alt=1250.5,
            climb=-2.25,
        ),
        #  Every field zero. This settles the minimum-length question empirically rather than
        #  from memory: v2 trims trailing zeros, and the answer is that it stops at one byte
        #  and never emits a zero-length payload. Worth a vector because it is the boundary
        #  a "while last byte is zero" loop walks straight off.
        "heartbeat_all_zero": mavlink.MAVLink_heartbeat_message(
            type=0,
            autopilot=0,
            base_mode=0,
            custom_mode=0,
            system_status=0,
            mavlink_version=0,
        ),
    }


def _fields(message: mavlink.MAVLink_message) -> dict[str, object]:
    """The message's decoded field values, for the tests that check semantics rather than bytes.

    Carried now, though framing does not use them, because the decode work reads this same
    file and regenerating it later to add a column nobody can review is worse than emitting
    one field map today.
    """
    return {name: getattr(message, name) for name in message.get_fieldnames()}


def _vector(name: str, message: mavlink.MAVLink_message, note: str) -> dict[str, object]:
    raw = message.pack(_mav())
    payload_length = raw[1]

    return {
        "name": name,
        "note": note,
        "message": message.get_type(),
        "message_id": message.get_msgId(),
        #  The seed the CRC is computed with. Emitted so the codec's hand-transcribed table
        #  can be checked against pymavlink's, which is the single most valuable number in
        #  this file: a wrong seed fails every frame of one message type while every other
        #  message type keeps working.
        "crc_extra": type(message).crc_extra,
        "payload_length": payload_length,
        #  The declared length from the message definition. Where this exceeds
        #  payload_length, v2 truncation happened and the decoder must zero-extend.
        "declared_payload_length": type(message).unpacker.size,
        "bytes": raw.hex(),
        "fields": _fields(message),
    }


def _corrupt_crc(frame: bytes) -> bytes:
    """Flip one bit in the checksum, leaving a frame that is well-formed but must be rejected.

    The last two bytes are the CRC. Corrupting those rather than the payload is deliberate:
    a corrupted payload would also be caught, but by corrupting the checksum the vector
    isolates the check itself -- the parser cannot pass this one by accident.
    """
    return frame[:-1] + bytes([frame[-1] ^ 0x01])


def _v1_frame() -> bytes:
    """A MAVLink v1 HEARTBEAT, hand-assembled.

    v1 is recognised and skipped, not supported, so there is no reason to carry a v1
    dialect import for one frame. The layout is STX, len, seq, sysid, compid, msgid, payload,
    CRC -- six header bytes counting STX, against v2's ten, and no incompat/compat flags. That
    four-byte difference is why a parser that assumes v2 lengths desynchronises on one of these
    rather than skipping it, and it is also why the message id sits at offset 5 here and at 7
    in v2.
    """
    payload = bytes([0x05, 0x00, 0x00, 0x00, 0x01, 0x03, 0x51, 0x04, 0x03])
    header = bytes([len(payload), 0x00, SRC_SYSTEM, SRC_COMPONENT, 0x00])

    #  pymavlink's x25crc is the same CRC-16/MCRF4XX the v2 frames use; only the span it
    #  covers differs. Seeded with HEARTBEAT's crc_extra, exactly as v1 does.
    crc = mavlink.x25crc(header + payload)
    crc.accumulate(bytes([mavlink.MAVLink_heartbeat_message.crc_extra]))

    return bytes([STX_V1]) + header + payload + crc.crc.to_bytes(2, "little")


def _unknown_message_frame() -> bytes:
    """A well-formed v2 frame carrying a message id the station has no decoder for.

    ATTITUDE_QUATERNION (id 31) is real, is broadcast by real autopilots, and is not one of
    the four the console needs -- so it is exactly the traffic the parser has to step over
    without complaint. It is skipped by its length field rather than checksum-verified,
    because verifying it would require a CRC seed for a message with no decoder behind it.
    """
    return mavlink.MAVLink_attitude_quaternion_message(
        time_boot_ms=12_345,
        q1=0.7071,
        q2=0.0,
        q3=0.7071,
        q4=0.0,
        rollspeed=0.125,
        pitchspeed=-0.25,
        yawspeed=0.5,
    ).pack(_mav())


def build() -> dict[str, object]:
    messages = _messages()

    vectors = [
        _vector("heartbeat", messages["heartbeat"], "Full payload; no truncation applies."),
        _vector(
            "global_position_int",
            messages["global_position_int"],
            "Every field non-zero, so the full 28-byte payload is on the wire.",
        ),
        _vector(
            "global_position_int_truncated",
            messages["global_position_int_truncated"],
            "Nine trailing zero bytes trimmed; the cut falls inside relative_alt.",
        ),
        _vector("sys_status", messages["sys_status"], "battery_remaining is -1, meaning unmeasured."),
        _vector("vfr_hud", messages["vfr_hud"], "Four IEEE-754 floats, one of them negative."),
        _vector(
            "heartbeat_all_zero",
            messages["heartbeat_all_zero"],
            "All fields zero. Truncation stops at one byte; a zero-length payload never occurs.",
        ),
    ]

    heartbeat = messages["heartbeat"].pack(_mav())
    position = messages["global_position_int"].pack(_mav())
    back_to_back = heartbeat + position

    #  Split inside the payload rather than on the header boundary. A parser that buffers
    #  whole headers but not partial payloads passes a header-boundary split and fails this
    #  one, so the easier case would have been the one that proves nothing.
    split_offset = len(heartbeat) + 14

    streams = {
        "back_to_back": {
            "note": "Two complete frames in one buffer, no separator.",
            "bytes": back_to_back.hex(),
            "expect": ["heartbeat", "global_position_int"],
        },
        "split_mid_payload": {
            "note": (
                "The same two frames, delivered as two buffers split inside the second "
                "frame's payload. Must yield the same frames as back_to_back."
            ),
            "chunks": [back_to_back[:split_offset].hex(), back_to_back[split_offset:].hex()],
            "expect": ["heartbeat", "global_position_int"],
        },
        "corrupt_crc_then_good": {
            "note": (
                "A HEARTBEAT with one bit flipped in its checksum, followed by a good frame. "
                "The first must be rejected and the second must still be delivered -- which "
                "is what one-byte resync buys over discarding the buffer."
            ),
            "bytes": (_corrupt_crc(heartbeat) + position).hex(),
            "expect": ["global_position_int"],
        },
        "leading_garbage": {
            "note": (
                "Sixteen bytes of noise ahead of a good frame, covering a parser attached "
                "mid-stream. The noise contains a false 0xFD at offset 2 declaring a 2-byte "
                "payload and HEARTBEAT's message id, so it resolves as a checksum failure "
                "within this buffer. A false start byte declaring a large payload instead "
                "delays every frame behind it until that many bytes arrive -- which is "
                "unavoidable, since it cannot be told from a genuine frame split across two "
                "reads, but is emphatically not untestable: see the parser suite's "
                "IncompleteCandidate_DelaysTheFramesBehindItButDoesNotLoseThem, which pins the "
                "part that matters, that the queue is delivered and not discarded."
            ),
            "bytes": (
                bytes([0x00, 0xFF, 0xFD, 0x02, 0x00, 0x00, 0x42, 0x99,
                       0xAB, 0x00, 0x00, 0x00, 0xCD, 0xEF, 0x01, 0x23])
                + heartbeat
            ).hex(),
            "expect": ["heartbeat"],
        },
        "v1_frame_then_v2": {
            "note": (
                "A MAVLink v1 HEARTBEAT ahead of a v2 frame. v1 is recognised and skipped, "
                "not decoded, and the v2 frame behind it must survive."
            ),
            "bytes": (_v1_frame() + position).hex(),
            "expect": ["global_position_int"],
        },
        "unknown_message_id": {
            "note": (
                "ATTITUDE_QUATERNION (id 31), which the station has no decoder for, ahead of "
                "a good frame. Skipped by its length field and counted, never logged per frame."
            ),
            "bytes": (_unknown_message_frame() + position).hex(),
            "expect": ["global_position_int"],
        },
        "signed_frame": {
            "note": (
                "A frame with MAVLINK_IFLAG_SIGNED set in incompat_flags and a 13-byte "
                "signature block appended. Signing is not implemented and never will be, so "
                "this must be rejected and counted rather than misparsed -- the signature "
                "bytes would otherwise be read as the start of the next frame."
            ),
            "bytes": _signed_frame(heartbeat).hex(),
            "expect": [],
        },
    }

    return {
        "generator": "tools/mavlink-vectors/generate.py",
        "pymavlink_version": _pymavlink_version(),
        "stx_v1": STX_V1,
        "stx_v2": STX_V2,
        "src_system": SRC_SYSTEM,
        "src_component": SRC_COMPONENT,
        "vectors": vectors,
        "streams": streams,
    }


def _signed_frame(frame: bytes) -> bytes:
    """Take a good frame and make it a signed one.

    Hand-assembled rather than produced through pymavlink's signing support, because setting
    that up needs a key, a link id and a timestamp source -- three things that would have to
    be pinned to keep the fixture deterministic, all to produce bytes this codec is only ever
    going to reject. What matters here is the shape: the flag set, and thirteen more bytes
    between the checksum and whatever follows.
    """
    signed = bytearray(frame)
    signed[2] |= 0x01  # MAVLINK_IFLAG_SIGNED

    #  The CRC now covers a changed incompat_flags byte, so recompute it -- otherwise this
    #  vector would be rejected for a bad checksum and never reach the signing check it exists
    #  to exercise.
    payload_length = signed[1]
    crc = mavlink.x25crc(bytes(signed[1 : 10 + payload_length]))
    crc.accumulate(bytes([mavlink.MAVLink_heartbeat_message.crc_extra]))
    signed[10 + payload_length : 12 + payload_length] = crc.crc.to_bytes(2, "little")

    #  linkId, 6-byte timestamp, 6-byte truncated HMAC. Fixed bytes: the values are never
    #  checked, only stepped over.
    return bytes(signed) + bytes([0x01]) + bytes(range(0x10, 0x16)) + bytes(range(0x20, 0x26))


def _pymavlink_version() -> str:
    import pymavlink

    return pymavlink.__version__


def main() -> int:
    document = build()

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    #  newline="\n" and a trailing newline, so the file this writes on Windows is the file it
    #  writes on Linux. Without it the reproduction check -- regenerate, expect an empty git
    #  diff -- fails for everyone on the wrong platform and tells them nothing useful.
    with OUTPUT_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, indent=2, sort_keys=True)
        handle.write("\n")

    print(f"wrote {OUTPUT_PATH.relative_to(pathlib.Path(__file__).resolve().parents[2])}")
    print(f"  {len(document['vectors'])} frame vectors, {len(document['streams'])} stream cases")
    return 0


if __name__ == "__main__":
    sys.exit(main())
