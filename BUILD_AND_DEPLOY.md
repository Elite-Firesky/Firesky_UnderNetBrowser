# Build and Deployment Guide

This document describes how to build the **managed** and **native** components of Firesky UnderNet Browser and deploy them to Schedule I. It does not change mod behavior; adjust paths/versions to your local environment.

## Prerequisites

### Common
- Windows x64.
- A local **Schedule I** install with **MelonLoader (net6)** set up.
- **Microsoft Edge WebView2 Runtime** (Evergreen, x64) installed for runtime; WebView2 SDK/native package available for native builds.

### Managed (C#)
- .NET 6 SDK (`dotnet`).
- Access to referenced assemblies from the game/MelonLoader install:
  - MelonLoader, Il2CppInterop, 0Harmony, game/Unity Il2Cpp assemblies.
  - `S1API` and `ModManager&PhoneApp` (as referenced by `Firesky_UnderNetBrowser.csproj`; paths may differ per setup).
- `Firesky_UnderNetBrowser.csproj` uses `GamePath` (defaults to `C:\Program Files (x86)\Steam\steamapps\common\Schedule I\MelonLoader`). Override for your machine, e.g.:
  - `dotnet build Firesky_UnderNetBrowser.csproj -c Release -p:GamePath="D:\Games\Schedule I\MelonLoader"`

### Native (C++ / CMake)
- Visual Studio 2022 (or Build Tools) with Desktop C++ workload, MSVC x64 toolset.
- Windows 10/11 SDK.
- CMake 3.20+ (or use VS/MSBuild with generated solutions).
- Microsoft.Web.WebView2 native headers/libs (NuGet package, e.g. `microsoft.web.webview2`) for components that require them (see `native/GpuRenderer/CMakeLists.txt` / component READMEs).

## Build

Run commands from the repository/project root unless noted.

### 1) Managed mod DLL

```powershell
dotnet build Firesky_UnderNetBrowser.csproj -c Release
# or with custom game path:
# dotnet build Firesky_UnderNetBrowser.csproj -c Release -p:GamePath="<path-to-Schedule I>\MelonLoader"
```

**Output:** `bin/Release/Firesky_UnderNetBrowser.dll` (and any copied content, e.g. `WebView2Loader.dll` per project config). `bin/`/`obj/` are build artifacts (not committed).

> The project references `Redist/WebView2/lib/Microsoft.Web.WebView2.Core.dll` and copies `Redist/WebView2/WebView2Loader.dll` as content (see `.csproj`). Ensure required redistributables are present in `Redist/WebView2/` for your build.

### 2) Native renderer (`UnderNetGpuRenderer`)

Primary runtime component: `native/GpuRenderer`.

```powershell
# Configure (example; adjust generator/paths as needed)
cmake -S native/GpuRenderer -B native/GpuRenderer/build -G "Visual Studio 17 2022" -A x64

# Build Release x64
cmake --build native/GpuRenderer/build --config Release
# or with MSBuild:
# msbuild native/GpuRenderer/build/UnderNetGpuRenderer.sln /p:Configuration=Release /p:Platform=x64 /m
```

**Typical output:** `native/GpuRenderer/build/bin/Release/UnderNetGpuRenderer.dll` (exact path may vary by CMake config; verify after build).

`native/GpuRenderer/build/` and other CMake/MSBuild intermediates are generated (not committed). See `native/GpuRenderer/README.md` for integration notes (Unity plugin/device readiness) and phase status.

### 3) Optional components

- **`native/CompositionPoC/`**: standalone feasibility scaffold; build separately if needed (see its README/CMake). Not required for normal mod deployment.
- **`tools/VirtualEndpointLoopbackTest/`**: standalone investigation harness with its own `build.cmd`/sources; not part of mod runtime.

## Deployment

Deploy to a **local** Schedule I install (paths vary by user). Do not commit game-install paths or deployed binaries.

### Typical layout

| Component | Deploy to |
|---|---|
| `Firesky_UnderNetBrowser.dll` (managed mod) | `<Schedule I>\Mods\` |
| `Microsoft.Web.WebView2.Core.dll`, `WebView2Loader.dll` (WebView2 dependencies) | `<Schedule I>\Mods\UnderNetBrowser\` (or alongside the mod DLL in `<Schedule I>\Mods\`; mod resolves both locations) |
| `UnderNetIcon.png` (app icon) | embedded in `Firesky_UnderNetBrowser.dll` (no separate file required; loose-file fallback still supported) |
| `UnderNetGpuRenderer.dll` (native) | `<Schedule I>\Schedule I_Data\Plugins\x86_64\` (Unity native plugin path; restart game after changes) |
| Default browser extension (`uBlockOriginLite`) | pre-seed in `<Schedule I>\UserData\UnderNetBrowser\WebView2Composition\Extensions\uBlockOriginLite\` for clients, or keep a source copy (e.g. `BrowserExtensions\uBlockOriginLite` in repo) for auto-deploy; runtime uses the WebView2 composition profile under `UserData\UnderNetBrowser\WebView2Composition\` (do not duplicate into extra `UserData\...\Extensions` or game `Mods` folders at runtime) |

A local `for_client/`-style package folder may be used to stage these files for distribution/testing, but it is a deployment artifact and is excluded from version control. The staged client package should contain only files required by the client runtime (no redundant extension copies).

### Notes

- **Restart the game** after updating native plugins/DLLs to ensure clean loads and correct Unity graphics device integration.
- After changes, verify in-game behavior and check MelonLoader logs. Runtime diagnostics (if enabled) and WebView2 user/profile data are written under `UserData/UnderNetBrowser/` in the game directory (local, not committed).
- Ensure the Evergreen WebView2 Runtime is installed on the target machine.
- Keep managed and native binaries in sync when interfaces/behavior change.

## Troubleshooting (build)

- **Missing game/MelonLoader/S1API references:** set `-p:GamePath=...` correctly and confirm dependency DLL locations/versions match your Schedule I + mod loader setup.
- **Native build cannot find WebView2 SDK:** provide/configure the WebView2 native package path per CMake/component requirements.
- **Wrong architecture:** build native components for **x64 / Release** to match the game process.
- **Stale artifacts:** clean `bin/`, `obj/`, and native `build/` directories if you hit unexplained issues (generated; safe to recreate).
