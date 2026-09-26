# Reference notes — what Second Key takes from the existing repositories

Ticket S2. Written before any product code, so that every later pull request can say
which of these decisions it implements rather than rediscovering them.

The rule for every entry: **port the model, not the code.** The existing repositories
verify an MCP agent (agent-eval-bench), a search service (listing-search-bench), an LLM
judge (judge-worker) and an architecture (Portcullis). Second Key verifies a migrated
legacy system against its recorded behaviour. What transfers is the method and the
disciplines that made those suites trustworthy; the runtime code does not.

Paths are relative to each repository's root at the commit named in its heading.

## 1. agent-eval-bench → C3 contract format and engine

`github.com/konradcinkusz/agent-eval-bench` @ `12b1bbd` (public, MIT).
At that commit: **38 scenarios, 341 assertions, 68 of them absence assertions (19.9%)**
— counted from `evals/scenarios/**.yaml`, which is the number to quote.

| What | Where | How it lands in Second Key |
|---|---|---|
| Scenarios are data, not code, validated by a strict schema | `evals/schema/scenario.schema.json` (`additionalProperties: false` everywhere) | `schemas/contract.schema.json` is strict the same way: a mistyped key fails loudly instead of being ignored |
| Mutually exclusive fields rejected by the schema, not by review | `scenario.schema.json`, `$defs.assertion` — `tool_called` with both `times` and `at_least` is rejected ("a silent no-op") | The contract schema rejects the equivalent shapes: a predicate with both `value` and `ref`, a tolerance with both `absolute` and `relative` |
| `why` on every scenario — what breaks if it stops passing | `evals/README.md`, "Reading a scenario" | Every clause carries a required `why` |
| Absence assertions are first-class | `tool_not_called`, `event_not_emitted` in `$defs.assertion` | `kind: never` clauses and the `absent` operator; the validator reports the absence share of a contract |
| The two-assertion rule: a refusal is asserted as a refusal **and** an absence | `evals/README.md`, "What the validator enforces"; `scripts/validate-scenarios.mjs` | Semantic validation beyond the schema: a contract with no `never` clause is rejected |
| Validation beyond the schema: duplicate ids, unknown references | `scripts/validate-scenarios.mjs` | `ContractRules`: duplicate clause ids, references to undefined extractors, regexes that do not compile |
| Three evaluation disciplines | `tests/AbsenceConcierge.Evals/Assertions/AssertionEvaluator.cs`, class doc comment | (1) **Nothing matches prose**: predicates run over structured values — status, headers, JSON fields, values extracted from HTML by selector — never over free text. (2) **No assertion passes vacuously**: a clause whose scope never matched is `unexercised`, which is reported, never counted as a pass. (3) **An unrecognised assertion is an error**: an unknown operator fails validation; the engine has no default branch that passes |
| Deliberately broken variants, each with the assertion that must catch it | `tests/AbsenceConcierge.Evals/Mutations/BrokenAgents.cs` | Broken-candidate fixtures in the engine and comparator tests (S8, S13); the full mutation engine is C9 in phase 02. A variant that survives is a missing clause, not a curiosity |
| Where a scenario came from | `origin.kind`: designed, production-trace, manual-session, review, incident | Clause `provenance.origin` keeps those values and adds `mined` (C2) and `llm-draft` |
| A baseline pinned to the specification version; comparison across a contract change is refused | `evals/baselines/layer1.json`, `tests/AbsenceConcierge.Evals/Reporting/Baseline.cs` | `verdict.json` records the contract's name, revision and SHA-256; comparing verdicts across contract revisions is refused (phase 02) |

**Not ported:** the MCP agent runtime, the trace/span model, Layer 2 (the LLM judge in
the verdict path — design rule 1 forbids a model in the verdict), the HR fixture world.

## 2. listing-search-bench → the public specimen's shape

`github.com/konradcinkusz/listing-search-bench` @ `5751287` (public, MIT).
At that commit: **28 scenarios, 128 assertions**, 29 of them exclusions/absences (≈22%).

It is the second specimen of the same method, which is what makes the method
portable rather than tailored. Nothing from it runs in Second Key; it is the pattern for
`letsgolegacy.secondkey-nopcommerce-bench`: a public repository whose evaluation corpus,
numbers and findings are the product's proof, published with its limitations.

## 3. judge-worker → C8 advisory triage (phase 02–03)

`github.com/konradcinkusz/judge-worker` @ `96dce14` (public, MIT, TypeScript).
Only the calibration logic transfers, rewritten in C# (design rule 3: only technologies
the buyer already runs).

| What | Where | How it lands |
|---|---|---|
| Unweighted Cohen's kappa; returns *undefined*, never 1.0, when every pair falls in one category | `src/calibration/cohenKappa.ts` | The same function, with the same refusal to report agreement nobody demonstrated |
| A calibration gate: at least 10 labels and κ ≥ 0.6, otherwise scores are reported and gate nothing | `src/calibration/calibrate.ts`, `CALIBRATION_GATE` | C8 triage stays advisory until calibrated on reviewers' labels, and even calibrated it **never changes the verdict** (design rule 1) |
| Mutant judges that prove the calibration can fail | `src/mutations/mutantJudgeProviders.ts` | Mutant triagers in C8's tests |

**Not ported:** the queue, ingestion, pricing and the worker runtime.

## 4. ArchGate / Portcullis → C5 gate (separate repository)

Private `github.com/konradcinkusz/archgate` @ `4a375b9`, renamed internally to
Portcullis on 2026-09-13; continued publicly as `konradcinkusz/letsgolegacy.portcullis`.

The gate is **not** ported into this repository: it is its own tool with its own
release cycle, and the core only reads its output.

| What | Where | How it lands |
|---|---|---|
| Diff-scoped gate: pre-existing findings are reported, only findings on changed lines block | `docs/DIFF-GATE.md`, `GateResult(Blocked, Scope, BlockingErrorCount)` | The evidence pack's gate section separates blocking findings from reported ones |
| SARIF 2.1 as the exchange format | added in Portcullis ticket R3 | `sk gate` reads SARIF; C10 embeds it; nothing else about the gate is assumed |
| Rule ids `PORTCULLIS_<...>`; migration rules `PORTCULLIS_MIG_SYSTEM_WEB`, `PORTCULLIS_MIG_HTTPCONTEXT_CURRENT`, `PORTCULLIS_MIG_SYNC_OVER_ASYNC`, `PORTCULLIS_MIG_CONFIGURATION_MANAGER` | `src/Portcullis.Rules/RuleRegistry.cs` | Shown by id in the evidence pack; mapped to standards in C4 |
| Mutation testing as a habit, not an event | `docs/MUTATIONS.md` | This repository's CI carries a Stryker.NET threshold from the first commit (S1) |

## 5. architecture-standards → the rules this repository is held to

`github.com/konradcinkusz/architecture-standards` (public, MIT), declared in
`.claude/settings.json` (REPO-BASELINE.md §7).

| Guide | Applies to |
|---|---|
| `docs/architecture/00-REFERENCE-ARCHITECTURE.md` | Everything; P5 (secrets) and P13 (test at the layer that has the logic) bite hardest here |
| `docs/guides/REPO-BASELINE.md` | S1: hygiene files, secret scanning in pre-commit **and** CI, one-command onboarding |
| `docs/guides/TESTING-STRATEGY.md` | Every test project; §9 — every test entry point is executed by CI |
| `docs/guides/AI-EVALS.md` | C2 LLM drafting and C8 triage |
| `docs/guides/PRIVATE-CLOUD-DELIVERY.md` | C12 (sandbox repository) |
| `docs/guides/SECURITY-REVIEW.md` | C1 capture and C10 evidence — recordings may hold personal data |

## 6. Repositories deliberately not used by the core

| Repository | Why not here |
|---|---|
| `context-pin` | Its idea — a standards version pinned per repository, drift detected in CI — is absorbed into C4 (`letsgolegacy.secondkey-standards`) as a NuGet package, a git tag and one CI step; no separate service |
| `authservice` | Only for a trial of the portal that we host ourselves (C11, phase 03). Production uses the customer's IdP over OIDC (design rule 6) |
| Keploy (external) | The early plan to build capture on it was dropped: eBPF runs only on Linux, and legacy .NET Framework runs on Windows/IIS |
