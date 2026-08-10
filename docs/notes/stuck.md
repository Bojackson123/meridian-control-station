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
