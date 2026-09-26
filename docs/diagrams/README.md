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

`mermaid-config.json` sets the theme in the house colours, a font with Polish glyphs, and
compact spacing. It names the layout (`"layout": "dagre"`) on purpose: Mermaid 12 ignores
`nodeSpacing` and `rankSpacing` unless the layout is named, and without the compact spacing
the labels shrink below a readable size once a diagram is scaled to the text width.

Size a diagram for the page, not for the screen. The text is about 470 pt wide, so a
diagram more than about 1000 px wide prints its 15 px labels below 7 pt; prefer a
top-to-bottom layout, short labels, and a size bound in the `\includegraphics` call
(`width=\linewidth,height=0.6\textheight,keepaspectratio`) over a wide left-to-right chain.

## Language: one Polish set

The labels are Polish, with technical names kept in English, because the only document
that includes these diagrams is written in Polish. There is no English set, so nothing can
drift between two. If an English edition of the document is ever added, it decides then
whether to translate the diagrams: a translated set means a second `.mmd` per diagram and a
drift the build cannot see; a shared set means English readers get Polish boxes. That
decision belongs here, written down, when it is taken.
