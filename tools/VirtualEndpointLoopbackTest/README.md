# Virtual Endpoint Loopback Test (WASAPI)

Standalone native C++ harness for PoC: capture rendered audio from a dedicated virtual render endpoint via WASAPI loopback.
Outside existing browser rendering path. Does not modify mod binaries.

## Build (Release x64)
Requires Visual Studio 2022 Build Tools / MSVC (Windows SDK).

```powershell
# from repo root or this folder
$cl = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC" # adjust if needed
# easiest: use Developer Command Prompt env, or run:
cmd /c ""C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" && cl /nologo /std:c++17 /EHsc /O2 /DUNICODE /D_UNICODE src\VirtualEndpointLoopbackTest.cpp /Fe:build\VirtualEndpointLoopbackTest.exe ole32.lib oleaut32.lib avrt.lib mmdevapi.lib uuid.lib"
```

Or use the included `build.cmd`.

## Usage
- List endpoints: `VirtualEndpointLoopbackTest.exe --list`
- Capture (by name substring, default "CABLE Input"): `VirtualEndpointLoopbackTest.exe --capture --device "CABLE Input" --seconds 30`
- Writes status/PCM stats to console every second. Nonzero PCM/peak indicates audio activity.

## Notes
- Captures loopback from the specified **render** endpoint (what is rendered to it). It does not capture system-wide default output unless you explicitly target it.
- Isolation depends on routing only WebView2 audio to that endpoint and no other apps/game audio. The harness reports capture activity for that endpoint only.
