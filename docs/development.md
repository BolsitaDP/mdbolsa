# Development Guide

## Prerequisites

- .NET 8 SDK (currently installed: 8.0.418)
- Visual Studio 2022 with the **.NET Desktop Development** and
  **Windows App SDK (WinUI)** workloads — required starting Phase 1, not
  needed to build Core/Data/tests today.

## Build & Test

```bash
dotnet build src/MdBolsa.sln
dotnet test src/MdBolsa.sln
```

`MdBolsa.Desktop.WinUI` isn't part of the solution yet (Phase 1); only
`MdBolsa.Core`, `MdBolsa.Data`, and their test projects build today.

## Environments

Running from Visual Studio or the CLI always defaults to **Development**.
Development uses:

- App data under `%LOCALAPPDATA%\MdBolsa.Dev\`
- The sample vault at [`samples/dev-vault/`](../samples/dev-vault) by
  default
- `appsettings.Development.json` (copy from
  [`config/appsettings.Development.json.example`](../config/appsettings.Development.json.example),
  never commit the real file)

Switching to **Production** is an explicit, deliberate configuration
change — never the default, never triggered implicitly by a build
configuration alone. Production configuration lives only on the Raspberry
Pi and is never committed to this repo.

## Branching

- `feature/*` branches off `develop` for individual tasks (e.g.
  `feature/wiki-links`).
- Merge completed features into `develop` for integration testing.
- `develop` merges into `main` only for stable, production-ready states.
- Raspberry Pi deployments come from tagged commits on `main` — never
  automatic, always an explicit deploy step.

## Secrets & Configuration

Never commit `.env`, `appsettings.Development.json`, or
`appsettings.Production.json` — only the `.example` templates in
[`config/`](../config). See [architecture.md](architecture.md) for how
configuration maps to Dev/Prod isolation.
