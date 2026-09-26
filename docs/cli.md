# `sk` — the command line

Every step of the chain runs from the command line, in a pipeline (design rule 7).

```sh
dotnet tool install --global SecondKey.Cli   # once published; until then:
dotnet run --project src/SecondKey.Cli -- --help
```

| Command | Step | Status in this build |
|---|---|---|
| `sk capture` | C1 — record traffic through a proxy in front of the legacy system | available (S9) |
| `sk mine` | C2 — propose contract clauses from recordings | planned, phase 02 (exits 70) |
| `sk replay` | C6 — replay a capture against both sides under identical conditions | available (S10–S11) |
| `sk compare` | C7 — compare the two sides against the contract into `verdict.json` | available (S12–S13) |
| `sk gate` | C5 — summarize the gate's SARIF per rule | S14 |
| `sk mutate` | C9 — inject defects the contract must catch | planned, phase 02 (exits 70) |
| `sk evidence` | C10 — assemble the evidence pack | S14 |
| `sk validate <file>...` | check artifacts against their formats | available |

`--config <path>` points every command at a `secondkey.yaml` (default: `./secondkey.yaml`
when it exists). Options given on the command line override the file.

## `sk compare`

```sh
sk compare --contract contract.yaml --run .secondkey/run.skrun --out .secondkey/verdict.json
```

Places every replayed exchange in one of four classes — `equal`, `equal-under-contract`,
`regression`, `fix-candidate` — and writes the verdict ([ADR 0003](adr/0003-verdict-classes.md),
[verdict format](formats/verdict.md)). It prints the counts and every exchange a person
has to look at, and exits **0** on `pass`, **1** on `fail` (any regression) and **2** on
`review` (no regression, a fix candidate to decide). A run with no results is invalid
input (3): nothing was compared, so nothing can pass.

## Exit codes

A pipeline branches on these; they do not change without a major version.

| Code | Name | Meaning |
|---|---|---|
| 0 | Success | The verdict passed, the gate passed, the artifact is valid |
| 1 | Failed | What was checked failed: a regression in the verdict, a blocking finding at the gate |
| 2 | Review | No regression, but a fix candidate needs a person's decision |
| 3 | InvalidInput | An input — an artifact or the configuration — is invalid; nothing was decided |
| 4 | RuntimeError | The command could not complete: a system unreachable, a file unwritable |
| 64 | Usage | The command line itself is wrong |
| 70 | NotImplemented | The command exists in the plan but not in this build |

## secondkey.yaml

```yaml
version: 1
capture:
  listen: http://127.0.0.1:8080          # where the recording proxy listens
  target: http://127.0.0.1:5080          # the legacy system behind it
  out: .secondkey/traffic.skcap
  sessionKeys: [ASP.NET_SessionId]       # cookies that identify a user session
  redactHeaders: [authorization, cookie, set-cookie]
replay:
  capture: .secondkey/traffic.skcap
  out: .secondkey/run.skrun
  scenarioMode: session                  # session | exchange
  legacy:
    baseUrl: http://127.0.0.1:5080
    reset: { sqlServerSnapshot: { connectionStringEnv: SK_LEGACY_DB, database: Shop, snapshot: Shop_sk } }
  candidate:
    baseUrl: http://127.0.0.1:5081
    reset: { http: { path: /__sk/reset } }
  correlation:                           # values the server generates that later requests must echo
    - { name: csrf, regex: 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', formField: __RequestVerificationToken }
compare: { contract: contract.yaml, run: .secondkey/run.skrun, out: .secondkey/verdict.json }
evidence: { verdict: .secondkey/verdict.json, contract: contract.yaml, sarif: [gate.sarif], out: .secondkey/evidence, pdf: auto }
```

Secrets never live in this file: a connection string is named by the environment variable
that holds it (`connectionStringEnv`), and `secrets.env.example` lists every such variable.
