# Firesky UnderNet Browser

Started as an idea to stream a W2G room, ended up in a real browser, GPU rendered.

**Firesky UnderNet Browser** adds an **UnderNet** TV app powered by **Microsoft Edge WebView2** (composition mode). Pages are rendered off-screen and captured into Unity (D3D11/Windows Graphics Capture), so you can browse, watch videos, and run browser-based content directly on the TV without an external browser window.

> **Status:** Active development. Performance and compatibility depend on your game version, mod loader/dependencies, WebView2 runtime, GPU/driver, and Windows version.
Some features are experimental or best-effort (see Known Issues).

---

## Features

- **In-game WebView2 TV browser**
  - Native WebView2 CompositionController rendering path (no visible external browser window).
  - Captured and presented in the Schedule I TV UI.
- **Built-in UnderNet home page**
  - Default home: `undernet:home` with quick shortcuts for common sites.
  - Configurable home URL.
- **Navigation controls**
  - Address bar / URL navigation, **Home**, and **Reload** controls in the TV app.
- **Game Input Mode (F6, configurable)**
  - Toggleable mode for browser games and interactive pages.
  - Forwards real key down/up input (WASD, arrow keys, Space, Shift, Ctrl, Alt, Enter, Tab, number keys, function keys, and common keys) so held keys work as expected.
  - URL-bar typing remains separate from page/game input.
  - Suppresses normal Schedule I controls while the browser owns input.
  - Mouse movement/clicks/wheel and focus behavior are preserved.
  - Held keys are released on focus loss, detach, navigation, or shutdown.
  - **ESC** is forwarded to the page/game while in Game Input Mode; normal ESC behavior outside Game Input Mode is preserved.
- **uBlock Origin Lite (bundled)**
  - Default extension assets included and auto-deployed to the active WebView2 composition profile when needed.
  - Basic built-in request blocking and optional consent handling are configurable.
- **Discord shortcut (Placeholder-prototype)**
  - If Discord is running, attempts to mirror the Discord window in the browser view (visual placeholder).
  - If Discord is not running, opens Discord Web as a fallback.
  - Mirror interactivity (clicks/typing/chat forwarding) is currently non-functional.
- **Session and lifecycle handling**
  - **Normal close:** parks/reuses the WebView2 composition session for faster reopens.
  - **X close (app close button):** performs a full shutdown and cleanup of the browser/WebView2 runtime (helps prevent lingering `msedgewebview2.exe` processes, including after heavy browser games).
  - Cleanup on world/menu unload and game quit/deinitialize.
- **Configurable settings (MelonPreferences / Mod Manager compatible)**
  - Enable/disable mod, debug/diagnostic logging.
  - Home URL, render resolution (composition width/height).
  - Keybinds: global cursor toggle, reload, Game Input Mode.
  - Ad-block/consent options and optional performance diagnostics output.
  - Spatial audio PoC toggle is present but **off by default** and currently non-functional/experimental (see Known Issues).

## Requirements

### Game / mod loader
- **Schedule I** (IL2CPP, Unity 2022.3.x)
- **MelonLoader 0.7.x (net6)**
- **S1API** (required for the TV app integration)

### System / runtime
- **Windows x64** with **D3D11** and **Windows Graphics Capture (WGC)** support
- **Microsoft Edge WebView2 Runtime (Evergreen, x64)** installed
  - Download: https://developer.microsoft.com/microsoft-edge/webview2/ > Download > Evergreen Standalone Installer > x64 
(Most Windows systems already have this, specially if you play Xbox/Microsoft game titles, it's mostly a requirement)

> Notes: WebView2 Evergreen runtime is required on the player's machine. The mod uses the system WebView2 runtime and stores browser profile/user data locally under MelonLoader `UserData`.

## Installation

1. Install **MelonLoader 0.7.x (net6)** for Schedule I and confirm the game launches with it.
2. Install **S1API** (and any dependencies it requires) into your Schedule I mod setup.
3. Install the **Microsoft Edge WebView2 Runtime (Evergreen, x64)** if it is not already installed.
4. Download and extract the mod package.
5. Copy/merge the package contents into your **Schedule I** game directory so the files end up in these locations:
   - `Mods\Firesky_UnderNetBrowser.dll`
   - `Mods\UnderNetBrowser\Microsoft.Web.WebView2.Core.dll`
   - `Mods\UnderNetBrowser\WebView2Loader.dll`
   - `Schedule I_Data\Plugins\x86_64\UnderNetGpuRenderer.dll`
   - `UserData\FireskyUnderNetBrowser\WebView2Composition\Extensions\uBlockOriginLite\` *(default extension, pre-seeded)*
6. Launch the game and open the **UnderNet** app from the in-game TV.

## Usage & Controls

- **Open:** Launch **UnderNet** from the in-game TV home/apps.
- **Navigate:** Use the address bar, home button, reload button, and on-page links/shortcuts.
- **Close:**
  - Use the app's normal close flow for a **warm** session (faster reopen).
  - Use the app's **X** button for a **full shutdown** (cleans up WebView2/browser processes).

### Default keybinds (configurable)

| Action | Default | Notes |
|---|---:|---|
| Toggle cursor visibility/lock | **F3** | Global toggle (works outside the TV/browser). |
| Reload page | **F5** | While browser/TV app is active. |
| Toggle Game Input Mode | **F6** | Forwards gameplay-style key input to the browser page. |

Keybinds are stored as Unity `KeyCode` names in mod preferences and can be changed in config/Mod Manager settings.

## Configuration

Configuration is stored in MelonPreferences under the `FireskyUnderNetBrowser` category (editable via MelonLoader prefs/Mod Manager, depending on your setup).

Common options:
- **Enabled**: Enable/disable the UnderNet app.
- **HomeUrl**: Default URL on open (`undernet:home` for the built-in home page).
- **BasicAdBlockEnabled**: Basic built-in ad/tracker request blocking.
- **AutoConsentEnabled**: Best-effort auto-accept/dismiss for some consent dialogs (may cause unwanted clicks on some sites).
- **GpuCompositionRenderWidth / GpuCompositionRenderHeight**: Internal browser render resolution (restart/reopen may be required).
- **KeyToggleCursor / KeyReload / KeyGameInputMode**: Configurable keybinds.
- **DebugLogging / AlwaysLogDiagnostics**: Logging verbosity.
- **PerformanceDiagnosticsEnabled / DiagnosticsSampleIntervalSeconds / DiagnosticsOutputDirectory**: Optional low-overhead diagnostics.

Runtime data (WebView2 profile/cache, home page, diagnostics output when enabled) is stored under:
- `UserData\FireskyUnderNetBrowser\`

## Known Issues & Limitations

- **Cold start can be slow:** First open (or reopen after a full X-close shutdown) may take several seconds while WebView2 creates the composition controller/profile and initializes extensions/runtime. Reopening after a **normal** close is much faster due to warm session reuse.
- **Discord mirror is a placeholder:** Mirroring a running Discord window is best-effort and primarily visual; click/typing/chat interactivity is not reliable. Discord Web fallback is used when Discord is not running.
- **Spatial audio:** Spatial audio proof-of-concept is **disabled/off by default** and currently not an active supported feature due to runtime/platform constraints.
- **Site/app compatibility and performance:** Complex websites, video sites, WebGL pages, and browser games depend heavily on WebView2/Chromium behavior, site scripts, and your hardware. Input/render performance may vary by content.
- **Restart required for native updates:** Replacing `UnderNetGpuRenderer.dll` requires a game restart.
- **Experimental mod:** Expect occasional bugs or edge cases; check MelonLoader logs when reporting issues.

## Troubleshooting

- **App does not appear or fails to load:** Verify MelonLoader 0.7.x (net6), S1API, and all required DLLs are installed in the correct folders.
- **Browser shows no content / WebView2 errors:** Ensure the **Microsoft Edge WebView2 Runtime (Evergreen, x64)** is installed.
- **Slow first load:** This is often normal on a cold start (WebView2/controller/extension initialization). Try a normal close and reopen to benefit from warm reuse.
- **Lingering `msedgewebview2.exe` after use:** Close the app using the **X** button to perform a full shutdown/cleanup.
- **After updating:** Restart the game, especially if `UnderNetGpuRenderer.dll` changed.
- **Logs:** Check `MelonLoader\Latest.log` for `[Firesky UnderNet Browser]` entries.

## Credits & Third-Party Notices

- **uBlock Origin Lite** — bundled extension assets in `BrowserExtensions/uBlockOriginLite/` include their own license/readme files; use and redistribution must comply with their terms.
- **Microsoft Edge WebView2** — runtime/redistributable components are subject to Microsoft’s licenses/terms.
- Built for the **Schedule I** modding ecosystem using **MelonLoader** and **S1API** (and related IL2CPP/Harmony interop tooling provided by the mod loader environment).

## Permissions

Please review and respect the licenses of included third-party components (especially bundled browser extension assets). For redistribution, mod packs, or derivative releases, include appropriate attribution and comply with all applicable third-party license terms. If you are unsure, ask the mod author before reuploading.