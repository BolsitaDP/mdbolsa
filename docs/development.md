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

### The end-to-end server tests

Half of `MdBolsa.Server.Tests` (sync, attachments, device tokens, version
history) drives the real client against a **running server and a real
PostgreSQL**. Without them those tests are reported as **skipped**, with the
reason - not as passed - so read the totals: `Passed: 27, Skipped: 27` means the
sync paths were *not* exercised.

To run them for real:

```bash
docker compose up -d                              # Postgres, 127.0.0.1:5432
dotnet run --project src/Server/MdBolsa.Server    # the API, http://localhost:5080
dotnet test tests/MdBolsa.Server.Tests            # in another terminal
```

Set `MDBOLSA_REQUIRE_SERVER=1` to make a missing stack a **failure** instead of
a skip - use it before tagging a release, where green has to mean tested. The
tests tidy up the rows they create, so they can be run repeatedly against the
shared development database; they never touch anything but `localhost`.

## Run the Desktop Shell

Double-click the **`mdbolsa.lnk`** shortcut on the desktop. It runs
[`run.cmd`](../run.cmd), which locates the `winapp` CLI in the NuGet
cache and runs:

```bash
"$HOME/.nuget/packages/microsoft.windows.sdk.buildtools.winapp/<version>/tools/win-x64/winapp.exe" \
  run src/Client/MdBolsa.Desktop.WinUI/MdBolsa.Desktop.WinUI.csproj -c Debug --arch x64 --detach
```

`<version>` is looked up automatically (highest installed) — check
`Microsoft.Windows.SDK.BuildTools.WinApp`'s `PackageReference` version in
`MdBolsa.Desktop.WinUI.csproj` if you need to know. Requires Developer
Mode (Settings → Privacy & security → For developers). First run also
installs any missing Windows App Runtime MSIX packages and registers a
loose-layout package — no manual MSIX install step needed.

If the shortcut ever goes missing (new machine, moved repo), recreate it
with:

```powershell
$repo = "C:\Users\sgira\OneDrive\Escritorio\mdbolsa"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut("$([Environment]::GetFolderPath('Desktop'))\mdbolsa.lnk")
$s.TargetPath = "$env:SystemRoot\System32\cmd.exe"
$s.Arguments = "/c `"`"$repo\run.cmd`"`""
$s.WorkingDirectory = $repo
$s.WindowStyle = 7   # minimised: no console window in the way
$s.IconLocation = "$repo\src\Client\MdBolsa.Desktop.WinUI\Assets\AppIcon.ico,0"
$s.Save()
```

**There is no portable .exe to launch directly.** The build does produce
`MdBolsa.Desktop.WinUI.exe`, but it dies on startup with
`REGDB_E_CLASSNOTREG`: WinUI 3 packaged apps need package identity for
the Windows App Runtime to resolve. Two things that look like they
should work and don't: `create-debug-identity` on the published exe
(registers the package in an empty install location, exe still fails),
and `winapp run <publish-folder>` (registers, but the published exe
still can't resolve the runtime). Project mode is the one that works.
A real standalone build means a signed `.msix` — Phase 14.

Once the window is open, click **Browse...** to pick a vault folder with
the Windows folder picker (the text box is still there if you'd rather
paste a path). The chosen folder is remembered, so the next launch
reopens it automatically. To try the sample vault, pick
[`samples/dev-vault/`](../samples/dev-vault).

Don't try to pre-seed configuration by writing files into
`%LOCALAPPDATA%\MdBolsa.Dev\` from outside the app (a script, a manual
test step) - packaged apps get that folder redirected to a per-package
virtualized location invisible to external writers. See
[architecture.md](architecture.md) for why. The remembered vault path
is different: it lives in the app's own `ApplicationData.Current`
settings store, which is the only place that *is* writable from
outside.

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
