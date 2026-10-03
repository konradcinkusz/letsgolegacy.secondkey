#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# One-command onboarding (REPO-BASELINE.md §3).
#
#   ./scripts/setup.sh            run every step
#   ./scripts/setup.sh --check    report what is missing, change nothing
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

CHECK_ONLY=false
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=true

missing=0
ok()   { printf '  [ ok ] %s\n' "$*"; }
warn() { printf '  [ -- ] %s\n' "$*"; }
fail() { printf '  [FAIL] %s\n' "$*"; missing=$((missing + 1)); }
step() { printf '\n%s\n' "$*"; }

step "1. Prerequisites"
if command -v dotnet >/dev/null 2>&1; then
  sdk="$(dotnet --version 2>/dev/null || echo unknown)"
  case "$sdk" in
    10.*) ok ".NET SDK $sdk" ;;
    *)    fail ".NET SDK $sdk found; this solution targets net10.0 — https://dotnet.microsoft.com/download/dotnet/10.0" ;;
  esac
else
  fail ".NET SDK 10 not found — https://dotnet.microsoft.com/download/dotnet/10.0"
fi

# Optional prerequisites, named so that skipping is an informed choice (P8).
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  ok "docker (running)"
else
  warn "docker not running (optional — needed only for the SQL Server snapshot tests, S11;
         without it those tests report themselves skipped locally; CI's Linux build-and-test
         job always runs them, while its Windows and mutation jobs skip them on purpose)"
fi
# The two tools the repository's own scripts shell out to. Neither is needed to build or test.
if command -v curl >/dev/null 2>&1; then
  ok "curl"
else
  warn "curl not found (optional — needed only by scripts/e2e.sh, the end-to-end run on the sample shop)"
fi
if command -v python3 >/dev/null 2>&1; then
  ok "python3 $(python3 --version 2>&1 | cut -d' ' -f2)"
else
  warn "python3 not found (optional — needed only by scripts/e2e.sh, which checks the verdict and the pack
         with it, and scripts/mutation.sh, which writes its summary table with it)"
fi
if command -v gitleaks >/dev/null 2>&1; then
  ok "gitleaks $(gitleaks version 2>/dev/null || echo '')"
else
  warn "gitleaks not found (optional — needed for the pre-commit secret scan in step 3;
         the CI job scans regardless) — https://github.com/gitleaks/gitleaks/releases"
fi
chrome=""
# The order PdfRenderer searches in: Chrome before a Chromium of unknown origin, which on
# GitHub's Ubuntu runners hangs in headless mode where Chrome prints at once.
for c in "${SK_CHROME_PATH:-}" google-chrome google-chrome-stable chrome microsoft-edge microsoft-edge-stable msedge chromium chromium-browser; do
  [[ -n "$c" ]] && command -v "$c" >/dev/null 2>&1 && { chrome="$c"; break; }
done
if [[ -n "$chrome" ]]; then ok "headless browser: $chrome"
else warn "no Chrome, Edge or Chromium found (optional — needed only to render the evidence pack as PDF;
         the HTML report is produced regardless; set SK_CHROME_PATH to point at one)"; fi

if $CHECK_ONLY; then
  step "Check complete."
  [[ "$missing" -gt 0 ]] && { echo "  $missing required tool(s) missing."; exit 1; }
  exit 0
fi
[[ "$missing" -gt 0 ]] && { step "Stopping: $missing required tool(s) missing."; exit 1; }

step "2. Restore"
dotnet restore SecondKey.slnx
dotnet tool restore
ok "packages and local tools restored"

step "3. Pre-commit secret scan  (optional — catches a credential before it becomes history)"
if command -v gitleaks >/dev/null 2>&1; then
  cp scripts/hooks/pre-commit .git/hooks/pre-commit
  chmod +x .git/hooks/pre-commit
  ok "hook installed at .git/hooks/pre-commit"
else
  warn "skipped: gitleaks not installed"
fi

step "4. Local secrets"
if [[ -f .env ]]; then ok ".env exists — left untouched"
else cp secrets.env.example .env; ok "created .env from secrets.env.example (every variable in it is optional)"; fi
# Nothing loads .env: sk and the scripts read the process environment only (P5).
ok "sk does not read .env; export what you fill in first:  set -a; . ./.env; set +a"

step "Done. Build and test with:  dotnet build SecondKey.slnx && dotnet test SecondKey.slnx"
