# mdbolsa

A personal, local-first, self-hosted knowledge management application —
Markdown notes, wiki-links, backlinks, search, a knowledge graph, canvas,
and multi-device sync — inspired by Obsidian but with its own architecture.

Full requirements: [docs/vision.md](docs/vision.md). Architecture:
[docs/architecture.md](docs/architecture.md). Decision log:
[docs/decisions/](docs/decisions/).

## Status

**Phase 4 — Wiki links and backlinks.** The WinUI shell is a read-only
vault/note browser: open a vault, click a note to see its content and
its backlinks (also clickable). Phase 3's editor (typing + saving) is
**blocked** on `feature/editor` by an unresolved WindowsAppSDK 2.4.0
crash reading a `TextBox`'s edited content back — see the Known Issues
section in [docs/architecture.md](docs/architecture.md) before touching
that branch. This branch deliberately stays read-only to avoid it. See
[docs/vision.md §17](docs/vision.md#17-phased-roadmap) for the full phase
roadmap.

## Structure

```
docs/                       Vision, architecture, dev/deploy/sync docs, ADRs
src/Client/MdBolsa.Core/     Domain logic, parsing, indexing (platform-agnostic)
src/Client/MdBolsa.Data/     SQLite-backed storage (platform-agnostic)
src/Client/MdBolsa.Desktop.WinUI/  WinUI 3 shell
tests/                       Unit tests for Core and Data
samples/dev-vault/           Fake vault used as the Development default
config/                      `.example` templates for local config/secrets
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
"Open vault", then click a note to view it and its backlinks. See
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
