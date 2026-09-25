# MechanicAI.Desktop

The Windows workstation app: WinUI 3 on the Windows App SDK, single-project MSIX.

The view models live in `src/MechanicAI.Presentation`, which has no WinUI dependency, and they are
unit-tested in `tests/MechanicAI.Presentation.Tests`. This project contains only:

- `App.xaml(.cs)`: the Generic Host (application, infrastructure and presentation services),
  Serilog file logging, global exception handlers, and database initialization at startup.
- `MainWindow.xaml(.cs)`: Mica backdrop, custom title bar, `NavigationView` + `Frame`, universal
  search, a global notice bar, and the startup and sign-in overlays.
- `Views/*Page.xaml`: one page per feature, bound to its view model with `x:Bind`.
- `Services/`: WinUI implementations of the presentation abstractions (navigation, dialogs,
  dispatcher, file picker, clipboard, launcher, theme).
- `Helpers/Ui.cs`: static functions for `x:Bind` function bindings (visibility, InfoBar severity).

| Page | View model |
| --- | --- |
| Home | `HomeViewModel` |
| Vehicles | `VehiclesViewModel` |
| Diagnostics | `DiagnosticsViewModel` |
| Trouble codes | `DtcLookupViewModel` |
| Live data | `LiveDataViewModel` |
| Knowledge base | `KnowledgeBaseViewModel` |
| Wiring | `WiringViewModel` |
| Research | `ResearchViewModel` |
| Assistant | `AssistantViewModel` |
| Training | `TrainingViewModel` |
| Apprentice | `ApprenticeViewModel` |
| Shop | `ShopViewModel` |
| History | `HistoryViewModel` |
| Settings | `SettingsViewModel` |
| (window) | `ShellViewModel` |

## Requirements

- Windows 10 1809 (build 17763) or later. Windows 11 is recommended because Mica needs it.
- .NET 10 SDK.
- Visual Studio 2022 17.14 or later, or Visual Studio 2026, with the **WinUI application
  development** workload. That workload installs the Windows App SDK C# templates and the MSIX
  tooling.

## Build and run

### Visual Studio

1. Open `MechanicAI.slnx`.
2. Set **MechanicAI.Desktop** as the startup project. The solution maps every solution platform
   (including *Any CPU*) to `x64` for this project, because WinUI needs a real platform. To build
   for ARM64 or x86, use the command line with `-p:Platform=ARM64` or change the mapping in
   Configuration Manager.
3. Choose a launch profile:
   - **MechanicAI.Desktop (Package)** (default): deploys the MSIX layout and runs it.
   - **MechanicAI.Desktop (Unpackaged)**: works only if the project is built unpackaged (see below).
4. Press F5.

### Command line

```powershell
# Packaged (MSIX layout). Run the app from Visual Studio or install the package (see Packaging).
dotnet build src/MechanicAI.Desktop -c Debug -p:Platform=x64

# Unpackaged: produces a normal .exe that you can run directly. The Windows App SDK runtime is
# bundled with it (WindowsAppSDKSelfContained is set automatically when WindowsPackageType=None).
dotnet build src/MechanicAI.Desktop -c Debug -p:Platform=x64 -p:WindowsPackageType=None
src\MechanicAI.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\MechanicAI.exe
```

## Packaging

The project is a single-project MSIX (`EnableMsixTooling=true`), and `Package.appxmanifest` holds
the package identity:

- Identity `MechanicAI.Workstation`, publisher `CN=Mechanic AI`, version `1.0.0.0`.
- Capabilities: `runFullTrust`, `internetClient`, `privateNetworkClientServer`. The last one is
  there for Ollama on the LAN, a SearXNG instance and the shop server.

To build a sideloadable package:

```powershell
dotnet publish src/MechanicAI.Desktop -c Release -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<thumbprint of a code-signing cert in CurrentUser\My>
```

The certificate subject must match `Publisher` in `Package.appxmanifest`. Visual Studio can also
do this: right-click the project, then **Package and Publish** > **Create App Packages**. For
unpackaged (xcopy or installer) distribution, publish with `-p:WindowsPackageType=None`. The
publish profiles in `Properties/PublishProfiles` (`win-x64`, `win-x86`, `win-arm64`) produce
self-contained, ReadyToRun output.

## Runtime layout

| What | Where |
| --- | --- |
| Database, settings, documents, photos, exports | `%LOCALAPPDATA%\MechanicAI` (for MSIX, the package's redirected LocalAppData). Override it with `MECHANICAI_DATA_DIR`. |
| Logs (daily rolling, 14 days) | `<data>\logs\mechanicai-YYYYMMDD.log` |
| API keys | `<data>\secrets.dat`, encrypted with Windows DPAPI for the current user |

Errors from view-model operations show up as an InfoBar on the page. Unhandled UI exceptions are
logged and shown in the window's notice bar, so the app keeps running. Process-level crashes are
logged before the process exits.

## Building on Linux and macOS

The WinUI XAML compiler and the MSIX tooling run only on Windows. On other operating systems this
project turns into an empty no-op (see `Build/NonWindows.targets`), so `dotnet build MechanicAI.slnx`
and `dotnet test MechanicAI.slnx` still work there and build and test everything else.
`-p:ForceDesktopBuild=true` makes a real attempt anyway. NuGet restore succeeds, and the build then
stops at `MarkupCompilePass1`, where the XAML compiler needs Windows.

## Assets

`Assets/` contains the app icon (`AppIcon.ico`: 16, 24, 32, 48, 64 and 256 px) and the standard
WinUI tile, splash and store images. `tools/make_icons.py` generates all of them (it needs Pillow):

```bash
python tools/make_icons.py src/MechanicAI.Desktop/Assets
```
