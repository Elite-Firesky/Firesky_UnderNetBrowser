# Firesky UnderNet Browser

A MelonLoader/S1API mod for **Schedule I** that adds an in-game **UnderNet** TV/browser app using a native WebView2 CompositionController rendering path (WGC/D3D11) blitted into Unity. It is designed for IL2CPP Schedule I (Unity 2022.3.x, D3D11) and keeps managed UI/input/lifecycle logic separate from the native rendering/browser host.

> **Status:** active development. Behavior, performance, and compatibility depend on game version, MelonLoader/S1API, WebView2 runtime, GPU/driver, and OS. Some features are experimental; spatial audio capture is currently gated/disabled by config.

## Project layout (logical separation)

- **Managed (C# / .NET 6, IL2CPP mod):** project root
  - `*.cs` — mod entry/lifecycle (`UnderNetBrowserMod`), app/UI/input (`UnderNetBrowserApp`, `UnderNetBrowserInputHooks`), config, sync, diagnostics, WebView2 probing, and managed/native interop/capture glue (`UnderNetBrowserGpuNative`, `UnderNetBrowserCompositionCapture`, etc.).
  - `Firesky_UnderNetBrowser.csproj`, `Properties/` — managed build/project metadata.
- **Native (C++ / CMake, Windows x64):** `native/`
  - `native/GpuRenderer/` — primary native plugin/DLL (`UnderNetGpuRenderer`): Unity D3D11 interop, WebView2 CompositionController hosting, WGC visual capture, D3D11 blit/copy to Unity-owned texture, input forwarding, diagnostics.
  - `native/CompositionPoC/` — standalone feasibility/prototype scaffold for the composition+WGC path (not required for normal mod runtime).
- **Tools:** `tools/` (e.g. `VirtualEndpointLoopbackTest` harness/source for audio-capture investigation).
- **Browser assets:** `BrowserExtensions/uBlockOriginLite/` — bundled extension assets deployed into the WebView2 profile/user data as configured by the mod.
- **Redistributables (build/runtime deps):** `Redist/WebView2/` — WebView2 loader/core assemblies referenced/packaged for the managed build (see build guide).
- **Docs:** `STAGE3_*.md`, component READMEs under `native/`.

Generated outputs, binaries, local deployment copies, diagnostics/logs, caches, and WebView2 user data are intentionally excluded from version control (see `.gitignore`).

## Architecture overview

1. **Managed mod (MelonLoader + S1API):** registers the UnderNet TV app, builds UI, handles game/input integration, and drives the browser lifecycle.
2. **Native renderer (`UnderNetGpuRenderer.dll`):** loaded as a Unity native plugin / via interop, runs WebView2 on a composition path (no visible browser window) and captures frames via Windows Graphics Capture (`CreateFromVisual`) into D3D11 textures.
3. **Frame delivery:** native copies/blits captured frames into a Unity-owned D3D11 texture on the render thread; managed side presents it in the TV UI.
4. **Input/navigation:** managed forwards pointer/keyboard/navigation/focus events to native/WebView2; URL/security handling and extension deployment follow mod logic (including uBlock Origin Lite assets).

The mod targets the **D3D11** Unity graphics path and requires a compatible Evergreen WebView2 runtime on the machine.

## Dependencies

### Runtime (game/user machine)
- Schedule I (IL2CPP) with MelonLoader (net6) and the mod dependencies referenced by the project (e.g. S1API, Harmony/Il2CppInterop as provided by MelonLoader, game/Unity assemblies).
- Microsoft Edge WebView2 Runtime (Evergreen), x64.
- Windows with D3D11 and WGC support for the composition capture path.

### Build (developer machine)
- **Managed:** .NET 6 SDK; access to Schedule I + MelonLoader/S1API/game assemblies (paths configured in `Firesky_UnderNetBrowser.csproj`, e.g. `GamePath`).
- **Native:** Visual Studio 2022/Build Tools (MSVC), Windows SDK, CMake 3.20+; NuGet WebView2 native package headers/libs as required by `native/GpuRenderer` CMake (see build guide/component README).
- Windows x64.

## Build & deployment

See **[BUILD_AND_DEPLOY.md](BUILD_AND_DEPLOY.md)** for step-by-step instructions to build managed and native components and deploy them to a Schedule I install/package layout.

## Configuration & data (runtime, not in repo)

- Mod config and diagnostics (when enabled) are written under the game's MelonLoader `UserData` area (e.g. `UserData/UnderNetBrowser/`), including WebView2 user-data/profile folders and perf diagnostics. These are local/runtime artifacts and are excluded from source control.
- Local game paths, deployment targets, and machine-specific secrets must not be committed.

## License / third-party notices

- Bundled **uBlock Origin Lite** extension assets under `BrowserExtensions/uBlockOriginLite/` include their own license/readme files; comply with their terms.
- WebView2 redistributables and other third-party components are subject to their respective licenses/terms.
