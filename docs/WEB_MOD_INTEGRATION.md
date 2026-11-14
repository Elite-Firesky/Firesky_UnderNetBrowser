# UnderNet Local App UI Host (Simple Integration)

> **Status:** Initial foundation implemented (local homepage/apps routing + folder convention + built-in example). This is intentionally simple and designed as a plug-and-play UI host for UnderNet and other mods.

## What this enables

UnderNet can act as a **UI renderer/framework** inside Schedule I:
- Other MelonLoader/S1API mods handle game logic/state.
- UnderNet hosts the interface as local HTML/CSS/JS (easy to build and iterate compared to many Unity UI workflows).

Think of it like an in-game "computer/browser" where apps/pages are just folders with an `index.html`.

## Folder structure (plug-and-play)

Content root:
```text
Schedule I/UserData/UnderNetBrowser/root/
  Homepage/
    index.html              # Built-in/home page (customizable)
  Apps/
    <app-id>/
      index.html            # App entrypoint (required)
      assets/               # optional (css/js/images/etc.)
```

- `<app-id>` should be simple and URL-safe: letters, numbers, `_`, `-` (recommended lowercase).
- Keep app files inside its folder.
- `root/Homepage/index.html` and `root/Apps/example/index.html` are auto-created if missing.

## Routes

- `undernet:home` (aliases: `about:home`, `home:home`, `home:homepage`) → `root/Homepage/index.html`
- `home:<app-id>` → `root/Apps/<app-id>/index.html` (if it exists)
- `undernet:<app-id>` and `undernet:app/<app-id>` are also accepted as aliases for the same app route

Examples:
- `home:example`
- `home:my-cool-ui`

If an app route is requested but the app folder/index is missing, UnderNet falls back to the homepage.

## How other mods can integrate (simple)

### Option A — Drop-in content (no UnderNet API required)
1. Create/copy files to `UserData/UnderNetBrowser/root/Apps/<app-id>/index.html`
2. Ask the user (or your mod/docs) to open `home:<app-id>` in UnderNet
3. Your MelonLoader/S1API mod handles game logic; the HTML page is the UI/view

This is enough for dashboards, launchers, tools, info panels, and many custom interfaces.

### Option B — Launch from your mod (recommended when a small API is added later)
A future small C# helper/API can let another mod open an app route directly (e.g. open `home:<app-id>` from a keybind/item/action).  
Current foundation already supports the routing/resolution side; launching is just navigation.

## Homepage behavior

- Default homepage is deployed to `root/Homepage/index.html` if missing.
- Legacy `UserData/UnderNetBrowser/Home/home.html` is migrated/copied to `root/Homepage/index.html` once if the new homepage does not exist yet (to preserve user edits where possible).
- You can customize `root/Homepage/index.html` to act as a launcher/desktop for your apps.

## Built-in example

UnderNet auto-creates a sample app:
- `root/Apps/example/index.html`
- Open via: `home:example`

Use it as a starting template for your own UI.

## Current scope and notes

- **Simple by design:** folder + `index.html` + route. No manifest required, no JS↔game bridge required.
- **Local files:** pages run from local `file:///` paths inside WebView2.
- **Safety:** app IDs/paths are validated and resolved under the local apps root (no path traversal).
- **Future extensions (optional):** app discovery/launcher cards, minimal C# open/register helpers for other mods, and an opt-in capability-based JS↔host bridge for real-time game integration.

## Minimal example app

`UserData/UnderNetBrowser/root/Apps/hello/index.html`
```html
<!doctype html>
<html>
  <head><meta charset="utf-8"><title>Hello</title></head>
  <body style="font-family:sans-serif;background:#0b1110;color:#e9f7ee;padding:24px">
    <h1>Hello from UnderNet</h1>
    <p>Simple local app UI.</p>
    <p><a href="undernet:home" style="color:#7cff6b">Back to Home</a></p>
  </body>
</html>
```

Open: `home:hello`