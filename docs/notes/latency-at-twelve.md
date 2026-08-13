# latency-at-twelve.md

What MCS-001's one-second budget is actually being spent on, measured at twelve vehicles rather
than asserted.

MCS-001 says each field must update **within 1 second of frame receipt at the station**. It is the
only requirement in the baseline whose verification needs an instrument rather than an assertion,
and the one most likely to be marked verified on the strength of "it looks fast" — so this note
records the method, the numbers, and the parts of the path that were not measured.

**Date:** 2026-08-13

---

## The path, and where it was cut

```
vehicle -> [ udp | frame decode | ingest receipt | store | fan-out | project | serialise | SSE ]
                                                                                    |
                                    ~~~~ socket, and nginx in the deployed stack ~~~~
                                                                                    |
                        [ EventSource message | coalesce to a frame | React render | paint ] -> operator
```

The budget starts at the **ingest receipt** — MCS-001 says "frame receipt at the station", so
everything left of that stamp is a vehicle's problem and not the station's. It ends at the pixel.

It was measured in two halves, each with one clock, because no single clock spans both processes.
Composing them is the only honest way to get a total: a station timestamp subtracted from a browser
timestamp is exactly the cross-clock arithmetic the whole system is arranged to prevent.

---

## The station half — 0.53 ms median, 7.05 ms worst

`tests/Mcs.Integration.Tests/TelemetryLatencyTests.cs`, which runs in CI, so this number is
re-measured on every push rather than being a thing that was once true.

Twelve vehicles, each reporting 4 Hz — the simulator's real position rate — for twenty rounds:
240 frames. Every round reports all twelve back to back, which is the burst the fan-out has to
survive rather than an evenly spread arrival. The clock starts immediately before the frame enters
`TelemetryIngest` and stops when a client has **parsed it off the stream**, so the measurement
covers admission, fan-out, projection, JSON serialisation, SSE framing and the client's own
deserialisation.

```
240 frames, 12 vehicles at 4 Hz each
  median   0.53 ms
  p95      6.57 ms
  worst    7.05 ms
```

The station's own account of the same frames agrees: the worst age it reported for any frame at the
moment it serialised it was 5 ms. Two numbers from opposite sides of the same event, which is what
would catch an age stamped at the wrong moment.

**Not included: the socket.** `StationApplication` is a `WebApplicationFactory`, so its transport is
in memory. What is missing is the kernel's loopback and, in the deployed stack, nginx — and the
smoke suite's `Stream_SurvivesTheProxy` shows the proxy does not buffer the stream, which is the
failure mode that would matter here.

---

## The browser half — 11.7 ms median to the DOM, 40.3 ms worst to the next frame

Measured by hand against the running console: local API, twelve simulator processes from
`tools/fleet-at-twelve.ps1`, the Vite dev server, Chrome on a 60 Hz display, all on one machine.
Thirty seconds, 633 frames.

A second `EventSource` on the same stream provides the arrival time, and a `MutationObserver` on the
fleet panel provides the moment the row carries the new position — both from the page's own
`performance.now()`, so this is one clock throughout. Matching is on the rendered value: a frame
counts as displayed when the panel shows *that* position, formatted as the panel formats it, not
when some update happened.

```
633 frames, 12 vehicles
  arrival -> row shows the position      median 11.7 ms   p95 16.8 ms   worst 34.0 ms
  arrival -> the animation frame after   median 17.2 ms   p95 20.5 ms   worst 40.3 ms
```

The second row is an **upper bound on paint**: the console applies updates inside a
`requestAnimationFrame` callback, which paints at the end of that frame, so a callback scheduled
from the mutation runs strictly after the pixels. The real paint is somewhere below it.

The median of ~12 ms is not work — it is waiting for the next frame. `coalesce.ts` defers every
update to an animation frame on purpose, so at 60 Hz a frame arriving at a uniformly random moment
waits 8.3 ms on average before anything is drawn, and the measured median sits where that predicts.
**Removing the coalescer would buy about ten milliseconds of a one-second budget and cost the
property that the marker and its row can never disagree**, which is not a trade worth making.

**632 of 633 frames reached the panel.** The one that did not was coalesced away: two frames from
the same vehicle landed inside one animation frame and only the newer was drawn. That is the
coalescer working as designed — nothing an operator could have seen was dropped, since the state
that replaced it was newer — and it is recorded here because the ratio is the thing to watch. One
in six hundred is a fleet reporting faster than the display refreshes, occasionally. A large
fraction would mean the display had stopped keeping up.

**Measured on the dev server**, whose modules are unbundled and unminified. A production build is
the faster case, so this is the conservative number.

### One other thing the same session recorded

With the console up at twelve vehicles, the page's own resource list was read out:

```
performance.getEntriesByType('resource')  ->  37 requests, 1 distinct origin
                                              http://localhost:5173
```

That is the observation MCS-009 cites. It is worth being exact about what it does and does not
show: it is one page load plus thirty seconds of flying, on the dev server, by one person. It
proves that nothing reached elsewhere **during that session** — it is not a proof that nothing ever
will, which is what the `default-src 'self'` policy is for and what a browser-driven check in CI
would be for.

### These are one run's numbers

Re-running the station half gives a different set — a second run on the same machine produced
`median 0.43 ms, p95 1.89 ms, worst 6.78 ms`, and the p95 in particular moves by several times
between runs, because at these magnitudes it is measuring whichever frame collided with a garbage
collection. The stable claim is the order of magnitude and the assertion that guards it, not the
digits. Quote the digits as "about a millisecond through the station and about a frame in the
browser", which is what they support.

---

## The total, and what is still unmeasured

```
  station     worst   7.05 ms
  browser     worst  40.30 ms
              -----  --------
  sum                47.35 ms   against MCS-001's 1000 ms
```

Adding two worst cases that did not occur on the same frame overstates the real worst case, which is
the safe direction. The typical figure is nearer 18 ms.

**The socket between the two halves is not measured.** The station half ends at an in-memory
transport and the browser half begins at an `EventSource` message, so the loopback read — and nginx
in the deployed stack — falls between them. It would have to cost twenty times the entire rest of
the path to threaten the budget, on a link that is loopback or a compose bridge. Closing that gap
properly needs one clock spanning both processes, which is the thing this system does not have and
should not grow.

The honest summary is therefore: **the station spends about 1% of MCS-001's budget and the browser
about 4%, with the remainder unaccounted for but bounded by a local socket.** If either number ever
approaches its share, the first thing to suspect is the fan-out holding the store's gate, and the
second is the console re-rendering twelve rows for one vehicle's frame.
