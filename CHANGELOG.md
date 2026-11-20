# Changelog

## v1.0.3 - 2026-11-20

- Reworked the homepage desktop layout to separate **Apps** (installed/mod apps) from **Shortcuts** (web/local launch shortcuts) for clearer organization.
- Added custom website shortcuts on the homepage: `+` to add, with edit/remove controls for user-created shortcuts only (pre-built shortcuts remain protected).
- Custom shortcuts are persisted in the browser profile (`localStorage`) and support `http/https` URLs (scheme auto-normalized when omitted).
- Added player/save **Net Worth** display on the homepage (System Status), synced from the game via S1API (`S1API.Money.Money.GetNetWorth()`) with periodic updates similar to in-game time sync.
- Set default WebView zoom to **90%** for pages without a saved override and remember per-site/page zoom levels when changed by the user (applied automatically on navigation).
- Bumped managed homepage content to **v13** so the updated layout/features deploy to existing installs.

## v1.0.2 - 2026-11-20

- Open WebView2 popup/new-window requests in-place instead of dropping them (existing navigation/policy checks still apply).
- Add WebView2 `ProcessFailed` diagnostics and recovery hooks: fatal-state signaling on browser-process exit, best-effort reload on renderer/frame-process exits (unresponsive events logged without forced reload).
- Disable default WebView2 context menus for the embedded in-game browser to avoid overlay/focus issues.
- Remove duplicate `AddScriptToExecuteOnDocumentCreated` registration for the cosmetic/ad-mitigation script.
- Harden native custom route resolution for in-page `home:`/`undernet:home` and app shortcut navigations (tries both expected content roots for robustness).
- Fix homepage installed app shortcuts (e.g. example app) disappearing on revisit/reload by reinjecting app data when the homepage is (re)entered.
- Fix TV UI repair logic to operate only on the app-owned UI root (no ancestor/game transform mutations), avoid false "badScale" repairs when the hierarchy is only hidden by upstream game/S1API scaling, and skip repair while detached to gameplay.
