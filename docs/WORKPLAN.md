# Work plan — `letsgolegacy.secondkey` (core)

The core repository holds every Second Key component that has no reason of its own to
live elsewhere. In phase 01 that is the whole verification chain except the gate:
CLI, HTTP capture, contract engine, replay, comparator and the unsigned evidence pack.

Ticket IDs match the cross-repository backlog. A ticket is one pull request; its
"done when" line is the acceptance criterion and is not paraphrased in the PR.

## The product interface: artifacts between components

Every component reads and writes files, never another component's internals. Any
component can be replaced as long as it reads and writes the same artifacts.

| Artifact | Format | Produced by | Consumed by |
|---|---|---|---|
| `*.skcap` | JSONL events, one per line, correlated by request id | C1 capture | C2 miner, C6 replay |
| `contract.yaml` | YAML validated by JSON Schema | C2 miner + a human | C3 engine, C7 comparator, C9 mutations |
| `standards/` | Markdown → skills + NuGet | C4 (`letsgolegacy.secondkey-standards`) | migration agent, C5 gate |
| `*.sarif` | SARIF 2.1 | C5 gate (`letsgolegacy.portcullis`) | C10 evidence |
| `*.skrun` | JSONL results, both sides per scenario | C6 replay | C7 comparator |
| `verdict.json` | JSON | C7 comparator, C9 mutations | C10 evidence, C11 portal |
| `evidence.dsse` | in-toto / DSSE | C10 evidence (signed from phase 03) | C11 portal, auditor |

## Design rules this repository enforces

1. The verdict is deterministic: pass/fail is decided only by code with no language
   model in it. A model may draft contract clauses or triage diffs; it never decides.
2. Everything runs inside the customer's boundary; zero egress by default.
3. Only technologies the buyer already runs: .NET, SQL Server, Entra ID, GitHub or
   Azure DevOps, Kubernetes.
4. The legacy system is untouchable: capture needs no code change and no recompilation.
5. Evidence is a cryptographic artifact, not a dashboard.
6. No identity service of our own; the customer's IdP is used.
7. Every step runs from the CLI in a pipeline; a portal is a window, not the interface.

## Phase 01 — demo (gate: a public evidence pack on nopCommerce 3.x)

Critical path: S0 → S1 → S3–S6 → S7–S14 → S15 → (bench P3) → (bench P4–P8) → (bench P9).

| ID | Deliverable | Done when | Status |
|---|---|---|---|
| S0 | Master prompt for S1–S3 (bootstrap and schemas) | It passes the self-containment test of `GENERATE-MASTER-PROMPT.md` §6 | done (#1) |
| S1 | Bootstrap: repository baseline (hygiene files, secret scanning, one-command onboarding, `.claude/settings.json` declaring architecture-standards), empty solution, CI with build, tests and a Stryker.NET threshold | CI is green | done (#1) |
| S2 | `docs/reference-notes.md`: what is ported from agent-eval-bench, judge-worker and ArchGate (now Portcullis), with file paths | Written, before any code | done (#1) |
| S3 | JSON Schema, sample and validator test for `*.skcap` | The sample validates and a broken line is rejected | done (#2) |
| S4 | JSON Schema and sample for `contract.yaml`: predicates on the response, DB deltas and outbound calls, absence assertions, tolerances, set semantics, per-clause provenance | The sample validates | done (#2) |
| S5 | JSON Schema and sample for `*.skrun` | The sample validates | done (#2) |
| S6 | JSON Schema and sample for `verdict.json` with the four classes | The sample validates | done (#2) |
| S7 | C0 CLI `sk`: System.CommandLine, `secondkey.yaml`, six subcommands as stubs, documented exit codes | `sk --help` lists all six, and each stub returns its exit code under test | done (#3) |
| S8 | C3 contract engine: `contract.yaml` evaluated against `*.skrun` into per-clause verdicts | Fixtures pass and a mutation score is reported | done (#3) |
| S9 | C1 HTTP capture: YARP recording proxy writing `*.skcap`, inbound only | A request through the proxy to a sample app produces a valid `.skcap` line | done (#4) |
| S10 | C6 replay: `*.skcap` replayed against two base addresses, `*.skrun` written | Both sides are recorded for the sample | done (#4) |
| S11 | C6 state reset: SQL Server snapshot restored before each scenario | A test proves two consecutive scenarios start from the same state | done (#4) |
| S12 | C7 canonical JSON and normalization (ignored paths, tolerances, sets, timestamps, GUIDs) | Normalization tests pass | in review (#5) |
| S13 | C7 diff and four-class verdict into `verdict.json` | Fixtures produce both a regression and a fix candidate | in review (#5) |
| S14 | C10 unsigned evidence pack: standalone HTML from `verdict.json`, SARIF and the contract; PDF through a headless browser | The pack renders from fixtures | in review (#5) |
| S15 | End-to-end job: capture → replay → compare → evidence on the sample app, in CI | One CI job produces the pack | in review (#5) |
| S16 | C6 outbound stubs with WireMock.Net from recorded traffic | Phase 01 only if the specimen calls out (payments, shipping); otherwise phase 02 | phase 02 — neither the sample shop nor nopCommerce's demo configuration calls out |

## Phase 02 and later (starts after the phase 01 gate)

| Component | Work | Phase |
|---|---|---|
| C2 contract miner (`sk mine`) | Daikon-style invariants from recordings; LLM drafts through `IChatClient`; clause lifecycle proposed → accepted as a PR to `contract.yaml`; provenance and statistical support per clause; silence on small samples | 02 |
| C9 mutation engine (`sk mutate`) | Scenarios as an xUnit project over `WebApplicationFactory` so Stryker.NET can mutate the migrated code; LLM semantic mutants aimed at contract clauses; kill rate per clause; untested clauses flagged | 02 |
| C1 SQL/queues/PII | Moves to `letsgolegacy.secondkey-capture` (Windows/IIS side, own release cycle) | 02 |
| C8 advisory triage | C# port of judge-worker's calibration (Cohen's kappa on reviewer labels, no number on a small sample); `Microsoft.Extensions.AI` structured output; never changes the verdict | 02–03 |
| C10 signing | in-toto attestations in DSSE envelopes, cosign with the customer's KMS/HSM key, no public transparency log, OCI (ORAS) or WORM storage | 03 |
| Contracts library | Reusable contract fragments across systems | 03 |

## Known hard problems (tracked, not hidden)

- C1: attributing SQL statements to an HTTP request without code changes is a
  heuristic with an explicitly reported margin of uncertainty.
- C2: false invariants from small samples; report support per clause, stay silent on
  thin data.
- C6: time and randomness in legacy code (`DateTime.Now`, `Guid.NewGuid()`) without DI
  are handled by normalization in C7, not by touching the legacy system.
- C9/C5: run time on large solutions; mutate and analyse only code changed in the PR.
- C10: the report form an auditor accepts is agreed with the first partners, not guessed.
