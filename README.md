# mdbolsa

A personal, local-first, self-hosted knowledge management application —
Markdown notes, wiki-links, backlinks, search, a knowledge graph, canvas,
and multi-device sync — inspired by Obsidian but with its own architecture.

Full requirements: [docs/vision.md](docs/vision.md). Architecture:
[docs/architecture.md](docs/architecture.md). Decision log:
[docs/decisions/](docs/decisions/).

## Status

**Phase 4 — Wiki links and backlinks.** The WinUI shell opens a vault,
lists its notes, and lets you open, edit, and save one as plain
Markdown text while seeing its backlinks update alongside it. Phase 3's
editor (typing + saving) was blocked for a while by a WindowsAppSDK
2.4.0 crash reading a `TextBox`'s edited content back — resolved (see
docs/architecture.md's Known Issues section for what fixed it and what's
still not fully understood about why). See
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
"Open vault", then click a note to view/edit it and see its backlinks.
See [docs/development.md](docs/development.md) for the exact run
command and the full dev setup / branching workflow.

## Principles

- **Local-first.** The desktop app is fully usable offline; the Raspberry
  Pi is a sync/storage server, never the primary runtime.
- **Markdown is canonical.** SQLite only ever holds derived indexes, never
  the only copy of note content.
- **Dev/Prod isolation.** Development never touches production data;
  switching to Production is always an explicit, deliberate step.
- **No overengineering.** Every dependency and abstraction has to earn its
  place against the current phase's actual need.
