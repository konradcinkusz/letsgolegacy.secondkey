#!/usr/bin/env node
/**
 * render-diagrams.mjs — renders every docs/diagrams/<slug>.mmd to
 * docs/diagrams/rendered/<slug>.pdf, a vector PDF the LaTeX documents include through
 * the house preamble's \dgm{<slug>}.
 *
 * architecture-standards, docs/research/00-RESEARCH-DOCUMENTATION.md, "Diagrams in a PDF":
 * one source per diagram, rendered to vector, rendered output never committed, the
 * renderer pinned in package-lock.json and resolved from node_modules (never `npx mmdc`,
 * which on a fresh clone reaches past an empty node_modules to a squatter package).
 *
 * The renderer drives a headless Chromium. Point it at the browser the machine already has
 * (PUPPETEER_EXECUTABLE_PATH or CHROME_BIN) instead of downloading a second one; the
 * sandbox flags are the working set for CI and dev containers, which commonly run as root.
 *
 * The standard's recipe reads `mmdc -i … -o ….pdf --pdfFit -b transparent`. Neither flag is
 * passed here, on purpose: the pinned @mermaid-js/mermaid-cli 12 has no --pdfFit, because it
 * fits a PDF to the diagram unless --pdf-paper-format is given; and without -b the PDF keeps
 * the renderer's default white background, which on the page's white is the same thing.
 * Re-check both against `mmdc --help` when the renderer is bumped.
 *
 * Usage: node scripts/render-diagrams.mjs [slug-filter...]
 */

import { readdirSync, mkdirSync, existsSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const sources = join(root, 'docs', 'diagrams');
const rendered = join(sources, 'rendered');
const mmdc = join(root, 'node_modules', '.bin', 'mmdc');

if (!existsSync(mmdc)) {
  console.error('render-diagrams: node_modules/.bin/mmdc is missing. Run `npm ci` first.');
  process.exit(1);
}

mkdirSync(rendered, { recursive: true });

const browser = process.env.PUPPETEER_EXECUTABLE_PATH || process.env.CHROME_BIN;
const puppeteer = {
  args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  ...(browser ? { executablePath: browser } : {}),
};
const puppeteerConfig = join(rendered, 'puppeteer-config.json');
writeFileSync(puppeteerConfig, JSON.stringify(puppeteer, null, 2));

const filters = process.argv.slice(2);
const slugs = readdirSync(sources)
  .filter((name) => name.endsWith('.mmd'))
  .map((name) => name.slice(0, -'.mmd'.length))
  .filter((slug) => filters.length === 0 || filters.some((f) => slug.includes(f)))
  .sort();

if (slugs.length === 0) {
  console.error('render-diagrams: no diagram matched.');
  process.exit(1);
}

for (const slug of slugs) {
  execFileSync(mmdc, [
    '-i', join(sources, `${slug}.mmd`),
    '-o', join(rendered, `${slug}.pdf`),
    '-c', join(sources, 'mermaid-config.json'),
    '-p', puppeteerConfig,
    '-q',
  ], { stdio: 'inherit' });
  console.log(`render-diagrams: ${slug}.pdf`);
}
