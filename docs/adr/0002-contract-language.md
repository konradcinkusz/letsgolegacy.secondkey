# ADR 0002 — The contract language

- **Status:** accepted
- **Date:** 2026-09-26
- **Ticket:** S4

## Context

A contract has to be readable by the people who sign off a change (risk, change
approval, audit), precise enough to evaluate without judgement, and expressive enough to
state what must *never* happen. The systems it describes are legacy web applications:
much of their behaviour is visible only in server-rendered HTML.

agent-eval-bench showed which disciplines make such a corpus trustworthy
([`reference-notes.md`](../reference-notes.md) §1): data not code, strict schema,
absence assertions first-class, no vacuous passes, unknown assertions as errors.

## Decision

- **Clauses are `must` or `never`**, each with a required `why` and a provenance whose
  `status` is `proposed` or `accepted`; only accepted clauses decide a verdict. A contract
  needs at least one accepted `never` clause.
- **Predicates select values by path** from an observation (request, response, extracted
  values, database deltas, outbound calls) and apply one operator from a closed set. The
  path language is small and ours — dotted names, indexes, `[*]`, `**`, quoted names —
  rather than full JSONPath, because every construct in it must be explainable in the
  natural-language rendering of a clause.
- **Nothing matches prose.** Values inside HTML are pulled out by named extractors (CSS
  selector, regex with one group, JSON path, header) with explicit type and culture, and
  clauses refer to them as `extract.<name>`. With `compare.html: extracts` an HTML page is
  compared only through those values.
- **No vacuous passes.** A clause that matched no request is `unexercised`, and a
  selection that is empty fails an `all` predicate.
- **Normalization lives in the contract** (ignore, tolerances, sets, masks), so the rules
  that make two versions "equal under contract" are reviewed with the clauses they serve.

## Consequences

- An extractor whose selector stops matching after a markup change makes its clauses
  fail or go unexercised — loudly, never silently.
- Contract authors must quote bracketed paths inside YAML flow mappings; the format
  documentation says so.
- The language will grow by adding operators to the closed set, with schema, engine and
  rendering changed together.
