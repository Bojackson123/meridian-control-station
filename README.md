# Meridian Control Station

A ground control station for simulated uncrewed vehicles: live fleet telemetry, mission
planning with pre-flight conflict checking, and command with a durable audit trail.

Built in the open, one working slice at a time. **This is early** — the section below says
exactly what runs today, and nothing here describes anything that doesn't.

---

## What runs today

A hardcoded telemetry feed flying a vehicle around a closed circuit at 1 Hz, into a bounded
in-memory store, served over HTTP as a snapshot and a live event stream — and a Postgres
database the station migrates on startup and reports the state of over HTTP. The console
draws that feed: an offline basemap that runs with the network off, with the fleet on it,
each vehicle a marker pointed along the heading it reported.

| | |
| --- | --- |
| Telemetry model and ingest boundary | working, tested |
| Bounded store — 12 vehicles, per-vehicle history, live subscriptions | working, tested |
| Fake vehicle feed | working, tested |
| Structured JSON logging | working |
| Postgres — schema migration on startup, `/health` and `/health/db` | working, tested |
| Telemetry HTTP API — `/api/vehicles` and an SSE `/api/telemetry/stream` | working, tested |
| Offline basemap — dark MapLibre style, zoom-adaptive graticule, no third-party requests | working |
| Map console — the fleet on the basemap, each marker oriented by heading | working |
| Console state language — live / stale / lost, alerts | not yet — a dead feed shows only in the browser console |
| Docker Compose — database, API and console, one command, offline | working |
| MAVLink, mission planning, deconfliction, auth | not yet |

From a clean clone, with nothing installed but Docker:

```bash
./tools/bootstrap-env.sh    # tools\bootstrap-env.ps1 on Windows — writes .env with a generated password
docker compose --env-file .env -f deploy/compose/compose.yaml up --build
```

Then open `http://localhost:8080`. The API is on `http://localhost:8081` for `curl`; the
console reaches it through the web container's `/api` proxy instead, so the browser only ever
talks to one origin.

`--env-file` is not optional and not decoration. Compose looks for `.env` beside the compose
file, and the bootstrap scripts write it to the repo root — pointing at it explicitly is the
smaller of the two costs. Omit it and the stack refuses to start, naming the first variable
it could not resolve.

---

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins
10.0.302, rolling forward to the latest patch) and a Postgres for the API to migrate. No
accounts, nothing to sign up for.

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
with nothing behind that proxy the basemap draws but stays empty. Set
`FakeFeed__VehicleCount=12` and you get twelve markers spaced around the circuit.

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

## Layout

```
src/Mcs.Core        the domain — telemetry model, ingest boundary, bounded store
src/Mcs.Api         ASP.NET Core host; the fake feed lives here for now
src/Mcs.Adapters    vehicle adapters (empty)
src/Mcs.Simulator   vehicle simulator (stub)
web/                React + TypeScript + Vite console; the basemap is served from web/public
tests/              unit tests for the core and the feed; integration tests against a real
                    Postgres via Testcontainers; a system suite that drives the running
                    compose stack over HTTP, and skips when no stack is up
deploy/migrations/  numbered .sql files, applied by the API on startup
deploy/compose/     compose.yaml — the whole stack, database and API and console
docs/notes/         engineering notes, including what got stuck and why
```

`Mcs.Core` has no package references at all, deliberately: adding a second vehicle type
later must not touch a core file, and a core that has grown a web or database dependency
makes that claim indefensible.

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
