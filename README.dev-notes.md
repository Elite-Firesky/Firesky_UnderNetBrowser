# Firesky UnderNetBrowser - workspace notes

## Source of truth (edit these)
- `UnderNetBrowserApp.cs`, other `*.cs`, and project config (`Firesky_UnderNetBrowser.csproj`): runtime/mod logic.
- `Homepage\*.html`: bundled homepage source files shipped with the mod.
  - `index.html`, `games.html`, `settings.html`, `notepad.html` are managed homepage pages (marker: `UnderNetBrowser:managed-homepage version=N`).
  - At runtime, the mod copies/updates these into `UserData/UnderNetBrowser/root/Homepage/` (or game-equivalent UserData path).
  - If bundled files are missing, `UnderNetBrowserApp.cs` has minimal hardcoded fallback HTML; prefer keeping/fixing `Homepage\*.html`.

## Generated / output (do not treat as source)
- `bin\Release\*`: build outputs (DLL/PDB/deps/runtime files). Rebuild regenerates copied content.
- `obj\*`: compiler intermediates (safe to delete).
- `native\*\build\...`: native build artifacts.

## Packaging snapshot (duplicate risk)
- `for_client\...`: appears to be a prepared client/release payload (includes mod files and sample `UserData/UnderNetBrowser/root/...` copies).
  - Useful for distribution/testing, but not the code/source of truth.
  - If you change homepage content or runtime behavior, update `Homepage\*.html` / C# source first, then rebuild/refresh this package intentionally.

## Root archive
- `_archive\_UnderNetBrowserApp_head.cs` (workspace root): moved snapshot of an older monolithic app file kept for reference; not part of the project build.

## Homepage flow (important)
1. Source: `Firesky_UnderNetBrowser\Homepage\*.html`
2. Build copies bundled homepage files to output (`bin\Release\Homepage\...`)
3. Runtime ensures/deploys managed pages into `UserData/UnderNetBrowser/root/Homepage\...`
4. AI route/shortcut should resolve to `https://www.google.com/ai` (not `undernet:ai`) per current implementation.
