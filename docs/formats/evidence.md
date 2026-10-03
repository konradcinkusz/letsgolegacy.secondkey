# The evidence pack

`sk evidence` assembles what an approver — a change board, a second line of defence, an
auditor — needs to decide on a migrated system without running Second Key: one directory
that explains the verdict, carries its inputs, and names the SHA-256 of every file.

```sh
sk evidence --verdict verdict.json --contract contract.yaml --run run.skrun \
            --sarif portcullis.sarif --out evidence --pdf auto
```

The command exits 0 when the pack is written, whatever the verdict says: the verdict's
own exit code is `sk compare`'s, and a pipeline should publish the pack that explains a
failure. It refuses (exit 3) a contract or a run that is not the one the verdict names, so
a pack can never pair a verdict with the wrong inputs.

## What is in it

| File | What it is |
|---|---|
| `index.html` | The report: the outcome, the gate, every regression and fix candidate with its differences, every clause with how each side did, what the contract says does not matter, what the evidence does not show, and the digests. One standalone file: no script, no network — a content security policy forbids both — and every recorded value HTML-encoded. |
| `report.pdf` | The same report printed by a headless Chromium-based browser (see below). |
| `verdict.json` | The verdict, as `sk compare` wrote it. |
| `contract.yaml` | The contract the verdict was computed from. |
| `run.skrun` | The run, when `--run` is given — with the contract, enough to recompute the verdict. Leave it out when the recorded traffic must stay inside its boundary. |
| `sarif/*.sarif` | The gate's logs, unchanged. |
| `statement.intoto.json` | An [in-toto Statement v1](https://github.com/in-toto/attestation/blob/main/spec/v1/statement.md) naming every file but itself and `manifest.json` — which is written after it and lists it — with its SHA-256, with the outcome, the summary, the contract and run digests and the gate's result as its predicate (`predicateType` `https://github.com/konradcinkusz/letsgolegacy.secondkey/evidence/v1`). **Unsigned** (`"signed": false`). |
| `manifest.json` | Every file but itself, with its SHA-256 and size. |

Writing a pack into a directory that holds a previous pack replaces exactly the files that
pack's manifest lists; a directory holding anything else is refused, never emptied, and
`sk evidence` exits 64: the command asked for a place it cannot write to.

Everything that can refuse a run happens before the directory is changed: the inputs are
validated, a directory that holds files but no pack is refused, and the report is printed to
PDF in a staging directory of the system's temporary files and moved in afterwards. So a run
that fails there — `--pdf required` without a browser that can print — leaves the output
directory exactly as it found it: not created if it did not exist, and a previous pack still
whole. (A previous pack with a stranger file beside it is still refused only after its own
files are removed; see above.)

## Checking a pack

```sh
cd evidence
python3 -c "import json,hashlib; [print('ok' if hashlib.sha256(open(f['path'],'rb').read()).hexdigest()==f['sha256'] else 'CHANGED', f['path']) for f in json.load(open('manifest.json'))['files']]"
sk validate verdict.json contract.yaml
sk compare --contract contract.yaml --run run.skrun --out recomputed.json   # the same verdict, apart from createdAt, tool and the input file names
```

## The PDF

`--pdf auto` (the default) prints the report when a Chromium-based browser is on the
machine and says so when none is; `--pdf required` makes a missing PDF a failure (exit 4, and
nothing is written — the pack's directory is untouched);
`--pdf off` skips it. The browser is found through `SK_CHROME_PATH`, then the PATH —
Google Chrome and Edge before Chromium, whose builds vary more — then where Chrome, Edge
and Playwright install it. A browser that has not printed within a minute is stopped, and
the error says the last thing it wrote. The browser's sandbox is switched off, because it
is unavailable to root in containers and on some CI images; what it renders is the pack's
own static page, which can neither run a script nor make a request.

## The gate

`sk gate --sarif <log>...` summarizes SARIF 2.1.0 logs per rule and exits 1 when a finding
blocks. **An error blocks** unless an accepted suppression covers it or the log marks it as
known from the baseline (`baselineState` `unchanged` or `absent`). Warnings and notes are
reported and never block. Every rule a log declares is listed, including those that found
nothing: the report records what was checked, not only what was found. A file that is not
SARIF 2.1.0 is invalid input (exit 3) — never read as "no findings".

## Not yet

Signing: the statement is signed with the owner's key into a DSSE envelope
(`evidence.dsse`) in phase 03. Until then the pack proves which files belong together and
what they say, and nothing about who assembled it.
