# mdbolsa

A personal, local-first, self-hosted knowledge management application —
Markdown notes, wiki-links, backlinks, search, a knowledge graph, canvas,
and multi-device sync — inspired by Obsidian but with its own architecture.

Full requirements: [docs/vision.md](docs/vision.md). Architecture:
[docs/architecture.md](docs/architecture.md). Decision log:
[docs/decisions/](docs/decisions/).

## Status

**Phase 0 — Repository and architecture.** Solution/project skeleton and
docs are in place; there is no runnable UI yet. See
[docs/vision.md §17](docs/vision.md#17-phased-roadmap) for the full phase
roadmap. Next up: **Phase 1 — Native Windows shell.**

## Structure

```
docs/                       Vision, architecture, dev/deploy/sync docs, ADRs
src/Client/MdBolsa.Core/     Domain logic, parsing, indexing (platform-agnostic)
src/Client/MdBolsa.Data/     SQLite-backed storage (platform-agnostic)
src/Client/MdBolsa.Desktop.WinUI/  WinUI 3 shell (placeholder — Phase 1)
tests/                       Unit tests for Core and Data
samples/dev-vault/           Fake vault used as the Development default
config/                      `.example` templates for local config/secrets
```

## Build

```bash
dotnet build src/MdBolsa.sln
dotnet test src/MdBolsa.sln
```

Building/running the WinUI 3 shell requires Visual Studio 2022 with the
Windows App SDK workload, starting Phase 1. See
[docs/development.md](docs/development.md) for the full dev setup and
branching workflow.

## Principles

- **Local-first.** The desktop app is fully usable offline; the Raspberry
  Pi is a sync/storage server, never the primary runtime.
- **Markdown is canonical.** SQLite only ever holds derived indexes, never
  the only copy of note content.
- **Dev/Prod isolation.** Development never touches production data;
  switching to Production is always an explicit, deliberate step.
- **No overengineering.** Every dependency and abstraction has to earn its
  place against the current phase's actual need.
