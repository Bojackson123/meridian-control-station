# mavlink-parser.md

What writing a MAVLink v2 framing codec by hand actually taught, written while it was fresh.

Not a tutorial and not a restatement of the specification, which is published and good. This is the
handful of things that were not obvious from reading it: which of the difficulties I expected turned
up, which ones did not, what turned up instead, and why the tests are arranged the way they are. The
codec is `src/Mcs.Adapters/Mavlink` — a streaming parser, a serialiser, the checksum, and a
four-entry message table.

**Date:** 2026-08-13

---

## The frame

Six lines, because everything below refers to them.

```
 0    1    2       3      4    5     6      7  8  9      10 ...          n    n+1  n+2 ...
+----+----+-------+------+----+-----+------+---------+-----------------+---------+-----------+
|0xFD|LEN |INCOMPT|COMPAT|SEQ |SYSID|COMPID| MSG ID  |    PAYLOAD      |   CRC   | SIGNATURE |
+----+----+-------+------+----+-----+------+---------+-----------------+---------+-----------+
      \___________________ checksummed ______________________________/   2 bytes   13 bytes,
       plus CRC_EXTRA, which is never transmitted                        LSB first  only if
                                                                                    INCOMPT & 1
```

Ten bytes of header, a 24-bit little-endian message id, 0–255 bytes of payload, a two-byte
CRC-16/MCRF4XX seeded with `0xFFFF`, and an optional 13-byte signature block. Frames run back to
back with nothing between them, which matters more than it sounds — see resync.

The checksum covers the length byte through the last payload byte. The start byte is excluded,
sensibly: it is the thing you are searching for when resyncing, and a checksum covering it could not
be computed until after the frame had been found anyway.

**The `CRC_EXTRA` byte is the load-bearing part.** It is accumulated last, after the payload, and is
derived from the message's field names and types — so two messages whose bytes are otherwise
identical checksum differently. That is what stops a receiver from decoding a frame against the
wrong definition when the dialects at the two ends of a link have drifted apart. It is never
transmitted; both ends must already know it.

---

## Why hand-written

Because I want to be able to rebuild it. The rule I have been applying to this project is that
scaffolding, compose files and CI can be offloaded freely, and the two things that must be mine are
the framing and the staleness logic. A generated dialect library would have produced a working
station and nothing I could defend in a conversation.

The scope is deliberately small: it decodes the four messages the console displays, and it is
**framing only**. The payload is bytes, and nothing in the parser knows that message id 33 means a
position report. Framing fails loudly — a bad checksum is unambiguous — and semantics fail quietly,
a field read at the wrong offset being a plausible number in the wrong place. Merging the two layers
puts the quiet failures where the loud ones are.

---

## What I expected to be hard, and what actually was

I predicted two: payload truncation, and `CRC_EXTRA`. Both turned up, and neither was where the time
went.

**Truncation was straightforward, and its mirror image was not.** v2 strips trailing zero bytes on
the wire, so the payload that arrives is frequently shorter than the message definition — and *how
much* shorter depends on the values rather than the message type. A vehicle sitting at exactly zero
altitude sends a shorter position report than the same vehicle at 120 m. That is handled once, at
the frame boundary, by zero-extending back to the declared length; a decoder handed a short payload
has already lost the information needed to recover, because it cannot tell four missing bytes from a
field that was genuinely zero.

What I had not thought about was the opposite case. A payload can arrive **longer** than the
definition, with a checksum that passes, and that is not corruption: v2 extension fields are
excluded from `CRC_EXTRA` by design, precisely so a newer sender's frame validates against an older
receiver's seed. The format's instruction is to read the fields you know and ignore the rest. Had I
written the obvious length check — *decoded length must equal declared length* — it would have
rejected exactly one message type against current firmware, since `SYS_STATUS` has grown extension
fields, while every other message kept working perfectly. That is the quiet, per-message failure
this whole codec is arranged to prevent, and I would have shipped it.

**`CRC_EXTRA` was a transcription risk rather than a conceptual one.** The mechanism is simple. The
danger is that a wrong seed is not a uniform failure: get it right for `HEARTBEAT` and wrong for
`GLOBAL_POSITION_INT`, and the station shows a vehicle that never moves. Four seeds are transcribed
by hand, so the test suite asserts the table against the seeds pymavlink used, and that single
assertion is the most valuable one in the suite.

### What actually cost the time

**pymavlink would not install.** The reference implementation is the whole basis of the test
strategy below, and `pip install pymavlink` failed building a Rust extension it picked up as a
transitive dependency in 2.4.43, which publishes no wheel for this machine's architecture. The
answer was the last release that computes its CRC in pure Python. The pin is load-bearing and the
full account is in [`stuck.md`](stuck.md) under 2026-08-11 — worth reading before anyone
helpfully bumps the version.

**A length byte I could not verify, believed anyway.** This is the one I would tell someone about.

An unknown message id has no seed, so its frame cannot be checksummed at all, and its length byte is
the only thing saying where it ends — while being exactly what corruption damages. The first version
of the parser trusted it and stepped over the frame. Unknown traffic is the *common* case on a real
link, so that path runs constantly.

Measured, with a test written to see what it cost: ten bytes of noise ahead of eight valid position
reports delivered **one** of them and destroyed seven. The parser recorded that as a single
`UnknownMessagesSkipped` — a counter whose documentation says *ordinary traffic, nothing is wrong*.
A real loss, reported by the one number that means everything is fine. Nothing logged, nothing
failed, and on screen it would have been a vehicle updating slower than it should.

The fix comes from the frame layout: frames run back to back, so an honest length claim ends where
another frame begins. If the byte just past the claimed frame is not a start byte, the claim is not
corroborated and the head is treated as one byte of noise instead. That costs nothing in the
ordinary case, and it turns the measurement above into all eight frames delivered. The same
correction had to be made a second time, in a different disposition — a *signed* frame with an
unknown id reaches its skip with the same unverified byte, and was booking its losses to
`SignedFramesRejected`, a counter documented as a configuration mismatch. Two callers now share one
rule, which is what stops the next disposition added above them from being a third such hole.

The lesson generalises past MAVLink: **a counter that means "normal" must never be able to absorb a
loss.** If it can, the system has a silent failure mode with a green light on it.

**A record that compared payloads by reference.** Two frames decoded from identical bytes compared
unequal, while printing identically in the failure message. A C# record compares its fields with
`Equals`, and for a backing `byte[]` that is reference equality. The test that noticed was the one
asserting a frame split across two reads yields the same result as an unsplit one — which is exactly
the comparison a caller would reach for, and would have failed for reasons having nothing to do with
the parser.

---

## Why round-tripping your own output proves nothing

This is the most transferable idea here.

The obvious test for a codec is to encode a message, decode it, and assert you got back what you put
in. It feels thorough. It is worth almost nothing, because **a parser fed its own writer's output
agrees with itself no matter how wrong both halves are.** A transposed `CRC_EXTRA` seed, a checksum
computed over the wrong span, an off-by-one in the truncation rule — each cancels exactly between an
encoder and the decoder that mirrors it, and the round-trip stays green through all three. You have
tested that your code is self-consistent, which was never in doubt.

So the evidence here is a set of committed byte vectors packed by pymavlink — the implementation the
rest of the world flies — and the two directions are asserted **separately**:

- **decode**: pymavlink's bytes produce the expected header fields and payload;
- **encode**: the same field values reproduce pymavlink's bytes **byte for byte**.

One round-trip test does exist. It is there to pin behaviour for payload values the fixture does not
cover, and it is explicitly not the evidence.

Two decisions about the vectors that are worth the words:

**The generator is committed alongside its output.** Committing only the script would put a second
language toolchain into the build, a cost every contributor pays on every clone to regenerate a file
that changes about twice a year. Committing only the output would leave a wall of hex nobody can
check or extend — a magic number the size of a file. CI never needs Python; the check on the
generator is that rerunning it leaves `git diff` empty, which is also what proves it is
deterministic.

**The seed table covers only the ids this station decodes.** Generating the whole dialect's table
was the alternative: mechanical, free, and wrong here. It buys the ability to checksum messages that
are then discarded, produces a 250-entry file nobody reads, and materially weakens the claim that
the parser is hand-written. Transcribing four is the exercise. The cost is real and is described
next.

---

## Resync: one byte, not the buffer

When a checksum fails, the parser discards **the start byte** and rescans from the next one.

Dropping the whole buffer is the obvious alternative and is wrong. A corrupted frame is very often
followed immediately by a good one, and losing a position report the station had already received in
full is HAZ-01 by another road — a console showing an older picture than it was actually given.
Dropping one byte also handles the subtler case for free: a false start byte inside a payload
resolves into the real frame that was there all along.

The same reasoning explains why the parser is a streaming one at all, when today's link is UDP and
every datagram happens to hold a whole frame. A parser that can only accept a complete frame has
nowhere to keep bytes it has not made sense of yet, so its only possible response to one corrupt
byte is to discard everything it was handed — good frames included. It is also the same state
machine a serial link needs, and the difference would otherwise show up as a rewrite on the day a
radio is plugged in.

Three things this costs, all of them stated rather than discovered later:

**An unknown message id cannot be verified.** Four seeds, not two hundred and fifty. A corrupt
length byte on an unknown message desynchronises the stream until the next resync, where a full
table would have caught it immediately. Accepted, mitigated by the corroboration rule above, and
recovered from within one frame.

**v1 traffic is skipped, never decoded** — and only once its checksum passes, which requires the
message to be one of the four. A bare `0xFE` is not trusted to mean a v1 frame: on any link with
noise roughly one byte in 256 is `0xFE`, and skipping on the strength of one would silently swallow
up to 263 bytes, including a complete position report already received. Real v1 traffic of a type
this station does not decode is therefore scanned byte by byte rather than skipped, and does not
reach the v1 counter — so that counter under-reports. It exists to notice v1 on the link at all.

**One exposure remains open, deliberately.** A false start byte declaring a large payload delays
every frame behind it until those bytes arrive, because it cannot be distinguished from a genuine
frame split across two reads — and genuine splits are ordinary, so resyncing past it would destroy
real frames. Nothing is lost, only delayed, and a test named for that behaviour pins it. Closing it
needs the parser to look past an unresolved candidate and deliver verified frames from behind it,
which is a larger change than framing needs and would deliver out of order.

---

## What is not implemented

**Signing, and it is not planned.** Signed frames are recognised, rejected and counted rather than
misparsed. Signing is a substantial sub-feature — key management, timestamp windows, replay
rejection — that nothing here needs, and a half-implementation of an authentication mechanism is
worse than none, because it invites the assumption that frames were authenticated. The signature
block *is* handled as a length, though: those 13 bytes sit between a frame's checksum and the next
start byte, and a parser that resynced through them instead could find a false start byte inside an
HMAC, which is as close to random as bytes get.

**MAVLink v1**, beyond stepping over it. Nothing decodes it and nothing will.

**The rest of the dialect.** Four messages are decoded, because six fields are displayed. Every
other id is counted and stepped over.

**Routing, forwarding, and any notion of a component being addressed.** The station listens; it
transmits nothing at all.

**Sequence-gap accounting.** The sequence byte is carried on every decoded frame, and a gap in it is
the only evidence of loss that survives a link which is otherwise delivering — but nothing yet reads
it. Worth knowing that is a decision rather than an oversight: it answers a different question from
staleness, which asks whether *this* station has heard from a vehicle recently, against its own
clock.

**Nothing in the parser logs.** Every discard increments a counter instead. A ground station sees
dozens of message types it has no decoder for, and a log line each would train whoever is watching
to ignore the stream — after which the one line that mattered scrolls past unread.
