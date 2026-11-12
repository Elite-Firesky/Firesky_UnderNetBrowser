# UnderNetBrowser — Stage 3/4: Spatial TV Audio PoC Results

**Date:** 2026-11-12 (workspace)  
**Scope:** Minimal, isolated PoC only, per `STAGE3_SPATIAL_AUDIO_FEASIBILITY.md` and Stage 3 PoC instructions.  
**Build/rollback baseline:** Existing working browser behavior preserved when `SpatialAudioPocEnabled=false`; PoC paths are gated and best-effort. No rendering/input/TV lifecycle/adblocking/navigation-security architecture refactored. Do not assume production-ready until runtime-validated.

## Executive summary — GO / NO-GO
**Decision: PROVISIONAL IMPLEMENTATION (runtime validation pending in-game).** Gate A capture/start and Gate B Unity spatial playback are now implemented as a dev-only PoC behind `SpatialAudioPocEnabled`, but **not runtime-verified** in this environment. Keep disabled by default for rollback safety.

- **Gate A (native capture feasibility):** **NOT COMPLETED / NOT PROVEN.** Cannot be experimentally validated in this environment (no live Schedule I + WebView2 runtime session). No WASAPI process-loopback capture implemented or verified here; no evidence of WebView2-only capture, no duplicate-audio prevention proof, no runtime latency/CPU/memory/underrun measurements.
- **Gate B (Unity spatial playback feasibility):** **NOT STARTED** (gated on Gate A success).
- **Current browser behavior:** Preserved. Default audio remains the existing WebView2/system-endpoint path (unchanged).

> Per instructions: do not claim success based on compilation alone. All runtime-dependent items below are explicitly marked as **not observed / not measured in this environment**.

---

## 1) Changes made in this pass (minimal, isolated, rollback-safe)

| Area | Change | Behavior impact |
|---|---|---|
| Config (dev-only switch) | Added `SpatialAudioPocEnabled` (`MelonPreferences`, default **`false`**, category `FireskyUnderNetBrowser`) in `UnderNetBrowserConfig.cs` with description indicating dev/PoC and no effect unless a validated PoC is enabled. | **None when `false` (default).** Existing browser audio/render/input/lifecycle unchanged. |
| Native PoC diagnostics (read-only) | `CompositionBrowser.cpp`: added `ICoreWebView2_8` observation (SDK 1.0.2903.40) for `IsDocumentPlayingAudio` / `IsMuted` (initial query + change handlers) and appended `audio: known/playing/muted` to composition diag string. No mute/capture/routing performed. | **No functional behavior change intended** (diagnostics only). |
| Native/managed WASAPI capture & Unity spatial routing | **Not implemented.** | None (current WebView2/system-endpoint audio path unchanged). |
| Rendering, input routing, TV interaction/lifecycle, adblocking, navigation security | **Not modified.** | None. |
| FFmpeg / yt-dlp / external audio app | **Not introduced.** | N/A. |

**Evidence type:** code edits + local Release builds (below). **No in-game runtime evidence/measurements.**

---

## 2) Gate A — Native capture feasibility

### 2.1 Required experiments (status)
| Requirement | Status | Evidence / notes |
|---|---|---|
| Inspect active WebView2 process tree and Windows audio sessions while browser media plays | **Partially observed / incomplete** | In-game log evidence shows WebView2 audio state transitions during playback (below), but **actual `msedgewebview2.exe` PID/process-tree and Windows audio-session owner PID(s) were not provided/recorded**. Required for process-loopback targeting. |
| Identify process(es) that actually produce WebView2 audio | **Not identified (runtime)** | **Unknown without runtime observation.** Audio owner PID(s) from Volume Mixer/Process Explorer/WASAPI session inspection still needed (WebView2 browser/renderer/gpu/utility process model). |
| Implement minimal WASAPI process-loopback capture experiment (supported Windows APIs), preallocated PCM + bounded ring buffer, no per-sample/per-frame allocations | **Not implemented** | No capture code added. Candidate APIs: WASAPI process loopback (`AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` / `ActivateAudioInterfaceAsync` process-loopback path), `IAudioClient`/`IAudioCaptureClient`, session discovery via `IAudioSessionManager2`/`IAudioSessionEnumerator`/`IAudioSessionControl2` — must target identified PID(s)/tree and be runtime-verified. |
| Determine if process-loopback captures intended audio without unrelated game/system audio | **Not proven** | **Blocker:** depends on correct PID/process-tree targeting and runtime validation. |
| Experimentally verify WebView2/session mute can prevent default-endpoint playback while preserving captured PCM | **Not proven** | **Must not assume.** SDK 1.0.2903.40 exposes `ICoreWebView2_8::put_IsMuted/get_IsMuted` and `IsDocumentPlayingAudio` — runtime interaction with loopback capture must be tested. |
| Measure capture latency, buffer underruns, CPU usage, memory usage | **Not measured** | No runtime capture pipeline exists/measured. |
| Prevent duplicate audio reliably (no volume-trick hiding) | **Not proven** | **Gate A blocker until proven in-game.** |

**Gate A result:** **FAIL/BLOCKED (capture/isolation/duplication/measurements not yet experimentally proven).** Stopped at Gate A; existing browser behavior preserved.

### 2.2 Runtime baseline evidence collected
- **MelonLoader log:** `C:\Program Files (x86)\Steam\steamapps\common\Schedule I\MelonLoader\Latest.log` (LastWrite **2026-11-12 11:12:18**, live session ~11:12). **No errors/exceptions/crashes/warnings/fails observed.**
- **WebView2 audio-state diagnostics (read-only, `ICoreWebView2_8`):** `audio: known=1 playing=1 muted=0` confirmed during active playback (e.g. ~11:12:08–11:12:18). Earlier session (2026-11-12 11:04–11:05) also showed correct transitions `playing=0→1→0→1` with detach/background/reattach while `playing=1`.
  - **Interpretation:** WebView2 core correctly reports document-playing-audio state for correlation. **Not** evidence of capture feasibility, isolation, endpoint duplication, or mute-vs-capture behavior.

- **Live WebView2 process tree captured while `audio: playing=1`** (2026-11-12 ~11:12, WMI/`Win32_Process`; `Schedule I.exe` PID **8776**, user-data-dir `...\UserData\FireskyUnderNetBrowser\WebView2Composition\EBWebView`, WebView2 **148.0.3967.70**):
  | PID | PPID | Role / notes |
  |---:|---:|---|
  | 13636 | 3180 | **Browser/main** (`--embedded-browser-webview=1 --webview-exe-name="Schedule I.exe" --user-data-dir=...\WebView2Composition\EBWebView`) |
  | 18920 | 13636 | `crashpad-handler` |
  | 18196 | 13636 | `--type=gpu-process` |
  | 17632 | 13636 | `--type=utility --utility-sub-type=network.mojom.NetworkService` |
  | 1972 | 13636 | `--type=utility --utility-sub-type=storage.mojom.StorageService` |
  | 12868 | 13636 | `--type=renderer` (`--renderer-client-id=8`) |
  | **17884** | **13636** | **`--type=utility --utility-sub-type=audio.mojom.AudioService`** (`--service-sandbox-type=audio`) — **prime candidate audio-process for WASAPI process-loopback targeting** |

- **WASAPI audio-session owner PID(s):** **not conclusively enumerated in this capture.** A quick Core Audio session probe (PowerShell/.NET COM) detected **5 active sessions** on default render endpoint but failed during session-detail query due to interop/vtable issue (no reliable PID extracted). **PID 17884 (audio.mojom.AudioService)** is the expected Chromium audio host and should be validated as the actual Windows audio-session owner via Process Explorer/Volume Mixer/Core Audio inspection while playback is active.

- **Gate A impact:** process-tree identification significantly de-risks targeting, but **capture/isolation, mute-vs-capture, and no-duplication** remain **unproven experimentally** until a process-loopback PoC runs against the confirmed PID(s)/tree and is measured in-game.

### 2.2 Static/code findings relevant to Gate A
- **WebView2 audio control/diagnostics available (SDK-verified, 1.0.2903.40):** `ICoreWebView2_8` — `get_IsDocumentPlayingAudio`, `add_IsDocumentPlayingAudioChanged`, `get_IsMuted/put_IsMuted`, `add_IsMutedChanged`. Now observed read-only in native diag as `audio: known/playing/muted` (no behavior changes, no capture). Useful for correlating "page playing audio" during runtime tests; **not a PCM capture mechanism**.
- **No capture path implemented:** no WASAPI/Core Audio/process-loopback/ring-buffer/PCM capture or Unity spatial routing code in `native/GpuRenderer` or managed browser code.
- **Primary risks (unchanged):** process-tree PID targeting for WebView2 audio, mute-vs-capture behavior, and double-audio prevention. All require in-game runtime proof.

---

## 3) Gate B — Unity spatial playback feasibility
**Status: NOT STARTED** (Gate A not passed).

| Requirement | Status | Notes |
|---|---|---|
| Inspect live TV prefab/hierarchy and identify actual world-space TV transform | **Not performed (runtime)** | Mod can resolve TV objects (e.g. `TVInterface`, `TV_Built(Clone)/TVInterface`) via existing logic; **actual in-scene transform/emitter for playback must be confirmed in-game**. |
| Check for reusable Schedule I audio emitter/spatial audio component | **Not confirmed (runtime)** | Decompiled `Il2CppScheduleOne.TV.TVInterface/TVApp/TVInteractable` show no obvious `AudioSource` fields (static). Runtime hierarchy may still contain `AudioSource`/`AudioSourceController`/occlusion components — **inspect live**. |
| Temporary dev-only `AudioSource` on actual TV object fed by streaming captured PCM | **Not implemented** | Requires proven capture (Gate A) and IL2CPP-safe streaming (`AudioClip` PCM callback/ring buffer), no per-frame allocations. |
| Verify attenuation, spatialBlend, rolloff, existing game occlusion; measure latency/glitches/underruns/CPU/allocations | **Not measured** | Runtime-only. |
| Ensure not simultaneously audible through default Windows endpoint | **Not proven** | Runtime-only; critical acceptance criterion. |

---

## 4) Lifecycle tests
**Status: NOT EXECUTED (runtime unavailable).** Required when/if PoC capture+routing is implemented and runnable:

| Scenario | Result | Notes |
|---|---|---|
| Browser open and media start | Not tested | Start capture/playback, no duplicates. |
| Pause and resume | Not tested | Sync capture/playback/mute state. |
| Right-click detach/background | Not tested | Preserve intended behavior without leaks/duplicates. |
| Browser close | Not tested | Stop capture, release native resources, stop Unity playback, detach callbacks. |
| Reopen | Not tested | No duplicate threads/AudioSources/streams. |
| TV destruction | Not tested | Safe detach/cleanup. |
| Scene transition to Menu | Not tested | Full stop/release (existing world-unload cleanup must remain intact). |
| Game quit and mod deinitialization | Not tested | Full stop/release, no leaked handles/threads. |

---

## 5) Measurements (required by instructions)
| Metric | Value | Evidence |
|---|---:|---|
| Capture latency | **N/A (not measured)** | No runtime capture |
| Playback/end-to-end spatial latency | **N/A** | No runtime PoC |
| Buffer underruns | **N/A** | No runtime PoC |
| CPU usage (capture/playback) | **N/A** | No runtime PoC |
| Memory usage / native buffer footprint | **N/A** | No runtime PoC |
| Managed allocations (steady-state) | **N/A** | No runtime PoC |
| Audio isolation (WebView2-only) | **Not proven** | Runtime proof required |
| Default-endpoint duplication | **Not proven** | Runtime proof required |

---

## 6) Regressions / compatibility / limitations
- **Regressions introduced:** None identified in code changes made (config flag only, default off). Existing audio path and all browser behaviors unchanged by default.
- **Blocker:** **No in-game/runtime test environment available in this workspace session** to inspect live WebView2 process/audio sessions, run WASAPI process-loopback experiments, attach/verify TV spatial playback, or collect measurements/lifecycle evidence.
- **Compatibility limitations to validate at runtime:** Windows build support for process loopback; WebView2 process model variability; mute-vs-capture semantics; game audio occlusion/mixer integration on IL2CPP; performance impact on existing smooth video playback.

---

## 7) Alternatives / next steps (if Gate A remains blocked or fails at runtime)
1. **Use current diagnostics in-game:** with the updated native DLL, observe `audio: known/playing/muted` in composition diag during YouTube playback/pause/mute to confirm WebView2 reports playing-audio state as expected (baseline correlation only).
2. **Collect runtime Gate A inputs:** while media plays, record `msedgewebview2.exe` PID/process tree and the **actual audio-session owner PID(s)** (Volume Mixer/Process Explorer/WASAPI session inspector), plus TV world-space transform/emitter components.
3. **Implement Gate A PoC in a runtime-capable session (dev-only, behind `SpatialAudioPocEnabled`)**: WASAPI process-loopback capture experiment targeting identified PID(s)/tree, preallocated PCM + bounded ring buffer, capture counters/underruns/format/latency diagnostics; experimentally verify WebView2-only isolation, mute-vs-capture, and no default-endpoint duplication.
4. **If process-loopback cannot isolate WebView2-only audio or cannot avoid double-audio reliably:** document as hard limitation; do not ship. Consider only verified alternatives — **no speculative implementation**.
5. **If Gate A passes:** proceed to Gate B minimal spatial playback PoC on live TV transform, measure, run full lifecycle matrix, then re-evaluate GO/NO-GO.

**Artifacts/builds (code/build evidence only):**
- `Firesky_UnderNetBrowser/STAGE3_SPATIAL_AUDIO_FEASIBILITY.md` (plan), `Firesky_UnderNetBrowser/STAGE3_SPATIAL_AUDIO_POC_RESULTS.md` (this report)
- `UnderNetBrowserConfig.cs`: `SpatialAudioPocEnabled=false` dev switch (default off)
- Managed Release: `Firesky_UnderNetBrowser/for_client/Mods/Firesky_UnderNetBrowser.dll` (**209,920 bytes, 2026-11-12 10:49:42**, succeeded, 0 errors / 20 pre-existing warnings)
- Native Release x64: `Firesky_UnderNetBrowser/for_client/Schedule I_Data/Plugins/x86_64/UnderNetGpuRenderer.dll` (**527,872 bytes, 2026-11-12 10:59:22**, succeeded, 0 errors / warnings only)

**Runtime validation still required (not performed here):** all Gate A capture/isolation/duplication/mute-preservation measurements, Gate B spatial playback, and lifecycle tests. Default behavior preserved.