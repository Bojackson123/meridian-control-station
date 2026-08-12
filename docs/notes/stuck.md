# stuck.md

A running log kept during build. Format: date, symptom, what I tried, what it was.
Not a polished document — the point is that it is contemporaneous.

---

## 2026-08-07 — NU1903 on a transitive package, on the first build

**Symptom.** First `dotnet build` of the fresh scaffold came back green but with four
NU1903 warnings: `Microsoft.OpenApi` 2.0.0 has a known high severity vulnerability
(GHSA-v5pm-xwqc-g5wc / CVE-2026-49451). Flagged in Mcs.Api and, oddly, in
Mcs.Integration.Tests — a project I had not added any OpenAPI package to.

**What I tried.**
1. Read the advisory rather than reacting to the word "high". It is a stack overflow
   during parsing of a document containing a circular `$ref`. Impact is availability
   only — the report explicitly disclaims RCE, auth bypass, and credential exposure.
   One aggregator page titles it "Remote Code Execution"; the upstream advisory does
   not say that. Went to the source.
2. Traced where it came from. I never referenced it directly — it arrives via
   `Microsoft.AspNetCore.OpenApi` 10.0.0, which the `webapi` template pulls in.
   Integration.Tests inherits it through its project reference to Mcs.Api.
3. Asked the reachability question before the patch question: the vulnerable path is
   the *reader*. Mcs.Api generates a spec; it does not parse untrusted OpenAPI
   documents. Vulnerable code present, not reachable from my call graph.
4. Pinned forward anyway: `dotnet add src/Mcs.Api package Microsoft.OpenApi
   --version 2.7.5`. Pinned the version explicitly instead of taking latest, because
   there is also a 3.x line and AspNetCore.OpenApi 10.0.0 expects 2.x — an unpinned
   add would have swapped a warning for a build break.

**What it was.** A transitive supply-chain warning, unreachable in this codebase, but
worth fixing on day one regardless: NuGetAudit promotes NU1903 to a hard error under
`--warnaserror`, and Release builds set that by default. Left alone it would have
surfaced later as a red CI run, where I would have been debugging it under "why is the
smoke suite failing" instead of "what is this warning".

**Carry forward.** Advisory published 2026-06-30, newer than the .NET 10 templates.
Any freshly scaffolded webapi project will have this until the templates update.

---

## 2026-08-08 — which half of the write goes inside `_subscriberGate`

**Symptom.** Not a failure — I could not talk myself through the ordering note left on
`InMemoryTelemetryStore.Write`. I had written the append outside the gate and the
fan-out inside it, which looked like it satisfied "the fan-out is under the gate", and
I could not see what was left to decide.

**What I tried.**
1. Wrote out the three placements instead of arguing about them in the abstract. The
   gate orders the rings against the subscriber list, and a write touches both — the
   append changes what a future seed contains, the fan-out reaches whoever is already
   registered. `Subscribe` holds the gate across seed-and-register, so anything not
   inside the gate is a window it can slip into.
   - append outside, fan-out inside (what I had): Subscribe seeds *after* the append
     and registers *before* the fan-out → the subscriber gets the frame twice.
   - fan-out inside, append after: Subscribe registers *after* the fan-out and seeds
     *before* the append → the subscriber never gets it at all. HAZ-01.
   - both inside: exactly once whichever side wins.
   The "or, worse" in the note is not two symptoms of one bug. It is one failure mode
   per half-measure.
2. Found the part that has nothing to do with `Subscribe`, which is what I had actually
   been missing. The ring's lock orders two concurrent appends; the gate orders two
   concurrent fan-outs; nothing tied those two orders together. Thread A appends F1 and
   is preempted, B appends F2 and fans it out, A resumes and fans out F1 — a subscriber
   registered the whole time receives F1 after F2. No subscription race involved. One
   gate across the pair makes delivery order equal append order, globally.
3. Checked whether per-vehicle independence could be kept by holding the *ring's* lock
   across append plus fan-out. It cannot: that takes `_gate` → `_subscriberGate` while
   `Subscribe` takes `_subscriberGate` → reads the rings. Opposite order, deadlock on
   the first subscribe that races a write. Escaping it needs lock-free `Latest` reads,
   which is a much bigger design than 120 writes/second justifies.

**What it was.** The decision the note was pointing at, stated properly: resolve-or-admit,
append and fan out are one critical section. The cost is that writes for different
vehicles now serialise, which is a genuine departure from the per-vehicle independence
claimed on the type — but the fan-out already took the gate on every write, so it is the
same single uncontended acquisition, just held across an array store as well. The ring's
own lock still earns its place against the readers, which never take the gate.

Two consequences I nearly missed. `Subscribe`'s internal order stops mattering, because
no write can occur between its seed and its register. And a newly admitted ring has to be
appended to *before* it goes into `_rings` — `GetLatestSnapshot` enumerates the dictionary
without the gate, so publish-then-append is observable as a ring with nothing in it.

**Carry forward.** `_admissionGate` was dead the moment this landed — every writer holds
`_subscriberGate` across the count-then-add, so the check-then-act it existed to make
atomic already is. Deleted it the same day, which also meant rewriting three paragraphs
of the type remarks that had become false: the "hot path is a per-vehicle lock" sentence,
"why admission needs its own lock", and the claim that a vehicle's writes never block a
different vehicle's. Worth noting how quietly those went stale — the code was correct and
the prose above it was not, and only the prose is load-bearing for the next person.
`VehicleRing.Append` has since landed, so the write path runs end to end — that sentence
sat here claiming otherwise for a day, which is the same failure as the paragraphs above
and worth recording rather than quietly deleting.

---

## 2026-08-09 — a basemap that renders and a map that stays empty

**Symptom.** Two separate things on the same day, both under the offline basemap.

The first was cheap to spot and expensive to believe: the background layer painted, the
scale bar and attribution appeared, and the graticule layer drew nothing. No console error,
no failed request — one request to `/node_modules/.vite/deps/maplibre-gl-worker.mjs` sitting
at *pending* forever. The dev server had said, once, on startup and above a screen of other
output: "the file does not exist in the optimize deps directory."

The second only showed up while checking the first. Zooming out left the grid covering a
rectangle in the middle of the screen with bare map around it — sometimes. Other times the
same zoom settled correctly.

**What I tried.**

1. Read what MapLibre v6 actually does to start its worker, rather than trusting the dev
   server's suggested `optimizeDeps.exclude`. It is `new URL('./maplibre-gl-worker.mjs',
   import.meta.url)`, resolved at runtime. No bundler can see through that, so nothing
   copies the file — and this is not a dev-only problem: `vite build` emitted no worker
   chunk either, so the production bundle had the same silent hole. That is the part worth
   remembering. The dev server's warning pointed at the dev server, and the bug was in both.
2. Rejected copying the worker into `public/` with an asset import. `maplibre-gl-worker.mjs`
   imports `./maplibre-gl-shared.mjs` relatively, so a copy of the worker alone lands next to
   a file that is no longer its sibling. `?worker&url` bundles the pair and returns a URL on
   this origin, which `setWorkerUrl` accepts. Costs 471 KB of duplicated shared code in the
   output; it is served from localhost and it buys a map that works.
3. For the second symptom, assumed stale data and moved the update from `moveend` to `move`,
   reasoning that the memo would reject the frames that had not crossed a cell boundary.
   Wrong, and it made things worse: a zoom animation grows the viewport every frame, so the
   grid genuinely differs every frame, and every one re-indexes the source in the worker.
   The tiles then never finish for as long as the camera moves. Reverted.
4. Stopped reading screenshots and exposed the map on `window` for ten minutes to get
   numbers. That ended it. At every zoom from 2 to 20 the source contained 15–20 features,
   the drawn extent covered the viewport on both axes, and `queryRenderedFeatures` returned
   24–80. The implementation was correct the whole time.

**What it was.** One real bug and one misread. The worker was real, and it was a production
bug wearing a dev-server warning. The rectangle was MapLibre re-tiling the GeoJSON source
after `setData`, photographed mid-flight — every screenshot I took during an ease animation
caught a partial grid, and every one I took after it settled was complete. I spent longer on
the symptom that was not a symptom, because a screenshot is evidence of *something* and it is
easy to keep collecting more of it instead of asking the map what it holds.

Two things did come out of chasing it. The spacing ladder had 5x gaps in it (`0.05` to
`0.01`), and "coarsest spacing with at least four divisions across the screen" turns a 5x gap
into twenty divisions on screen — visible as a grid far denser than intended at some zooms,
and invisible at others, which is why it took a screenshot to notice at all. A 1-2-5 ladder
bounds it at ten. And the meridians were being cut at the last whole parallel, so at 30°
spacing they stopped at 60° and left the top of a world-scale view empty; the projection
limit bounds how far a meridian is *drawn* but removes a parallel outright, and collapsing
both into one pair of indices had quietly conflated them.

**Carry forward.** The pure parts of the graticule are exercisable from Node with
`--experimental-strip-types` against the real `.ts` file, no test runner and no build. Both
the ladder bound and the coverage property were found that way in seconds after a morning of
squinting at JPEGs. `web/` still has no test framework; when it gets one, `chooseSpacingDegrees`
and `gridFor` are already written to be called directly and should be the first things in it.

---

## 2026-08-09 — a stream that starves the map, and one that lies about being alive

**Symptom.** Three faults stacked on one another while putting the vehicle on the map, and the
first two look identical from a screenshot.

1. With the telemetry client wired in, the console rendered the background and *nothing else*
   — no graticule, no vehicle — and stayed that way. Not slow: 45 seconds in, `isStyleLoaded()`
   was still false and `map.on('load')` had never fired, so neither data layer had been
   attached. Exactly the picture the previous entry's worker bug produced, which cost me a
   while: I had a ready-made explanation and it was the wrong one.
2. Stopping the API left the marker frozen where it was, no console error at all, and
   restarting the API did not bring it back. A reload did.
3. Loading the page against an API that was already down was a permanent failure, not a
   delayed one — nothing ever appeared, even minutes after the API came back.

**What I tried.**

1. Chased the pending worker request again, since the symptom matched. It was a red herring
   twice over: the extension's network panel reports worker-context requests as `pending`
   whether or not they completed, and a plain `fetch()` of the same URL returned 131 KB in
   22 ms. The dev server was never the problem.
2. Assumed my code had thrown inside the `load` handler and swallowed it in a promise. It had
   not — no error anywhere, and the handler simply had not been called.
3. Stashed the whole change and reloaded. **This is what ended it.** The pre-change code
   rendered the graticule in about six seconds on the same dev server, same page, back to
   back. That converted "a flaky dev environment" into "my change does this", which is the
   only useful form of the question.
4. Then read the connection ordering rather than the code: the client opened the `EventSource`
   in the effect body, *before* the map had loaded.

**What it was.**

The stream was starving the map. MapLibre fetches its worker script at low priority, and an
SSE response is one that never completes, so the browser's scheduler left the worker request
queued behind it indefinitely. No worker means no GeoJSON source ever finishes loading, which
means `load` never fires, which means neither layer is ever attached. Opening the connection
inside the `load` handler instead fixes it outright, and is now commented in `App.tsx` as a
constraint rather than a preference — the parallelism I was buying was worth a few hundred
milliseconds and cost the entire basemap.

The frozen marker was a different lie. Two of them, in fact, and `EventSource` only recovers
from the third:

- an established connection that *drops* is retried by the browser, on its own schedule, and
  needs no help;
- a connection *attempt* answered with an HTTP status — which is what any proxy in front of a
  stopped API produces — makes the spec fail the connection permanently. Measured:
  `readyState` 2, one error event, no further attempts, ever. This is not an edge case, it is
  what a restarting station looks like from the browser;
- a connection the proxy holds open after the upstream has died produces *no event at all*.
  The dev server did this for 33 seconds without a murmur, and would have done it forever.

The last one is the dangerous one, because it is precisely HAZ-01: an operator watching a
console that is confidently displaying a position from several minutes ago. It is also the
one the station already had the mechanism for and the console was ignoring — the 15-second
heartbeat exists so the stream is never idle, which makes silence *itself* the fault signal.
The client now treats 40 seconds without any event, heartbeat included, as a dead stream and
reopens it. All three cases are commented where they are handled.

**Carry forward.** Every reopen re-fetches the snapshot as well as resubscribing, so the
fleet is corrected in one request rather than converging vehicle by vehicle as each reports.
And the console still shows the last known positions during an outage with nothing on screen
to say so — the browser console is the only place a disconnect is visible today. That is
MCS-002's job and needs the state language designed before it can be built, but it is the
gap I would close first.

---

## 2026-08-10 — the smoke assertion I could not make fail

**Symptom.** The compose smoke suite hits the SSE stream twice on purpose: once straight at
the API, once through nginx. The second exists for one reason — a proxy that buffers the
stream passes the direct test and delivers nothing to a console — so before believing it I
went to reproduce the failure it was written for. Set `proxy_buffering on;` in the `/api`
block, rebuilt the web image, ran the suite. All eight green. The assertion written to catch
buffering did not notice buffering.

**What I tried.**

1. Suspected `X-Accel-Buffering: no`, which the API sends and nginx honours regardless of
   `proxy_buffering`. Added `proxy_ignore_headers X-Accel-Buffering;` so both belts were off
   at once. Still all eight green.
2. Stopped guessing and measured, with `curl -N` timestamping each `data:` line through the
   proxy and directly. Proxied: events at 30.13, 30.85, 31.85, 32.80, 33.74 — one per
   second. Direct: the same, to within tens of milliseconds. With buffering fully on, nginx
   was adding no delay at all.
3. Checked whether the app-side header at least survives the hop, so the suite could assert
   on it instead. It does not — nginx consumes `X-Accel-Buffering` and it is absent from the
   proxied response headers. Nothing to assert.
4. Went looking for a proxy-only fault the assertion *does* catch, and the config names one
   itself: `proxy_pass` goes through a variable, so a wrong service name is a 502 at request
   time rather than a refusal to boot. Pointed it at `api-typo:8080`. The proxied stream test
   failed with "answered 502" and the direct one stayed green — which is the discrimination
   the assertion exists to provide, just not via the mechanism I expected.

**What it was.** `proxy_buffering on` does not mean nginx withholds a response until a buffer
fills. It means nginx is *allowed* to read from the upstream faster than the client drains,
so that a slow client cannot hold an upstream connection open. Against a fast client on a
local socket there is nothing to decouple, and each proxied read goes straight out. The
burst-or-nothing symptom the setting is famous for needs a slow or distant client, a
compressing filter, or payloads that make the buffer arithmetic bite — none of which a smoke
test running next to the container reproduces.

**Carry forward.** The `proxy_buffering off;` line stays: it is correct, it is free, and the
failure it prevents is real for a browser two networks away even though it is invisible from
here. But nothing in the suite guards it, and the honest statement of what the proxied stream
test covers is "the proxy is present, resolves, and passes an event stream through" — not
"the proxy is not buffering." Worth knowing before someone deletes the line on the strength
of a green suite.

The general lesson is cheaper than the specific one: the suite would have shipped green,
with a comment claiming it guarded something it did not, if I had trusted the assertion
instead of spending twenty minutes trying to make it fail. A test written for a specific
failure mode and never run against that failure mode is a guess with a green tick on it.

---

## 2026-08-11 — pymavlink will not install, and the newest version is the reason

**Symptom.** `pip install pymavlink` into a fresh 3.12 venv failed building a wheel for
`fastcrc`, a transitive dependency. The real error was several screens up from the one pip
prints last: `fastcrc` is a Rust extension, cargo was invoked to build it from source, and
`link.exe` failed with `link: extra operand ...  Try 'link --help'` — a GNU `link` on PATH
answering for MSVC's linker.

Not a surprise, which is why the vectors were done first: they need a second toolchain, and a
second toolchain is the most likely thing in this work to be annoying offline. Better to find
that out on day one than in week three.

**What I tried.**
1. Read past the last error to the first one. Two failures were reported — `target-lexicon`
   and `memoffset`, both *build scripts*, not the crate itself. A build script failing to
   link means the toolchain is wrong, not the source.
2. Checked the target triple in the cargo output: `aarch64-pc-windows-msvc`. This machine is
   Windows on ARM, and `fastcrc` publishes no win-arm64 wheel, so pip falls back to building
   it. Nothing about the fallback is going to work without a working MSVC link.exe, and
   fixing PATH shadowing to build a CRC library felt like the wrong thing to spend the day on.
3. Went looking for whether the dependency was avoidable rather than fixable. `pip index
   versions pymavlink` lists 2.4.43 as the release that introduced it; 2.4.42 computes its
   CRC in pure Python. Installed 2.4.42 — clean, no build step, imports, packs frames.
4. Confirmed it was a real answer and not a lucky one before building on it: packed a
   HEARTBEAT and checked the frame against the specification by hand, and pulled the
   `crc_extra` values for the four messages the station needs. The wire format is the wire
   format; the version that computes the checksum in C rather than Python does not emit
   different bytes.

**What it was.** A platform gap, not a broken environment. `fastcrc` became a hard dependency
in 2.4.43 and has no wheel for this architecture, so every pymavlink from that release on is
uninstallable here without a C and Rust toolchain configured to agree with each other.

**Carry forward.** The pin in `tools/mavlink-vectors/requirements.txt` is load-bearing and the
reason is written there, because `pymavlink==2.4.42` next to a much newer release otherwise
reads as neglect and the obvious "helpful" change is to bump it. Worth knowing that the pin
costs nothing: this project uses pymavlink to emit reference bytes, and the bytes are defined
by the protocol rather than the library.

The Docker fallback — run the generator in a pinned `python:3.12-slim` — was the plan if this
had not worked, and is still the answer for anyone who hits it on a platform where even 2.4.42
will not build. Not needed here, so not committed; a second path nobody exercises rots.
