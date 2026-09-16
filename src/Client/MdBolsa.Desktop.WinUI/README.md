# MdBolsa.Desktop.WinUI (placeholder)

This project is intentionally **not scaffolded yet**.

Phase 0 stops at repository/architecture setup. The actual WinUI 3 project
(`App.xaml`, `MainWindow.xaml`, `Package.appxmanifest`, and the `.csproj` with
correct Windows App SDK wiring) is Phase 1's deliverable ("Native Windows
shell"), and is created from Visual Studio 2022's WinUI 3 project template so
the generated packaging/manifest files are correct out of the box.

Requirements before Phase 1 can start:
- Visual Studio 2022 with the **.NET Desktop Development** and
  **Windows App SDK (WinUI)** workloads/components installed.

Once created, this project will:
- Target `net8.0-windows10.0.19041.0`.
- Contain no business logic — it is a thin shell that composes and displays
  [`MdBolsa.Core`](../MdBolsa.Core) and [`MdBolsa.Data`](../MdBolsa.Data) via
  dependency injection.
- Be added to [`../../MdBolsa.sln`](../../MdBolsa.sln).
