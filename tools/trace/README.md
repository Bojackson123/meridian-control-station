# tools/trace

Checks every row of [`docs/requirements.md`](../../docs/requirements.md) against the evidence it
claims, and fails if they disagree.

- A row whose **Method** includes `Test` needs at least one test that **reported passing** and
  carries `[Verifies("MCS-NNN")]` — or, in the console, that id in brackets in its title.
- A row whose Method includes `Inspection`, `Demonstration` or `Analysis` needs an `evidence:`
  link that still resolves.
- A tag naming an id that is not in the table fails.
- A row that says **not verified — &lt;reason&gt;** passes, and is checked in the other direction:
  if tagged tests pass against it, the row is stale.

It reads **test results**, never test source. A tag on a skipped test, or on a test whose suite
dropped out of CI, satisfies every naive check and proves nothing; that is the same failure
`MCS_SMOKE_REQUIRED` exists to prevent, one layer up.

## Running it

The tool consumes one directory. Producing that directory means running every suite — including
the smoke suite, which needs the compose stack up, because MCS-009's only test evidence lives
there. Without the stack, `[SmokeFact]` skips, and the trace fails saying the tagged test was
skipped. That is the correct answer, not a bug in the run.

```bash
dotnet build -c Release

for s in Core Api Adapters Simulator Integration; do
  dotnet test tests/Mcs.$s.Tests -c Release --no-build \
    --logger "trx;LogFileName=Mcs.$s.Tests.trx" \
    --results-directory "$PWD/artifacts/test-evidence"
done

(cd web && npm run test -- --reporter=default --reporter=junit \
    --outputFile.junit=../artifacts/test-evidence/web.junit.xml)

docker compose --env-file .env -f deploy/compose/compose.yaml up -d --build --wait
MCS_SMOKE_REQUIRED=1 dotnet test tests/Mcs.System.Tests -c Release \
  --logger "trx;LogFileName=Mcs.System.Tests.trx" \
  --results-directory "$PWD/artifacts/test-evidence"

cp tests/*/bin/Release/net10.0/Mcs.*.Tests.dll artifacts/test-evidence/

dotnet run --project tools/trace -- --evidence artifacts/test-evidence
```

`artifacts/` is git-ignored. `--reporter=default` on the vitest line is load-bearing: naming a
reporter replaces the console one, so without it a failing console test is a red step with nothing
under it.

In CI this is three jobs rather than one command — `build-and-test` and `smoke` each upload what
they produced, and a `trace` job merges the two and runs the last line.

## Why the assemblies travel with the results

The tags are read out of the built `*.Tests.dll` with `MetadataReader`, which reads the file as
bytes: nothing is loaded, nothing is executed, and none of a suite's dependencies need to be
present. That is what lets a bare `.dll` move between CI jobs beside its `.trx`.

The alternative — reading `[Verifies]` out of the `.cs` files — needs a second, approximate C#
parser, and cannot tell a live attribute from a commented-out one. That failure points at green,
which is the wrong direction for this particular tool.

An xUnit `[Trait]` would have removed the need for the assemblies entirely, and does not survive:
the TRX writer fills `<TestCategory>` only from the property MSTest sets, and the xUnit adapter
never sets it.

## What it does not catch

The contiguity check is a ratchet against **deleting** a requirement to make the table green. It
is not a ratchet against **downgrading** one — nothing here stops a row moving from `verified` to
`not verified — <reason>`. Catching that needs the file's history, which this tool does not read;
the guard is that the downgrade is a visible line in a diff.
