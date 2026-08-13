# what-can-go-wrong.md

Eight things this station is built to prevent, what would cause each one, how bad it would
be, and what actually stops it.

It is written in four plain-English columns because that is the shape that survives being
read by someone who has not read anything else here. **No standard is cited** — this is not a
STANAG-anything implementation and it does not connect to real aircraft, so naming a process
it did not follow would be worse than saying plainly what could go wrong. There is no risk
matrix and no severity score either: *how bad* is a sentence, and a five-point scale invites
the question of what produced the scale, which has no honest answer.

**The arrows run from here to the requirements, not the other way round.** Every *what stops
it* ends in the identifiers of the requirements that implement it, and those requirements
exist because of these hazards rather than the reverse — MCS-002 and MCS-003 are here because
of HAZ-01, which is a better answer to "why does staleness detection matter" than "staleness
seemed important." The requirements themselves live in [`requirements.md`](requirements.md) with
their rationales, their verification methods and their evidence.

Two of these hazards have nothing built against them, and one carries a residual that is
accepted rather than mitigated. All three say so in the row. An unmitigated hazard stated
plainly is something you can plan against; one hidden behind an empty column is not.

**The identifiers are load-bearing and never change.** `HAZ-01` is referenced from source
comments in the core, in the console and in the design note, so renumbering a hazard would
silently invalidate every one of those references. A hazard that stops being real gets struck
through here with the reason, and keeps its number.

---

## The eight

| | What happens | How bad | What stops it |
| --- | --- | --- | --- |
| **HAZ-01** | The console shows a picture the operator believes is current, and it isn't | Worst in the system | MCS-002, MCS-003, MCS-005, MCS-013 |
| **HAZ-02** | A vehicle executes a plan the operator did not approve | Severe — uncommanded trajectory | MCS-006 — **not built** |
| **HAZ-03** | Two vehicles under common control are routed through the same block of sky | Severe — the only physical one | **nothing built**; no requirement yet |
| **HAZ-04** | The age of the data is measured against a clock that is not the station's | HAZ-01 arriving quietly, from inside the mitigation | MCS-002, MCS-003, MCS-005, MCS-012, MCS-013 |
| **HAZ-05** | A vehicle reports an impossible value and the console renders it as plausible | The operator is shown a number nobody measured | MCS-004, MCS-012 |
| **HAZ-06** | A vehicle is admitted or dropped without the operator being told | The fleet on screen is not the fleet reporting | MCS-010, plus one **accepted** residual |
| **HAZ-07** | The station reports itself healthy while its schema has drifted from the code | Quiet, and it corrupts the record | MCS-011 |
| **HAZ-08** | The console reaches a third-party origin | The offline claim becomes false; the fleet's position leaks | MCS-009 |

---

## The hazards

### HAZ-01 — The console shows the operator a picture they believe is current, and it isn't

**What could cause it.** Silent link loss — and note that the last frame before a radio dies
almost always reports a healthy link, so the vehicle's own account of its link is no help
here. A render that stalls while the data behind it keeps moving. An alert that fired and was
dismissed while it was off-screen. The console losing contact with the *station* and going on
drawing the last fleet it was sent. And one failure a layer further down: a corrupt length
byte in the wire framing consuming several good frames on its way past, with the loss counted
as ordinary unknown traffic — a real loss reported by the one number that means everything is
fine.

**How bad.** The worst in the system. Every operator decision inherits the false picture, and
a stale position is exactly what is needed to command a vehicle into a state it is not in. It
also makes every other hazard here worse, because it is the one that removes the operator's
ability to notice.

**What stops it.** A vehicle's telemetry is marked stale after three seconds of silence
measured against the station's clock (MCS-002), and lost after fifteen. While it is stale the
console renders it in a visibly different state that includes the age of the data, on the map
and in the fleet panel at once, and never by colour alone (MCS-003) — the state language and
its reasoning are in [`notes/console-design.md`](notes/console-design.md). Every frame is
stamped with the station's own receipt time at the moment it arrives (MCS-005). When the
console stops hearing from the station at all, every vehicle takes the lost rendering and its
age chip reads `?`, because the console cannot measure an age the station stopped reporting
and must not invent one (MCS-013).

The framing case is mitigated too, and differently: the parser will not step over a frame on
the word of a length byte it cannot verify. Unknown message ids have no checksum seed
available, so the length claim is corroborated against the byte that should follow it — MAVLink
frames run back to back, so the next byte must be a start byte — and an uncorroborated claim
is resynced one byte at a time instead of being trusted. That behaviour has **no requirement
of its own**; it is a property of the framing codec, pinned by the codec's tests rather than
by the baseline. It is written down here so the next person to touch the parser knows that
`UnknownMessagesSkipped` is a mitigation and not merely a statistic.

One mitigation is designed and not built: alerts that persist until acknowledged, in a bar
that nothing the operator does can scroll away from. The design is in the console note; the
console currently renders that bar and puts one thing in it — whether the station is still
talking.

### HAZ-02 — A vehicle executes a plan the operator did not approve

**What could cause it.** A corrupted upload. A partial upload accepted as though it were
whole. A plan cached on the vehicle from an earlier session and flown instead of the current
one.

**How bad.** Severe: an uncommanded trajectory, with nothing unusual on screen. The vehicle is
doing exactly what it believes it was told, reporting normally while it does it, so this one
does not announce itself.

**What stops it.** MCS-006 — no arm command for a mission until the vehicle has acknowledged a
plan whose checksum matches the plan the operator approved. **It is not built.** Nothing in
this station commands a vehicle at all: the adapter contract is a name and a run method, with
no command member, so the hazard is currently out of reach rather than mitigated. That is an
accident of scope and not a design, which is why the requirement is written down now — so the
mitigation is settled before the capability that needs it exists, rather than during it.

### HAZ-03 — Two vehicles under common control are routed through the same block of sky

**What could cause it.** No conflict check before a plan is approved. A check run against
paths the vehicles do not actually fly — a planner that assumes instant course changes is
describing a different aircraft from the one in the air. An operator override with no record
of who made it or why.

**How bad.** Severe, and the only hazard here whose consequence is physical rather than
informational. It is also the one where being wrong slightly is enough.

**What stops it.** **Nothing built, and no requirement yet.** Pre-flight conflict evaluation,
a planner course-change limit that keeps the trajectory bound honest, and durable overrides
carrying a reason are the intended mitigations; the requirements arrive with the feature,
because a requirement written now would be a guess about a design that does not exist, and the
table would carry an ID that means nothing.

One piece of it is already true and worth recording. The simulator's turn radius is *derived*
from its cruise speed and bank limit — `R = v²/(g·tan φ)` — and never configured, so a
separation margin computed against that number later will describe the aircraft that actually
flies. A configured turn rate would let the two drift apart while both continued to look
right.

### HAZ-04 — The age of the data is measured against a clock that is not the station's

**What could cause it.** A vehicle's reported time used as its receipt time. The browser's
clock used to compute a vehicle's age. A wall-clock step — an NTP correction of a minute
backwards takes a minute off every vehicle's age at once, and renders a fleet that stopped
reporting ten minutes ago as live again. Two different clocks stamping and reading the same
frame.

**How bad.** This is HAZ-01 arriving quietly, from inside the component built to prevent it,
and it is harder to see: the display is behaving correctly and the number it is behaving on is
wrong. A machine thirty seconds out renders a lost aircraft as live. A skew of a few seconds
around a three-second threshold switches the mitigation off without changing anything visible.

**What stops it.** Every frame is stamped by the station at the moment of arrival (MCS-005),
and the type system carries the distinction: `VehicleTelemetry` holds only what the vehicle
claimed and has no time field at all, while `TelemetryFrame` — whose constructor is internal
and whose only caller is a single-use ingest receipt — holds the station's observation. An
adapter stamping a frame with a vehicle's clock is not a mistake that is available to make; it
is code that does not compile.

The age itself comes from monotonic elapsed time rather than the subtraction of two calendar
readings, so the clock stepping cannot move it. The browser is sent the computed state and age
rather than the ingredients for them (MCS-003), and the one duration it does measure is its
own silence, which is the only one it can witness (MCS-013). A negative age — the signature of
two clocks — throws rather than clamping to zero (MCS-012), because the clamp a reasonable
person would write reports the vehicle as live, and silence answered with "everything is fine"
is the whole hazard.

### HAZ-05 — A vehicle reports an impossible value and the console renders it as plausible

**What could cause it.** A unit conversion missed at the adapter boundary — MAVLink carries
position as integer degrees times 1e7, and much aviation equipment reports knots and feet. A
0–1 fraction sent where a percentage was expected. A zero substituted for a field the vehicle
never reported. A sensor returning its fault value in band.

**How bad.** The operator is shown a number nobody measured, and nothing looks wrong, because
plausible values look like plausible values. Clamping is what makes it invisible: a clamped
200% battery renders as a believable 100% and the adapter's fault is never discovered.
Battery is the sharp case, since it is the number that would make an operator abort — but a
missed conversion on position puts the track somewhere off Africa, which at least announces
itself.

**What stops it.** Reject, never clamp, at the ingest boundary (MCS-012): latitude, longitude,
speed, heading, battery and link status are each validated on construction and an out-of-range
value throws rather than being folded into range. Altitude is inseparable from the reference it
was measured against, so a bare number cannot be constructed at all (MCS-004).

The three fields that can genuinely be absent — ground speed, heading and battery — are
nullable, and absence is never rendered as zero. A zero speed is a vehicle at rest, a zero
heading is a nose pointing north, and a zero battery is a flat one; all three are confident
claims the data does not support. Where the heading is absent the marker loses its nose
entirely, which is the honest content of the data.

### HAZ-06 — A vehicle is admitted or dropped without the operator being told

**What could cause it.** More vehicles reporting than the console was built to display. A
subscriber falling behind the feed and its queue overflowing. A vehicle slot that is never
released.

**How bad.** An operator counting eleven aircraft on a display built for twelve has no way to
tell whether the twelfth is missing or was never there. The fan-out direction is worse: a
console quietly skipping frames shows positions that are old with nothing to say that they
are, which is HAZ-01 with an internal cause.

**What stops it.** The store admits at most twelve vehicles and **rejects the thirteenth
loudly** — it throws, naming the vehicle it refused and the cap it hit, rather than dropping
the frame (MCS-010). Twelve is a system-wide commitment rather than a store detail: the fleet
panel is sized so that twelve rows fit without scrolling, so the bound is honoured on screen as
well as in memory, and a panel that scrolled would put the vehicle needing attention
off-screen. A subscriber that falls 256 frames behind loses its **oldest** frames, not its
newest — this is a state stream and not an event log, so the newest frame is the one that has
to survive.

**One residual here is accepted rather than mitigated.** The MAVLink port authenticates
nobody: message signing is not implemented and is not planned, and the adapter binds every
interface by default because the simulator sends to it from another container. A sender that
can reach the port can therefore occupy all twelve slots with invented system ids, after which
every genuine vehicle is refused until the station is restarted, since slots are never
reclaimed on their own. This is accepted because the station runs on a loopback or a compose
network and is not deployed anywhere that anyone else can reach — and because a
half-implemented authentication mechanism is worse than none, in that it invites the
assumption that frames were authenticated. The partial mitigation available today is binding
`Adapters__Mavlink__ListenAddress` to a specific interface, and the README says so in its
limitations. On any network not under the operator's control, this is the first thing to fix.

### HAZ-07 — The station reports itself healthy while its schema has drifted from the code

**What could cause it.** A migration edited after it shipped. A database restored from a
backup taken at a different version. Two builds pointed at one database.

**How bad.** Quiet, and it corrupts the thing everything else is recorded in. A station that
starts against a schema it does not expect fails at the moment it first writes something that
matters, and that will not be the moment anyone is watching. It is the same failure as a
console showing a position that is no longer true, one layer down.

**What stops it.** Migrations are numbered files compiled into the API and applied on startup,
each in its own transaction, under a Postgres advisory lock so two instances starting together
cannot both apply the same one. Each is recorded with a SHA-256 of its contents, and a
checksum that no longer matches **stops the station** rather than being logged and stepped
over (MCS-011) — a migration is immutable once shipped, and the fix for a needed change is
always a new file. `/health/db` reads the applied version back out and compares it with the
version the running build was compiled against, so "is this the database this build expects"
is a question with an answer rather than an assumption. `/health` runs no checks at all and
never will: liveness that can go red gets a container restarted for a problem restarting does
not fix, and a readiness probe is the only kind that can report this one.

### HAZ-08 — The console reaches a third-party origin

**What could cause it.** A basemap style pointing at a tile CDN. A `glyphs` or `sprite` key
added to the style — MapLibre fetches glyph range files the moment any layer uses a text
field, so a single label turns the map into an off-origin request. A font, an icon set or an
analytics script arriving inside a dependency.

**How bad.** Two failures at once, and the second is the one that gets forgotten. The console
stops working offline, so the claim that the whole stack runs from one command with no
accounts and no network becomes false — and it becomes false at exactly the moment someone
tries it, which is the first impression. And every tile request tells a third party where the
fleet is, at the rate the operator pans.

**What stops it.** Everything the console loads is served from its own origin (MCS-009): the
basemap style and all of its data are committed under `web/public`, there is no tile CDN and
no API key, and the page carries a `default-src 'self'` policy so a dependency that starts
reaching elsewhere fails loudly rather than quietly making the offline claim false. The style
has no `glyphs` key, no `sprite` key and no labels at all, and the reasoning is recorded in the
style file's own metadata beside the keys it does not have. The graticule is generated from the
viewport rather than shipped as geodata, so the map needs no external source to be legible at
any zoom.

---

## What this table is not

It is not a safety case, and no hazard analysis method produced it. It was written by reading
the system and asking, at each layer, what it could show an operator that is not true.

**Single author on both sides of every interface, and nothing here has been reviewed by anyone
else.** The most likely defect in this table is a hazard that never occurred to its author,
and a second reader is the only known fix for that.

The vehicles are simulated. Nothing in this repository has flown, connected to an autopilot,
or been used to control anything.

Mitigations marked as built are pinned by tests, and the ones that are not built say so. Both
directions of that have to stay true: a mitigation removed from the code without its row
changing here would be this file describing a protection that no longer exists, which is the
same failure the table is written against.
