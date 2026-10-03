# verdict.json — reading a verdict

`sk compare` reads a contract and a run and writes one verdict: every replayed exchange in
exactly one of four classes, and the run's outcome. It is computed by code alone — the
same contract and run always give the same verdict, apart from `createdAt`, `tool` (the
build that computed it) and `inputs.*.path` (the names of the files it was given), which
are the only fields that differ (design rule 1).
[`schemas/verdict.schema.json`](../../schemas/verdict.schema.json) is the authority on the
format; [`schemas/samples/verdict.json`](../../schemas/samples/verdict.json) is what
`sk compare` produces from the sample contract and run, and a test keeps it that way.

## What is compared

For each exchange, both sides' answers are reduced to the part that is behaviour:

| Compared | Not compared |
|---|---|
| `response.status` | the request — it is the same capture on both sides |
| the headers in `compare.headers` (default `content-type`, `location`) | every other header (`date`, `server`, …) |
| the body: JSON, text or base64 | an HTML page under `compare.html: extracts` (the default) — it is compared through its extracted values instead |
| `extract` — every extracted value, and every extraction that failed | the body's size |
| `db` row changes and `outbound` calls, when recorded | an error's message — only its kind (`timeout`, `connection`, …) |

Two things that differ only because the sides run at different addresses are made equal
before any contract rule: each side's own base URL becomes `<base-url>` wherever it
appears, and a `content-type` is written in one case and spacing. Object keys are ordered
and numbers written in one form (`1`, `1.0` and `1.00` are the same value).

Clauses are evaluated on the full observation, not only on what is compared — which is
why a clause can decide an exchange whose compared answers are identical.

## The four classes, in order

| Step | Class | Reasons |
|---|---|---|
| A side has no answer: a timeout, a refused connection, a failed reset, or no result at all | `regression` | `missing-result: candidate (timeout)` |
| An accepted clause passes on legacy and fails, or cannot be evaluated, on the candidate | `regression` | `clause-regression: <id>`, one per clause |
| An accepted clause fails, or cannot be evaluated, on legacy and passes on the candidate | `fix-candidate` | `clause-fixed: <id>`, one per clause |
| No difference | `equal` | `identical` |
| Every difference is explained by a normalization rule | `equal-under-contract` | `normalized: <rule>`, one per rule |
| Anything else | `regression` | `uncovered-difference` |

The first step that applies decides. Proposed clauses are evaluated and listed with each
exchange but decide nothing. A clause broken on both sides decides nothing either; its
summary says `violated-both`. See [ADR 0003](../adr/0003-verdict-classes.md) for why the
order is what it is.

The **outcome** is `fail` if any exchange is a regression, `review` if none is but at
least one is a fix candidate, and `pass` otherwise.

## Differences and the rules that explain them

`diffs` lists every raw difference, located by a path in the contract's own path language
— so it can be pasted into an `ignore` rule or a clause as it is. Each one a
normalization rule accounts for names that rule in `normalizedBy`:

| `normalizedBy` | The rule |
|---|---|
| `ignore: response.body.json.generatedAt` | the difference is at or below an ignored path |
| `tolerances: response.body.json.total` | two numbers within that tolerance |
| `masks.timestamps`, `masks.guids`, `masks.patterns.<name>` | two strings equal once masked; several masks are listed together |
| `sets: response.body.json.items` | an order difference inside an array compared as a set — only when nothing else in that array differs |

Rules apply in a fixed order — ignore, then masks, then sets, with tolerances when numbers
are compared. `masks.timestamps` covers ISO-8601 date-times (`2026-09-26T10:00:00Z`,
`2026-09-26 10:00`); other date forms such as `/Date(1411725600000)/` are deliberately not
masked, so a change of date *format* between the two versions is still seen.

A difference with no `normalizedBy` is one nothing in the contract explains. The way to
resolve it is a reviewed change to the contract: a normalization rule if it does not
matter, a clause if it does.

## Summary

`summary` counts scenarios, exchanges and each class. `summary.clauses` counts the
contract's clauses (`total`), those that decide (`accepted`), and of those how many some
request exercised and how many none did (`unexercised` — never reported as passed). The
**absence share** is the part of accepted clauses that are `never` clauses: a contract
that only says what must happen has said half of what it needs to.
