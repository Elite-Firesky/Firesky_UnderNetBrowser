# UnderNetBrowser — Stage 3: Spatial TV Audio (Technical Feasibility Report + Minimal PoC Plan)

**Date:** 2026-11-12 (workspace inspection)  
**Project:** `Firesky.UnderNetBrowser` (S1API `TVApp`, IL2CPP, MelonLoader 0.7.3, net6, Unity 2022.3.62f2, D3D11, WebView2 Evergreen composition mode)  
**Status:** Research/inspection only. **No speculative audio implementation added. No rendering/input/TV lifecycle/adblocking behavior changed.**

> Runtime/in-game validation is **not possible in this environment**. Findings below are based on project/source inspection, installed WebView2 SDK headers (`Microsoft.Web.WebView2 1.0.2903.40`), decompiled game references in `knowledge/_ilspy_ASC/Il2CppScheduleOne.TV/*`, and Windows/WebView2/Unity API behavior as verifiable from documentation-level knowledge. All capture/routing behaviors called out as "must be tested" require in-game/runtime proof before full implementation.

---

## 1) Current audio path (inspected)

### 1.1 WebView2 / native host
- **Composition host:** `native/GpuRenderer/src/CompositionBrowser.cpp` creates a hidden native host window (`CreateWindowExW(..., WS_POPUP/hidden)`), a WinRT compositor/visual target, and a `CoreWebView2CompositionController` (`CreateCoreWebView2CompositionController`) hosted in that visual. Frames are captured via WGC `CreateFromVisual` and copied/blitted to a Unity-owned D3D11 texture (`UnderNetGpuRenderer.dll` exports/events, `UnderNetBrowserCompositionCapture.cs` tick/GL events).
- **No audio capture/routing code exists:** Searched managed/native project for `audio/Audio/WASAPI/loopback/IAudio/IMMDevice/CoreAudio/AudioSource/PCM/ring` — no existing browser-audio capture, buffering, resampling, or Unity playback path found.
- **WebView2 audio output (current):** Not explicitly configured/muted/routed in code. With default behavior, WebView2 media/audio plays through the **system default Windows audio endpoint** (audible globally), originating from the **WebView2 browser process tree** (browser + renderer/GPU/utility processes), not from Unity.
- **Relevant WebView2 APIs available in installed SDK 1.0.2903.40 (verified in `WebView2.h`):** `ICoreWebView2_8::get_IsDocumentPlayingAudio`, `add_IsDocumentPlayingAudioChanged`, `get_IsMuted/put_IsMuted`, `add_IsMutedChanged`. These can support lifecycle/diagnostics/mute control but **do not provide PCM/audio buffer capture**.

### 1.2 Managed/Unity side
- `UnderNetBrowserApp.cs` / `UnderNetBrowserCompositionCapture.cs` / `UnderNetBrowserGpuNative.cs` manage composition texture, input, TV UI, detach/reattach, and TV screen mirroring (`_tvScreenMirrorStates`, `TryApplyDetachedTvScreenMirror`) onto TV screen renderers during detach. No `UnityEngine.AudioSource` creation/attachment for browser audio found.
- Media lifecycle today relies on page scripts/navigation/teardown (e.g. pause/clear `video,audio` elements and navigate `about:blank` on close/cleanup paths) plus native composition shutdown; audio itself is not owned by Unity.

### 1.3 Schedule I TV audio-emitter components (game API inspection)
- Decompiled `Il2CppScheduleOne.TV.TVInterface`, `TVApp`, `TVInteractable` (`knowledge/_ilspy_ASC/Il2CppScheduleOne.TV/*`) show UI/canvas/camera/home-screen/player/time-label/interactable fields and open/close/player management — **no obvious `AudioSource`/speaker/emitter fields** in these types.
- `TVInterface`/`TVInteractable`/TV hierarchy `GameObject`s (e.g. `TVInterface`, `TV_Built(Clone)/TVInterface`) are resolvable at runtime by the mod; **runtime hierarchy inspection is required** to determine whether an `AudioSource` (or game audio component such as `AudioSourceController`) exists on the physical TV object/prefab/children and whether it is suitable/reusable for spatial playback.
- Project knowledge also notes game audio helpers exist (`AudioManager`, `AudioSourceController`) — reuse is possible in principle but must be verified against actual runtime components/behavior (IL2CPP-safe access, ownership, occlusion/attenuation expectations).

**Conclusion (current path):** Browser audio is **2D/system-output** (default endpoint), decoupled from Unity world-space and TV position. To achieve spatial TV-origin audio, audio must be **captured from the WebView2 audio source and rendered through a Unity 3D `AudioSource` at the TV** (or an equivalent engine-attached emitter), with careful handling of duplication/muting, sync, and lifecycle.

---

## 2) Capture options — feasibility assessment

### 2.1 WASAPI process-loopback capture (preferred candidate, if viable)
- **What it is:** Windows process-specific loopback capture (WASAPI, `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` / `ActivateAudioInterfaceAsync` with process loopback target) to capture audio rendered by a **target process** (and, depending on configuration/support, its process tree) into PCM buffers on the client.
- **Pros:** Can avoid capturing the entire system mix (unlike endpoint loopback) and is the most direct path to isolate WebView2-origin audio; supports low-latency pull/push-style buffering.
- **Key unknowns / risks (must test):**
  - **OS/runtime support:** Process loopback requires sufficiently recent Windows 10 builds (commonly 2004+/build 19041+ APIs). Must detect capability and fail gracefully on unsupported systems.
  - **WebView2 process tree isolation:** WebView2 audio may be produced by **renderer/browser/GPU/utility child processes** with dynamic PIDs. Capturing only the browser/controller host PID may miss audio. A robust PoC must (a) discover relevant WebView2 PIDs for the active profile/controller, and/or (b) capture the process tree / include target process children if supported, or (c) fall back to another strategy. This is a primary feasibility risk.
  - **Mute vs capture behavior:** **Do not assume** muting the WebView2 session (`ICoreWebView2_8::put_IsMuted`) or muting an audio session will still allow loopback capture. Muting often silences the rendered stream seen by loopback. **Must test actual behavior** to avoid either double-audio (captured + still audible on endpoint) or silent capture.
  - **Avoiding double-audio without losing capture:** If process loopback captures pre-endpoint render for that process only, endpoint may not need to be audible for that stream (ideal) — but this is implementation/OS-dependent and must be proven. If not, alternatives include per-session volume/mute routing tricks (fragile) or accepting endpoint loopback constraints (not acceptable if it captures all game audio).

### 2.2 Windows Audio Session APIs (WASAPI session enumeration/control)
- **What it is:** Enumerate audio sessions (`IAudioSessionManager2` / `IAudioSessionEnumerator` / `IAudioSessionControl2`) to identify sessions belonging to WebView2 processes and observe/control per-session volume/mute.
- **Feasibility:** Useful for **identifying** WebView2 audio-producing processes/sessions and potentially coordinating mute state; **not by itself a PCM capture mechanism** for feeding Unity (endpoint loopback captures the device mix). Best used in combination with process-loopback capture (PID discovery) and lifecycle control.
- **Risks:** Sessions/process mapping can be transient across navigations/process model changes; per-session mute may affect capture (must test).

### 2.3 Endpoint loopback capture (fallback, likely unsuitable as primary)
- Captures the render endpoint mix (all system/game audio). **Not suitable** for routing "browser audio from TV" because it would re-spatialize/duplicate unrelated game/system sounds. Only mentionable as a diagnostic fallback, not a product path.

### 2.4 Native audio plugin integration with Unity
- **Viable integration surface:** Native C++ DLL (existing `UnderNetGpuRenderer` plugin context) can perform WASAPI capture/resampling/ring-buffering and expose PCM to C#; C# can feed Unity via `AudioClip.Create(name, lengthSamples, channels, frequency, stream:true, pcmReaderCallback, pcmSetPositionCallback)` or an `OnAudioFilterRead`/streaming component on the TV `AudioSource`.
- **Constraint:** Must avoid per-frame managed allocations (use preallocated/ring buffers, marshal/copy efficiently, IL2CPP-safe interop), and keep threading safe (audio callback thread vs capture thread vs Unity main thread).

### 2.5 Isolation & duplication summary
| Approach | Isolates WebView2? | Risk of capturing game audio? | Risk of double-audio? | Notes |
|---|---:|---:|---:|---|
| Process-loopback (correct PID/tree) | **Potentially yes** | Low (if correctly targeted) | Medium (**must test mute/capture interaction**) | Best candidate; depends on OS + process-tree targeting |
| Session control only | N/A (no capture) | N/A | N/A | Discovery/control helper only |
| Endpoint loopback | No | **High** | High | Not acceptable for spatial TV audio |

**Research verdict:** **Technically plausible on supported Windows builds** via WASAPI process-loopback + Unity streaming `AudioSource`, but **feasibility is conditional** on (1) process-tree targeting for WebView2, (2) proving mute/capture behavior to prevent double-audio while still capturing, and (3) stable low-latency streaming without impacting video. These require **runtime PoC validation**.

---

## 3) Unity audio integration (design, pending PoC validation)

- **Attachment:** Create/attach a dedicated `AudioSource` to the **actual TV world-space object** (resolved via existing TV hierarchy/component discovery, e.g. `TVInterface`/`TV_Built*` roots or physical screen transform). Prefer reusing an existing suitable TV emitter only if runtime inspection confirms it is safe/owned appropriately and does not regress game behavior.
- **Streaming path:** Native capture thread writes PCM into a lock-free or minimally locked **ring buffer** (preallocated). Unity side reads via `AudioClip` PCM reader callback (streaming) or pushes to a buffered playback component; **no per-frame managed allocations** in steady state.
- **Format handling:** Capture mix format (typically float32, 48 kHz, stereo) must be **resampled/channel-converted** as needed to match Unity/output expectations; handle device/sample-rate changes on reopen.
- **Latency/underruns:** Target low buffering latency (tens of ms) while tolerating jitter; implement underrun handling (silence/fade) to avoid pops/clicks. Tune dynamically only if safe; measure, do not guess.
- **Spatialization:** `spatialBlend = 1.0` (3D), configure `minDistance`/`maxDistance` and `rolloffMode` to feel TV-origin and room-appropriate. **Investigate reuse of Schedule I/custom audio occlusion/attenuation** (`AudioLowPassFilter`, `AudioSourceController` occlusion behavior, game audio mixers/groups) at runtime — do not assume default Unity rolloff matches game.
- **Playback state:** Respect page/media play-pause where possible (`IsDocumentPlayingAudio`), app detach/background behavior (composition continues in background per current design), and game pause/focus changes.

---

## 4) Lifecycle requirements (design)

- **Browser open / composition start:** resolve TV emitter, (re)create streaming clip/source, start capture once WebView2 audio-producing process(es) identified; ensure no duplicate streams.
- **Browser pause:** mute/pause playback and/or pause capture as appropriate (and align with page media state if needed) without leaking audio.
- **Browser close / detach-to-gameplay (if app stays background):** stop capture/playback according to intended behavior (if composition/media continues in background, define explicitly) — but **on full close and on world unload/quit-to-menu must fully stop** audio/capture and release all native/managed buffers/resources (consistent with existing cleanup guarantees).
- **TV destruction / scene change:** detach from destroyed Unity objects, stop capture, dispose buffers safely.
- **Reopen:** tear down previous capture/session/clip fully before starting new; prevent duplicate `AudioSource`s or capture threads.
- **Game pause/focus changes:** pause/mute/resume in sync with game behavior; avoid stuck audio or underrun bursts on resume.

---

## 5) Testing plan (runtime, required before full implementation)

Test at: **very close to TV, normal room distance, maximum audible distance, behind walls/obstacles, while walking away**, and during **browser close/reopen, video pause/resume, game pause/focus changes, detach/background behavior, world unload/quit-to-menu**.

Measure/verify:
- Perceived origin is the physical TV (spatialization/attenuation/occlusion).
- **No double-audio** (must not remain audible on default endpoint if routed spatially, unless intentionally designed and proven safe).
- **No capture of unrelated game/system audio**.
- Latency (acceptable for video watching), underruns/pops/clicks, resampler stability.
- No regression to **smooth video playback, existing audio quality, input, TV lifecycle, adblocking, or performance**.
- Clean teardown: no duplicate streams, no leaked processes/handles/buffers, no audio after close/menu/quit.

---

## 6) Minimal proof of concept (PoC) — scope before full implementation

Because runtime behavior (process-tree audio source, mute-vs-capture, duplication, occlusion integration) cannot be proven in this environment, the **minimal PoC to implement and validate in-game first** should be narrowly scoped:

1. **Discovery/diagnostics (native + managed logging, safe):**
   - Use `ICoreWebView2_8` `IsDocumentPlayingAudio`/`IsMuted` (and change events) to correlate "page playing audio" with capture state.
   - Enumerate WebView2-related process IDs for the active composition profile/controller (and/or WASAPI audio sessions) to identify candidate audio-producing processes.
2. **Process-loopback capture spike (native):**
   - On supported Windows builds, attempt WASAPI process-loopback capture targeting identified WebView2 process(es)/tree; buffer PCM in a preallocated ring buffer; expose read/counters/format via native exports for diagnostics.
   - Experimentally validate **(a)** which PID(s) carry audio, **(b)** whether muting WebView2/session prevents capture, **(c)** whether captured audio still plays on the default endpoint (double-audio risk).
3. **Unity streaming spike (managed):**
   - Create a temporary 3D `AudioSource` at/near the resolved TV transform fed by a streaming `AudioClip` from native ring-buffer data; verify spatial attenuation and basic sync/underrun behavior.
4. **Go/no-go criteria for full implementation:** isolated capture of WebView2 audio only, no double-audio, acceptable latency/quality, no video/perf regressions, and reliable lifecycle/teardown across detach/reopen/pause/menu/quit. If process-loopback isolation or mute/capture constraints fail, document blockers and consider alternatives (e.g. WebView2/browser-level strategies if any become available/verified) — do not ship a speculative/duplicating solution.

**Deliverable provided now:** this feasibility report and PoC plan. **Full implementation must not proceed until the PoC passes runtime validation on target environment (Windows, Evergreen WebView2, Schedule I IL2CPP/MelonLoader).**

---

## 7) Audit notes (this pass)
- **Code changes:** documentation-only (`Firesky_UnderNetBrowser/STAGE3_SPATIAL_AUDIO_FEASIBILITY.md` added). No native/managed runtime behavior modified.
- **APIs verified in installed SDK:** `ICoreWebView2_8` mute/document-playing-audio interfaces present in `WebView2.h` (1.0.2903.40).
- **Build/test:** no binaries rebuilt (no code changes); in-game/runtime testing not performed.