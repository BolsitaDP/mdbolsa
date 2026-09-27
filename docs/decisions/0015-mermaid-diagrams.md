# 0015 - Diagrams as Mermaid flowcharts, in the preview

Date: 2026-09-27
Status: accepted
Related: 0013 (live preview and source), 0009 (local graph), 0002 (Markdown is canonical)

## What this is

A ```` ```mermaid ```` fence in a note is drawn as a diagram in the preview pane,
beside the source. The note is still Markdown; the diagram is a second way of
reading a block of text that was always there.

Phase 12 is "canvas **and** diagrams". This is the diagrams half, and it is first
on purpose - see "Why not the canvas first" below.

## The decision that matters: refuse rather than approximate

The parser understands the flowchart subset of Mermaid. Anything outside it
returns a **parse failure**, and the shell shows the block as code with the reason
underneath. It does not render a partial diagram.

This is the whole design, and it is inherited from 0013. The alternative -
"render what you can, drop the rest" - produces a picture that looks right and is
wrong about what the person wrote, and the reader has no way to tell. A code block
is a visible failure; a wrong diagram is an invisible one. The error carries the
line number, because "this did not render" is not actionable and "line 4: expected
an arrow after `C`" is.

The same reasoning decides what *is* silently ignored: `style`, `classDef` and
`click` are skipped **with a warning**, because they change appearance and never
meaning. A diagram missing your custom colours is still the diagram you wrote; a
diagram missing an arrow is not.

## Hand-written, no Mermaid.js

Mermaid is a large JavaScript library. Bundling it means a WebView2 host, a
bundled .js file, and a rendering path that cannot be unit tested - and the
project already decided against that trade for the Markdown renderer (0013), for
the same reason. So the parser is hand-written in Core, next to the Markdown one,
and it is testable.

What that costs is stated plainly in the demo note: `sequenceDiagram`,
`classDiagram`, `stateDiagram`, `erDiagram`, `gantt`, `pie` and `journey` are not
drawn. That is a real limitation and the next thing to add, if it is wanted -
`sequenceDiagram` in particular, because it is the second most common thing
people write. Each is a self-contained addition to the same two files.

## A ranked layout, not the force layout

`GraphLayout` is force-directed, which is right for a relationship map over a
whole vault and wrong for a flowchart: nobody draws a decision tree and expects a
hairball. So `FlowchartLayout` is a second algorithm - longest-path ranking plus
barycentre crossing reduction, then a pass that pushes boxes apart and a pass that
pulls them back towards their parents.

It is a separate algorithm but the same discipline as the graph: pure arithmetic
in Core, no randomness, bounded iteration, so the same diagram always produces the
same picture. That is what makes the tests assertable and the preview jump-free.

Two things it had to get right, both of which it got wrong first:

- **Cycles.** `A --> B --> A` has no rank order. The relaxation is bounded, so a
  cycle leaves its members sharing a rank and reads fine, instead of never settling.
- **A lone node in a rank still belongs next to its parents.** Centring every row
  is simple and wrong: it puts a converging node under the middle of the diagram
  rather than beside the parent that makes it unique, and the edge from that
  parent then runs the full width of the picture.

## The floor, and why a diagram scrolls

`FlowchartLayout.MinimumScale` is the one judgement call in the layout: a diagram
is never shrunk below 0.6 to force it to fit. Below that, boxes hold fewer
characters and a node labelled "Escribes una nota" shows four letters and an
ellipsis. Past the floor the layout stops shrinking and reports a content extent
larger than the pane, and the shell turns that into a scrollbar.

A diagram of unreadable stubs that fits perfectly is worse than a diagram you have
to scroll, because the first one looks correct. This was also found by writing the
test: the first version shrank a ten-node diagram until every label was an
ellipsis, and the test said so.

## Why not the canvas first

The canvas needs a file format decision before any code - what a `.canvas` file
is, whether it is canonical, and how it syncs given that it is neither a note nor
an attachment. That is an ADR of its own and it should be written before the
first line of it.

Diagrams needed no such decision: they live inside a `.md` file that already
syncs, and they added no new file type, no new table, and no new endpoint. Doing
them first meant the shared-presentation-layer question ADR 0009 left open ("if a
second visual representation appears, the colour decisions should move to a shared
place") got answered by `Presentation` while there was only one new consumer,
instead of being answered twice in a hurry.

## Known crashes, and one of them mine

`FlowchartPresenter` re-lays out when the pane is resized, and the first version
did that inside a `SizeChanged` handler - mutating each child's size and position
while the tree was still being measured. That killed the process with the stowed
native exception this runtime is known for (`0xc000027b` in
`Microsoft.UI.Xaml.dll`). It is now deferred to the next dispatcher turn, which is
after the measure pass.

The same phase also removed the `Expander` it had introduced. Not a style choice:
the Known Issues in `architecture.md` are a list of what this runtime has crashed
on, and `Expander` was a control type nothing else in the app used. The toggle is
a `Button` and a `Visibility`, which is what the sidebar panels already do.

## Testing

- 59 parser tests. Every one is an input a real note contains and a naive reader
  gets wrong: nested delimiters in a label (`A[what if [this] happens?]`), a
  quoted label that still has to close its shape (`A["text"]`), the two spellings
  of an edge label, `[/` which opens both a parallelogram and a trapezoid, and a
  node id that starts with `-` reading `-->` as a node called `--`.
- 17 layout tests, asserting the three things that are assertable: a chain runs in
  a line, branches do not overlap, and the same diagram always lays out the same.
- 20 tests over the four diagrams in `Ejemplo/Diagramas.md`, checking the geometry
  the shell is handed: every box inside the frame, no two overlapping, every edge
  pointing at two real nodes. These stand in for the XAML, which a test cannot
  reach.

**Not verified automatically**: that the boxes actually appear. The drawing code
is XAML in a desktop window. The shell survived being relaunched and the preview
toggled with the demo note present, but the diagram itself was never seen drawn -
the input harness in that session could not get synthetic clicks into the window
(UIAutomation and SendInput both returned nothing). It needs a human.
