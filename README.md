# Meridian Control Station

A ground control station for simulated uncrewed vehicles: live fleet telemetry, mission
planning with pre-flight conflict checking, and command with a durable audit trail.

Built in the open, one working slice at a time. **This is early** — the section below says
exactly what runs today, and nothing here describes anything that doesn't.

---

## What runs today

A hardcoded telemetry feed flying a vehicle around a closed circuit at 1 Hz, into a bounded
in-memory store that the API layer will read from.

| | |
| --- | --- |
| Telemetry model and ingest boundary | working, tested |
| Bounded store — 12 vehicles, per-vehicle history, live subscriptions | working, tested |
| Fake vehicle feed | working, tested |
| HTTP API | not yet — the only endpoint is the OpenAPI document |
| Map console | not yet — `web/` is an empty Vite scaffold |
| Postgres, Docker Compose, structured JSON logging | not yet |
| MAVLink, mission planning, deconfliction, auth | not yet |

There is no `docker compose up` yet. When there is, it will be in this README.

---

## Running it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` pins
10.0.302, rolling forward to the latest patch). Nothing else — no Docker, no database, no
accounts.

```bash
dotnet build
dotnet test
dotnet run --project src/Mcs.Api
```

The app listens on `http://localhost:5271`. `/openapi/v1.json` serves the OpenAPI document
in Development; every other path is a 404 until the telemetry API lands.

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
web/                React + TypeScript + Vite scaffold
tests/              unit tests for the core and the feed; integration (Testcontainers)
                    and system (compose smoke) projects exist but are empty
deploy/compose/     deployment (empty)
docs/notes/         engineering notes, including what got stuck and why
```

`Mcs.Core` has no package references at all, deliberately: adding a second vehicle type
later must not touch a core file, and a core that has grown a web or database dependency
makes that claim indefensible.

---

## Three decisions worth knowing about

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

---

## Non-goals

- **Durable telemetry history.** Telemetry stays in memory in a bounded ring buffer. Mission
  plans, commands, overrides and alert acknowledgements will be durable; a week of position
  reports will not.
- **Any claim of standards compliance.** This is not a STANAG-anything implementation, and
  it does not connect to real aircraft.
