# Meridian Control Station

[![CI](https://github.com/Bojackson123/meridian-control-station/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Bojackson123/meridian-control-station/actions/workflows/ci.yml)

A ground control station for simulated uncrewed vehicles: live fleet telemetry, mission
planning with pre-flight conflict checking, and command with a durable audit trail.

![The console: one simulated vehicle on the bundled offline basemap, updating at 1 Hz](docs/demo-v0.1.gif)

---

## What this is

A personal project, built in the open one working slice at a time. This is `v0.1` — the
first tag, and a walking skeleton: one fake vehicle on a map, a database the station
migrates on startup, structured logs, and the whole thing under one `docker compose up`.

The table below says exactly what runs at this tag, and nothing in this file describes
anything that doesn't. It does not connect to real aircraft.

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

A hardcoded telemetry feed flying a vehicle around a closed circuit at 1 Hz, into a bounded
in-memory store, served over HTTP as a snapshot and a live event stream — and a Postgres
database the station migrates on startup and reports the state of over HTTP. The console
draws that feed: an offline basemap that runs with the network off, with the fleet on it,
each vehicle a marker pointed along the heading it reported.

| | Status at `v0.1` |
| --- | --- |
| Telemetry model and ingest boundary | working, tested |
| Bounded store — 12 vehicles, per-vehicle history, live subscriptions | working, tested |
| Fake vehicle feed | working, tested |
| Structured JSON logging | working |
| Postgres — schema migration on startup, `/health` and `/health/db` | working, tested |
| Telemetry HTTP API — `/api/vehicles` and an SSE `/api/telemetry/stream` | working, tested |
| Offline basemap — dark MapLibre style, zoom-adaptive graticule, no third-party requests | working |
| Map console — the fleet on the basemap, each marker oriented by heading | working; a vehicle that reported no heading is drawn without a nose rather than pointed north |
| Persistence of domain data | not yet — the schema mechanism is proven, the tables that use it arrive with the features that define them |
| Console state language — live / stale / lost, alerts | not yet — a dead feed shows only in the browser console |
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
src/Mcs.Api         ASP.NET Core host; the fake feed lives here for now
src/Mcs.Adapters    vehicle adapters; Mavlink/ holds the hand-written v2 codec and the UDP link
src/Mcs.Simulator   the aircraft — kinematics, a waypoint follower, and MAVLink out over UDP
web/                React + TypeScript + Vite console; the basemap is served from web/public
tests/              unit tests for the core, the feed, the codec and the aircraft; integration
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
what one of them happens to do: the MAVLink link in `Mcs.Adapters`, which decodes a wire
format, and the fake feed, which stays in the API host because it invents telemetry instead of
decoding anything. Whether the M3 ground vehicle can be added without touching a core file is
the test of that, and it will be run against a real diff.

---

## Limitations

- Single author on both sides of every interface. Nothing here has been integrated against
  software someone else wrote.
- Simulated vehicles only. The simulator's flight model is deliberately thin: constant airspeed,
  a bank-limited turn, a bounded climb rate, and no wind, drag or mass. The one property spent
  time on is the turn, because a later separation margin is a claim about it; a better aircraft
  would not make the station better.
- Two vehicles by default, one from the simulator over UDP and one from the in-process fake feed
  that predates it. The fake one is on its way out. The 12-vehicle bound is designed into the data
  structures and tested, not demonstrated on screen — set `FakeFeed__VehicleCount=12` to see it.
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
  no evidence of a degraded link. Whether a vehicle has gone quiet is a question
  about the station's clock, and nothing answers it yet — see the last item below.
- Nothing on screen yet distinguishes a vehicle reporting now from one that stopped ten
  minutes ago.

---

## What's next

In order, without dates:

- **Deleting the fake feed.** The MAVLink path now carries a real aircraft end to end; the two
  run side by side for exactly as long as it takes to compare them.
- **A console state language.** Live, stale and lost as things an operator can see, so the
  last limitation above stops being one.
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

```
event: telemetry
data: {"vehicleId":"UAV-01","latitudeDegrees":34.7333065,"longitudeDegrees":-86.5835293,
       "altitude":{"meters":300,"reference":"Msl"},"groundSpeedMetersPerSecond":20.9439510,
       "headingDegrees":126.0114069,"batteryPercent":99.5554147,"linkStatus":"Healthy",
       "receivedAtUtc":"2026-08-09T22:54:29.3154398+00:00"}
```

`groundSpeedMetersPerSecond`, `headingDegrees` and `batteryPercent` are nullable and arrive as
`null` when the vehicle did not report them — never as `0`, which is a speed, a bearing and a
flat battery respectively. A client that substitutes a number for one of them puts a claim on
screen that no vehicle made.

Wrapped here to fit the page — on the wire each `data:` is a single line, because a raw
newline inside one would break SSE framing. After fifteen seconds of silence the stream sends
an `event: heartbeat` instead, so an idle connection is not dropped by a proxy that thinks it
has gone away.

Two things the payload is careful about: the altitude carries the reference it was measured
against, never a bare number, and `receivedAtUtc` is the station's own observation of when the
frame arrived — not a time the vehicle claimed. Staleness is derived from it.

`/openapi/v1.json` serves the OpenAPI document in Development.

**Without a database the station does not start.** It retries for thirty seconds, then logs
why and exits non-zero. An API that came up anyway would be reporting itself healthy while
something it depends on was missing, which is the same failure the console itself is built
to avoid — and on screen it looks like a quiet fleet rather than a broken station.

The interesting output is on stdout. In Development the feed logs one frame per second:

```
info: Mcs.Api.FakeFeed.FakeVehicleFeed[0]
      Fake vehicle feed started: 1 vehicle(s) at 1 Hz on a 400 m circuit about
      34.7304, -86.5861, one lap in 120 s at 20.94 m/s.
dbug: Mcs.Api.FakeFeed.FakeVehicleFeed[0]
      Fake vehicle feed wrote VehicleTelemetry { Id = UAV-01, LatitudeDegrees = 34.7339882,
      LongitudeDegrees = -86.585868, Altitude = 300 m Msl, GroundSpeedMetersPerSecond = 20.94,
      HeadingDegrees = 93.04, BatteryPercent = 99.96, LinkStatus = Healthy }.
```

The numbers cohere, and it's worth checking that they do: consecutive frames are ~20.9 m
apart against a reported 20.94 m/s, and the heading advances 3°/s — one 360° lap in 120 s.

### The console

```bash
cd web && npm install && npm run dev
```

A dark map centred on the feed's circuit, a scale bar, a graticule that changes spacing with
the zoom — and the fleet on top of it, one marker per vehicle, rotated to the heading in its
latest frame. **Start the API first:** the dev server proxies `/api` to it on port 5271, and
with nothing behind that proxy the basemap draws but stays empty.

The marker steps once a second rather than gliding, and that is the interesting part. Smooth
motion means interpolating between frames, which puts the vehicle at a position it never
reported — a nicer-looking console that is lying about where something is. At 1 Hz you are
watching the station show you exactly what it was told, and nothing else.

What the console does *not* do yet: nothing on screen distinguishes a vehicle reporting now
from one that stopped ten minutes ago. Stop the API and the markers stay exactly where they
were, with only the browser console saying so. The client notices — it treats silence on the
stream as a fault and reopens the connection, so the map recovers on its own when the API
comes back — but saying it on screen needs the visual language designed first, and that is
the next piece of console work.

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

### Configuring the feed

The `FakeFeed` section of `appsettings.json`, overridable by environment variable
(`FakeFeed__VehicleCount=12`):

| Setting | Default | Range |
| --- | --- | --- |
| `VehicleCount` | 1 | 1–12 |
| `RateHz` | 1.0 | 0.1–10 |
| `OriginLatitudeDegrees` | 34.7304 | ±85 |
| `OriginLongitudeDegrees` | -86.5861 | ±180 |
| `RadiusMeters` | 400 | 50–50 000 |
| `OrbitPeriodSeconds` | 120 | 10–3 600 |
| `AltitudeMetersMsl` | 300 | -500–20 000 |
| `EnduranceSeconds` | 2 700 | 60–86 400 |

Values are validated at startup: a setting outside its range stops the host with a message
naming it, rather than flying a plausible-looking circuit somewhere nobody meant.

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
is designed against is a console that shows an operator a picture they believe is current
when it isn't. That's why the store rejects a thirteenth vehicle loudly instead of dropping
it, why a full subscriber queue drops its *oldest* frames rather than its newest, and why
values are rejected rather than clamped — a clamped 200% battery renders as a believable
100%, and the operator never learns the adapter is broken.

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
