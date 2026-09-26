# Artifact formats

The artifacts are Second Key's product interface: every component reads and writes
these files and nothing else, so any component can be replaced by one that honours them.
The JSON Schemas in [`/schemas`](../../schemas) are the authority; samples that validate
against them live in [`/schemas/samples`](../../schemas/samples) and are checked in CI.

| Artifact | Schema | Shape | Written by | Read by |
|---|---|---|---|---|
| `*.skcap` | [`skcap.schema.json`](../../schemas/skcap.schema.json) | JSON Lines: `capture.header`, then `http.exchange` events | `sk capture` (C1) | `sk replay` (C6), `sk mine` (C2) |
| `contract.yaml` | [`contract.schema.json`](../../schemas/contract.schema.json) | YAML document — see [contract.md](contract.md) | a person, helped by `sk mine` | `sk compare` (C7), `sk mutate` (C9) |
| `*.skrun` | [`skrun.schema.json`](../../schemas/skrun.schema.json) | JSON Lines: `run.header`, `state.reset` and `exchange.result` events, `run.end` | `sk replay` (C6) | `sk compare` (C7) |
| `verdict.json` | [`verdict.schema.json`](../../schemas/verdict.schema.json) | JSON document — see [verdict.md](verdict.md) | `sk compare` (C7) | `sk evidence` (C10), portal (C11) |
| evidence pack | [in-toto Statement v1](https://github.com/in-toto/attestation/blob/main/spec/v1/statement.md) for `statement.intoto.json` | a directory — see [evidence.md](evidence.md) | `sk evidence` (C10) | an approver, an auditor, portal (C11) |

Rules that hold for all of them:

- **Versioned.** Every event and document carries `v`. A reader refuses a version it does
  not know rather than guessing.
- **Strict.** Unknown keys are errors. A mistyped key that is silently ignored is a rule
  nobody is enforcing.
- **Beyond the schema.** A capture starts with exactly one header and keeps its recorded
  order (`seq` strictly increasing, ids unique). A run ends with `run.end`, whose result
  count must agree with the file — a run without it was interrupted and is never
  compared. A contract's references resolve, regexes compile and paths parse.
- **Headers** are stored lower-case, every value a list; a redacted value is the literal
  `<redacted>`.
- **Bodies** are stored as parsed JSON (`encoding: json`), text, or base64.
- **Digests.** A verdict names the SHA-256 of the contract and the run it was computed
  from, so it can be tied to exactly the inputs that produced it.

Validate any artifact with `sk validate <file>`.
