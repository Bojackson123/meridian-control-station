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
