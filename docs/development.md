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

Plain `dotnet run` does **not** correctly register the debug package
identity with this (preview) tooling and will fail with
`REGDB_E_CLASSNOTREG`. Use the `winapp` CLI directly instead — it ships
inside the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package, not
as an installable `dotnet tool`:

```bash
"$HOME/.nuget/packages/microsoft.windows.sdk.buildtools.winapp/<version>/tools/win-x64/winapp.exe" \
  run src/Client/MdBolsa.Desktop.WinUI/MdBolsa.Desktop.WinUI.csproj -c Debug --arch x64 --detach
```

Substitute the installed `<version>` (check the folder, or the
`Microsoft.Windows.SDK.BuildTools.WinApp` `PackageReference` version in
`MdBolsa.Desktop.WinUI.csproj`). Requires Developer Mode (Settings →
Privacy & security → For developers). First run also installs any
missing Windows App Runtime MSIX packages and registers a loose-layout
package — no manual MSIX install step needed.

Once the window is open, type a vault folder's **absolute** path (e.g.
the full path to [`samples/dev-vault/`](../samples/dev-vault)) into the
text box and click "Rescan vault". It indexes that vault into a local
SQLite db and lists what it found. There's no default/remembered path
yet (Phase 2 doesn't persist app settings) - re-typing it after every
restart is expected for now.

Don't try to pre-seed configuration by writing files into
`%LOCALAPPDATA%\MdBolsa.Dev\` from outside the app (a script, a manual
test step) - packaged apps get that folder redirected to a per-package
virtualized location invisible to external writers. See
[architecture.md](architecture.md) for why.

## Environments

Running from Visual Studio or the CLI always defaults to **Development**
- there is no code path that switches to Production implicitly. The
`AppEnvironment`/`appsettings.{Environment}.json` config loading
described in `config/*.example` is not wired up yet (nothing needs it
until a server exists to point at, Phase 8+); today "Development" just
means the app only ever touches its own local SQLite index under
`%LOCALAPPDATA%\MdBolsa.Dev\` and whatever vault folder you type in.
Switching to **Production** will always be an explicit, deliberate
configuration change — never the default, never triggered implicitly by
a build configuration alone. Production configuration will live only on
the Raspberry Pi and will never be committed to this repo.

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
