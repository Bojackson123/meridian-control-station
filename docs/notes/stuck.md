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
