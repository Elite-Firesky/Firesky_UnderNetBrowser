# UnderNetGpuRenderer (native PoC)

Experimental native DLL for **Target A (zero CPU pixel copies)** GPU rendering integration with Schedule I (Unity 2022.3.62f2, IL2CPP, MelonLoader, D3D11).

## Purpose
- **Phase 3 (first milestone):** prove Unity native plugin + D3D11 device integration by creating/updating an **animated D3D11 test texture** on Unity's render thread and exposing its native `ID3D11Texture2D*` for `Texture2D.CreateExternalTexture` on the in-game TV.
- **Phase 4 (next):** extend to WebView2 CompositionController + DirectComposition + Windows Graphics Capture → GPU copy into Unity texture (no JPEG/CPU readback/managed uploads).

## Current status
- Implements Unity plugin entry points (`UnityPluginLoad/Unload`, `GetRenderEventFunc`), captures Unity D3D11 device via `IUnityGraphicsD3D11`, and supports render-event driven test texture create/update/destroy.
- **Not yet implemented:** WGC capture path, browser/composition integration in native layer, resize/lifecycle hardening for production.

## Build requirements (Windows x64)
- Visual Studio 2022 (Desktop development with C++) or Build Tools
- Windows 10/11 SDK
- CMake 3.20+

No WebView2/WIL deps required for the Phase 3 animated texture test. (Phase 4 will require WebView2 SDK, WIL/WinRT headers similar to WebViewToolkit.)

## Build
```powershell
# from Firesky_UnderNetBrowser\native\GpuRenderer
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```
Output: `build\bin\Release\UnderNetGpuRenderer.dll`

## Deployment / integration notes (important)
- **Preferred (Unity plugin path):** copy `UnderNetGpuRenderer.dll` to `Schedule I_Data\Plugins\x86_64\` and **restart the game** so Unity loads it and calls `UnityPluginLoad` with `IUnityGraphicsD3D11` (D3D11 device). This is the most reliable way to obtain the correct Unity device/render integration.
- **Mod runtime loading:** if loaded only via `LoadLibrary` from a MelonLoader mod after startup, Unity may not call `UnityPluginLoad`, so `UnGpu_IsUnityDeviceReady()` can remain false. Phase 2 validation must confirm device acquisition in the actual game.
- Keep the existing JPEG/screencast pipeline available as a diagnostic fallback until GPU path is verified in-game.

## Managed-side usage (concept)
1. P/Invoke `UnGpu_GetRenderEventFuncPtr()` (or load plugin-exported render event func)
2. `GL.IssuePluginEvent(func, UN_GPU_EVENT_CREATE_TEST_TEXTURE)` on render thread
3. Poll/read `UnGpu_GetTestTexturePtr()` + dimensions, call `Texture2D.CreateExternalTexture(w,h,TextureFormat.RGBA32,false,false,ptr)` and assign to TV
4. Per-frame `GL.IssuePluginEvent(func, UN_GPU_EVENT_UPDATE_TEST_TEXTURE)`
5. On close: destroy texture event, release Unity texture reference as appropriate

All results must be validated in-game (device, external texture, update, render, cleanup). No performance/compatibility claims without measurement.