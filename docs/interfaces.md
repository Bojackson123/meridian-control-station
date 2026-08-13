# interfaces.md

The contract this station speaks: what a vehicle has to supply for the station to display it, and
what the station serves to anything reading from it.

It describes what is **implemented**, not what is planned. Where the station takes a field and
ignores three others, that is written down, because a field the receiver ignores is the one an
integrator will spend an afternoon on. Where a rule is enforced by rejection, the rejection is
described too — this station's whole design leans on refusing data rather than repairing it, and an
integrator who does not know that reads a dropped message as a bug in their own encoder.

**The section numbers are stable and referenced from elsewhere.** [`requirements.md`](requirements.md)
cites §2 for the rule that vehicle time is untrusted. Sections get added at the end; they do not get
renumbered.

Two identifier shapes appear here. `MCS-001` and friends are requirements, in
[`requirements.md`](requirements.md); `HAZ-01` and friends are hazards, in
[`what-can-go-wrong.md`](what-can-go-wrong.md). Neither is a MAVLink concept.

---

## 1. What a vehicle supplies

MAVLink v2 over UDP. The station **binds a socket and listens**; it transmits nothing at all — no
heartbeat, no stream-rate request, no acknowledgement. A vehicle sends to it, and whatever arrives
is what the station has. Where it listens is `Adapters:Mavlink` in the API's configuration,
`0.0.0.0:14550` by default and overridable as `Adapters__Mavlink__ListenAddress` and
`Adapters__Mavlink__Port`.

That the link is one-directional is a property of this milestone rather than of UDP: there is no
command path yet, so there is nothing to send. It has one consequence worth stating plainly — **the
station never asks for a message rate.** A vehicle that has not been configured to emit the four
messages below emits none of them, and the station's symptom is an empty fleet rather than an error.

### The four messages

| Message | id | Payload | `CRC_EXTRA` | What is taken from it |
| --- | --- | --- | --- | --- |
| `HEARTBEAT` | 0 | 9 | 50 | nothing — the fact that it arrived |
| `SYS_STATUS` | 1 | 31 | 124 | `battery_remaining` |
| `GLOBAL_POSITION_INT` | 33 | 28 | 104 | `lat`, `lon`, `alt` |
| `VFR_HUD` | 74 | 20 | 20 | `groundspeed`, `heading` |

Anything else is counted and discarded. A message id outside this set has no `CRC_EXTRA` seed here,
so it cannot be checksum-verified at all — the seed is an input to the checksum — and the frame is
stepped over rather than examined. That is the ordinary case on a real link and not an error
condition.

### What is decoded and deliberately unused

The reasons are the useful half of this section, because each one is a decision that could
reasonably have gone the other way.

| Field | Why it is not used |
| --- | --- |
| `GLOBAL_POSITION_INT.relative_alt` | Height above the point the vehicle armed at. **This is not AGL** and is never relabelled as such: the two are equal only over flat terrain, and there is no terrain model here to make the conversion honest. |
| `GLOBAL_POSITION_INT.hdg` | Centidegrees, finer than `VFR_HUD`'s whole degrees and estimated at the same instant as the position — and still not used, so that each field on screen has exactly one source. Preferring whichever message arrived last makes a field's provenance a function of link timing. |
| `GLOBAL_POSITION_INT.vx` `vy` `vz` | Ground speed could be derived as `sqrt(vx² + vy²)`, and an angle from the same components would be **course over ground**, not heading. In any wind they differ, and the console shows heading. |
| `GLOBAL_POSITION_INT.time_boot_ms` | The vehicle's clock. See [§2](#2-time-and-trust). |
| `VFR_HUD.alt` | Altitude comes from `GLOBAL_POSITION_INT` so that the height an operator reads was estimated at the same instant as the position it is shown beside. |
| `VFR_HUD.airspeed`, `climb`, `throttle` | Nothing on the console displays them. |
| `SYS_STATUS.voltage_battery`, `current_battery` | Battery percentage is taken as reported and **never derived from voltage**: the mapping from pack voltage to remaining charge depends on chemistry, temperature and load, and inventing one produces a number that looks measured. |
| `SYS_STATUS` sensor bitmasks, `drop_rate_comm`, error counts | Decoded and read by nothing. The fault work that will read them is not built, and the simulator sends one healthy mask, always. |
| `HEARTBEAT.base_mode`, `custom_mode`, `system_status` | Presence is what a heartbeat contributes today. Arm state lives in `base_mode` and will be read from there when there is something that acts on it. |

### How messages become one vehicle's picture

**MAVLink does not send a telemetry frame.** It sends several messages on independent schedules, so
"where this vehicle is now" is composed by the station rather than received:

- **One report is emitted per `GLOBAL_POSITION_INT`**, and only per `GLOBAL_POSITION_INT`. Emitting
  on every inbound message would make the console's update rate a property of how the sender is
  configured; emitting on a timer would put a frame on screen at a moment nothing arrived.
- **A position emits even with no `VFR_HUD` behind it**, carrying `null` for ground speed and
  heading. Holding it back would keep a vehicle whose position is known entirely off the console.
- **The most recent `VFR_HUD` and `SYS_STATUS` are carried forward** onto later positions. A sender
  whose `VFR_HUD` stops therefore produces fresh reports carrying an old heading; the frame really
  is fresh, and only the fields inside it are not. Nothing detects that today, and it is the one
  gap in §2's guarantee that is not covered by staleness.
- **Vehicle identity is the MAVLink system id**: system 1 is `MAV-001`, zero-padded so ids sort
  lexicographically. Sender state is keyed by system **and** component, so a gimbal's messages
  cannot fold into the airframe's state — but the id comes from the system alone, so two components
  of one system that both emit positions interleave into one vehicle. Nothing detects that either.

### What the station needs from the sender

- **`GLOBAL_POSITION_INT` at 1 Hz or faster.** The staleness threshold in §2 is three seconds, so a
  vehicle reporting position more slowly than that is correctly shown as stale between its own
  reports. This is the only rate the station's behaviour depends on.
- **v2 frames.** v1 is recognised so it can be stepped over cleanly, and is never decoded.
- **Unsigned frames.** Signing is not implemented; signed frames are rejected and counted.
- **True heading, and metric units**, per [§3](#3-units-and-references).

---

## 2. Time and trust

**The station timestamps every frame from its own clock, at the moment the bytes arrive, and ages
everything against that stamp. No time a vehicle reports is used for anything.**

That is the rule MCS-002 rests on, and the rest of this section is why it is built the way it is
rather than merely intended.

**The receipt is taken before decoding, not after.** Ingest is two-phase — the clock is read when a
frame arrives, the frame is decoded, and the reading is then exchanged for a stamped frame. Reading
the clock after decoding would fold the decode cost invisibly into the recorded age of the data;
this way it is measured instead (MCS-005). A receipt can be exchanged exactly once.

**A vehicle's own time is carried as data and ages nothing.**
`GLOBAL_POSITION_INT.time_boot_ms` is decoded and used by nothing. This is structural rather than
disciplinary: what an adapter produces is a *report*, which has no time field for one to be read
from, and only the ingest boundary can turn a report into a stamped frame. There is no expression
available to an adapter that stamps a frame with a vehicle's clock. A vehicle whose boot counter is
wrong, whose GPS time is unset, or which is deliberately lying cannot age its own data.

**Ages are monotonic, not a subtraction of two calendar readings.** An age is elapsed time between
two readings of the station's monotonic clock. Wall time steps: an NTP correction of a minute
backwards would otherwise take a minute off every vehicle's age at once, and render a fleet that
stopped reporting ten minutes ago as live again — HAZ-01 arriving from the station's own clock.

**The consumer's clock does not participate either.** The station sends the *answer* — a state and
an age in milliseconds — rather than the ingredients for one. A browser thirty seconds out would
otherwise render a live aircraft as lost or, far worse, a lost one as live.

**A negative age is rejected rather than clamped.** A frame cannot arrive in the future; if one
appears to have, two different clocks are in play, and the clamp a reasonable person would write —
to zero — reports the vehicle as live. Silence answered with "everything is fine" is precisely the
hazard.

### The two thresholds

| State | Silence | Where the number comes from |
| --- | --- | --- |
| `Live` | under 3 s | — |
| `Stale` | 3 s or more | **Sourced.** 3× the slowest telemetry period this station is built against (1 Hz), which is what separates network jitter from link loss: a vehicle at 1 Hz must miss three consecutive reports to reach it, and the two or three datagrams a busy link drops in a row do not. |
| `Lost` | 15 s or more | 5× stale. **The multiplier is notional** — the construction is sourced, but nothing measured says five rather than four or eight. It is bounded from above by something real: a vehicle must reach `Lost` well inside the forty seconds a console waits before treating the stream itself as dead, or "one aircraft has gone quiet" and "the station has stopped talking to me" become the same picture. |

Both are constants rather than configuration. One sourced number is defensible; a settings surface
invites a deployment where the mitigation for the worst hazard in the system is quietly switched
off, and *"which station was that configured on?"* is not a question an operator should have to ask
about a marker's colour.

**These are states of the data, not of the vehicle.** Nothing here concludes that an aircraft has
crashed, landed or gone home. `Lost` says the station has not heard from it for long enough that its
last known position should no longer be acted on. A vehicle can be flying perfectly while its
telemetry is `Lost`, and that is exactly the case an operator has to be able to see.

**Staleness is not the link status the vehicle reports.** `linkStatus` is the vehicle's claim about
its own radio, made in a frame that by definition arrived; `state` is the station's observation of
silence. The last frame before a link dies almost always says `Healthy`. Neither is derived from the
other.

---

## 3. Units and references

Conversion happens once, at the adapter boundary. Everything above it is metric, degrees, and
seconds.

| Quantity | On the wire | At the boundary | Notes |
| --- | --- | --- | --- |
| Latitude, longitude | `int32`, degrees × 1e7 | signed decimal degrees, WGS-84 | ±90 / ±180. Both longitude endpoints are accepted; −180 and +180 name the same antimeridian. |
| Altitude | `int32` millimetres | metres, **paired with `Msl`** | Taken from `GLOBAL_POSITION_INT.alt`. The reference is not on the wire — it is in the field's name — and this is the boundary where it stops being implicit (MCS-004). |
| Ground speed | `float` m/s | m/s | Finite and non-negative. No upper bound: there is no defensible ceiling, and an invented one would refuse a legitimate report from some later airframe. |
| Heading | `int16` degrees | degrees, normalised into [0, 360) | Senders differ on 0–359 versus ±180; both land in the same place. |
| Battery | `int8` percent | percent, 0–100, or `null` | A percentage, never a 0–1 fraction. `-1` on the wire means unmeasured and travels onward as `null`. |

Three of these carry a trap worth naming.

**Heading is degrees clockwise from _true_ north, and the station applies no declination
correction.** It takes `VFR_HUD.heading` as sent. A vehicle reporting magnetic heading is therefore
displayed as if it were true, and the whole picture rotates by the local declination — silently,
because every individual value is plausible. If your autopilot reports magnetic, correct it before
it reaches the station.

**Heading is where the nose points, not the direction of travel.** Course over ground is a different
quantity, and in any wind it differs. MCS-001 asks for heading.

**An altitude is never a bare number.** It travels as a value and its reference together, in one
object, both on the wire out (§5) and in the station's own model. A consumer that receives a number
has no way to ask what it is above, and the day MSL↔AGL conversion arrives there would be no way to
tell which stored values needed it.

---

## 4. What is rejected, and loudly

**Reject, never clamp.** A clamped 200% battery renders as a believable 100% and the operator never
learns the adapter is broken. Rejection at the boundary makes a broken sender look broken, which is
the only condition under which it gets fixed. The same reasoning refuses a thirteenth vehicle,
drops the *oldest* frames from a full subscriber queue, and refuses a negative age (§2). It is
HAZ-01 in [`what-can-go-wrong.md`](what-can-go-wrong.md) that all of this is arranged against: a
console confidently showing a picture that is not current.

Rejection happens at three granularities, and the difference matters to anyone diagnosing a link.

**One frame, counted.** Framing discards nothing quietly and logs nothing either — every discard
increments a counter, because a ground station sees dozens of message types it has no decoder for
and a log line each would train whoever is watching to ignore the stream. Counters are published
periodically to the log as `parsed`, `crcFailures`, `resyncedBytes`, `unknown`, `v1`, `signed` and
`incompatFlags`.

- a failed checksum — one byte is discarded and the scan resumes, not the buffer, so the good frame
  behind a corrupt one survives
- a message id the station has no decoder for
- a MAVLink v1 frame
- a frame carrying a signature block
- a frame declaring an incompatibility flag this parser does not implement, which the format's own
  rule says must be dropped

**One message, counted.** Past framing, a decoded message with a field outside its range is dropped
and the vehicle's previous state stands:

- latitude outside ±90 or longitude outside ±180 — reachable from a sender with its scaling wrong,
  and a longitude of 200 is the kind of value that renders *somewhere* rather than failing
- a battery percentage outside 0–100 that is not the wire's own `-1`; the last representable reading
  stays, because a corrupt message must not erase what a merely absent one does not
- a ground speed that is negative or not finite

**One vehicle, thrown.** The station accepts twelve vehicles. A thirteenth is refused with a named
exception rather than dropped silently — a fleet view that is quietly incomplete is undiagnosable,
where a refusal names the problem (MCS-010). The link survives it: the refusal is counted as
`vehiclesRejected` and the twelve that fit keep updating.

**And an altitude with no declared reference cannot be constructed at all** (MCS-004). This is a
type-level rule rather than a check that could be forgotten at a call site.

### What is deliberately *not* rejected

**A payload longer than the message definition.** v2 extension fields append to a definition and are
excluded from the `CRC_EXTRA` seed by design, precisely so that a newer sender's frame still
validates against an older receiver's table — and the format's instruction to that receiver is to
read the fields it knows and ignore the rest. Rejecting the longer form would break exactly one
message type against current firmware (`SYS_STATUS` has grown extension fields) and leave every
other one working: the quiet, per-message failure this codec is arranged to prevent.

**A payload shorter than the definition.** v2 strips trailing zero bytes on the wire, so how short a
message arrives depends on its *values* — a vehicle at exactly zero altitude sends a shorter
position report than the same vehicle at 120 m. The zeros are restored at the framing boundary.

**An implausibly fast vehicle.** Reject what is impossible, not what is merely surprising.

---

## 5. What the station serves

HTTP, no authentication, JSON in camelCase, enums as names rather than numbers. Two telemetry
endpoints and two probes. In the deployed stack the console reaches all of them through the web
container's `/api` proxy, so a browser talks to one origin (MCS-009).

### `GET /api/vehicles`

The latest frame from every vehicle the station knows about, each with its age and state as of the
request. `200` and `[]` when nothing is flying — never `404`, because "nothing is flying" is a valid
answer that a console has to render. A vehicle the station has never heard from is simply absent: it
has no state because there is nothing to be current or stale about.

### `GET /api/telemetry/stream`

Server-sent events. Two event types, and **both carry a JSON array of vehicles** — one element for a
single report, the whole fleet (possibly empty) for a tick. The event name is the discriminator, so
the payload needs none.

| Event | When | Contents |
| --- | --- | --- |
| `telemetry` | a vehicle reported | that one vehicle |
| `fleet` | every second, whether or not anything reported | every known vehicle, re-evaluated against the station clock |

**The `fleet` tick is what makes silence reportable.** A vehicle that has gone quiet sends nothing,
by definition, so a stream carrying only reports could never say that it went quiet — a consumer
would go on showing the last state it was told about, for exactly the vehicle its operator most
needs to know about. The tick is scheduled rather than idle-triggered for the same reason: a fleet
of twelve where one has stopped is never silent.

Its period is one second, derived as a third of the staleness threshold rather than picked, so a
vehicle crossing into `Stale` is on the wire within a third of the window that defines the crossing.
It also keeps an idle connection alive through a proxy, which is now a side effect rather than its
job; the response carries `X-Accel-Buffering: no`, without which nginx buffers the stream into
bursts that read as a broken feed.

A subscriber that falls behind loses its **oldest** frames, not its newest. This is a state stream,
not an event log: a slow client should be shown the present, not a smooth and complete replay of the
past.

**What a consumer owes in return.** The station cannot report its own silence any more than a
vehicle can, so a consumer must watch for the tick and stop presenting the fleet as current when
three of them have been missed (MCS-013). Measuring *how long it has itself been waiting* is the one
age a consumer is the only witness to; computing a *vehicle's* age from `receivedAtUtc` against its
own clock is the thing it must not do.

### The vehicle object

| Field | Type | Notes |
| --- | --- | --- |
| `vehicleId` | string | `MAV-001` for MAVLink system 1 |
| `latitudeDegrees`, `longitudeDegrees` | number | signed decimal degrees, WGS-84 |
| `altitude` | object | `{ "meters": number, "reference": "Msl" \| "Agl" \| "Hae" }` — never a bare number |
| `groundSpeedMetersPerSecond` | number \| **null** | |
| `headingDegrees` | number \| **null** | degrees clockwise from true north, [0, 360) |
| `batteryPercent` | number \| **null** | 0–100 |
| `linkStatus` | `"Healthy"` \| `"Degraded"` \| `"Lost"` | the *vehicle's* claim about its radio; always `Healthy` today |
| `state` | `"Live"` \| `"Stale"` \| `"Lost"` | the *station's* judgement — §2 |
| `ageMilliseconds` | integer | since receipt, by the station clock |
| `receivedAtUtc` | ISO 8601 | when the station received the frame |

Everything above `state` is a claim by the vehicle; `state`, `ageMilliseconds` and `receivedAtUtc`
are what the station observed about it. They travel in the same flat object on purpose, so that a
consumer cannot render one without the other.

**The three nullable fields are written as `null`, never omitted and never zero.** A vehicle reports
position and velocity in different messages at different rates, so the station can know exactly
where something is and not know which way it is pointing. Zero is a speed, a bearing and a flat
battery respectively — substituting one draws a vehicle stationary, pointing true north, and about
to be aborted for. A consumer must render absence as absence; the console draws a dash, and a marker
with no nose.

### The wire, as it actually appears

Captured from a running stack; `data:` is one line on the wire, wrapped here to fit the page.

```
event: telemetry
data: [{"vehicleId":"MAV-001","latitudeDegrees":34.733993,"longitudeDegrees":-86.5847606,
        "altitude":{"meters":340,"reference":"Msl"},"groundSpeedMetersPerSecond":22,
        "headingDegrees":90,"batteryPercent":99,"linkStatus":"Healthy","state":"Live",
        "ageMilliseconds":92,"receivedAtUtc":"2026-08-13T18:35:11.6566293+00:00"}]
```

`GET /api/vehicles` returns the same objects, as an array, without the SSE framing.

Stopping the aircraft and reading the `fleet` ticks alone is the whole of §2 on the wire. Each tick
carries the full object above; excerpted here to the three fields that tell the story, the position
and `receivedAtUtc` freeze while the station's own judgement keeps moving:

```
event: fleet   "state":"Live", "ageMilliseconds":2498,  "receivedAtUtc":"...T18:34:26.5603319+00:00"
event: fleet   "state":"Stale","ageMilliseconds":3504,  "receivedAtUtc":"...T18:34:26.5603319+00:00"
event: fleet   "state":"Stale","ageMilliseconds":14573, "receivedAtUtc":"...T18:34:26.5603319+00:00"
event: fleet   "state":"Lost", "ageMilliseconds":15583, "receivedAtUtc":"...T18:34:26.5603319+00:00"
```

### `GET /health` and `GET /health/db`

```
{"status":"Healthy"}
{"status":"Healthy","expectedSchemaVersion":1,"schemaVersion":1}
```

`/health` is liveness and **runs no checks at all**, deliberately: a liveness probe that fails
because a dependency is down asks an orchestrator to restart a process that is working.
`/health/db` is readiness, is allowed to go red, and reports the schema version the database has
applied against the one this build was compiled for.

---

## 6. What this contract does not cover

| Not here | Why |
| --- | --- |
| Commands — arm, upload, acknowledge | Nothing commands a vehicle yet. The adapter contract has no command member, because a signature with no caller is a guess. |
| Any second vehicle protocol | One adapter exists. When there are two, what they share will be described from both rather than derived from this one. |
| Message signing | Not implemented and not planned. Signed frames are recognised, rejected and counted rather than misparsed. A half-implemented authentication mechanism is worse than none, because it invites the assumption that frames were authenticated. |
| MSL ↔ AGL conversion | Needs terrain elevation the station does not hold. Until it does, a value is consumed against the reference it arrived with. |
| Authentication and authorisation | There is none. Anything the API exposes is exposed to anyone who can reach it — and the MAVLink port accepts datagrams from anyone who can reach *it*, which with a twelve-vehicle cap and no reclaim means a sender can occupy the whole fleet with invented system ids. Bind a specific interface on any network you do not control. |
| Durable telemetry history | A stated non-goal. Telemetry is bounded and in memory: 12 vehicles, 600 frames each. |
