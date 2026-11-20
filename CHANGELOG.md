# Changelog

## v1.0.2 - 2026-11-20

- Open WebView2 popup/new-window requests in-place instead of dropping them (existing navigation/policy checks still apply).
- Add WebView2 `ProcessFailed` diagnostics and recovery hooks: fatal-state signaling on browser-process exit, best-effort reload on renderer/frame-process exits (unresponsive events logged without forced reload).
- Disable default WebView2 context menus for the embedded in-game browser to avoid overlay/focus issues.
- Remove duplicate `AddScriptToExecuteOnDocumentCreated` registration for the cosmetic/ad-mitigation script.
