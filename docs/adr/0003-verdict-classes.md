# ADR 0003 — Four verdict classes, and unexplained differences fail closed

- **Status:** accepted
- **Date:** 2026-09-26
- **Ticket:** S6 (applied by S13)

## Context

Replay tools prove parity: the new system does what the old one did, including its bugs.
Second Key's claim is different — the new system still keeps its **contract**. So a
difference is not automatically wrong, and sameness is not automatically right. The
approver needs every compared exchange placed in a class they can act on, and the
decision must be deterministic (design rule 1: no model in the verdict).

## Decision

Every exchange lands in exactly one class:

| Class | Meaning |
|---|---|
| `equal` | Identical after canonicalization alone |
| `equal-under-contract` | Identical once the contract's normalization rules are applied |
| `regression` | The candidate breaks a clause the legacy system keeps — **or differs in a way no clause and no normalization rule explains** |
| `fix-candidate` | The candidate keeps a clause the legacy system broke; always decided by a person |

In order: a clause kept by legacy and broken by the candidate is a regression, whatever
else is true; then normalized equality; then a clause broken by legacy and kept by the
candidate makes a fix candidate; anything left is a regression with the reason
`uncovered-difference`.

The run's outcome is `fail` if any exchange is a regression, `review` if there is no
regression but at least one fix candidate, and `pass` otherwise.

## Consequences

- **Fail closed.** An unexplained difference blocks until a person either adds a
  normalization rule (the difference does not matter) or a clause (it matters, and now it
  is stated). Every such decision is a reviewed change to the contract — which is exactly
  the record the second line of defence needs.
- Clauses broken on both sides are reported as `violated-both` — a pre-existing defect the
  migration preserved — and do not by themselves fail the run.
- Advisory triage (C8) may help a reviewer sort hundreds of diffs; it never changes a
  class.
