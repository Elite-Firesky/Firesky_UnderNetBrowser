# Firesky UnderNet Browser

## Description

Started as an idea to stream a W2G room, ended up as a real in-game browser — **GPU rendered**.

**Firesky UnderNet Browser** adds an **UnderNet** TV app for **Schedule I** powered by **Microsoft Edge WebView2** (composition mode). Pages are rendered off-screen and captured into Unity (D3D11 / Windows Graphics Capture), so you can browse, watch videos, and run browser-based content on the in-game TV without an external browser window.

> **Status:** Near completion / active testing. Performance and compatibility depend on your game version, MelonLoader/S1API, WebView2 runtime, GPU/driver, Windows version, and the content you load. Some features are experimental or best-effort (see **Known Issues & Limitations**).

---

## Main features

### In-game WebView2 browser
- Native **WebView2 CompositionController** rendering path (no visible external browser window), presented in the Schedule I TV UI.
- Address bar / URL navigation with **Home**, **Reload**, and browser controls.
- **Close behavior:**
  - **Normal close:** parks/reuses the WebView2 composition session for faster reopens (warm session).
  - **X close (app close button):** performs a full shutdown/cleanup of the browser/WebView2 runtime to help prevent lingering `msedgewebview2.exe` processes (including after heavy browser games).
- Cleanup on world/menu unload and game quit/deinitialize.

### Built-in UnderNet homepage & local pages
- Default home: `undernet:home` (configurable `HomeUrl`).
- Homepage uses in-game time/date, quick shortcuts (with favicons for external website shortcuts), and managed pages/assets deployed/updated from bundled homepage files.
- Built-in routes/pages include `undernet:home`, `undernet:games`, `undernet:settings`, `undernet:notepad` (and compatible `home:*` aliases).
- **In-browser settings page:** view/apply/persist MelonPreferences settings live from the built-in settings UI (some changes still require browser/app reopen, hard reset/Home, or restart depending on the setting).

### Local/mod apps (HTML-based)
- Supports local apps from `UserData\UnderNetBrowser\root\Apps\<app-id>\index.html`.
- Open apps via `home:<app-id>` (or `undernet:<app-id>` / `undernet:app/<app-id>` routes).
- Installed/mod apps are discovered and shown on the homepage; modders can provide simple HTML apps with optional `app.json` or `manifest.json`.

### Game Input Mode, fullscreen, and controls
- **Game Input Mode (F6, configurable):** forwards gameplay-style key input to the web page (WASD, arrows, Space, Shift, Ctrl, Alt, Enter, Tab, number keys, function keys, and common keys) so held keys work as expected.
  - URL-bar typing remains separate from page/game input; normal Schedule I controls are suppressed while the browser owns input.
  - Mouse movement/clicks/wheel and focus behavior are preserved.
  - **ESC** is forwarded to the page/game while in Game Input Mode; normal ESC behavior outside Game Input Mode is preserved.
  - Held keys are released on focus loss, detach, navigation, or shutdown.
  - **Best-effort page FPS overlay:** while in Game Input Mode, a small top-left **`FPS (page)`** indicator is injected into the page (measured via `requestAnimationFrame`). This is **not** the game’s FPS and is **not** a guaranteed engine/capture/WebGPU-accurate FPS.
- **App fullscreen (F11, fixed):** toggles browser/app fullscreen (hides chrome and expands content to the available area) and restores on exit. Recreates/sizes the composition for the active viewport; listed in settings UI but not configurable.
- **Tab key:** cycles focusable page elements.
- **F3 (configurable):** toggle cursor visibility/lock (global).
- **F5 (configurable):** reload page while the browser/TV app is active.

### Discord shortcut (best-effort)
- `undernet:discord` uses a stable mirror/shortcut flow: attempts to mirror a running Discord window when available, otherwise falls back to Discord Web.
- Back/exit behavior is handled to avoid black/`about:blank` states where possible.
- Discord window mirroring is primarily visual and best-effort; mirror interactivity (clicks/typing/chat forwarding) may be limited or unreliable.

### Audio, performance, and rendering options
- **Distance-based audio volume (best-effort):** attenuates browser/WebView2 audio based on player-to-TV distance (process-level attenuation). Configurable full/silent distances; beyond the silent range, detached remote arrow controls are also disabled.
- **Adaptive rendering (visual-only):** distance/visibility-based quality tiers can reduce visual workload (capture throttling, optional visual standby showing the last frame, and experimental resolution scaling) while keeping the WebView2 session/page state/audio running. Designed to avoid browser restarts/session churn.
- **Render resolution (quality target/cap):** configurable internal composition render **Width × Height** (default 1920×1080), fitted to the current viewport aspect/chrome/fullscreen state.
  - Can be set above the game/native display resolution (e.g. 3840×2160) as a quality target/cap.
  - **Requires closing/reopening the browser app** (or a restart/recreate path) to apply; warm reuse does not resize an existing composition.
  - Higher resolutions increase GPU/capture/composition cost and may not overcome blur introduced by the in-game TV/presentation path.
- **WebView2 testing flags (advanced):** optional Chromium/WebView2 arguments (e.g. background timer/rendering behavior, autoplay policy, frame-rate limit, custom args) applied on environment creation (cold start / hard reset / restart).

### Extensions and privacy-related options
- **uBlock Origin Lite (bundled):** default extension assets are included and auto-deployed to the active WebView2 composition profile when needed.
- **Basic ad/tracker blocking:** configurable built-in request blocking.
- **Auto-accept consent dialogs:** optional best-effort behavior (may cause unintended clicks on some sites; off by default).

## Installation instructions

1. Install **MelonLoader 0.7.x (net6)** for Schedule I and confirm the game launches with it.
2. Install **S1API** (and any dependencies it requires) into your Schedule I mod setup.
3. Install the **Microsoft Edge WebView2 Runtime (Evergreen, x64)** if it is not already installed.
4. Download and extract the mod package.
5. Copy/merge the package contents into your **Schedule I** game directory so the files end up in these locations:
   - `Mods\Firesky_UnderNetBrowser.dll`
   - `Mods\UnderNetBrowser\Microsoft.Web.WebView2.Core.dll`
   - `Mods\UnderNetBrowser\WebView2Loader.dll`
   - `Schedule I_Data\Plugins\x86_64\UnderNetGpuRenderer.dll`
   - `UserData\UnderNetBrowser\WebView2Composition\Extensions\uBlockOriginLite\` *(default extension, pre-seeded/recommended)*
6. Launch the game and open the **UnderNet** app from the in-game TV.

**Notes:**
- After updating `UnderNetGpuRenderer.dll` (native plugin), **restart the game**.
- After updating/replacing mod files, a restart is recommended to ensure clean WebView2/native plugin loads.

## Requirements

### Game / mod loader
- **Schedule I** (IL2CPP, Unity 2022.3.x)
- **MelonLoader 0.7.x (net6)**
- **S1API** (required for the TV app integration)

### System / runtime
- **Windows x64** with **D3D11** and **Windows Graphics Capture (WGC)** support
- **Microsoft Edge WebView2 Runtime (Evergreen, x64)** installed
  - Download: https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Standalone Installer, x64 recommended)

> WebView2 Evergreen Runtime is required on the player's machine. The mod uses the system WebView2 runtime and stores browser profile/user data locally under MelonLoader `UserData`.

## Usage & Controls

- **Open:** Launch **UnderNet** from the in-game TV.
- **Navigate:** Use the address bar, Home/Reload controls, on-page links/shortcuts, and supported `undernet:*` / `home:*` routes.
- **Close:**
  - **Normal close:** warm session (faster reopen).
  - **X button:** full shutdown/cleanup (recommended if you want to ensure WebView2/browser processes are fully stopped).

### Keybinds / shortcuts

| Action | Default | Configurable | Notes |
|---|---:|:---:|---|
| Toggle cursor visibility/lock | **F3** | Yes | Global toggle. |
| Reload page | **F5** | Yes | While browser/TV app is active. |
| Toggle Game Input Mode | **F6** | Yes | Forwards gameplay-style key input to the page; shows best-effort page/rAF FPS overlay while enabled. |
| Toggle app fullscreen | **F11** | No | Hides chrome and expands browser content; restores on exit. |
| Cycle focusable page elements | **Tab** | No | Standard page focus navigation. |

Keybinds are stored as Unity `KeyCode` names in MelonPreferences and can be changed in config/Mod Manager (where supported).

## Configuration

Configuration is stored in MelonPreferences under the `FireskyUnderNetBrowser` category (displayed as **Firesky UnderNet Browser**) and is also exposed in the built-in in-app settings page (`undernet:settings`) for live apply/persist in many cases.

Common options include:
- **Home URL:** `HomeUrl` (default `undernet:home`).
- **Blocking/consent:** `BasicAdBlockEnabled`, `AutoConsentEnabled`.
- **Distance audio:** `DistanceVolumeEnabled`, `DistanceVolumeFullDistance`, `DistanceVolumeSilentDistance`.
- **Keybinds:** `KeyToggleCursor`, `KeyReload`, `KeyGameInputMode` (F11 app fullscreen is fixed).
- **Render resolution:** `GpuCompositionRenderWidth` × `GpuCompositionRenderHeight` (quality target/cap, fitted to viewport aspect; can exceed game/native display resolution; **restart/reopen required to apply**).
- **Adaptive rendering:** enable/tiers/distances/hysteresis/delay, capture FPS targets, visual standby, and experimental resolution scaling.
- **Diagnostics/logging:** `DebugLogging`, `AlwaysLogDiagnostics`, `PerformanceDiagnosticsEnabled`, `DiagnosticsSampleIntervalSeconds`, `DiagnosticsOutputDirectory`.
- **Advanced WebView2 testing args:** optional switches and custom browser arguments (applied on WebView2 environment creation: cold start / hard reset / restart).

Runtime data (WebView2 profile/cache, managed homepage/apps content, diagnostics output when enabled) is stored under:
- `UserData\UnderNetBrowser\`

## Known Issues & Limitations

- **Cold start can be slow:** first open (or reopen after a full X-close shutdown) may take several seconds while WebView2 creates the composition controller/profile and initializes extensions/runtime. Reopening after a normal close is much faster (warm session reuse).
- **Render quality / blur:** even at higher render resolutions (including > native/4K), perceived sharpness can be limited by the in-game TV/app presentation path (surface/material/scaling/filtering outside the WebView2 source texture). Blurriness affecting both web content and Unity browser chrome may indicate this presentation bottleneck. Texture sampling/filtering behavior may be adjusted during testing.
- **Game Input Mode FPS overlay is best-effort:** the in-game overlay shows **page/rAF FPS**, not the game’s FPS and not a guaranteed WebGPU engine FPS or capture FPS. Some pages/content may report differently or interfere with overlays.
- **RMB detach / TV reattach robustness:** detach/reattach behavior has been improved (including recovery for cases where TV state becomes temporarily unreadable), but because TV/app state can vary by save, system, and game behavior, please report any remaining cases where you get stuck or lose interaction (include `MelonLoader\Latest.log`).
- **Discord mirror limitations:** Discord window mirroring is best-effort and primarily visual; interactivity (clicks/typing/chat forwarding) may be unreliable. Discord Web fallback is used when Discord is not running.
- **Site/app compatibility and performance:** complex websites, video sites, WebGL/WebGPU pages, and browser games depend on WebView2/Chromium behavior, site scripts, and hardware. Input/render/performance can vary by content.
- **Known reported issues under investigation:**
  - Up/Down arrow navigation for search suggestions (e.g. Google/YouTube) may not behave as expected in all cases.
  - Intermittent CLR fatal world-load crash reported as `0x80131506` in `S1API.SaveableAutoRegistry.DiscoverSaveableTypes` / `Assembly.GetTypes()` (may be external to this mod; include logs/details when reporting).
- **Restart required for native updates:** replacing `UnderNetGpuRenderer.dll` requires a game restart.

## Troubleshooting

- **App does not appear or fails to load:** verify MelonLoader 0.7.x (net6), S1API, and all required DLLs are installed in the correct folders.
- **Browser shows no content / WebView2 errors:** ensure the **Microsoft Edge WebView2 Runtime (Evergreen, x64)** is installed.
- **Slow first load:** often normal on a cold start; try a normal close and reopen to benefit from warm session reuse.
- **Lingering `msedgewebview2.exe` after use:** close the app using the **X** button to perform a full shutdown/cleanup.
- **After updating:** restart the game, especially if `UnderNetGpuRenderer.dll` changed.
- **Logs:** check `MelonLoader\Latest.log` for `[Firesky UnderNet Browser]` entries (and enable diagnostics options only when needed).

## Shout outs

Huge thanks to everyone and everything that made this possible:

- **TVGS / Schedule I** for the game and sandbox that inspired this mod.
- **MelonLoader** and the IL2CPP/Harmony modding ecosystem for the mod loader foundation.
- **S1API** and its contributors for the TV app integration layer used by this mod.
- **Microsoft Edge WebView2** team/project for the embedded browser runtime that powers rendering and web compatibility.
- **uBlock Origin Lite** developers and contributors for the bundled extension assets (see third-party notices below).
- The **Schedule I modding community** for testing, feedback, ideas, and issue reports.

### Third-party notices
- **uBlock Origin Lite** — bundled extension assets in `BrowserExtensions/uBlockOriginLite/` include their own license/readme files; use and redistribution must comply with their terms.
- **Microsoft Edge WebView2** — runtime/redistributable components are subject to Microsoft's licenses/terms.

### Permissions
Please review and respect the licenses of included third-party components (especially bundled browser extension assets). For redistribution, mod packs, or derivative releases, include appropriate attribution and comply with all applicable third-party license terms. If you are unsure, ask the mod author before reuploading.