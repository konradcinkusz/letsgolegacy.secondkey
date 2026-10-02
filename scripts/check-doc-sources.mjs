#!/usr/bin/env node
/**
 * check-doc-sources.mjs — the framework documentation's citations of this
 * repository still point at files that exist, and the core has not moved past
 * the commit the documentation says it describes.
 *
 * WHY THIS EXISTS.
 *
 *   docs/papers/letsgolegacy-framework.tex is a hand-written presentation of
 *   sources it does not own, and it accepts that nothing keeps its prose in
 *   step with them. architecture-standards (docs/research/
 *   00-RESEARCH-DOCUMENTATION.md, "Documents that borrow the house style")
 *   asks for exactly that statement, and adds: if the two visibly diverge more
 *   than once, add a cheap mechanical check. They did — most commits to
 *   docs/papers so far re-synced the document with code that had moved.
 *
 * WHAT IT CHECKS, AND WHAT IT DELIBERATELY DOES NOT.
 *
 *   S1  Every \path{letsgolegacy.secondkey/<file>} the chapters cite names a
 *       file or directory that exists in this checkout. A renamed or deleted
 *       source fails the build. Globs and <placeholders> are skipped.
 *   S2  The core's commit in chapter 01's version table is compared with
 *       HEAD, outside the documentation's own files (docs/papers,
 *       docs/diagrams and the tooling that builds them: the two papers
 *       scripts, the renderer, package.json and its lock file, and the PDF
 *       workflow), so a documentation change never reports itself as drift.
 *       When the code moved past
 *       it, the changed files are printed as a warning (a GitHub annotation in
 *       CI) — the reader of the run learns which sources to re-read. It warns
 *       rather than fails, because a code change is not wrong for lacking a
 *       documentation change in the same commit.
 *
 *   It does NOT read the other repositories the document cites, and it cannot
 *   tell whether a sentence is still true. It catches the cheap half of drift:
 *   a citation that no longer resolves, and a pin that no longer matches.
 *
 * Zero dependencies. Needs the full history for S2 (actions/checkout with
 * fetch-depth: 0); without the pinned commit, S2 says so and is skipped.
 *
 * Usage: node scripts/check-doc-sources.mjs
 * Exit:  0 = every cited core file exists; 1 = not.
 */

import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
const root = git('rev-parse', '--show-toplevel');
const chapters = join(root, 'docs', 'papers', 'framework');
const inCi = process.env.GITHUB_ACTIONS === 'true';

// S1 — cited core files exist.
const missing = [];
let cited = 0;
for (const name of readdirSync(chapters).filter((n) => n.endsWith('.tex')).sort()) {
  const text = readFileSync(join(chapters, name), 'utf8');
  for (const [, path] of text.matchAll(/\\path\{letsgolegacy\.secondkey\/([^}]+)\}/g)) {
    if (/[*<>]/.test(path)) continue;
    cited += 1;
    if (!existsSync(join(root, path.replace(/\/$/, '')))) missing.push(`${name}: ${path}`);
  }
}

// S2 — the core's pin in chapter 01 against HEAD.
const table = readFileSync(join(chapters, '01-wprowadzenie.tex'), 'utf8');
const pin = /\\path\{letsgolegacy\.secondkey\} & \\texttt\{([0-9a-f]{7,40})\}/.exec(table)?.[1];
if (!pin) {
  console.error('check-doc-sources: no commit for letsgolegacy.secondkey in the version table of 01-wprowadzenie.tex.');
  process.exit(1);
}
let known = true;
try { git('cat-file', '-e', `${pin}^{commit}`); } catch { known = false; }
if (!known) {
  console.log(`check-doc-sources: the pinned core commit ${pin} is not in this checkout's history; pin check skipped.`);
} else {
  const own = ['docs/papers', 'docs/diagrams', 'scripts/check-doc-sources.mjs', 'scripts/check-papers.mjs',
    'scripts/render-diagrams.mjs', 'package.json', 'package-lock.json', '.github/workflows/build-docs-pdf.yml'];
  const moved = git('diff', '--name-only', pin, 'HEAD', '--', '.', ...own.map((path) => `:(exclude)${path}`));
  if (moved) {
    const files = moved.split('\n');
    const message = `the core moved past ${pin}, the commit the documentation describes; re-read these sources and update the document and its pin: ${files.join(', ')}`;
    console.log(inCi ? `::warning title=Documentation pin::${message}` : `check-doc-sources: warning: ${message}`);
  } else {
    console.log(`check-doc-sources: the core's code is unchanged since the pinned ${pin}.`);
  }
}

if (missing.length) {
  console.error(`check-doc-sources: ${missing.length} cited core file(s) do not exist:\n  ${missing.join('\n  ')}`);
  process.exit(1);
}
console.log(`check-doc-sources: all ${cited} cited core paths exist.`);
