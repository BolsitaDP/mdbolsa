# mdbolsa

A personal, local-first, self-hosted knowledge management application —
Markdown notes, wiki-links, backlinks, search, a knowledge graph, canvas,
and multi-device sync — inspired by Obsidian but with its own architecture.

Full requirements: [docs/vision.md](docs/vision.md). Architecture:
[docs/architecture.md](docs/architecture.md). Decision log:
[docs/decisions/](docs/decisions/).

## Status

**Phase 7 — Knowledge graph.** The WinUI shell opens a vault, lists its
notes, lets you open/edit/save one as plain Markdown text while seeing its
backlinks, searches the whole vault (SQLite FTS5 — no new dependency), reads
tags from both frontmatter (`tags:`) and inline `#hashtags` into a tag panel —
click a tag to filter the notes list — shows a read-only line with the note's own
metadata, and has a graph view: whole-vault or note-local, with a force-directed
layout computed in Core and drawn as circles and lines (drag to pan, click a node
to inspect it). Picking the vault uses the Windows folder picker, the choice is
remembered, and right-clicking a note renames it. Phase 3's editor (typing +
saving) was blocked for a while by a WindowsAppSDK 2.4.0 crash reading a
`TextBox`'s edited content back; that and two later native crashes (navigating
the page that owns the editor, and opening XAML popups) are documented in
docs/architecture.md's Known Issues section — read it before adding UI, because
this runtime is unforgiving. See
[docs/vision.md §17](docs/vision.md#17-phased-roadmap) for the full phase
roadmap.

## Running it

Double-click **`mdbolsa.lnk`** on the desktop: it rebuilds and launches the
shell. There is no standalone `.exe` — this is a packaged WinUI 3 app, so it
needs package identity; see [docs/development.md](docs/development.md) for why,
and for the exact commands.

## Structure

```
docs/                       Vision, architecture, dev/deploy/sync docs, ADRs
src/Client/MdBolsa.Core/     Domain logic, parsing, indexing (platform-agnostic)
src/Client/MdBolsa.Data/     SQLite-backed storage (platform-agnostic)
src/Client/MdBolsa.Desktop.WinUI/  WinUI 3 shell
src/Server/MdBolsa.Server/   ASP.NET Core sync/storage API (Phase 8)
tests/                       Unit tests for Core, Data and Server
samples/dev-vault/           Fake vault used as the Development default
config/                      `.example` templates for local config/secrets
run.cmd                      Build + launch the desktop shell
```

## Build

```bash
dotnet build src/MdBolsa.sln
dotnet test src/MdBolsa.sln
```

Running the WinUI 3 shell (not just building it) requires Windows
Developer Mode enabled and the `winapp` CLI (plain `dotnet run` doesn't
register the debug package identity correctly with this preview
tooling). Once it's running, type a vault folder path (e.g.
`samples/dev-vault`, as an absolute path) into the text box, click
"Open vault", then either click a note to view/edit it and see its
backlinks, click a tag in the panel to filter the list by it, type
into the search box and click "Search" ("Show all notes" clears back
to the full list), or click "Graph" for the knowledge-graph view.
See
[docs/development.md](docs/development.md) for the exact run command
and the full dev setup / branching workflow.

## Principles

- **Local-first.** The desktop app is fully usable offline; the Raspberry
  Pi is a sync/storage server, never the primary runtime.
- **Markdown is canonical.** SQLite only ever holds derived indexes, never
  the only copy of note content.
- **Dev/Prod isolation.** Development never touches production data;
  switching to Production is always an explicit, deliberate step.
- **No overengineering.** Every dependency and abstraction has to earn its
  place against the current phase's actual need.
