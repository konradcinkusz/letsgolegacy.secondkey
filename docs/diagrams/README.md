# Diagrams

The diagrams of the letsgolegacy framework documentation
([`docs/papers/letsgolegacy-framework.tex`](../papers/letsgolegacy-framework.tex)). One
diagram per file, one source: `<slug>.mmd` here is the only copy, rendered to a vector PDF
by [`scripts/render-diagrams.mjs`](../../scripts/render-diagrams.mjs) into `rendered/`,
which is build output and never committed. The document includes a diagram by slug through
the house preamble's `\dgm{<slug>}`, never by path.

```sh
npm ci                                  # the renderer, pinned in package-lock.json
node scripts/render-diagrams.mjs        # every diagram; pass slugs to render only some
```

`mermaid-config.json` sets the theme in the house colours and a font with Polish glyphs.

## Language: one Polish set

The labels are Polish, with technical names kept in English, because the only document
that includes these diagrams is written in Polish. There is no English set, so nothing can
drift between two. If an English edition of the document is ever added, it decides then
whether to translate the diagrams: a translated set means a second `.mmd` per diagram and a
drift the build cannot see; a shared set means English readers get Polish boxes. That
decision belongs here, written down, when it is taken.
