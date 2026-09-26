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
| `sk gate` | C5 — summarize the gate's SARIF per rule | available (S14) |
| `sk mutate` | C9 — inject defects the contract must catch | planned, phase 02 (exits 70) |
| `sk evidence` | C10 — assemble the evidence pack | available (S14) |
| `sk validate <file>...` | check artifacts against their formats | available |

`--config <path>` points every command at a `secondkey.yaml` (default: `./secondkey.yaml`
when it exists). Options given on the command line override the file; the two list
options of `sk capture`, `--session-key` and `--redact-header`, add to the file's lists.

## `sk capture`

```sh
sk capture --target http://127.0.0.1:5080 --listen http://127.0.0.1:8080 --session-key ASP.NET_SessionId
```

Starts a recording proxy. Clients talk to `--listen` instead of the legacy system; every
request goes to `--target` unchanged, except that it asks for an uncompressed answer so
the recording can be read, and the client gets the legacy system's answer unchanged. The
legacy system itself is not touched: only its clients' address changes.

| Option | In `secondkey.yaml` | Default | Meaning |
|---|---|---|---|
| `--target <url>` | `capture.target` | none — required | the legacy system behind the proxy |
| `--listen <url>` | `capture.listen` | `http://127.0.0.1:8080` | where the proxy listens |
| `--out <file>` | `capture.out` | `.secondkey/traffic.skcap` | the recording |
| `--session-key <name>` | `capture.sessionKeys` | none | a cookie or header whose value identifies a user session; repeatable |
| `--redact-header <name>` | `capture.redactHeaders` | — | a header whose value never reaches the recording, on top of `authorization`, `proxy-authorization`, `cookie`, `set-cookie` and `x-api-key`, which are always redacted; repeatable |
| `--max-body-bytes <n>` | `capture.maxBodyBytes` | 1 MiB | a body beyond this is cut and marked truncated |
| `--exit-after <n>` | — | none | stop by itself once `n` exchanges are recorded |

The session key decides the scenarios: `sk replay` replays the exchanges of one session
as one scenario, in order. An exchange belongs to the session its request names — by a
cookie or a header of that name — or, on a first visit, to the one its answer sets as a
cookie. An exchange with neither is a session of its own, so without a key every exchange
is replayed alone.

Ctrl+C or SIGTERM stops the proxy cleanly: it takes no new requests, lets those in flight
finish — for up to 30 seconds, then it cuts them off — closes the file, prints a summary
and exits 0:

```text
sk capture: recorded 290 exchange(s), skipped 0 the legacy system did not answer.
```

A request the legacy system did not answer — refused, reset, timed out — gets the proxy's
own 502 or 504 and is **not** recorded, because the proxy's error is not the legacy
system's behaviour. That exchange is then missing from its scenario, so a scripted
recording checks that the skipped count is 0 before it uses the file.

Every exchange is written and flushed as it completes, so a killed capture keeps what it
recorded — but a kill in the middle of a write leaves a torn last line, which
`sk validate` refuses. Where a pipeline can only kill the process (a Windows runner
stopping a background job, for one), give it `--exit-after` with the number of exchanges
the script sends, so it stops by itself.

## `sk replay`

```sh
sk replay --capture .secondkey/traffic.skcap --legacy http://127.0.0.1:5080 --candidate http://127.0.0.1:5081
```

Replays every scenario of the capture against the legacy system, then against the
candidate, each side reset to its starting state before the scenario — identical
conditions, so that what differs is the systems, not the replay. Each scenario on each
side gets a fresh cookie jar: recorded cookies are never sent, only those the side itself
set during the scenario. Redirects are not followed; a 302 is an answer to compare.

| Option | In `secondkey.yaml` | Default | Meaning |
|---|---|---|---|
| `--capture <file>` | `replay.capture` | none — required | the `*.skcap` to replay |
| `--legacy <url>` | `replay.legacy.baseUrl` | none — required | the legacy system |
| `--candidate <url>` | `replay.candidate.baseUrl` | none — required | the candidate |
| `--out <file>` | `replay.out` | `.secondkey/run.skrun` | the run |
| `--scenario-mode <mode>` | `replay.scenarioMode` | `session` | `session`: one scenario per recorded session; `exchange`: one per exchange |
| `--timeout <seconds>` | `replay.timeoutSeconds` | 30 | how long to wait for each answer |
| `--legacy-reset-http <path>` | `replay.legacy.reset` | no reset | reset the legacy side before each scenario with `POST <path>`; replaces the file's `reset` |
| `--candidate-reset-http <path>` | `replay.candidate.reset` | no reset | the same for the candidate |

A request that gets no answer is recorded with the reason — `timeout`, `connection` or
`protocol` — in place of a response. The command prints a summary:

```text
sk replay: 38 scenario(s), 580 result(s), 0 without an answer, 0 failed reset(s) -> .secondkey/run.skrun
```

and exits 0, or **4** when any reset failed. A scenario whose reset failed is not sent to
that side at all — every exchange of it is recorded as `reset-failed` — so the run is
written, but it is not a fair comparison.

### Resetting a side

Each side takes one `reset` in `secondkey.yaml`, run before every scenario:

| Kind | Keys | Succeeds when |
|---|---|---|
| `http` | `path`; `method` (default `POST`) | the side answers 2xx; `path` is resolved against the side's `baseUrl` |
| `sqlServerSnapshot` | `connectionStringEnv`, `database`, `snapshot` | the database is restored from the snapshot |
| `command` | `run`; `workingDirectory` (optional) | the command exits 0; it runs under `/bin/sh -c`, or `cmd.exe /c` on Windows |

`sqlServerSnapshot` restores a SQL Server database from a database snapshot. Know what it
does and what it does not:

- **The starting state is the database at the first reset.** The snapshot is created
  then, and kept after the replay; a snapshot of that name that already exists is used as
  it is. A second replay therefore starts from the state the first one saved. To take a
  new starting state, drop the snapshot (`DROP DATABASE [Shop_sk]`) — SQL Server also
  refuses to drop the database, or restore it from a backup, while a snapshot of it
  exists.
- **Every connection to the database is closed**, the application's pooled ones included:
  the restore sets the database to single-user with immediate rollback. `sk` does not
  wait for the application to recover and does not retry the scenario's first request —
  whatever the application answers is what is recorded. Check the first answer after a
  restore once, before relying on it; an application that runs timers against the
  database (schedule tasks) can fail until it is restarted.
- **Only the database is reset.** Whatever the application keeps in memory — caches,
  sessions, timers — survives the restore. Resetting that is the caller's job; when it
  cannot be reset per scenario, the contract masks the values it affects and says why.
- The login in the connection string must be allowed to create a database and to restore
  this one — `dbcreator`, in a sandbox.

### Watching the database

```yaml
replay:
  legacy:
    probe: { sqlServer: { connectionStringEnv: SK_LEGACY_DB, tables: [dbo.Order, dbo.ShoppingCartItem] } }
```

Reads every row of the named tables before and after each request, and records in the
result the rows the request inserted, deleted and updated. It reads the whole of each
table every time: it is meant for the small, seeded database of a sandbox, not a
production-sized one.

### Correlation

A value the server generates and later requests must send back — an anti-forgery token in
a hidden form field, typically — is stale in the recording. A correlation rule takes the
first group of `regex` from each answer, on the same side and within the same scenario,
and writes it into the named `formField` of later `application/x-www-form-urlencoded`
posts, or into the `header` when the recorded request carried that header.

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

## `sk gate` and `sk evidence`

```sh
sk gate --sarif portcullis.sarif
sk evidence --verdict verdict.json --contract contract.yaml --run run.skrun --sarif portcullis.sarif --out evidence
```

`sk gate` summarizes the gate's SARIF per rule and exits **1** when a finding blocks — an
error that is neither suppressed nor known from the baseline — and 0 otherwise.
`sk evidence` writes the pack and exits 0 whatever the verdict, so a pipeline can publish
the pack that explains a failure; it refuses a contract or a run the verdict does not name.
Both are described in [the evidence pack](formats/evidence.md).

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

A command stopped with Ctrl+C or SIGTERM gets a minute to stop cleanly. One that has not
stopped by then is ended with the signal's own code, 130 or 143, and whatever it was
writing may be incomplete.

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
gate: { sarif: [gate.sarif] }
evidence: { verdict: .secondkey/verdict.json, contract: contract.yaml, run: .secondkey/run.skrun, sarif: [gate.sarif], out: .secondkey/evidence, pdf: auto }
```

Secrets never live in this file: a connection string is named by the environment variable
that holds it (`connectionStringEnv`), and `secrets.env.example` lists every such variable.

Relative paths resolve against the directory that holds the file. On Windows, write a path
with forward slashes (`C:/sk/traffic.skcap`) or in single quotes (`'C:\sk\traffic.skcap'`):
inside double quotes YAML reads a backslash as the start of an escape, so `"C:\sk\a.skcap"`
is not the path it looks like.

## Line endings and digests

Every JSON document `sk` writes — `verdict.json`, the pack's `manifest.json` and
`statement.intoto.json` — ends its lines with `\n` on every platform, as the `*.skcap` and
`*.skrun` lines already did. The same verdict is the same bytes, and so the same SHA-256,
whether it was computed on the Windows host next to the legacy system or on a Linux runner.
