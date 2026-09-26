# Analysis — phase 01, tickets S1–S16

Written by the ticket-analysis phase (`architecture-standards/docs/delivery/TICKET-ANALYSIS.md`)
for the core repository. It is the source the master prompt
([`S1-S3.master-prompt.md`](S1-S3.master-prompt.md)) is generated from; corrections go
here first, then the prompt is regenerated (GENERATE-MASTER-PROMPT.md §5).

**Revision:** 4 (2026-09-26).

## 1. The change

Build the phase 01 verification chain of Second Key — capture HTTP traffic from a legacy
system, replay it against the legacy system and a migrated candidate under identical
conditions, compare the two field by field against a written contract, and produce an
evidence pack — as a CLI (`sk`) over versioned file artifacts. Owning context: the
Second Key core (this repository). The gate (C5) and the standards pack (C4) are other
repositories and are consumed only through their artifacts (SARIF, skills).

## 2. The table

Acceptance criteria are copied verbatim from [`../WORKPLAN.md`](../WORKPLAN.md).

| Ticket | Acceptance criterion | Owning context and layer | Governed by | Guide to load | Files | Blocking question |
|---|---|---|---|---|---|---|
| S1 | CI is green | Repository root: build, CI, onboarding | P5; P13 | `REPO-BASELINE.md` §1–§3, §7; `TESTING-STRATEGY.md` §9 | `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `SecondKey.slnx`, `.editorconfig`, `.gitattributes`, `.gitleaks.toml`, `.config/dotnet-tools.json`, `stryker-config.json`, `.github/workflows/{ci,secret-scan,codeql}.yml`, `.github/dependabot.yml`, `.github/CODEOWNERS`, `.github/pull_request_template.md`, `scripts/setup.sh`, `scripts/scan-secrets.sh`, `scripts/hooks/pre-commit`, `.claude/settings.json`, `src/SecondKey.Artifacts/ToolInfo.cs`, `tests/SecondKey.Artifacts.Tests/ToolInfoTests.cs` | none |
| S2 | Written, before any code | Documentation | none — process | `GENERATE-MASTER-PROMPT.md` §0 (every claim cites its source) | `docs/reference-notes.md` | none |
| S3 | The sample validates and a broken line is rejected | `SecondKey.Artifacts` — format layer | P13 | `TESTING-STRATEGY.md` §5–§6 | `schemas/skcap.schema.json`, `schemas/samples/sample.skcap`, `src/SecondKey.Artifacts/Schemas/ArtifactSchemas.cs`, `src/SecondKey.Artifacts/Capture/*.cs`, `tests/SecondKey.Artifacts.Tests/SkcapTests.cs` | none |
| S4 | The sample validates | `SecondKey.Artifacts` — format layer | none — domain format | — | `schemas/contract.schema.json`, `schemas/samples/contract.yaml`, `src/SecondKey.Artifacts/Contracts/*.cs`, `tests/SecondKey.Artifacts.Tests/ContractTests.cs`, `docs/formats/contract.md`, `docs/adr/0002-contract-language.md` | closed — predicate vocabulary decided in ADR 0002 |
| S5 | The sample validates | `SecondKey.Artifacts` — format layer | none — domain format | — | `schemas/skrun.schema.json`, `schemas/samples/sample.skrun`, `src/SecondKey.Artifacts/Runs/*.cs`, `tests/SecondKey.Artifacts.Tests/SkrunTests.cs` | none |
| S6 | The sample validates | `SecondKey.Artifacts` — format layer | Design rule 1 (deterministic verdict) | — | `schemas/verdict.schema.json`, `schemas/samples/verdict.json`, `src/SecondKey.Artifacts/Verdicts/*.cs`, `tests/SecondKey.Artifacts.Tests/VerdictTests.cs`, `docs/adr/0003-verdict-classes.md` | closed — the four classes and the fail-closed rule decided in ADR 0003 |
| S7 | `sk --help` lists all six, and each stub returns its exit code under test | `SecondKey.Cli` — the product's interface | Design rule 7 (every step from the CLI) | — | `src/SecondKey.Cli/Program.cs`, `src/SecondKey.Cli/Commands/*.cs`, `src/SecondKey.Cli/ExitCodes.cs`, `src/SecondKey.Cli/Configuration/*.cs`, `docs/cli.md`, `tests/SecondKey.Cli.Tests/*.cs` | none |
| S8 | Fixtures pass and a mutation score is reported | `SecondKey.Contract` — domain | P13; design rule 1 | `TESTING-STRATEGY.md` §6; `AI-EVALS.md` (no model in the verdict path) | `src/SecondKey.Contract/*.cs`, `tests/SecondKey.Contract.Tests/*.cs`, `stryker-config.json`, `scripts/mutation.sh` | none |
| S9 | A request through the proxy to a sample app produces a valid `.skcap` line | `SecondKey.Capture` — edge in front of the legacy system | P11 (anti-corruption edge); P5 (recordings may carry secrets); design rule 4 (legacy untouched) | `SECURITY-REVIEW.md` | `src/SecondKey.Capture/*.cs`, `samples/SampleShop/*`, `tests/SecondKey.Capture.Tests/*.cs` | none |
| S10 | Both sides are recorded for the sample | `SecondKey.Replay` — application | none — domain | — | `src/SecondKey.Replay/*.cs`, `tests/SecondKey.Replay.Tests/ReplayEngineTests.cs` | none |
| S11 | A test proves two consecutive scenarios start from the same state | `SecondKey.Replay` — persistence edge | P4 (persistence behind a boundary) | `TESTING-STRATEGY.md` §4 (tier 2: a real database per run) | `src/SecondKey.Replay/Resets/*.cs`, `tests/SecondKey.Replay.Tests/ResetTests.cs`, `tests/SecondKey.Replay.Tests/SqlServerTests.cs` | accepted risk A1 |
| S12 | Normalization tests pass | `SecondKey.Compare` — domain | none — domain logic | — | `src/SecondKey.Compare/CanonicalJson.cs`, `src/SecondKey.Compare/ComparableDocument.cs`, `src/SecondKey.Compare/JsonDiff.cs`, `src/SecondKey.Compare/Normalizer.cs`, `tests/SecondKey.Compare.Tests/NormalizerTests.cs` — the diff moved here from S13, because a normalization rule is tested by the differences it explains | none |
| S13 | Fixtures produce both a regression and a fix candidate | `SecondKey.Compare` — domain | Design rule 1 | — | `src/SecondKey.Compare/Comparator.cs`, `src/SecondKey.Cli/Commands/CompareCommand.cs`, `tests/SecondKey.Compare.Tests/ComparatorTests.cs`; ADR 0003 amended (a fix candidate is decided before equality) | none |
| S14 | The pack renders from fixtures | `SecondKey.Evidence` — output edge | Design rule 5 (evidence is an artifact, not a dashboard) | `SECURITY-REVIEW.md` (what a report may contain) | `src/SecondKey.Evidence/*.cs`, `tests/SecondKey.Evidence.Tests/*.cs`, `src/SecondKey.Cli/Commands/{Gate,Evidence}Command.cs`, `samples/gate/portcullis.sarif` (synthetic, in Portcullis's shape) | accepted risk A2 |
| S15 | One CI job produces the pack | CI + sample | `TESTING-STRATEGY.md` §9 | `TESTING-STRATEGY.md` §3 | `.github/workflows/e2e.yml`, `scripts/e2e.sh`, `samples/SampleShop/*`, `samples/contract.yaml` | none |
| S16 | Phase 01 only if the specimen calls out (payments, shipping); otherwise phase 02 | `SecondKey.Replay` — outbound edge | P11 | — | deferred | closed — nopCommerce 3.90 with sample data uses offline payment and shipping methods, so the specimen makes no outbound calls; S16 moves to phase 02 |

### Out of scope, and why

- **C2 contract miner and C9 mutation engine**: `sk mine` and `sk mutate` are stubs in
  phase 01 (S7); both are phase 02 components.
- **Signing** (in-toto/DSSE with the customer's key) and WORM storage: phase 03. The
  phase 01 pack carries SHA-256 digests and an unsigned in-toto statement so signing
  later changes no format.
- **SQL and queue capture, PII tokenization**: phase 02, in `letsgolegacy.secondkey-capture`.
- **Any model in the verdict path**: excluded by design rule 1, permanently.

## 3. What the change puts at risk

| Risk | Kept by |
|---|---|
| Recordings (`*.skcap`, `*.skrun`) carry customer data into git | `.gitignore` excludes them except for synthetic samples and fixtures; the capture redacts configured headers before writing |
| A secret lands in a commit | gitleaks as a pre-commit hook **and** a CI job (P5, REPO-BASELINE §2) |
| A test entry point nobody runs | Every test project is in the solution and run by `ci.yml`; the e2e script is run by `e2e.yml` (TESTING-STRATEGY §9) |
| A verdict that depends on a model or on wall-clock time | The comparator is pure; time and random values in responses are masked by contract normalization, not by touching the legacy system |

## 4. Decisions and accepted risks

| Id | Decision or accepted risk | Who | Cost if wrong |
|---|---|---|---|
| D-1 | S7 defines six subcommands; `compare` and `validate` are added as two more because the chain needs them and the ticket does not forbid it | implementing session, recorded here | a CLI surface larger than the ticket, revisited in review |
| D-2 | A difference that no contract clause explains is classified as a **regression** (fail closed) — ADR 0003 | implementing session | more human review, never a missed regression |
| D-3 | nopCommerce serves HTML, so the contract language extracts values from HTML by CSS selector before any predicate runs; no predicate reads prose — ADR 0002 | implementing session | selectors break when markup changes; that breaks loudly as `unexercised`, not silently |
| A1 | The SQL Server snapshot test needs Docker; CI (ubuntu-latest) has it. Locally, without Docker, the test reports itself skipped with the reason; in CI `SK_REQUIRE_DOCKER=1` turns a missing Docker into a failure. `SK_SKIP_DOCKER=1` skips it on purpose, with the reason, and wins over the `SK_REQUIRE_DOCKER=1` every CI job inherits: the mutation job sets it, because the SQL Server code is excluded from mutation | implementing session | none in CI; a local run proves less than CI |
| A2 | PDF rendering needs a headless Chromium. CI has one; `SK_REQUIRE_PDF=1` in CI makes its absence a failure, locally it is reported as skipped | implementing session | none in CI |
| A3 | Since 26 IX 2026, on the owner's standing instruction, the implementing session merges a pull request once every check on its head is green; stacks merge in order, with merge commits. No person reviews a pull request before it merges: the checks — tests, mutation thresholds, the end-to-end job, CodeQL, the secret scan — are the review | owner | a defect no check catches reaches `main` unreviewed; mutation testing bounds what the tests miss on the paths it covers, and nothing more |

## 5. Revision log

| Run | What changed | What closed it | Still open |
|---|---|---|---|
| 1 | First pass: table for S1–S16, decisions D-1–D-3, accepted risks A1–A3 | — | none blocking |
| 2 | The replay joins mutation testing: `SecondKey.Replay` without its SQL Server code, from 60.7 % to 89.5 %. Tests now prove what a replay does not send (hop-by-hop headers, recorded cookies, redacted values, a recorded `content-length`), that a redirect is recorded rather than followed, that a base URL's path prefixes every request, and that correlation leaves bodies it cannot read alone. A1 gains `SK_SKIP_DOCKER`; A3 records how pull requests are merged now | this revision's pull request | the SQL Server reset and probe are covered by integration tests only |
| 3 | A1 held in the mutation job only on paper: it inherits `SK_REQUIRE_DOCKER=1`, which overrode `SK_SKIP_DOCKER=1`, so its SQL Server tests ran against a container. The deliberate skip now wins, and the attribute and the container fixture share one decision (`DockerGate`), so a skipped test never starts a container | this revision's pull request | none |
| 4 | The S8 and S11 rows name the files that were built: one `stryker-config.json` and `scripts/mutation.sh` at the root for every mutation-tested project, `Resets/` with `ResetTests.cs` and `SqlServerTests.cs` — the names the first pass planned were never created | this revision's pull request | none |
