# UnderNet WebView2 CompositionController PoC (feasibility scaffold)

**Status:** prototype scaffold, **not verified/built/run in this environment**. Intended to validate the *supported* CompositionController + DirectComposition + WGC(`CreateFromVisual`) path for delivering continuously updating browser pixels into a D3D11 texture **without CPU readback/JPEG/managed per-frame uploads** and **without a visible ordinary browser window** (subject to validation).

Preserves existing architecture: this is standalone and does **not** modify the working visible-host WGC fallback, Unity-owned texture path, native renderer, JPEG/CDP fallback, or navigation/UI.

## Intended resource/API flow (to prove)
1. `CreateDispatcherQueueController` (STA thread) for WinRT composition APIs.
2. `CreateCoreWebView2Environment` -> `CreateCoreWebView2CompositionController(parentHwnd, ...)` (parent HWND can be hidden/message-only; *capturability/update behavior must be validated*).
3. Build DComp/WinRT composition: `Compositor` -> container/root `Visual` -> child visual; set `CoreWebView2CompositionController.RootVisualTarget` to child visual; `Bounds`/size set; commit.
4. Capture composed output to D3D11: `Windows.Graphics.Capture.GraphicsCaptureItem::CreateFromVisual(rootVisual)` -> `Direct3D11CaptureFramePool` -> `GraphicsCaptureSession.StartCapture()`. Frames provide `ID3D11Texture2D` via `IDirect3DDxgiInterfaceAccess` (GPU).
5. Copy/blit frame texture into destination D3D11 texture (standalone: swapchain backbuffer; mod integration: existing Unity-owned D3D11 texture via render-thread path). No CPU readback.
6. Input via `SendMouseInput` / `SendPointerInput` (+ keyboard/focus handling per WebView2 composition guidance), with coordinate mapping to WebView bounds.

## Build notes
- Windows 10/11, VS 2022 Build Tools, Windows SDK, Evergreen WebView2 runtime.
- CMake configure/build (example): `cmake -S . -B build -G "Visual Studio 17 2022" -A x64 -DWEBVIEW2_NUGET_ROOT="C:/Users/Administrator/.nuget/packages/microsoft.web.webview2/1.0.2903.40/build/native"` then build Release.
- Requires WebView2 native headers/loader lib from the SDK/NuGet package.

## What must be proven in-game/system test
- Continuously updating frames (animated page/video) arrive via `CreateFromVisual` while host HWND is not a visible ordinary browser window.
- Pixels reach D3D11 texture and can drive Unity-compatible rendering path without CPU readback.
- Input/focus behavior acceptable for TV interaction model.

Do not claim success until continuously updating browser pixels are visibly rendered in the destination texture.
