# 0009-local-graph

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Phase 7 of [the roadmap](../vision.md#17-phased-roadmap): the knowledge graph
([vision.md §12](../vision.md#12-knowledge-graph)) — notes and other entities
become nodes, relationships become edges. Explicit requirements from the brief:
global graph, note-local graph, filtering, tags, groups, zoom, pan, node
selection, relationship exploration; **layout and rendering happen on the
client, the server never calculates visual node positions**.

Everything the graph needs is already indexed: notes (`notes`), resolved and
unresolved wiki links (`links`), and tags (`note_tags`). No new table, no new
scanner, no new dependency.

## Decision

**One builder, two scopes.** `GraphBuilder.BuildGlobal()` produces the whole
graph; `BuildLocal(centerNoteId, depth)` is a breadth-first expansion over that
same graph, returning the induced subgraph. One code path means the local graph
is by construction a subset of the global one, and depth is a parameter rather
than a second implementation. Traversal is **undirected** — a note's local graph
shows what it links to *and* what links to it, since backlinks are half of what
makes the view useful. Depth is clamped to `1..MaxDepth` (5) so a densely linked
vault can't ask for an unbounded neighbourhood.

**Tags are nodes, not just a filter.** A tag is a first-class node joined to the
notes carrying it. This is what makes the graph able to answer "what else is
like this note?" for notes that share only a tag, and it falls out of the model
for free given `note_tags` already exists. Tag edges are directed note → tag;
wiki-link edges are directed source → target but drawn as undirected lines
(direction is a data fact, not something this phase visualizes).

**Unresolved links are counted, not drawn.** A broken `[[link]]` has no node to
attach to, so it would be either invisible or a fake node. The model carries
`UnresolvedLinkCount` instead, and the UI states it ("3 broken link(s) are not
shown as nodes") — consistent with how [0006](0006-wiki-link-resolution.md)
keeps unresolved links in the index rather than discarding them, and leaving the
"broken links" panel for a later phase. Self-links are skipped entirely (a
zero-length line is not information).

**Layout lives in Core, not the shell.** `GraphLayout.Compute` is a
Fruchterman-Reingold force-directed layout: repulsion between all pairs,
attraction along edges, a cooling temperature, and a pull toward the viewport
centre. It's plain `double` arithmetic with a fixed iteration count and a
deterministic circular seed — **no RNG** — so the same graph always produces the
same pixels. That is what makes it assertable in unit tests (positions in
bounds, nodes separated, identical output across runs) and non-jarring in the
UI, and it matches the brief's client-side-layout rule while keeping the
algorithm reusable by the Phase 13 clients. The dictionary from node key to
index is built once per call, not per edge per iteration, which is the
difference between milliseconds and seconds at a few hundred nodes.

**The model is data; the shell draws it.** `GraphModel` has no coordinates, no
colours, no viewport. The WinUI page lays the graph out in a fixed virtual
space (1000×800) and maps it to the window with a scale/translate
`RenderTransform`, so resizing the window changes the view transform and nothing
else — the layout isn't recomputed on every resize. Nodes are `Ellipse`s, edges
are `Line`s, labels are `TextBlock`s: the same proven-stable primitives the rest
of the shell uses, no custom control templates yet.

**The graph is a second page, not another panel.** `MainPage` is already dense
(editor, notes, tags, backlinks, metadata); the graph is a different *view* of
the same indexes. The shared state both pages need — the open vault path, the
index factories, and the note last opened — moved into a small static
`AppSession`. This is not a DI container, and deliberately so: a single-window,
single-vault app doesn't need one yet, and adding `Microsoft.Extensions.DependencyInjection`
now would be exactly the "infrastructure because it might be useful someday"
that [vision.md §14](../vision.md#14-development-philosophy) warns against.

## Consequences

- The graph is a derived view like every other index: delete the SQLite file,
  rescan, and it comes back. No new persistence, and no way for it to disagree
  with the vault, since it reads the same three indexes the notes list, tag
  panel and backlinks read.
- Node colours are hardcoded in the shell (blue = note, purple = tag, gold =
  center). Fine for one view; if a second visual representation appears
  (Phase 12's canvas/diagrams), the colour/label decisions should move into a
  shared presentation layer then, not now.
- Zoom is two toolbar buttons plus touch/pen pinch (`ManipulationDelta`);
  mouse-wheel zoom was dropped because `PointerWheelChangedEventArgs` doesn't
  exist in the WinUI metadata this project builds against (verified by
  inspecting the `.winmd` strings) — not worth fighting the tooling for. Pan is
  drag, and "Reset view" re-fits.
- Interaction is click/drag only — no `TextChanged`-driven updates, for the
  reason documented in `MainPage`'s class comment.
- Node selection shows what a node is and how many relationships it has, and can
  re-center the local graph on it ("Make center"). It does **not** jump to the
  editor: opening a note from another page needs a cross-page navigation path
  that isn't worth building until there's a real reason to want it.
- Labels show a note's file name, not its full relative path, to keep the graph
  readable; the info bar under the graph shows the full path, so two same-named
  notes in different folders stay distinguishable.
- The layout is recomputed for every rebuild, which is O(n²) per iteration. Fine
  for a personal vault (hundreds of notes); a vault in the tens of thousands
  would need a Barnes-Hut approximation or a cheaper layout, and would want
  incremental updates rather than a full rebuild per view change.
