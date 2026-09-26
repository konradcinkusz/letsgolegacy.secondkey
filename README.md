# Second Key

**Independent, deterministic evidence that a .NET system migrated by an AI agent still
keeps its business contract.**

A migration agent turns the first key: it migrates the code. Second Key turns the
second: it records how the current system really behaves, replays that behaviour
against the migrated version, checks every difference against a written contract of
what must happen and what must never happen, and hands the approver a per-change
evidence pack. Second Key verifies; it does not rewrite.

> **Status: phase 01 (demo).** The chain runs end to end on the sample shop; the public
> specimen (nopCommerce 3.x) is next. Nothing here is released. The work plan, with every
> ticket and its acceptance criterion, is in [`docs/WORKPLAN.md`](docs/WORKPLAN.md).

## The chain

| Step | Component | Output |
|---|---|---|
| 1 Capture | C1 — recording proxy in front of the legacy system, no code change | `*.skcap` |
| 2 Contract | C2 miner + a human, C3 engine | `contract.yaml` |
| 3 Standards | C4 — standards delivered to the migration agent as skills | agent pull request |
| 4 Gate | C5 — Roslyn analyzers on changed lines ([Portcullis](https://github.com/konradcinkusz/letsgolegacy.portcullis)) | `*.sarif` |
| 5 Replay | C6 — both versions under identical conditions; C7 compares field by field against the contract | `*.skrun`, `verdict.json` |
| 6 Mutations | C9 — injected defects the contract must catch | kill rate per clause |
| 7 Evidence | C10 — one pack per pull request | `evidence/` |

## Try it

```sh
./scripts/setup.sh     # once: checks the tools, installs the secret-scanning commit hook
./scripts/e2e.sh       # the whole chain on the sample shop
```

`e2e.sh` starts the sample shop twice — the "legacy" side with the old globalization
behaviour, the "candidate" with the new — records traffic through the capture proxy,
replays it against both, compares against [`samples/contract.yaml`](samples/contract.yaml)
and writes the evidence pack to `.secondkey/e2e/evidence/`. CI runs the same job and
publishes the pack as the `evidence-pack` artifact. On the sample it finds what a real
migration hides: a product list sorted differently, which nothing in the contract allows;
a discount that drives a total below zero; and a checkout that no longer crashes on an
empty cart, put to a person as a fix. The commands are in [`docs/cli.md`](docs/cli.md);
how to read a verdict and a pack is in [`docs/formats/`](docs/formats/README.md).

## Repositories

| Repository | Holds |
|---|---|
| `letsgolegacy.secondkey` (this) | C0 CLI, C1 HTTP capture (phase 01), C2, C3, C6, C7, C8, C9, C10 |
| [`letsgolegacy.portcullis`](https://github.com/konradcinkusz/letsgolegacy.portcullis) | C5 gate |
| [`letsgolegacy.secondkey-standards`](https://github.com/konradcinkusz/letsgolegacy.secondkey-standards) | C4 standards pack |
| [`letsgolegacy.secondkey-capture`](https://github.com/konradcinkusz/letsgolegacy.secondkey-capture) | C1 from phase 02 (SQL, queues, PII) |
| [`letsgolegacy.secondkey-sandbox`](https://github.com/konradcinkusz/letsgolegacy.secondkey-sandbox) | C12 packaging inside the customer boundary |
| [`letsgolegacy.secondkey-portal`](https://github.com/konradcinkusz/letsgolegacy.secondkey-portal) | C11 evidence portal |
| [`letsgolegacy.secondkey-nopcommerce-bench`](https://github.com/konradcinkusz/letsgolegacy.secondkey-nopcommerce-bench) | Public specimen: nopCommerce 3.x |

## Licence

All rights reserved; see [`LICENSE`](LICENSE). The licensing model is an open decision.
