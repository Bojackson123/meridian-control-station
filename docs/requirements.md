# requirements.md

Thirteen requirements, each with the reasoning behind it, how it is verified, and — where it is
verified — what the evidence is.

**The table ships at whatever number is true.** Two of these are not verified and say so in their
own row, with the reason and what would change it. A requirements table is only worth reading if
the unverified rows are as easy to find as the verified ones, which is why the status is in the
index rather than at the bottom of a section.

Where a requirement exists because of a hazard, the hazard is named: the arrows run from
[`what-can-go-wrong.md`](what-can-go-wrong.md) to here, and most of this table is the mitigation
column of that one, written out properly.

## How to read it

**Type.** `[FUN]` a function the console performs · `[SAF]` there to prevent a hazard ·
`[INT]` a property of an interface or a boundary · `[OPS]` a property of running the thing.

**Method.** The four that mean something distinct here:

- **Test** — a passing assertion proves it. The row names the tests.
- **Inspection** — a human looking at the artifact proves it. The row names the artifact.
- **Demonstration** — running the thing proves it. The row names the run.
- **Analysis** — the argument proves it, and the argument has to be written down somewhere
  linkable. A row claiming Analysis with no link is a row claiming nothing.

**Numbers carry their sources.** Where a number is a judgement rather than a measurement, it says
so on the spot — `LostAfter`'s multiplier is the clear case, and labelling it notional is what
stops it from hardening into a fact by being repeated.

**Nothing is removed to make this green.** Removing a requirement takes a one-line entry in the
[removed](#removed) section with the reason. The section exists before there is any incentive to
use it, which is the entire mechanism.

---

## The table

| | Requirement, in short | Type | Method | Status |
| --- | --- | --- | --- | --- |
| [MCS-001](#mcs-001) | Six fields per vehicle, within 1 s of receipt at the station | FUN | Test | **verified** — measured at twelve |
| [MCS-002](#mcs-002) | Telemetry is stale after 3 s of silence, by the station clock | SAF | Test | **verified** |
| [MCS-003](#mcs-003) | A stale vehicle renders distinctly, and shows its age | SAF | Test + Inspection | **verified** |
| [MCS-004](#mcs-004) | An altitude with no declared reference is rejected | INT | Test | **verified** — one adapter, not two |
| [MCS-005](#mcs-005) | Every frame is stamped with the station's clock at ingest | INT | Test | **verified** |
| [MCS-006](#mcs-006) | No arm until the vehicle acknowledges the approved plan | SAF | Test | **not verified** — not built |
| [MCS-007](#mcs-007) | A new vehicle type needs no change to `Mcs.Core` | INT | Inspection | **not verified** — needs a second adapter |
| [MCS-008](#mcs-008) | The whole system stands up from one command | OPS | Demonstration | **verified** |
| [MCS-009](#mcs-009) | Nothing the console loads comes from another origin | INT | Test + Inspection | **verified** |
| [MCS-010](#mcs-010) | Twelve vehicles, and the thirteenth is refused loudly | SAF | Test | **verified** |
| [MCS-011](#mcs-011) | The station will not start against a drifted schema | INT | Test | **verified** |
| [MCS-012](#mcs-012) | Out-of-range values are rejected, never clamped | SAF | Test | **verified** |
| [MCS-013](#mcs-013) | A console that loses the station stops claiming the fleet is current | SAF | Test | **partly verified** — the rendering, not the detection |

---

## MCS-001

> The console shall display position, altitude, ground speed, heading, battery level and link
> status for each connected vehicle, updating each field within 1 second of frame receipt at the
> station.

**Type** FUN · **Method** Test

**Rationale.** One second at a 1 Hz minimum telemetry rate keeps the display within one reporting
period of the newest data. All six fields for *every* vehicle, not for the selected one: a
requirement satisfied only where the operator last clicked is not satisfied, which is why the panel
shows two lines per row rather than an expander.

**Evidence.** The fields: `web/src/panel/VehicleRow.test.tsx`, *shows all six of MCS-001 fields and
no chip*. The budget was measured rather than assumed, at twelve vehicles, in two halves —
0.53 ms median through the station and 11.7 ms median to the panel, worst cases 7.05 ms and
40.3 ms, about 5% of the budget between them. Method and the parts of the path that were *not*
measured are in `evidence: notes/latency-at-twelve.md`. The station half re-measures itself in CI
(`TelemetryLatencyTests`); the browser half was measured by hand and would need re-measuring after
any change to how the console renders.

---

## MCS-002

> The console shall mark a vehicle's telemetry as stale when no frame has been received for that
> vehicle for 3 seconds, measured against the station clock.

**Type** SAF · **Method** Test · **Mitigates** HAZ-01, HAZ-04

**Rationale.** Three seconds is 3× the slowest configured telemetry period (1 Hz), which is what
distinguishes network jitter from link loss: a vehicle at 1 Hz must miss three consecutive reports
to reach it, and the two or three datagrams a busy link drops in a row do not. **Station clock, not
vehicle clock** — vehicle time is untrusted ([`interfaces.md` §2](interfaces.md#2-time-and-trust)),
and `VehicleTelemetry` has no time field for one to be read from.

A second threshold, `LostAfter`, sits at 5× that. **The multiplier is notional**: the construction
is sourced — a multiple of the slowest configured period — but nothing measured says five rather
than four or eight. It is bounded from above by something real, in that a vehicle must reach lost
well inside the forty seconds the console waits before treating the stream itself as dead.

**Evidence.** `tests/Mcs.Core.Tests/TelemetryCurrencyTests.cs` — `StaleAfter_IsThreeSeconds`,
`FromAge_PutsTheBoundaryWhereTheRequirementDoes`,
`LostAfter_IsFiveTimesStale_AndInsideTheConsolesDeadStreamTimeout`,
`Of_AWallClockCorrection_DoesNotMoveTheAge`, `Of_IgnoresEverythingTheVehicleClaimed`.

---

## MCS-003

> While a vehicle is stale, the console shall render its last known position in a visually distinct
> stale state that includes the age of the data, and shall not render it in the live-vehicle state.

**Type** SAF · **Method** Test + Inspection · **Mitigates** HAZ-01, HAZ-04

**Rationale.** The display must never present dead data as current. "Visually distinct" is a
decision someone has to make rather than a property that falls out of implementing, so it was made
once, on paper, before any of it was built: every state differs from every other in at least two
channels and never in colour alone, and the age is on screen rather than in a tooltip, because an
operator scanning twelve vehicles hovers over none of them.

**Evidence.** Test: `web/src/vehicles/appearance.test.ts` — *draws a stale vehicle hollow, still
pointed, and carrying its age*, *draws a lost vehicle as a dashed ring with no heading at all*,
*separates every pair of states in more than one channel, and never in colour alone*; and
`web/src/panel/VehicleRow.test.tsx` for the same states in the panel. Inspection: the state
language, its contrast ratios and what the built console survived —
`evidence: notes/console-design.md`.

---

## MCS-004

> The adapter interface shall reject any position report that does not declare an altitude
> reference (MSL, AGL or HAE).

**Type** INT · **Method** Test · **Mitigates** HAZ-05

**Rationale.** An implicit altitude reference is the classic two-vehicle integration failure, and
rejection at the boundary makes the mistake loud instead of leaving it to be discovered by a
vehicle flying at somebody else's datum. `Altitude` pairs the number with its reference in one
type, so the requirement is met at every call site at once rather than by remembering to check. The
units and references this obliges a vehicle to supply are published in
[`interfaces.md` §3](interfaces.md#3-units-and-references).

**Evidence.** `tests/Mcs.Core.Tests/AltitudeTests.cs` —
`FromMeters_UndeclaredReference_ThrowsArgumentOutOfRange` and the `FromFeet` twin;
`tests/Mcs.Core.Tests/VehicleTelemetryTests.cs` —
`Create_DefaultAltitude_ThrowsArgumentExceptionNamingAltitudeAndMcs`.

**Where this is weaker than it reads.** The original wording said "both adapters, both directions".
There is one adapter, and telemetry only travels inbound, so what is verified is the boundary type
and the one decode path through it (`MavlinkTelemetryDecoder` reads `GLOBAL_POSITION_INT`'s MSL
altitude and declares it). The claim that the boundary holds for an adapter written later is not
evidence this table can offer yet.

---

## MCS-005

> The station shall assign every inbound telemetry frame a receipt timestamp from the station clock
> at the moment of ingest.

**Type** INT · **Method** Test · **Mitigates** HAZ-01, HAZ-04

**Rationale.** A single trusted time base, for MCS-002 now and for conflict windows later. Ingest is
two-phase — `BeginReceive` → decode → `Complete` — so the clock is read at arrival and the decode
cost is *measured* rather than folded invisibly into the recorded age of the data. The rule as a
vehicle integrator meets it is [`interfaces.md` §2](interfaces.md#2-time-and-trust).

**Evidence.** `tests/Mcs.Core.Tests/TelemetryIngestTests.cs` —
`Complete_StampsTheFrameWithArrival_NotWithTheTimeTheDecodeFinished`,
`BeginReceive_TakesTheArrivalTimeFromTheInjectedClock`,
`Complete_CalledTwice_ThrowsInvalidOperationExceptionCitingMcs`; and
`tests/Mcs.Core.Tests/TelemetryFrameTests.cs` —
`Assembly_ExposesExactlyOnePublicMemberThatTurnsATelemetryIntoAFrame`, which is the one that makes
this structural: outside the core there is no expression that yields a frame, so an adapter cannot
stamp one with a vehicle's clock.

---

## MCS-006

> The console shall not transmit an arm command for a mission until the vehicle has acknowledged a
> plan whose checksum matches the plan the operator approved.

**Type** SAF · **Method** Test · **Mitigates** HAZ-02

**Rationale.** A vehicle executing a plan the operator did not approve — a corrupted upload, a
partial upload accepted as whole, a stale plan cached on the vehicle — is an uncommanded trajectory
with nothing unusual on screen.

**Not verified, and not built.** Nothing here commands a vehicle: `IVehicleAdapter` has no command
member, so the hazard is out of reach rather than mitigated. It is in this table anyway because it
is a baseline requirement whose *verification* is what is pending, and because the mitigation
should be settled before the capability that needs it exists rather than during it. The test that
will verify it includes the corrupt-checksum injection case.

---

## MCS-007

> Adding a new vehicle type shall require no modification to `Mcs.Core`.

**Type** INT · **Method** Inspection

**Rationale.** This is the architectural claim the whole layering exists to support, and the one
most easily asserted without proof. `Mcs.Core` has no package references at all and its project
file being empty is what enforces that; `IVehicleAdapter` was derived from two implementations
rather than one, so it describes what telemetry sources have in common rather than what MAVLink
happens to need.

**Not verified.** One of those two implementations has since been deleted, so what exists today is
one adapter and an interface that has deliberately not been re-derived from it. The evidence is a
diff that does not exist yet: the commit adding a second, genuinely different vehicle type, and its
diffstat showing no file under `src/Mcs.Core` changed. Anything less is the claim restated.

---

## MCS-008

> The full system shall stand up from a single command on a clean machine, with no accounts, no API
> keys and no network access after the images are built.

**Type** OPS · **Method** Demonstration

**Rationale.** "It runs on my machine with four terminals open" is the failure this is written
against, and the demo being one command is what makes the offline claim checkable by a stranger
rather than believable.

**Evidence.** `evidence: ../.github/workflows/ci.yml` — the `smoke` job bootstraps `.env`, brings
the compose stack up with `--wait`, and runs `tests/Mcs.System.Tests` against it with
`MCS_SMOKE_REQUIRED=1`, which turns "no stack is listening" from a skip into a failure. A smoke
suite that silently skips reports green for a run that asserted nothing, so the flag is the load-
bearing part of this row.

---

## MCS-009

> Nothing the console loads at runtime shall come from an origin other than the one serving it.

**Type** INT · **Method** Test + Inspection · **Mitigates** HAZ-08

**Rationale.** Two failures at once: the console stops working offline, and every tile request tells
a third party where the fleet is. The basemap is therefore bundled, has no `glyphs` or `sprite` key
and no labels at all — MapLibre fetches glyph files the moment a layer uses a text field.

**Evidence.** Test: `tests/Mcs.System.Tests/StationSmokeTests.cs` —
`Basemap_IsServedFromTheWebOrigin`. Inspection: the `default-src 'self'` policy in
`web/index.html`, which turns this from something you once checked in DevTools into something that
fails loudly; and a recorded observation of the running console at twelve vehicles —
**37 requests, one origin** — in `evidence: notes/latency-at-twelve.md`, taken during that
session.

**Where this is weaker than it reads.** The smoke test proves the basemap is served from the web
origin; it does not prove the *absence* of a request to somewhere else. The CSP is what makes the
absence structural, and the CSP is verified by inspection rather than by a test that watches a real
page load. A browser-driven check of the request list would be the thing to add.

---

## MCS-010

> The station shall accept telemetry from at most 12 vehicles, and shall reject a further vehicle
> loudly rather than dropping its frames.

**Type** SAF · **Method** Test · **Mitigates** HAZ-06

**Rationale.** Twelve is a system-wide commitment rather than a store detail: the fleet panel is
sized so twelve rows fit without scrolling, and the latency above is measured at twelve. A
thirteenth vehicle silently dropped is a fleet view that is quietly wrong; refused with a named
exception, it is a diagnosable misconfiguration. The same reasoning runs the other way for a slow
subscriber, which loses its **oldest** frames — this is a state stream, not an event log.

**Evidence.** `tests/Mcs.Core.Tests/InMemoryTelemetryStoreTests.cs` —
`Write_ThrowsWhenAFurtherVehicleWouldExceedTheCap`, `CapacityException_CarriesWhatTheFeedsLogLineNeeds`,
`Forget_RacedWithWritersAdmittingNewVehicles_NeverExceedsTheCap`,
`Subscriber_ThatFallsBehind_LosesTheOldestFramesAndKeepsTheNewest`.

---

## MCS-011

> The station shall refuse to start against a database whose applied migrations do not match the
> ones compiled into the running build.

**Type** INT · **Method** Test · **Mitigates** HAZ-07

**Rationale.** A schema that has quietly drifted from the code is the same problem as a console
showing a position that is no longer true, one layer down, and it fails at the first write that
matters rather than at startup where someone is watching. A migration is immutable once shipped;
the fix for a needed change is always a new numbered file.

**Evidence.** `tests/Mcs.Integration.Tests/SchemaMigrationTests.cs` —
`Apply_WhenAnAppliedMigrationHasBeenEditedSinceItShipped_Fails`,
`Apply_ByTwoInstancesStartingTogether_AppliesEachMigrationOnce`,
`Apply_ToADatabaseAheadOfThisBuild_ContinuesRatherThanRefusingToStart`; and
`tests/Mcs.System.Tests/StationSmokeTests.cs` —
`Readiness_ReportsTheSchemaTheStationMigratedTo`, which is the readiness probe answering "is this
the database this build expects" over HTTP.

---

## MCS-012

> Values outside their defined range shall be rejected at the ingest boundary, and never clamped
> into it.

**Type** SAF · **Method** Test · **Mitigates** HAZ-05, HAZ-04

**Rationale.** A clamped 200% battery renders as a believable 100% and the operator never learns the
adapter is broken. The same reasoning covers the three nullable fields: absence is not zero,
because a zero speed is a vehicle at rest, a zero heading is a nose pointing north, and a zero
battery is the one number that would make an operator abort.

**Evidence.** `tests/Mcs.Core.Tests/VehicleTelemetryTests.cs` — the `ThrowsArgumentOutOfRange`
family across latitude, longitude, ground speed, heading and battery, plus
`Create_ImplausiblyFastVehicle_IsAccepted`, which pins the other half of the rule: reject what is
*impossible*, not what is merely surprising, since an invented ceiling would refuse a legitimate
report from whatever airframe is added later. Also
`tests/Mcs.Core.Tests/TelemetryCurrencyTests.cs` — `FromAge_NegativeAge_ThrowsRatherThanClamping`
and `Of_AFrameFromAnotherProvider_ThrowsRatherThanReportingItLive`, which are the same rule applied
to time, where the clamp a reasonable person would write reports the vehicle as live.

---

## MCS-013

> When the console stops receiving from the station, it shall render every vehicle's currency as
> unknown rather than continuing to present the last snapshot as current.

**Type** SAF · **Method** Test · **Mitigates** HAZ-01, HAZ-04

**Rationale.** MCS-002 and MCS-003 are the *station's* answer about a vehicle. This is the case they
cannot cover: the station has stopped answering, every age is unknown and growing, and going on
drawing the fleet as live because the last snapshot said so is the failure the whole state language
exists to prevent — with the added cruelty that it would look completely normal. The console
watches the station exactly the way MCS-002 has the station watch a vehicle: three missed fleet
ticks. It must be far shorter than the interval at which a dead connection is thrown away and
reopened, since a console patient about *reconnecting* is fine and a console patient about *saying
it has stopped hearing anything* is the hazard.

**Partly verified.** The rendering is tested: `web/src/vehicles/appearance.test.ts` — *demotes every
vehicle whatever its last reported state*, *shows no age, because the console has none to show*,
*is told apart from lost by the chip alone, which is why the station bar exists*. **The detection
is not.** Nothing exercises `web/src/telemetry/client.ts` deciding that three ticks have been
missed, that the timer is rearmed on every event, or that the bar clears when the station comes
back. That is the weakest verification in this table and it is a unit test's worth of work against
a fake clock, not a research problem.

---

## Removed

Nothing has been removed. A requirement leaves this table by getting a line here saying which one
and why — not by being deleted.

| ID | Requirement | Why it was removed | When |
| --- | --- | --- | --- |
| — | — | — | — |
