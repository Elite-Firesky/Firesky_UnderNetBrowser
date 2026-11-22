# Changelog

## v1.0.3 - 2026-11-20

- Reworked the homepage desktop layout to separate **Apps** (installed/mod apps) from **Shortcuts** (web/local launch shortcuts) for clearer organization.
- Added custom website shortcuts on the homepage: `+` to add, with edit/remove controls for user-created shortcuts only (pre-built shortcuts remain protected).
- Custom shortcuts are persisted in the browser profile (`localStorage`) and support `http/https` URLs (scheme auto-normalized when omitted).
- Added player/save **Net Worth** display on the homepage (System Status), synced from the game via S1API (`S1API.Money.Money.GetNetWorth()`) with periodic updates similar to in-game time sync.
- Set default WebView zoom to **90%** for pages without a saved override and remember per-site/page zoom levels when changed by the user (applied automatically on navigation).
- Added configurable browser texture sampling: `TextureFilterMode` (`Point`/`Bilinear`/`Trilinear`, default `Bilinear`) and `TextureAnisoLevel` (`0-16`, default `16`), applied to composition/GPU capture textures, exposed in in-app settings, and applied live on setting changes.
- Added configurable post-process `SharpenStrength` (`0..1`, default `1`) for the composition browser texture (native D3D11 4-tap unsharp mask in linear space), with live apply and in-app settings UI.
- Bumped small font sizes in managed homepage HTML pages to improve readability on in-game TV texture/bloom.
- Fixed Mod Manager & Phone App settings detection by aligning the MelonPreferences category ID to `UnderNetBrowser` (MelonInfo name) and updating the Mod Manager ordering patch lookup; removed legacy `FireskyUnderNetBrowser` category migration.
- Updated Mod Manager ordering patch list to include missing config keys (`TextureFilterMode`, `TextureAnisoLevel`, `ZoomLevelsByHostJson`, `SharpenStrength`).
- Fixed in-app settings `select` controls not opening in WebView2 CompositionController by replacing native `<select>` popups with segmented DOM controls.
- Fixed settings Save/Apply form payload to read segmented select values correctly (prevents `TextureFilterMode` from resetting on Save).
- Fixed number-input selection replace in composition mode (native `CompSendText` fallback uses `execCommand('insertText')` when `selectionStart/selectionEnd` are unavailable, e.g. `<input type="number">`).
- Added hold-repeat for Backspace/Delete and improved left/right arrow caret-movement fallbacks in editable fields; fixed number-input backspace/delete/arrows via temporary `type="text"` selection workaround.
- Reduced composition input double-actions by preferring script fallbacks over extra WM key taps for repeat/edit keys where both could apply.
- Fixed up/down arrow video volume control outside fullscreen by adding direct `video.volume` fallback in composition arrow script handling.
- Bumped managed homepage content to **v19** so the updated layout/features deploy to existing installs.

## v1.0.2 - 2026-11-20

- Open WebView2 popup/new-window requests in-place instead of dropping them (existing navigation/policy checks still apply).
- Add WebView2 `ProcessFailed` diagnostics and recovery hooks: fatal-state signaling on browser-process exit, best-effort reload on renderer/frame-process exits (unresponsive events logged without forced reload).
- Disable default WebView2 context menus for the embedded in-game browser to avoid overlay/focus issues.
- Remove duplicate `AddScriptToExecuteOnDocumentCreated` registration for the cosmetic/ad-mitigation script.
- Harden native custom route resolution for in-page `home:`/`undernet:home` and app shortcut navigations (tries both expected content roots for robustness).
- Fix homepage installed app shortcuts (e.g. example app) disappearing on revisit/reload by reinjecting app data when the homepage is (re)entered.
- Fix TV UI repair logic to operate only on the app-owned UI root (no ancestor/game transform mutations), avoid false "badScale" repairs when the hierarchy is only hidden by upstream game/S1API scaling, and skip repair while detached to gameplay.
