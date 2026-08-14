# overview.md

What this system is, what it is for, and how its pieces relate. The README sells it and shows it
running; this explains it to someone who has decided to understand it before reading
[`interfaces.md`](interfaces.md) or [`requirements.md`](requirements.md).

It describes the design, not the tag. Where a claim is about what has been *built*, the README's
"What's real at this tag" table is the authority, and this file points at it rather than
duplicating it.

---

## 1. What it is

A ground control station for uncrewed vehicles: it receives telemetry from vehicles it does not
control the code of, keeps a bounded recent picture of each one, and puts that picture in front of
an operator who has to decide something about it.

The vehicles are simulated. It does not connect to real aircraft, it implements no standard, and
nothing here has been integrated against software anyone else wrote.

The operator's question is not "what is vehicle 7's battery." It is **"which one needs me, and is
what I am looking at still true."** Almost every design decision below falls out of taking the
second half of that seriously.

### The hazard it is arranged against

[`what-can-go-wrong.md`](what-can-go-wrong.md) lists eight things that can go wrong. HAZ-01 is
first because it is the one the rest of the system is shaped by:

> The console shows the operator a picture they believe is current, and it isn't.

A control station that stops receiving looks exactly like a control station watching a quiet fleet.
Nothing about the failure is visible in the thing that fails — which is why "how old is this" is a
question this system answers everywhere, and answers with its own clock.

---

## 2. The shape

```
                    a vehicle, over a wire
                             │
                    ┌────────▼────────┐
                    │  Mcs.Adapters   │   protocol: MAVLink v2 framing, a UDP link
                    └────────┬────────┘
                             │  VehicleTelemetry
                    ┌────────▼────────┐
                    │    Mcs.Core     │   domain: ingest, currency, the bounded store
                    └────────┬────────┘
                             │  TelemetryFrame
                    ┌────────▼────────┐
                    │     Mcs.Api     │   host: HTTP, SSE, Postgres, logging
                    └────────┬────────┘
                             │  JSON over HTTP — interfaces.md §5
                    ┌────────▼────────┐
                    │      web/       │   console: MapLibre, offline basemap, fleet panel
                    └─────────────────┘

   Mcs.Simulator — a separate process, out of frame, transmitting at the adapter over UDP
```

Dependencies run inward only. `Mcs.Adapters` and `Mcs.Api` reference `Mcs.Core`; nothing references
them back.

**`Mcs.Core` has no package references at all**, and its project file being empty is the whole of
the enforcement. No logger, no web framework, no database driver, no hosting abstraction. A core
that has grown any of those makes the claim in §3 undefendable, and an empty `.csproj` is a thing a
reviewer can check in four seconds — which a written rule is not.

`Mcs.Simulator` is a **separate process in a separate container**, and that is the load-bearing part
of it: in-process it would have exercised none of the socket, none of the framing and none of the
datagram boundaries, which is to say none of what the adapter and the codec exist for. It shares the
station's frame *writer* and nothing else; the payload writers are written independently of the
station's readers, so a field at the wrong offset in one cannot cancel against the same mistake in
the other.

---

## 3. The core/adapter split

A vehicle type is a protocol. Everything that knows one is an adapter, and the contract between an
adapter and the station is:

```csharp
public interface IVehicleAdapter
{
    string Name { get; }
    Task RunAsync(CancellationToken stoppingToken);
}
```

That is the whole interface, and three absences in it are deliberate:

- **No command member.** Commanding is a later capability, and a signature with no caller is a
  guess about what it will need.
- **No statistics member.** Each adapter counts what its own link can go wrong in. A common counter
  shape would be invented rather than observed — the MAVLink link's `crcFailures` and `resyncedBytes`
  mean nothing to a link that frames differently.
- **Not `IHostedService`.** That is a hosting type, and the core takes no packages. The hosting
  dependency lives entirely in `Mcs.Api`, in one class that runs every registered adapter under one
  `Task.WhenAll`.

It was **derived from two implementations rather than one** — the MAVLink link, and a hardcoded feed
that used to invent telemetry in the host — so it describes what telemetry sources have in common
rather than what MAVLink happens to need. The hardcoded feed has since been deleted, and the
interface has deliberately not been re-derived from the survivor: a contract narrowed to one link is
no longer vehicle-agnostic.

**This is the architectural claim most easily asserted without proof, and it is
[MCS-007](requirements.md#mcs-007), which the table publishes as *not verified*.** The evidence is a
diff that does not exist yet: the commit adding a second, genuinely different vehicle type, with a
diffstat showing no file under `src/Mcs.Core` changed. Anything short of that is the claim restated.

### What the adapter is not allowed to decide

A faulting adapter is left to reach the host and stop it, because an adapter that died quietly is a
console that has stopped updating and does not say so. And **nothing in an adapter decides a vehicle
is gone** — silence is not a decision an adapter is in a position to make, and it is measured
against the station clock elsewhere.

---

## 4. Two types, because two different things are being said

The distinction the domain is built on:

| | `VehicleTelemetry` | `TelemetryFrame` |
| --- | --- | --- |
| Holds | what a vehicle **claimed** | that claim, plus `ReceivedAtUtc` |
| Whose clock | nobody's — there is no time field | the **station's** |
| Who can make one | any adapter | nothing outside `Mcs.Core` |

Which object you are holding tells you whether what is in it is trustworthy. `TelemetryFrame`'s
constructor is `internal`, and a single-use ingest receipt is its only caller, so **outside the core
there is no expression that yields a frame.** Stamping a frame with a vehicle's own clock is not a
mistake that is available to make; it is code that does not compile.

`VehicleTelemetry` has no time field at all, which is the same rule enforced from the other side. A
vehicle's own timestamp is untrusted — see [`interfaces.md` §2](interfaces.md#2-time-and-trust) —
and the cheapest way to keep an untrusted number from being used is not to carry it.

Values are **rejected, never clamped** ([MCS-012](requirements.md#mcs-012)). A clamped 200% battery
renders as a believable 100% and the operator never learns the adapter is broken. The three nullable
fields follow the same rule from the other direction: absence is written as absence, because a zero
speed is a vehicle at rest, a zero heading is a nose pointing north, and a zero battery is the one
number that would make an operator abort.

---

## 5. The station clock, and why ingest has two phases

Every frame is stamped by the station at the moment of arrival
([MCS-005](requirements.md#mcs-005)). Ingest is deliberately split:

```
ingest.BeginReceive()   ─ the clock is read here, at arrival
      decode            ─ framing, payload, whatever the protocol costs
receipt.Complete(t)     ─ the frame is minted, carrying the arrival time
```

One call would have been simpler and would have stamped the frame *after* decoding, folding the
decode cost invisibly into the recorded age of the data. Two calls make that cost a measured number
(`IngestDelay`) instead of a silent one. A receipt completes exactly once.

Time comes from an injected `TimeProvider` throughout — there is no `DateTimeOffset.UtcNow` anywhere
in the system, and durations use `GetTimestamp`/`GetElapsedTime` rather than subtracting wall-clock
readings. That is what lets currency be tested against a fake clock instead of against `Thread.Sleep`,
and it is why a wall-clock correction does not move a frame's age.

---

## 6. Currency: live, stale, lost

One derivation, two thresholds, computed in the core from the station clock:

| State | Reached at | On screen |
| --- | --- | --- |
| **live** | — | solid dart, pointed along its heading |
| **stale** | 3 s of silence | hollow amber dart, still pointed, carrying an **age chip** |
| **lost** | 15 s of silence | dashed ring, **no heading at all**, dimmest on the map |

Three seconds is 3× the slowest configured telemetry period, which is what distinguishes jitter from
link loss ([MCS-002](requirements.md#mcs-002)). The fifteen is 5× that, and **the multiplier is
notional** — the construction is sourced, the number five is not, and it says so where it is defined
rather than hardening into a fact by being repeated.

Three properties of this that are easy to lose and expensive to get back:

- **One derivation, two surfaces.** The map marker and the panel row are rendered from the same
  computed state, so a marker and its row cannot disagree. Two derivations is how a console ends up
  saying two things about one aircraft.
- **No state is carried by colour alone.** Solid dart, hollow dart and dashed ring stay apart in
  greyscale. A stale state that is "the same marker but amber" fails for about 8% of men and fails
  entirely in a screenshot pasted into a report.
- **Lost loses the heading.** The station knows where that aircraft *was* pointing some minutes ago.
  A confident nose on a dead track is the display asserting something it cannot support.

The reasoning, the contrast ratios and what the built console survived are in
[`notes/console-design.md`](notes/console-design.md).

### The case the station cannot answer

Everything above is the station's judgement about a *vehicle*. It says nothing about the station
having stopped answering — and a console that goes on drawing the last snapshot as live is HAZ-01
happening, with the added cruelty that it looks completely normal.

So the console watches the station exactly the way the station watches a vehicle: the station emits
a fleet tick every second whether or not anything moved, and three missed ticks means every age is
unknown and growing ([MCS-013](requirements.md#mcs-013)). This is deliberately far shorter than the
forty seconds after which a dead connection is thrown away and reopened. A console patient about
*reconnecting* is fine; a console patient about *saying it has stopped hearing anything* is the
hazard.

---

## 7. The bounded store

`ITelemetryStore` is bounded in both directions, and the bounds are **system-wide commitments rather
than store details**:

| Bound | Value | Why that number is everywhere |
| --- | --- | --- |
| Vehicles | 12 | the fleet panel is sized so twelve rows fit without a scrollbar, and the latency budget was measured at twelve |
| Frames per vehicle | 600 | recent history, not an archive |
| Subscriber queue | 256, drop-**oldest** | a state stream, not an event log |

Reject, never clamp, applied to admission: a **thirteenth vehicle is refused with a named
exception**, not dropped ([MCS-010](requirements.md#mcs-010)). A silently dropped vehicle is a fleet
view that is quietly wrong; a refused one is a diagnosable misconfiguration. Slots are never
reclaimed on the store's own initiative, which is the deliberate trade — and the exposure it creates
on an open link is written down in the README's limitations rather than left to be discovered.

The same reasoning runs the other way for a slow subscriber. Dropping its *newest* frames would show
it a smooth and complete replay of a past it has no use for; dropping the oldest shows it the
present, which is the only thing a state stream owes anyone.

Writes are a single critical section — resolve-or-admit, append and fan-out under one gate, with
readers never taking it. Splitting that write produces duplicate delivery or silent loss depending
on where you split it; all three placements are worked through in
[`notes/stuck.md`](notes/stuck.md).

---

## 8. Where persistence applies, and where it does not

There is a Postgres, it is migrated on startup, and **it holds no telemetry.**

That is a design decision, not a gap. Telemetry lives in the bounded in-memory store above; a week
of position reports is a stated non-goal. What *will* be durable is the set of things an operator is
accountable for: mission plans, commands, overrides, and alert acknowledgements. Those are decisions
with consequences, and a decision nobody can reconstruct afterwards is not much of a decision.

What the database mechanism does today is prove itself:

- Migrations are numbered `.sql` files **embedded into the API** and applied on startup, each in its
  own transaction, under a Postgres advisory lock so two instances starting together cannot both
  apply the same one.
- **A migration is immutable once shipped.** Each is recorded with a SHA-256, and a checksum that no
  longer matches stops the station rather than being logged and stepped over
  ([MCS-011](requirements.md#mcs-011)). The fix for a needed change is always a new numbered file. A
  schema that has quietly drifted from the code is the same problem as a console showing a position
  that is no longer true, one layer down — it fails at the first write that matters rather than at
  startup where someone is watching.
- **Without a database the station does not start.** It retries for thirty seconds, logs why, and
  exits non-zero. A station that came up anyway would be reporting itself healthy while something it
  depends on was missing.
- `/health` is liveness and **runs no checks at all**; `/health/db` is readiness and is the one
  allowed to go red. It reports the applied schema version against the version the running build was
  compiled for, so "is this the database this build expects" has an answer rather than an assumption.

---

## 9. The console

One screen: an offline basemap, the fleet on it, and a panel listing every vehicle with all six of
the fields the requirements name — for *every* vehicle, not for the selected one, because a
requirement satisfied only where the operator last clicked is not satisfied.

Two properties worth knowing:

**Nothing it loads comes from another origin** ([MCS-009](requirements.md#mcs-009)). The basemap and
everything it references are served from the same place as the page, there is no tile CDN and no API
key, and a `default-src 'self'` policy makes a dependency that starts reaching for one fail loudly.
It has no labels at all, because a style with a text field fetches glyph files from somewhere. Two
failures are being avoided at once: the console stops working offline, and every tile request tells
a third party where the fleet is.

**The marker steps rather than glides.** Smooth motion means interpolating between frames, which
puts the vehicle at a position it never reported — a nicer-looking console that is lying about where
something is. At four position reports a second you are watching the station show exactly what it was
told; on a slower link the steps get coarser, and that is the link being visible rather than a
defect.

---

## 10. How a claim in here gets checked

[`requirements.md`](requirements.md) states what the station is required to do, how each requirement
is verified, and — where it is not — why not. It ships at whatever number is true: rows that are not
verified say so in their own row, and a requirement leaves the table by getting a line in its
`Removed` section rather than by being deleted.

That is checked rather than maintained. A CI job reads the table on every push, matches each row
against the tests that **reported passing** and the evidence links that still resolve, and fails the
build when they disagree. A tag on a skipped test satisfies every naive check and proves nothing,
which is why outcomes are read from test *results* and never from test source.

---

## 11. What is not here

The README's table is the authority on what runs at any given tag, and its limitations section is
the authority on what is stubbed, notional or missing. In outline: nothing commands a vehicle
yet, nothing evaluates an alert, there is no authentication, fault flags are stubbed at both ends,
and no adapter has ever been pointed at a real autopilot.

The two documents that will join this set are `conflict-thresholds.md`, when there is deconfliction
to derive thresholds for, and whatever the second adapter's arrival makes necessary.
