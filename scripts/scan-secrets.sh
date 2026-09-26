#!/usr/bin/env bash
# Local mirror of the secret-scan CI job (REPO-BASELINE.md §4).
#
#   ./scripts/scan-secrets.sh            scan the working tree and its history
#   ./scripts/scan-secrets.sh --staged   scan only what is staged (what the hook runs)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if ! command -v gitleaks >/dev/null 2>&1; then
  echo "gitleaks is not installed, so nothing was scanned: https://github.com/gitleaks/gitleaks/releases" >&2
  # Non-zero on purpose: a scanner that reports success when it did not run is worse than none.
  exit 127
fi

if [[ "${1:-}" == "--staged" ]]; then
  gitleaks protect --staged --redact --config .gitleaks.toml --verbose
else
  gitleaks detect --redact --config .gitleaks.toml --verbose
fi
echo "No secrets found."
