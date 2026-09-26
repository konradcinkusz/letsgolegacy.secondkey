# ADR 0001 — The artifact files are the product interface

- **Status:** accepted
- **Date:** 2026-09-26
- **Tickets:** S3–S6

## Context

Second Key is a chain of components — capture, contract, replay, compare, evidence —
that will be built, replaced and deployed separately: capture runs on the customer's
Windows/IIS side, the rest in a Linux sandbox; the gate is a separate repository; later
components (miner, mutation engine, portal) arrive in later phases. The components must
be able to change without a coordinated release of all of them.

## Decision

Components communicate **only through versioned files** whose formats are defined by
JSON Schemas in `/schemas`: `*.skcap`, `contract.yaml`, `*.skrun`, `verdict.json` (and,
from other repositories, SARIF and skills). No component calls another's code.

- Event streams (captures, runs) are **JSON Lines**, so they can be appended while a
  system runs and read without loading whole into memory.
- Every event and document carries a **format version `v`**; readers refuse versions they
  do not know.
- Schemas are **strict** (`additionalProperties: false`), and rules a schema cannot
  express are checked in code and reported with the same located errors.
- Verdicts cite the **SHA-256** of their inputs.

## Consequences

- A format change is a reviewed change to a schema, its samples and `docs/formats/`;
  CI validates the samples on every build.
- Any component can be rewritten — including by a customer — as long as it reads and
  writes the same files.
- The formats are public by construction; recordings of real systems never are, and are
  kept out of git by `.gitignore`.
