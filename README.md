# Meridian Control Station

[![CI](https://github.com/Bojackson123/meridian-control-station/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Bojackson123/meridian-control-station/actions/workflows/ci.yml)

A ground control station for simulated uncrewed vehicles: live fleet telemetry, mission
planning with pre-flight conflict checking, and command with a durable audit trail.

![The console at v0.1: one simulated vehicle on the bundled offline basemap, updating at 1 Hz](docs/demo-v0.1.gif)

<sub>Recorded at `v0.1`, when the vehicle came from a hardcoded feed. The console now draws an
aircraft arriving as MAVLink over UDP; a new recording lands with the next tag.</sub>

---

## What this is

A personal project, built in the open one working slice at a time. This is `v0.1` plus the
MAVLink work that has landed since: a simulated aircraft flying a route in its own process,
transmitting real MAVLink v2 over UDP to a station that decodes it onto a map — with a database
the station migrates on startup, structured logs, and the whole thing under one
`docker compose up`.

The table below says exactly what runs at this tag, and nothing in this file describes
anything that doesn't. It does not connect to real aircraft.

The same discipline in more detail: [`docs/requirements.md`](docs/requirements.md) states what the
station is required to do and how each requirement is verified — including the two that are not,
and the one that is only half-verified. Those claims are not maintained by hand: a CI job reads the
table, matches each row against the tests that reported passing and the evidence links that still
resolve, and fails the build when they disagree.
[`docs/what-can-go-wrong.md`](docs/what-can-go-wrong.md)
is where those requirements came from. [`docs/interfaces.md`](docs/interfaces.md) is the contract
itself: what a vehicle has to supply, in which units, and what the station serves back.

---

## Quickstart

Docker with Compose v2, and nothing else. No accounts, no API keys, nothing to sign up for.

The build pulls base images and packages the way any build does. What runs afterwards does
not: once the stack is up, nothing it serves reaches another origin, and you can pull the
network out from under it and keep flying.

```bash
git clone https://github.com/Bojackson123/meridian-control-station.git
cd meridian-control-station
./tools/bootstrap-env.sh    # PowerShell: .\tools\bootstrap-env.ps1
docker compose --env-file .env -f deploy/compose/compose.yaml up --build
```

Then open `http://localhost:8080`. The API is on `http://localhost:8081` for `curl`; the
console reaches it through the web container's `/api` proxy instead, so the browser only ever
talks to one origin.

`--env-file` is not optional and not decoration. Compose looks for `.env` beside the compose
file, and the bootstrap script writes it to the repo root — pointing at it explicitly is the
smaller of the two costs. Omit it and the stack refuses to start, naming the first variable
it could not resolve.

Run the bootstrap script rather than copying `.env.example`: the password in that file is a
placeholder, and a stack standing up with a placeholder for a credential is the failure the
script exists to prevent.

**If you have run the stack before and then regenerated `.env`, tear the volume down first:**

```bash
docker compose --env-file .env -f deploy/compose/compose.yaml down -v
```

Postgres fixes the superuser password when it first initialises its data directory and ignores
the variable on every start afterwards, so a new `.env` against an old volume fails
authentication — the API reports it and exits non-zero rather than starting without a database.

---

## What's real at this tag

An aircraft flying a waypoint route in a separate process, transmitting MAVLink v2 over UDP to a
hand-written parser, decoded into a bounded in-memory store and served over HTTP as a snapshot and
a live event stream — and a Postgres database the station migrates on startup and reports the state
of over HTTP. The console draws what arrives: an offline basemap that runs with the network off,
with the fleet on it, each vehicle a marker pointed along the heading it reported.

There is no fake feed behind any of it. The hardcoded one that flew at `v0.1` was deleted once the
MAVLink path could replace it, rather than left behind a configuration flag — a second source of
truth about what the console is showing is a debugging cost with no upside.

| | Status at `v0.1` |
| --- | --- |
| Telemetry model and ingest boundary | working, tested |
| Bounded store — 12 vehicles, per-vehicle history, live subscriptions | working, tested |
| Fake vehicle feed | **deleted after `v0.1`** — the MAVLink path replaced it, and it was removed rather than left switchable |
| Structured JSON logging | working |
| Postgres — schema migration on startup, `/health` and `/health/db` | working, tested |
| Telemetry HTTP API — `/api/vehicles` and an SSE `/api/telemetry/stream` | working, tested |
| Offline basemap — dark MapLibre style, zoom-adaptive graticule, no third-party requests | working |
| Map console — the fleet on the basemap, each marker oriented by heading | working; a vehicle that reported no heading is drawn without a nose rather than pointed north |
| Persistence of domain data | not yet — the schema mechanism is proven, the tables that use it arrive with the features that define them |
| Console state language — live / stale (+age) / lost, on the map and in the fleet panel | landed after `v0.1`, tested; designed once in `docs/notes/console-design.md` and rendered from one derivation, so a marker and its row cannot disagree |
| Alerts and acknowledgement | not yet — the state language and the bar they belong in are designed and built; nothing evaluates an alert |
| Docker Compose — database, API, console and simulator, one command, offline | working |
| MAVLink v2 framing — parser and serializer, verified byte-for-byte against pymavlink vectors | landed after `v0.1`, tested |
| MAVLink message decode — the four messages the console displays, assembled into telemetry | landed after `v0.1`, tested against the same vectors |
| MAVLink over UDP — a bound socket feeding the codec, through the ingest boundary, into the store | landed after `v0.1`, tested |
| Air simulator — bank-limited kinematics and a waypoint follower, transmitting MAVLink from its own process and container | landed after `v0.1`, tested; its turn radius is asserted against `v²/(g·tan φ)`, and its bytes against the station's own decoder |
| Reading MAVLink from a real vehicle | not yet — the link is proved end to end against the simulator; nothing has been pointed at an autopilot |
| Mission planning, deconfliction, auth | not yet |

---

## Architecture

```
src/Mcs.Core        the domain — telemetry model, ingest boundary, bounded store
src/Mcs.Api         ASP.NET Core host; the HTTP surface, persistence and observability
src/Mcs.Adapters    vehicle adapters; Mavlink/ holds the hand-written v2 codec and the UDP link
src/Mcs.Simulator   the aircraft — kinematics, a waypoint follower, and MAVLink out over UDP
web/                React + TypeScript + Vite console; the basemap is served from web/public
tests/              unit tests for the core, the host, the codec and the aircraft; integration
                    tests against a real Postgres via Testcontainers; a system suite that
                    drives the running compose stack over HTTP, and skips when no stack is up
deploy/migrations/  numbered .sql files, applied by the API on startup
deploy/compose/     compose.yaml — the whole stack, database and API and console and aircraft
docs/notes/         engineering notes, including what got stuck and why
```

The core holds the vehicle-agnostic domain: what telemetry is, how it enters the system, and
how much of it is kept. Everything that knows a *protocol* — how to talk to a particular kind
of vehicle — is an adapter, and adapters depend on the core rather than the other way round.
Adding a ground vehicle later should mean writing a new adapter and changing no core file; a
core that has grown a web, database or protocol dependency makes that claim indefensible.

`Mcs.Core` therefore has no package references at all, and its project file being empty is
what enforces it. What the core does hold is the contract every telemetry source implements —
start producing telemetry until stopped, and nothing else. It was written only once there were
two implementations to derive it from, so it describes what they have in common rather than
what one of them happens to do: the MAVLink link in `Mcs.Adapters`, which decodes a wire format,
and the hardcoded feed that used to invent telemetry in the API host. Only the first still
exists — the second was deleted once it had served that purpose — and the interface has
deliberately not been re-derived from the survivor, since a contract narrowed to one link is no
longer vehicle-agnostic. Whether the ground vehicle can be added without touching a core file is
the test of that, and it will be run against a real diff.

---

## Limitations

- Single author on both sides of every interface. Nothing here has been integrated against
  software someone else wrote.
- Simulated vehicles only. The simulator's flight model is deliberately thin: constant airspeed,
  a bank-limited turn, a bounded climb rate, and no wind, drag or mass. The one property spent
  time on is the turn, because a later separation margin is a claim about it; a better aircraft
  would not make the station better.
- One aircraft. The simulator flies a single vehicle, and the station's 12-vehicle bound is
  designed into the data structures and covered by tests — including the rejection of a
  thirteenth — but it is **not demonstrated on screen**. Showing it would mean inventing vehicles,
  which is the thing that was just deleted, so it is stated rather than staged.
- Telemetry is in-memory and bounded. There is no durable history, and that is a stated
  non-goal rather than a gap.
- No authentication. Anything the API exposes is exposed to anyone who can reach it.
- The basemap is deliberately minimal — no labels, no coastline — to keep the stack fully
  offline.
- **MAVLink message signing is not implemented, and is not planned.** Signed frames are
  recognised, rejected and counted rather than misparsed. Signing is a substantial sub-feature —
  key management, timestamp windows, replay rejection — that nothing here needs, and a
  half-implementation of an authentication mechanism is worse than none, because it invites the
  assumption that frames were authenticated.
- The MAVLink codec decodes four message types, the ones the console displays. A frame carrying
  any other message id is counted and skipped, and cannot be checksum-verified at all, because
  the checksum seed is per-message.
- **The MAVLink link accepts datagrams from anyone who can reach it.** The adapter binds every
  interface by default, because the simulator sends to it from another container, and nothing
  authenticates a sender — signing is not implemented, per the entry above. Since the store admits
  twelve vehicles and never reclaims a slot on its own, a sender that can reach the port can
  occupy the whole fleet with invented system ids, after which every genuine vehicle is refused
  until the station is restarted. Set `Adapters__Mavlink__ListenAddress` to a specific interface
  on any network you do not control.
- A vehicle's altitude is reported above mean sea level and nothing converts it. `relative_alt`
  — height above the point the vehicle armed at — is decoded and deliberately unused, because it
  equals height above the ground only over flat terrain and there is no terrain model here to
  make the conversion honest.
- Fault flags are stubbed at both ends. The simulator sends one healthy sensor mask, always;
  `SYS_STATUS`'s sensor-health bitmasks are decoded and read by nothing; and the link status the
  API reports is always healthy, because a decoded frame is one that arrived, so this path holds
  no evidence of a degraded link. Whether a vehicle has gone quiet is a separate question, asked
  against the station's clock, and the console answers that one.
- Nothing evaluates an alert. The bar across the top of the console is the place alerts belong and
  it currently carries one thing — whether the station is still talking — and the fleet panel
  reserves the space the abort control will occupy without drawing a control that does nothing.

---

## What's next

In order, without dates:

- **Alerts that cannot be missed.** Geofence and battery conditions surfaced in the bar the
  console already reserves for them, acknowledged one at a time and never timed out.
- **Commands and tasking.** A command lifecycle with a durable audit trail, behind an
  authenticated operator.
- **A second vehicle type.** A ground vehicle, added by writing an adapter and not by
  editing the core.
- **Pre-flight conflict checking.** Mission plans checked against each other before anything
  flies.

---

## Running it without Docker

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins
10.0.302, rolling forward to the latest patch) and a Postgres for the API to migrate.

```bash
docker run --rm -d --name mcs-pg -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=mcs -p 5432:5432 postgres:18-alpine

dotnet build
dotnet test
dotnet run --project src/Mcs.Api
```

The `docker run` is one line on purpose: a `\` continuation is a bash-ism, and pasting it
into PowerShell runs a truncated command that fails somewhere less obvious than it should.

`dotnet test` needs Docker running for the integration suite, which starts its own
throwaway Postgres. The unit tests (`tests/Mcs.Core.Tests`, `tests/Mcs.Api.Tests`) need
nothing.

The app listens on `http://localhost:5271`:

```bash
curl -s localhost:5271/health      # {"status":"Healthy"}
curl -s localhost:5271/health/db   # {"status":"Healthy","expectedSchemaVersion":1,"schemaVersion":1}

curl -s localhost:5271/api/vehicles | jq     # latest frame per vehicle; [] before the first tick
curl -N localhost:5271/api/telemetry/stream  # the same frames, live, as they arrive
```

`-N` disables curl's own buffering. Without it the stream looks dead, and the endpoint gets
blamed for it.

**On Windows, spell it `curl.exe`.** In PowerShell, `curl` is an alias for
`Invoke-WebRequest`, which rejects `-N` outright and buffers a whole response before
returning it — so it could not print a stream even if the flag existed. `curl.exe` ships in
`System32` on Windows 10 and 11 and takes the flags above verbatim. `jq` does not ship with
Windows; drop the pipe and read the JSON raw, or install it.

**The wire contract lives in [`docs/interfaces.md`](docs/interfaces.md)** — the payload shape, both
event types, the units a vehicle must send in, and what gets rejected. One copy, because two copies
of a wire example diverge and the one that diverges is always the one somebody integrated against.

Two things worth knowing before reading it: the altitude carries the reference it was measured
against, never a bare number, and every vehicle arrives with the station's own judgement of how
current it is — a `state` and an age — rather than the ingredients for a consumer to compute one.

`/openapi/v1.json` serves the OpenAPI document in Development.

**Without a database the station does not start.** It retries for thirty seconds, then logs
why and exits non-zero. An API that came up anyway would be reporting itself healthy while
something it depends on was missing, which is the same failure the console itself is built
to avoid — and on screen it looks like a quiet fleet rather than a broken station.

The interesting output is on stdout, as compact JSON. Wrapped here for width; each of these is
one line:

```
{"@mt":"Starting {AdapterCount} vehicle adapter(s): {Adapters}.","AdapterCount":1,
 "Adapters":"mavlink-udp","SourceContext":"Mcs.Api.Adapters.VehicleAdapterService"}

{"@mt":"MAVLink UDP adapter listening on {EndPoint}.","EndPoint":"0.0.0.0:14550",
 "SourceContext":"Mcs.Adapters.Mavlink.MavlinkUdpAdapter"}

{"@mt":"MAVLink link: {Link}. Framing: {Framing}. Decode: {Decode}.",
 "Link":"datagrams=1023, bytes=35571, receiveErrors=0, written=482, vehiclesRejected=0, slowDecodes=0",
 "Framing":"parsed=1023, crcFailures=0, resyncedBytes=0, unknown=0, v1=0, signed=0, incompatFlags=0",
 "Decode":"decoded=1023, rejected=0, emitted=482, positionsWithoutHud=1",
 "SourceContext":"Mcs.Adapters.Mavlink.MavlinkUdpAdapter"}
```

The link statistics are periodic rather than per frame, because a ground station sees far too
much traffic for a line each. The numbers cohere, and it is worth checking that they do:
`parsed` equals `datagrams` because the simulator sends one frame per datagram, and `written`
is 482 of 1023 because only `GLOBAL_POSITION_INT` completes a telemetry frame — 4 Hz out of the
8.5 Hz the four message types add up to. `positionsWithoutHud=1` is the first position of the
run arriving before any `VFR_HUD` had been seen, which is exactly why speed and heading are
nullable on the wire.

Nothing in the parser logs. Every discard — a bad CRC, an unknown message id, a signed frame —
increments a counter above instead, since a log line per unknown message would train whoever is
watching to ignore the stream.

### The console

```bash
cd web && npm install && npm run dev
```

A dark map centred on the aircraft's route, a scale bar, a graticule that changes spacing with
the zoom — the fleet on top of it, one marker per vehicle rotated to the heading in its latest
frame, and beside it a panel listing every vehicle with all six of the fields the requirements
ask for. **Start the API first:** the dev server proxies `/api` to it on port 5271, and with
nothing behind that proxy the basemap draws but stays empty. The aircraft is a third process —
`cd src/Mcs.Simulator && dotnet run` — and without it the station is listening to an empty sky,
which looks exactly the same.

The marker steps rather than gliding, and that is the interesting part. Smooth motion means
interpolating between frames, which puts the vehicle at a position it never reported — a
nicer-looking console that is lying about where something is. At four position reports a second
you are watching the station show you exactly what it was told, and nothing else; on a slower
link the steps get coarser, and that is the link being visible rather than a defect.

**Stop the aircraft and watch what happens.** After three seconds its marker goes hollow and
amber, freezes on its last heading, and grows a chip counting the age of the data — on the map and
in its row at the same moment, because both come from one derivation rather than two. Twelve
seconds later it becomes a dashed ring with **no heading at all**, its speed and heading in the
panel become dashes, and it sits at the dimmest level on the screen. The station does not know
which way that aircraft is pointing; it knows where it was pointing some minutes ago, and a
confident nose on a dead track is the display asserting what it cannot support.

Then stop the API. Every vehicle goes to that same ring within about three seconds, every chip
reads `?`, and the bar across the top says `STATION UNREACHABLE`. A quiet vehicle in a healthy
fleet is stale because the station said so; a quiet station leaves every age unknown and growing,
and the one thing the console may not do is keep showing a live fleet on the strength of a
snapshot that has stopped arriving. It reconnects on its own, and the fleet comes back when the
station does.

Twelve of them at once is `tools/fleet-at-twelve.ps1` (or `.sh`), which is how the layout's claim
to fit twelve rows without a scrollbar gets checked rather than asserted.

Everything above is decided in `docs/notes/console-design.md` and its working drawing, once,
before any of it was built. Two rules from it are worth knowing while looking at the screen: **no
state is carried by colour alone** — put the browser in greyscale and solid dart, hollow dart and
dashed ring still separate — and **the age is on screen, never in a tooltip**, because an operator
scanning twelve vehicles hovers over none of them.

The basemap is bundled, not fetched: the MapLibre style and everything it references are
served from `web/public`, there is no tile CDN and no API key, and the page carries a
`default-src 'self'` policy so a dependency that starts reaching for one fails loudly rather
than quietly making the offline claim false. It has no labels, because a style with labels
downloads glyph files from somewhere. Confirm it yourself: DevTools → Network, tick
**Disable cache**, hard-reload, sort by domain — every request is to `localhost`.

There is no coastline. At a 400 m circuit the land polygon covers the whole screen and looks
exactly like the background, so it would be several hundred KB of committed geodata bought
for the zoom levels an operator never uses. The reasoning is recorded in the style file, and
a land layer drops in later without touching anything else.

### Configuring the link and the aircraft

Where the station listens — the `Adapters:Mavlink` section of the API's `appsettings.json`,
overridable as `Adapters__Mavlink__Port`:

| Setting | Default | Range |
| --- | --- | --- |
| `ListenAddress` | `0.0.0.0` | any address that parses |
| `Port` | 14550 | 0–65535, where 0 takes any free port |

There is deliberately no `Enabled` flag. An adapter that is configured but silently not running
is the failure this section exists to prevent, and which adapters exist is a question for the
host's registrations, where it can be read.

What flies — the `Simulator` section of the simulator's own `appsettings.json`, overridable as
`Simulator__TargetHost` (which is how Compose points it at the API):

| Setting | Default | Notes |
| --- | --- | --- |
| `TargetHost` / `TargetPort` | `127.0.0.1` / 14550 | a name that will not resolve is fatal at startup |
| `SystemId` | 1 | the station names the vehicle from this — system 1 becomes `MAV-001` |
| `CruiseSpeedMetersPerSecond` | 22 | with the bank limit, this *derives* the turn radius |
| `MaxBankAngleDegrees` | 25 | `R = v²/(g·tan φ)`, never configured directly |
| `StepHz` | 20 | the physics step, independent of every message rate |
| `HeartbeatHz` / `SysStatusHz` / `VfrHudHz` / `GlobalPositionHz` | 1 / 0.5 / 3 / 4 | deliberately non-harmonic |
| `Route` | four waypoints | at least two required |

Both are validated at startup: a setting outside its range stops the host with a message naming
it, rather than flying a plausible-looking route somewhere nobody meant. The cross-property
checks are the interesting ones — a message rate faster than the physics step is rejected, and
so is a capture radius smaller than the turn radius the envelope implies, because under it the
aircraft orbits a waypoint it can never reach and renders as a tidy loiter.

---

## Four decisions worth knowing about

**A vehicle's claims and the station's observations are different types.**
`VehicleTelemetry` holds only what a vehicle reported; `TelemetryFrame` pairs one with
`ReceivedAtUtc`, the station's own receipt time. Which object you're holding tells you
whether the data is trustworthy. An adapter can only produce the former, so stamping a frame
with a vehicle's own clock isn't a mistake available to make — it's code that won't compile.

**The receipt timestamp is taken at arrival, not after decoding.** `TelemetryIngest` splits
receipt into `BeginReceive` / `Complete`, so the decode cost is measured rather than baked
invisibly into the recorded age of the data. The frame's constructor is internal and a
single-use receipt is its only caller: outside the core there is no expression that yields a
frame.

**Everything the console shows must be able to be shown as stale.** The hazard this system
is designed against — HAZ-01, in [`docs/what-can-go-wrong.md`](docs/what-can-go-wrong.md) — is
a console that shows an operator a picture they believe is current when it isn't. That's why
the store rejects a thirteenth vehicle loudly instead of dropping it, why a full subscriber
queue drops its *oldest* frames rather than its newest, and why values are rejected rather
than clamped — a clamped 200% battery renders as a believable 100%, and the operator never
learns the adapter is broken. That file is the whole list: eight things that could go wrong,
what stops each one, and the two that nothing stops yet.

**A migration is immutable once it has shipped, and the database enforces it.** Schema
changes are numbered `.sql` files compiled into the API and applied on startup, inside a
transaction, under a Postgres advisory lock so two instances starting together cannot both
apply the same one. Each is recorded with a checksum, and a checksum that no longer matches
stops the station rather than being logged and stepped over — a schema that has quietly
drifted from the code is the same problem as a console showing a position that is no longer
true. `/health/db` reads the applied version back out and compares it with the version the
running build was compiled against, so "is this the database this build expects" is a
question with an answer rather than an assumption.

---

## Non-goals

- **Durable telemetry history.** Telemetry stays in memory in a bounded ring buffer. Mission
  plans, commands, overrides and alert acknowledgements will be durable; a week of position
  reports will not.
- **Any claim of standards compliance.** This is not a STANAG-anything implementation, and
  it does not connect to real aircraft.
