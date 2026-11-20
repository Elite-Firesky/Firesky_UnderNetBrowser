# Changelog

## v1.0.2 - 2026-11-20

- Open WebView2 popup/new-window requests in-place instead of dropping them (existing navigation/policy checks still apply).
- Add WebView2 `ProcessFailed` diagnostics and recovery hooks: fatal-state signaling on browser-process exit, best-effort reload on renderer/frame-process exits (unresponsive events logged without forced reload).
- Disable default WebView2 context menus for the embedded in-game browser to avoid overlay/focus issues.
- Remove duplicate `AddScriptToExecuteOnDocumentCreated` registration for the cosmetic/ad-mitigation script.
- Harden native custom route resolution for in-page `home:`/`undernet:home` and app shortcut navigations (tries both expected content roots for robustness).
- Fix homepage installed app shortcuts (e.g. example app) disappearing on revisit/reload by reinjecting app data when the homepage is (re)entered.
- Fix TV UI repair logic to operate only on the app-owned UI root (no ancestor/game transform mutations), avoid false "badScale" repairs when the hierarchy is only hidden by upstream game/S1API scaling, and skip repair while detached to gameplay.
