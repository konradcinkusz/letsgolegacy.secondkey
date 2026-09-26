#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# The phase 01 chain end to end on the sample shop (S15):
#
#   capture -> replay -> compare -> gate -> evidence
#
# Starts the sample shop twice — the legacy side with invariant globalization (the old
# behaviour), the candidate with ICU — records scripted traffic through the capture proxy in
# front of the legacy side, replays it against both, compares against samples/contract.yaml,
# summarizes the sample SARIF and assembles the evidence pack. Then checks the verdict holds
# what the sample is built to produce: every class at least once, the NLS -> ICU order change
# as an uncovered regression, the stacked discount as a clause regression, and the empty
# checkout that no longer answers 500 as a fix candidate.
#
#   ./scripts/e2e.sh                     a PDF when a browser is available
#   SK_PDF=required ./scripts/e2e.sh     what CI runs: no PDF is a failure
#
# Everything is written to $E2E_DIR (default .secondkey/e2e).
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
OUT="${E2E_DIR:-$REPO_ROOT/.secondkey/e2e}"
PDF="${SK_PDF:-auto}"
LEGACY_PORT="${E2E_LEGACY_PORT:-5080}"
CANDIDATE_PORT="${E2E_CANDIDATE_PORT:-5081}"
PROXY_PORT="${E2E_PROXY_PORT:-8080}"
CONTRACT="$REPO_ROOT/samples/contract.yaml"
SARIF="$REPO_ROOT/samples/gate/portcullis.sarif"
EXCHANGES=10

rm -rf "$OUT"
mkdir -p "$OUT/logs" "$OUT/jars"

pids=()
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'e2e: %s\n' "$*" >&2; for log in "$OUT"/logs/*.log; do printf -- '--- %s\n' "$log" >&2; tail -n 20 "$log" >&2 || true; done; exit 1; }

# Waits until a URL answers with the expected body.
wait_for() {
  local url="$1" expected="$2"
  for _ in $(seq 1 120); do
    if [[ "$(curl -fsS "$url" 2>/dev/null || true)" == "$expected" ]]; then return 0; fi
    sleep 0.5
  done
  fail "$url did not answer '$expected' within 60 s"
}

# One request through the capture proxy. The cookie jar is the user's session; "none" is a
# visitor without one. Prints the status, and never fails on it: a 500 is part of the story.
call() {
  local method="$1" path="$2" jar="$3" body="${4:-}"
  local args=(-sS -o /dev/null -w '%{http_code}' -X "$method")
  [[ "$jar" != none ]] && args+=(-b "$OUT/jars/$jar" -c "$OUT/jars/$jar")
  [[ -n "$body" ]] && args+=(-H 'content-type: application/json' --data "$body")
  printf '  %-4s %-22s %s -> %s\n' "$method" "$path" "$jar" "$(curl "${args[@]}" "http://127.0.0.1:$PROXY_PORT$path")"
}

step "build"
dotnet build SecondKey.slnx --configuration Release --nologo --verbosity quiet
SK=(dotnet "$REPO_ROOT/src/SecondKey.Cli/bin/Release/net10.0/SecondKey.Cli.dll")
SHOP="$REPO_ROOT/samples/SampleShop/bin/Release/net10.0/SampleShop.dll"

step "start the legacy side (invariant globalization) and the candidate (ICU)"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 SAMPLESHOP_VARIANT=legacy SAMPLESHOP_ALLOW_RESET=1 \
  dotnet "$SHOP" --urls "http://127.0.0.1:$LEGACY_PORT" > "$OUT/logs/legacy.log" 2>&1 &
pids+=("$!")
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 SAMPLESHOP_VARIANT=candidate SAMPLESHOP_ALLOW_RESET=1 \
  dotnet "$SHOP" --urls "http://127.0.0.1:$CANDIDATE_PORT" > "$OUT/logs/candidate.log" 2>&1 &
pids+=("$!")
wait_for "http://127.0.0.1:$LEGACY_PORT/health" legacy
wait_for "http://127.0.0.1:$CANDIDATE_PORT/health" candidate

step "capture: $EXCHANGES exchanges through the recording proxy in front of the legacy side"
"${SK[@]}" capture --listen "http://127.0.0.1:$PROXY_PORT" --target "http://127.0.0.1:$LEGACY_PORT" \
  --out "$OUT/traffic.skcap" --session-key sk_session --exit-after "$EXCHANGES" > "$OUT/logs/capture.log" 2>&1 &
capture_pid="$!"
pids+=("$capture_pid")
for _ in $(seq 1 120); do grep -q "sk capture: recording" "$OUT/logs/capture.log" && break; sleep 0.5; done
grep -q "sk capture: recording" "$OUT/logs/capture.log" || fail "the capture proxy did not start"

call GET  "/products?sort=name" none                                          # a visitor looks at the catalogue
call POST /cart/items           buyer   '{"sku":"ECL-02","quantity":3}'       # a buyer orders
call GET  /cart                 buyer
call POST /checkout             buyer   '{"email":"buyer@example.test"}'
call POST /cart/items           ruler   '{"sku":"ANG-04","quantity":9}'       # 9 x 3.10 is 27.900000000000002 in double arithmetic
call GET  /cart                 ruler
call POST /cart/items           bargain '{"sku":"APL-01","quantity":1}'       # a coupon larger than the cart
call POST /cart/coupon          bargain '{"code":"BIGSALE"}'
call GET  /cart                 bargain
call POST /checkout             empty   '{}'                                  # checkout with nothing in the cart

for _ in $(seq 1 60); do kill -0 "$capture_pid" 2>/dev/null || break; sleep 0.5; done
kill -0 "$capture_pid" 2>/dev/null && fail "the capture did not stop after $EXCHANGES exchanges"
wait "$capture_pid" || fail "sk capture failed"
cat "$OUT/logs/capture.log"
"${SK[@]}" validate "$OUT/traffic.skcap"

step "replay against both sides, each reset before every scenario"
"${SK[@]}" replay --capture "$OUT/traffic.skcap" \
  --legacy "http://127.0.0.1:$LEGACY_PORT" --candidate "http://127.0.0.1:$CANDIDATE_PORT" \
  --legacy-reset-http /__sk/reset --candidate-reset-http /__sk/reset --out "$OUT/run.skrun"

step "compare against the contract (a regression is expected: exit 1)"
set +e
"${SK[@]}" compare --contract "$CONTRACT" --run "$OUT/run.skrun" --out "$OUT/verdict.json"
compare_exit=$?
set -e
[[ "$compare_exit" == 1 ]] || fail "sk compare exited $compare_exit; the sample must fail (1)"

step "gate: the sample SARIF has warnings and a suppressed error, nothing blocking (exit 0)"
"${SK[@]}" gate --sarif "$SARIF"

step "evidence (PDF: $PDF)"
"${SK[@]}" evidence --verdict "$OUT/verdict.json" --contract "$CONTRACT" --run "$OUT/run.skrun" \
  --sarif "$SARIF" --out "$OUT/evidence" --pdf "$PDF"

step "check the verdict and the pack"
python3 - "$OUT" "$PDF" "${GITHUB_STEP_SUMMARY:-/dev/null}" <<'PY'
import hashlib, json, os, sys

out, pdf, summary = sys.argv[1], sys.argv[2], sys.argv[3]
verdict = json.load(open(os.path.join(out, "verdict.json"), encoding="utf-8"))
exchanges = verdict["exchanges"]
problems = []

def check(condition, message):
    if not condition:
        problems.append(message)

classes = {e["class"] for e in exchanges}
check(classes == {"equal", "equal-under-contract", "regression", "fix-candidate"}, f"every class at least once, got {sorted(classes)}")
check(verdict["outcome"] == "fail", f"outcome fail, got {verdict['outcome']}")

products = [e for e in exchanges if e["request"]["path"] == "/products"]
check(len(products) == 1 and products[0]["class"] == "regression" and products[0]["reasons"] == ["uncovered-difference"],
      "the NLS -> ICU order change is an uncovered regression")
check(any(d["path"].startswith("response.body.json.items[0]") and d.get("normalizedBy") is None for d in products[0]["diffs"]) if products else False,
      "the product order difference is reported and unexplained")

stacked = [e for e in exchanges if any(r in ("clause-regression: TOTAL-NEVER-NEGATIVE", "clause-regression: CART-PAGE-TOTAL-NEVER-NEGATIVE") for r in e["reasons"])]
check(len(stacked) >= 2, f"the stacked discount breaks both total clauses, found {len(stacked)} exchange(s)")

fixes = [e for e in exchanges if e["class"] == "fix-candidate"]
check(any(e["reasons"] == ["clause-fixed: CHECKOUT-NEVER-5XX"] for e in fixes), "the empty checkout is a fix candidate")

rules = {r for e in exchanges if e["class"] == "equal-under-contract" for r in e["reasons"]}
check("normalized: masks.guids" in rules and "normalized: masks.timestamps" in rules, f"the order's id and time are masked, rules: {sorted(rules)}")
check(any(r.startswith("normalized: tolerances: ") for r in rules), f"the double arithmetic is within a tolerance, rules: {sorted(rules)}")

pack = os.path.join(out, "evidence")
manifest = json.load(open(os.path.join(pack, "manifest.json"), encoding="utf-8"))
for f in manifest["files"]:
    digest = hashlib.sha256(open(os.path.join(pack, f["path"]), "rb").read()).hexdigest()
    check(digest == f["sha256"], f"{f['path']} matches its digest in the manifest")
names = {f["path"] for f in manifest["files"]}
for required in ("index.html", "verdict.json", "contract.yaml", "run.skrun", "statement.intoto.json", "sarif/portcullis.sarif"):
    check(required in names, f"the pack holds {required}")
if pdf == "required":
    check("report.pdf" in names, "the pack holds report.pdf")

s = verdict["summary"]
with open(summary, "a", encoding="utf-8") as handle:
    handle.write("### End-to-end verdict on the sample shop\n\n")
    handle.write(f"Outcome **{verdict['outcome']}** — {s['exchanges']} exchanges in {s.get('scenarios', '?')} scenarios\n\n")
    handle.write("| equal | equal under contract | regression | fix candidate |\n|---|---|---|---|\n")
    handle.write(f"| {s['equal']} | {s['equalUnderContract']} | {s['regression']} | {s['fixCandidate']} |\n\n")
    handle.write(f"Evidence pack: {len(manifest['files'])} files{' including the PDF' if 'report.pdf' in names else ''} (artifact `evidence-pack`).\n")

for e in exchanges:
    print(f"  {e['class']:<21} {e['exchange']}  {e['request']['method']} {e['request']['path']}{e['request'].get('query', '')}  {'; '.join(e['reasons'])}")
if problems:
    print("\ne2e: the verdict is not what the sample is built to produce:", file=sys.stderr)
    for p in problems:
        print(f"  - {p}", file=sys.stderr)
    sys.exit(1)
print(f"\ne2e: ok — {s['exchanges']} exchanges, every class present, pack of {len(manifest['files'])} files at {pack}")
PY
