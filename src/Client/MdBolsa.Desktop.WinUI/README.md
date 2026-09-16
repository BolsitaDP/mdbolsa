# MdBolsa.Desktop.WinUI

The WinUI 3 shell (Phase 1). Scaffolded via the official
[dotnet CLI WinUI templates](https://devblogs.microsoft.com/ifdef-windows/introducing-dotnet-new-templates-for-winui/)
(`dotnet new winui3`), not Visual Studio — no IDE is required to build or
run it, only the .NET SDK.

Contains no business logic — it composes and displays
[`MdBolsa.Core`](../MdBolsa.Core) and [`MdBolsa.Data`](../MdBolsa.Data),
referenced here but not yet wired into any UI (that starts in Phase 2,
once there's a vault to load).

## Run

```bash
dotnet run --project src/Client/MdBolsa.Desktop.WinUI
```

First run registers a dev package identity via the `winapp` CLI
(provided by `Microsoft.Windows.SDK.BuildTools.WinApp`) and launches the
app with package identity — no manual MSIX install step needed.
