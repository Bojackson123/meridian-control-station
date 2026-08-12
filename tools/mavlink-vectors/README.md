# MAVLink byte vectors

`generate.py` emits `tests/Mcs.Adapters.Tests/vectors/mavlink-v2.json` — frames packed by
pymavlink, which the hand-written codec in `src/Mcs.Adapters/Mavlink` is tested against.

**You do not need any of this to build or test the repository.** The fixture is committed, the
test project embeds it, and CI runs the MAVLink suite with no Python installed. This directory
matters only when the vector set changes.

## Why the output is committed as well as the script

Committing only the script would put pymavlink in the build, and a second language toolchain is a
cost every contributor pays on every clone to regenerate a file that changes about twice a year.

Committing only the output would leave a wall of hex nobody can check or extend — a magic number
the size of a file.

## Why the vectors exist at all

A codec tested against its own output is self-consistent by construction. A wrong CRC seed, a
checksum computed over the wrong span, and an off-by-one truncation rule all cancel between an
encoder and the decoder that mirrors it, and every round-trip test passes anyway. These bytes come
from the implementation the rest of the world flies, so a test against them is a test of agreement.

## Regenerating

```bash
py -3.12 -m venv .venv                          # python3.12 -m venv .venv elsewhere
.venv/Scripts/pip install -r requirements.txt   # .venv/bin/pip elsewhere
.venv/Scripts/python generate.py
git diff --exit-code ../../tests/Mcs.Adapters.Tests/vectors/
```

That last line is the point. The generator is deterministic — sorted keys, fixed message order,
literal field values, explicit `\n` newlines — so an unchanged vector set must produce an empty
diff. A non-empty one after an unrelated change means the generator has picked up something that
varies between runs, which is a bug in the generator.

pymavlink is pinned, and **not** to the newest release. The reason is in `requirements.txt` and in
`docs/notes/stuck.md` under 2026-08-11; read it before bumping the version.

## Adding a vector

Add the message to `_messages()` in `generate.py`, add a `_vector(...)` line for it in `build()`,
rerun, and commit the script and the fixture together. If the message is one the station decodes,
it also needs an entry in `MavlinkMessageId` — the test suite asserts the two agree, so a missing
entry fails rather than being skipped silently.
