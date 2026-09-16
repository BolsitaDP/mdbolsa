# Development Guide

## Prerequisites

- .NET 8 SDK (currently installed: 8.0.418)
- **Windows Developer Mode enabled** — Settings → Privacy & security →
  For developers → Developer Mode. Required to run/sideload
  `MdBolsa.Desktop.WinUI` locally without a signed MSIX. Not needed just
  to build.
- No Visual Studio IDE is required. The WinUI 3 project was scaffolded
  and is built/run entirely via the .NET CLI (`dotnet new winui3` +
  Microsoft's official CLI templates/tooling) plus Visual Studio Build
  Tools 2022 (already installed on this machine, providing MSBuild/C++
  toolchain bits the Windows SDK build tools need).

## Build & Test

```bash
dotnet build src/MdBolsa.sln
dotnet test src/MdBolsa.sln
```

## Run the Desktop Shell

```bash
dotnet run --project src/Client/MdBolsa.Desktop.WinUI -c Debug
```

Requires Developer Mode (see above) — without it, launch fails with
"Developer Mode is not enabled on this machine." First run registers a
debug package identity via the `winapp` CLI
(`Microsoft.Windows.SDK.BuildTools.WinApp`) and launches with that
identity; no manual MSIX install step needed.

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
