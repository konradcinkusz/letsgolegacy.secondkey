#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Mutation testing with Stryker.NET — the local mirror of CI's "mutation" job.
#
#   ./scripts/mutation.sh              every project in the table below
#   ./scripts/mutation.sh Artifacts    only the projects whose name contains the word
#
# Fails when any project's score is below the break threshold in stryker-config.json.
# Writes the scores to $GITHUB_STEP_SUMMARY when it runs in Actions, so the number is
# reported on every run rather than remembered (S1, S8).
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
FILTER="${1:-}"

# test project directory : project under test
#
# The verdict path — formats, contract engine, comparator, evidence — is mutation-tested,
# and so is the replay engine, which decides what the comparator gets to see. Two parts are
# left to integration tests instead, because mutating them would rerun their I/O per mutant,
# far outside the CI budget (TESTING-STRATEGY.md §2): the capture proxy, and the replay's
# SQL Server reset and probe, whose tests need a SQL Server container (they run in the
# build-and-test job, where SK_REQUIRE_DOCKER=1).
PAIRS=(
  "tests/SecondKey.Artifacts.Tests:SecondKey.Artifacts.csproj"
  "tests/SecondKey.Contract.Tests:SecondKey.Contract.csproj"
  "tests/SecondKey.Compare.Tests:SecondKey.Compare.csproj"
  "tests/SecondKey.Evidence.Tests:SecondKey.Evidence.csproj"
  "tests/SecondKey.Replay.Tests:SecondKey.Replay.csproj"
)

dotnet tool restore >/dev/null
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"
{
  echo "### Mutation scores"
  echo
  echo "| Project | Score | Killed | Survived | No coverage | Timeout |"
  echo "|---|---|---|---|---|---|"
} >> "$SUMMARY"

failed=0
for pair in "${PAIRS[@]}"; do
  test_dir="${pair%%:*}"
  project="${pair##*:}"
  [[ -n "$FILTER" && "$project" != *"$FILTER"* ]] && continue
  out="$REPO_ROOT/StrykerOutput/${project%.csproj}"
  rm -rf "$out"
  # Per-project scope: for the replay, everything but the SQL Server code, and without the
  # tests that need its container (SK_SKIP_DOCKER=1 skips them, with the reason).
  scope=()
  environment=()
  if [[ "$project" == "SecondKey.Replay.csproj" ]]; then
    scope=(--mutate "**/*.cs" --mutate "!**/SqlServer*.cs")
    environment=(SK_SKIP_DOCKER=1)
  fi
  echo "== Stryker: $project (tests: $test_dir)"
  if ! (cd "$test_dir" && env ${environment[@]+"${environment[@]}"} dotnet stryker --config-file "$REPO_ROOT/stryker-config.json" --project "$project" --output "$out" ${scope[@]+"${scope[@]}"}); then
    failed=1
  fi
  report="$(find "$out" -name 'mutation-report.json' | head -n 1)"
  if [[ -n "$report" ]]; then
    python3 - "$report" "$project" >> "$SUMMARY" <<'PY'
import json, sys
report, project = sys.argv[1], sys.argv[2]
counts = {}
for f in json.load(open(report))["files"].values():
    for m in f["mutants"]:
        counts[m["status"]] = counts.get(m["status"], 0) + 1
k, s = counts.get("Killed", 0), counts.get("Survived", 0)
n, t = counts.get("NoCoverage", 0), counts.get("Timeout", 0)
valid = k + s + n + t
score = f"{100 * (k + t) / valid:.2f}%" if valid else "n/a (no mutants)"
print(f"| {project} | {score} | {k} | {s} | {n} | {t} |")
PY
  fi
done
exit "$failed"
