using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using S1API.TVApp;
using S1API.Utils;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// WebView2-only TV browser app for Schedule I (IL2CPP/MelonLoader, net6).
    /// Renders WebView2 frames to the in-game TV texture and provides basic navigation controls.
    /// Stage E: legacy yt-dlp/FFmpeg/download/cache/transcode/local MP4/VideoPlayer pipeline removed.
    /// </summary>
    public class UnderNetBrowserApp : TVApp
    {
        protected override string AppName => "UnderNetBrowser";
        protected override string AppTitle => "UnderNet";

        private Sprite? _icon;
        protected override Sprite? Icon => _icon ??= LoadOrCreateIcon();

        private RawImage? _videoDisplay;
        private RectTransform? _videoRT;
        private RectTransform? _chromeRT;
        private GameObject? _chromeRoot;
        private InputField? _urlInput;
        private Text? _urlDisplayText;
        private Button? _backButton;
        private Button? _forwardButton;
        private Button? _reloadButton;
        private Button? _homeButton;
        private Button? _closeButton;
        private Text? _gameModeIndicatorText;
        private GameObject? _gameModeIndicatorRoot;
        private Text? _hintText;
        private string _hintBaseMessage = "";
        private float _hintAnimTime;
        private bool _uiBuilt;
        private bool _browserPointerDown;
        private bool _browserPointerRightDown;
        private bool _isPageFullscreen;
        private bool _detachedToGameplay; // app open/background but player returned to gameplay (no TV input ownership/suppression)
        // Game Input Mode: forwards real key down/up transitions for gameplay keys to WebView2.
        private bool _gameInputMode;
        private readonly System.Collections.Generic.HashSet<int> _gameKeysHeld = new System.Collections.Generic.HashSet<int>();
        private bool _gameInputPrevUrlFocused;
        private bool _repairStateLoggedBad;
        private bool _repairBackoffActive;
        private float _repairNextAllowedRealtime;
        private float _lastRepairStateLogRealtime;
        private string _lastRepairStateSignature = string.Empty;
        private bool _browserUiCriticalActionInProgress;
        private float _lastInputBlockAttempt;
        private readonly List<InputStateSnapshot> _savedInputStates = new List<InputStateSnapshot>();

        private sealed class InputStateSnapshot
        {
            public Type? StaticType;
            public object? Target;
            public string Member = "";
            public object? Prev;
            public bool IsProperty;
        }
        private UnderNetBrowserCompositionCapture? _gpuCompositionCapture;
        private bool _gpuCompositionMode;
        private bool _compositionWebFocused;
        private float _lastWebTextEntryActivityRealtime = -999f;
        private bool _compositionFocusPrimed;

        // External window mirror mode (e.g. Discord window capture via WGC)
        private UnderNetBrowserGpuCapture? _externalWindowCapture;
        private IntPtr _externalMirrorHwnd = IntPtr.Zero;
        private bool _externalMirrorMode;
        private string _externalMirrorLabel = "";
        private float _lastExternalMirrorAttemptTime;
        private int _lastSentPointerX = -1;
        private int _lastSentPointerY = -1;
        private float _lastPointerMoveSendTime;
        private bool _gameMouseCaptured;
        private float _gameVirtualMouseX;
        private float _gameVirtualMouseY;
        private float _lastGameModeFocusNudge = -999f;
        private string? _externalMirrorReturnUrl;
        // Distance-based browser media volume attenuation (best-effort, HTML video/audio elements)
        private Transform? _distanceVolumeTvTransform;
        private float _lastDistanceVolumeTvResolveTime = -999f;
        private float _lastDistanceVolumeUpdateTime = -999f;
        private float _lastDistanceVolumeApplied = 1f;
        private bool _distanceVolumeWasEnabled;

        // Adaptive Rendering (visual-only quality/performance; independent of audio)
        private enum AdaptiveVisualTier { Full = 0, Reduced = 1, Low = 2, VisualStandby = 3 }
        private AdaptiveVisualTier _adaptiveCurrentTier = AdaptiveVisualTier.Full;
        private AdaptiveVisualTier _adaptivePendingTier = AdaptiveVisualTier.Full;
        private float _adaptivePendingTierSince = -999f;
        private float _adaptiveLastEvalTime = -999f;
        private int _adaptiveAppliedCaptureIntervalMs = 0;
        private bool _adaptiveAppliedStandby = false;
        private string _adaptiveLastReason = "init";
        private bool _adaptiveLastTvVisible = true;
        private float _adaptiveLastDistance = -1f;
        private float _adaptiveLastDiagLogTime = -999f;
        private float _adaptiveUnfocusedSinceRealtime = -1f;
        private bool _browserHostCursorHidden;
        // Arrow-key media control repeat (seek/volume) for attached/detached browser playback
        private float _nextMediaArrowRepeatLeft = -1f;
        private float _nextMediaArrowRepeatRight = -1f;
        private float _nextMediaArrowRepeatUp = -1f;
        private float _nextMediaArrowRepeatDown = -1f;
        private float _nextUrlBarPasteRepeatTime = -1f;
        private readonly UnderNetBrowserSync _sync = new UnderNetBrowserSync();
        private readonly List<(GameObject go, bool wasActive)> _hiddenHudObjects = new List<(GameObject, bool)>();
        private readonly List<GameObject> _cachedHudHideCandidates = new List<GameObject>(16);
        private float _lastHudCandidateScanRealtime = -999f;
        private static Font? _cachedFont;
        private static UnderNetBrowserApp? _activeInstance;

        protected override void OnCreatedUI(GameObject container)
        {
            _activeInstance = this;
            if (_uiBuilt) return;
            try
            {
                BuildUI(container);
                _uiBuilt = true;
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI built in '{container.name}'");
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.LogError($"[UnderNetBrowser] BuildUI FAILED: {ex}");
                throw;
            }
        }

        private static string? _lastWorldUnloadCleanupKey;
        private static float _lastWorldUnloadCleanupTime;
        internal static void ShutdownForWorldUnload(string reason)
        {
            try
            {
                // Dedupe noisy repeated menu scene loads while still executing cleanup (idempotent).
                string key = reason ?? string.Empty;
                float now = 0f;
                try { now = UnityEngine.Time.realtimeSinceStartup; } catch { now = 0f; }
                if (key.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(_lastWorldUnloadCleanupKey, key, StringComparison.OrdinalIgnoreCase) && now > 0f && (_lastWorldUnloadCleanupTime > 0f && now - _lastWorldUnloadCleanupTime < 5f))
                    {
                        // Still perform idempotent cleanup, but skip duplicate log.
                        var activeDup = _activeInstance;
                        if (activeDup != null)
                        {
                            try { activeDup.ShutdownBrowserRuntime(reason); } catch { }
                        }
                        else
                        {
                            try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                        }
                        return;
                    }
                    _lastWorldUnloadCleanupKey = key;
                    _lastWorldUnloadCleanupTime = now;
                }
                else
                {
                    // For non-scene reasons, reset dedupe key to allow future scene cleanup logs.
                    _lastWorldUnloadCleanupKey = null;
                }

                var active = _activeInstance;
                if (active != null)
                {
                    active.ShutdownBrowserRuntime(reason);
                }
                else
                {
                    // No active app instance reference, but native composition/WebView2 may still be alive.
                    try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                }
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] World-unload browser cleanup executed ({reason}).");
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] ShutdownForWorldUnload failed (non-fatal): {ex.Message}");
            }
        }

        private void ShutdownBrowserRuntime(string reason)
        {
            try
            {
                // Stop interaction/input ownership and restore game-side state best-effort before tearing down browser.
                _detachedToGameplay = false;
                _detachRequested = false;
                AnyDetachActive = false;
                _compositionWebFocused = false;
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                try { UnderNetBrowserDiagnostics.SetBrowserIntentionalAlive(false); } catch { }
                try { ReleaseAllGameKeys("shutdown:" + reason); } catch { }
                _gameInputMode = false;
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { TryRestoreGameInput(); } catch { }
                try { TryRestoreGameHud(); } catch { }
                try { RestoreDetachedHomeScreenObjects(); } catch { }
            }
            catch { }

            try
            {
                try { StopExternalWindowMirror("shutdown:" + reason); } catch { }
                bool hadCapture = _gpuCompositionCapture != null || _externalMirrorMode;
                if (hadCapture)
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Shutting down browser/composition ({reason}).");

                // Best-effort: restore distance-attenuated WebView2 process volume before teardown.
                try { UnderNetBrowserGpuNative.CompSetDistanceAudioVolume(0, 1f); } catch { }
                _distanceVolumeWasEnabled = false;
                _lastDistanceVolumeApplied = 1f;
                _distanceVolumeTvTransform = null;
                try { ResetAdaptiveRenderingState("shutdown:" + reason); } catch { }

                // Best-effort teardown. Avoid script/navigation into a potentially hung WebView before dispose,
                // as these can block on native synchronization during unload/recovery shutdowns.
                try { ClearReattachFocusRecoveryState(); } catch { }
                try { ReleaseAllGameKeys("shutdown_dispose"); } catch { }
                var captureToDispose = _gpuCompositionCapture;
                _gpuCompositionCapture = null;
                _gpuCompositionMode = false;
                try { captureToDispose?.SendMouseUp(0, 0, false); } catch { }
                try { captureToDispose?.SendMouseUp(0, 0, true); } catch { }
                try { captureToDispose?.Dispose(); } catch { }

                // Always attempt native shutdown even if managed capture reference was already cleared.
                try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                try { UnderNetBrowserGpuNative.ClearTargetTexture(); } catch { }
            }
            catch { }

            try { _sync.Shutdown(); } catch { }
            try { _uiBuilt = false; } catch { }
            try { if (_activeInstance == this) _activeInstance = null; } catch { }
        }

        protected override void OnOpened()
        {
            try { UnderNetBrowserDiagnostics.OnTvInteractionBegin(); } catch { }
            base.OnOpened();
            _browserPointerDown = false;
            _isPageFullscreen = false;
            _detachRequested = false;
            AnyDetachActive = false;
            _explicitCloseRequested = false;
            bool wasDetached = _detachedToGameplay;
            _detachedToGameplay = false;
            _reattachBlockedUntilRealtime = -999f;
            try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
            TryHideGameHud();
            try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
            // If S1API/game re-opens the app while we were in detached-background state, treat as authoritative reattach and force focus.
            if (wasDetached)
            {
                if (_gameInputMode && UnderNetBrowserConfig.DebugLogging?.Value == true)
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] OnOpened(from detached): resetting GameInputMode to OFF for deterministic focus/ownership.");
                _gameInputMode = false;
                try { if (_gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(false); } catch { }
                try { RestoreDetachedHomeScreenObjects(restoreHiddenObjects: false); } catch { }
                try { TryForegroundS1AppForReattach(); } catch { }
                try { SetAppInteractionEnabled(true); } catch { }
                try { EnsureGraphicRaycastersEnabledForApp("OnOpened_from_detached"); } catch { }
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
                try { if (_urlInput != null && _urlInput.isFocused) _urlInput.DeactivateInputField(); } catch { }
                try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                _compositionWebFocused = true;
            }
            UpdateInputOwnershipState();
            // Hybrid suppression: hooks for supported callbacks + verified/explicit game API toggles + reflection fallback for unhooked paths.
            // Capture once per open so states can be restored on close.
            try { TryDisableGameInput(); } catch { }
            // Learn baseline hotbar slot to pin while open (fallback for unhooked equip paths).
            try { _lastKnownHotbarSlot = -1; TryRestoreEquippedSlotIndex(forceRestore: false); } catch { }
            // Always force web focus on open/reattach entry to avoid "opened but not interactive" states.
            BeginReattachFocusRecovery(wasDetached ? "OnOpened_from_detached" : "OnOpened");
            UnderNetBrowserConfig.Log("[UnderNetBrowser] OnOpened");
            try { UnderNetBrowserDiagnostics.OnBrowserVisible(); } catch { }

            var diagGo = _videoDisplay != null ? (_videoDisplay.transform.parent?.gameObject ?? _videoDisplay.gameObject) : null;
            if (diagGo != null)
            {
                RepairS1APICanvasState(diagGo, "OnOpened");
                DumpUIDiagnostics(diagGo, "OnOpened");
            }

            // UnderNet now always uses WebView2 composition mode (GPU composition browser).
            _gpuCompositionMode = true;
            try
            {
                var home = GetResolvedHomeUrl();
                var displayHome = UnderNetBrowserConfig.HomeUrl?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(displayHome)) displayHome = "undernet:home";
                if (displayHome.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase)) displayHome = "undernet:home";
                if (_urlInput != null)
                {
                    _urlInput.text = displayHome;
                    if (_urlInput.textComponent != null) { _urlInput.textComponent.text = displayHome; _urlInput.textComponent.enabled = true; }
                    try { _urlInput.ForceLabelUpdate(); } catch { }
                }

                try { SetUrlDisplayText(displayHome); } catch { }
                try
                {
                    var f = GetUIFont();
                    var ugo = _urlInput?.gameObject;
                    var ur = _urlInput?.GetComponent<RectTransform>()?.rect;
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] UrlBarDiag(open): input={(ugo!=null)} active={ugo?.activeInHierarchy} text='{_urlInput?.text}' disp='{_urlDisplayText?.text}' dispActive={_urlDisplayText?.gameObject?.activeInHierarchy} rect={(ur.HasValue ? $"{ur.Value.width:0.#}x{ur.Value.height:0.#}" : "null")} font={(f!=null ? f.name : "null")}");
                }
                catch { }

                // Fast path: reuse warm composition/WebView2 session when still healthy to avoid ~12s controller recreation.
                bool reused = false;
                try
                {
                    if (_gpuCompositionCapture != null)
                    {
                        int compStatus = 0;
                        try { compStatus = UnderNetBrowserGpuNative.CompGetStatus(); } catch { compStatus = 0; }
                        if (compStatus > 0 && _gpuCompositionCapture.Texture != null)
                        {
                            _gpuCompositionCapture.RenderingSuspended = false;
                            try { ResetAdaptiveRenderingState("warm_reopen"); } catch { }
                            try { ReleaseAllGameKeys("warm_reopen_navigate"); } catch { }
                            try { _gpuCompositionCapture.Navigate(home); } catch { }
                            try { _gpuCompositionCapture.RequestFocus(); } catch { }
                            if (_hintText != null) { _hintBaseMessage = ""; _hintText.enabled = false; }
                            _compositionWebFocused = true;
                            reused = true;
                            UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Reused warm composition browser session (fast reopen).");
                        }
                    }
                }
                catch { reused = false; }

                if (!reused)
                {
                    // Clean up stale/failed capture before cold start.
                    if (_gpuCompositionCapture != null)
                    {
                        try { _gpuCompositionCapture.Dispose(); } catch { }
                        _gpuCompositionCapture = null;
                        try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                    }

                    _gpuCompositionCapture = new UnderNetBrowserCompositionCapture();
                    GetDesiredCompositionRenderSize(out int compW, out int compH);
                    string compUserData = "";
                    try { compUserData = System.IO.Path.Combine(GetUserDataRoot(), "WebView2Composition"); } catch { compUserData = ""; }
                    try { EnsureDefaultBrowserExtensionDeployed(compUserData); } catch { }
                    try { UnderNetBrowserDiagnostics.OnBrowserLoadingBegin(); UnderNetBrowserDiagnostics.OnWebViewInitStart(); } catch { }
                    if (_gpuCompositionCapture.TryStart(home, compW, compH, compUserData))
                    {
                        try { ResetAdaptiveRenderingState("cold_start"); } catch { }
                        try { UnderNetBrowserDiagnostics.OnWebViewInitDone(); } catch { } // start request issued; async init continues natively
                        SetHint("Starting UnderNet browser (initializing...)");
                        _compositionWebFocused = true; // default to web interaction; URL bar focus will override when activated
                    }
                    else
                    {
                        SetHint("Composition browser unavailable. Check MelonLoader log / WebView2 runtime and native renderer.");
                        try { _gpuCompositionCapture.Dispose(); } catch { }
                        _gpuCompositionCapture = null;
                        _gpuCompositionMode = false;
                    }
                }
            }
            catch (Exception ex)
            {
                SetHint($"Composition browser start failed: {ex.Message}");
                try { _gpuCompositionCapture?.Dispose(); } catch { }
                _gpuCompositionCapture = null;
                _gpuCompositionMode = false;
            }
        }

        protected override void OnClosed()
        {
            // If a detach-to-gameplay was just requested, the app should remain alive (paused/backgrounded) with
            // its browser state intact. The in-app X button is the only explicit close action.
            // Note: if framework still invokes Close, we avoid tearing down resources and restore gameplay input.
            if (_detachRequested && (Time.realtimeSinceStartup - _detachRequestedTime) < 2.0f)
            {
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] OnClosed during detach request: preserving app/browser state (detach keeps app open).");
                try { base.OnClosed(); } catch { }
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { TryRestoreGameInput(); } catch { }
                TryRestoreGameHud();
                _isPageFullscreen = false;
                _compositionWebFocused = false;
                UpdateChromeVisibility();
                // Do not dispose browser/capture/composition state.
                return;
            }

            base.OnClosed();
            try { ReleaseAllGameKeys("close"); } catch { }
            _gameInputMode = false;
            _detachRequested = false;
            AnyDetachActive = false;
            _detachedToGameplay = false;
            try { RestoreDetachedHomeScreenObjects(); } catch { }
            _browserPointerDown = false;
            _browserPointerRightDown = false;
            try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
            try { UnderNetBrowserInputHooks.LogDiagnostics(force: true); } catch { }
            bool explicitXClose = _explicitCloseRequested;
            _explicitCloseRequested = false; // consume once
            _warmCloseRequested = false; // consume once (normal/warm close path)
            try { ClearReattachFocusRecoveryState(); } catch { }
            if (explicitXClose)
            {
                // Explicit app close (X): fully shut down browser/WebView2 to avoid lingering processes.
                // Avoid script/navigation calls into a potentially hung WebView (e.g. active text selection/drag)
                // before teardown, as they can block on native synchronization and freeze the game.
                try { ResetAdaptiveRenderingState("close_x"); } catch { }
                try { StopExternalWindowMirror("closed_x"); } catch { }
                var captureToDispose = _gpuCompositionCapture;
                _gpuCompositionCapture = null;
                _gpuCompositionMode = false;
                // Best-effort: release any active pointer drag/selection before native teardown (can reduce shutdown hangs).
                try { captureToDispose?.SendMouseUp(0, 0, false); } catch { }
                try { captureToDispose?.SendMouseUp(0, 0, true); } catch { }
                try { captureToDispose?.Dispose(); } catch (Exception ex) { try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] X close dispose failed (non-fatal): {ex.Message}"); } catch { } }
                try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                try { UnderNetBrowserGpuNative.ClearTargetTexture(); } catch { }
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Browser runtime fully shut down (X close).");
            }
            else
            {
                // Normal/framework close path: stop media and park browser for fast warm reopen (avoid ~12s controller recreation).
                // Full native shutdown remains in ShutdownBrowserRuntime/world unload.
                try { ResetAdaptiveRenderingState("warm_close"); } catch { }
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var ms=document.querySelectorAll('video,audio'); for(var i=0;i<ms.length;i++){try{ms[i].pause(); ms[i].src=''; ms[i].load();}catch(_){}}}catch(_){}})();"); } catch { }
                try { ReleaseAllGameKeys("close_navigate_blank"); } catch { }
                try { _gpuCompositionCapture?.Navigate("about:blank"); } catch { }
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = true; } catch { }
                // Keep composition/WebView2 session warm for faster next open.
                _gpuCompositionMode = _gpuCompositionCapture != null;
            }

            _compositionWebFocused = false;
            _compositionFocusPrimed = false;
            _hotbarSlotAccessorResolved = false;
            _cachedHotbarSlotMember = null;
            _cachedHotbarSlotTarget = null;
            _cachedHotbarSlotDeclaringType = null;
            // Legacy suppression restore (only relevant if fallback path was used)
            try { TryRestoreGameInput(); } catch { }
            TryRestoreGameHud();
            _isPageFullscreen = false;
            UpdateChromeVisibility();
        }

        protected override void OnPaused()
        {
            base.OnPaused();
            try { UnderNetBrowserInputHooks.UpdateOwnership(false, _urlInput != null && _urlInput.isFocused, _compositionWebFocused, false); } catch { }
            try { TryRestoreGameInput(); } catch { }
            TryRestoreGameHud();
            // Keep browser/composition running and state intact while paused/detached.
        }

        protected override void OnResumed()
        {
            base.OnResumed();
            bool wasDetached = _detachedToGameplay;
            _detachedToGameplay = false;
            _detachRequested = false;
            AnyDetachActive = false;
            _reattachBlockedUntilRealtime = -999f;
            try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
            // Do not reactivate hidden TV home/clock objects on resume; app remains active.
            try { RestoreDetachedHomeScreenObjects(restoreHiddenObjects: false); } catch { }
            if (wasDetached && _gameInputMode && UnderNetBrowserConfig.DebugLogging?.Value == true)
                UnderNetBrowserConfig.Log("[UnderNetBrowser] OnResumed(from detached): resetting GameInputMode to OFF for deterministic focus/ownership.");
            if (wasDetached) _gameInputMode = false;
            try { if (!_gameInputMode && _gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(false); } catch { }
            try { TryForegroundS1AppForReattach(); } catch { }
            try { SetAppInteractionEnabled(true); } catch { }
            try { EnsureGraphicRaycastersEnabledForApp("OnResumed"); } catch { }
            try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
            try { if (_urlInput != null && _urlInput.isFocused) _urlInput.DeactivateInputField(); } catch { }
            try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
            _compositionWebFocused = true;
            UpdateInputOwnershipState();
            try { TryHideGameHud(); } catch { }
            try { TryDisableGameInput(); } catch { }
            try { if (_lastKnownHotbarSlot < 0) TryRestoreEquippedSlotIndex(forceRestore: false); } catch { }
            BeginReattachFocusRecovery(wasDetached ? "OnResumed_from_detached" : "OnResumed");
        }

        protected override void OnUpdate()
        {
            try
            {
                if (!IsOpen) return;
                try { RefreshWebTextEntryState(); } catch { }
                // Clear detach transition flags after grace period (close-block/guard windows use time checks too).
                if ((_detachRequested || AnyDetachActive) && (Time.realtimeSinceStartup - _detachRequestedTime) > 5.0f)
                {
                    _detachRequested = false;
                    AnyDetachActive = false;
                }
                if (_detachedToGameplay)
                {
                    // If player re-enters TV interaction while we kept app/browser alive, re-acquire ownership/input control.
                    try
                    {
                        if (TryReattachToTvIfResumedWhileDetached())
                        {
                            // Reattached this frame: fall through to normal TV/browser interaction handling.
                        }
                        else
                        {
                            // Detached: app stays open/backgrounded, but gameplay must remain fully restored.
                            try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                            // Ensure game input stays restored (do not re-apply TV suppression while detached).
                            if (_savedInputStates.Count > 0) { try { TryRestoreGameInput(); } catch { } }
                            try { TryRestoreGameHud(); } catch { }
                            // Best-effort: keep cursor suitable for gameplay while detached, but don't fight pause/menu cursor.
                            try
                            {
                                if (Time.timeScale > 0.01f)
                                {
                                    // Only enforce gameplay cursor when no obvious UI cursor is needed.
                                    if (!(Cursor.visible && Cursor.lockState == CursorLockMode.None))
                                    {
                                        Cursor.lockState = CursorLockMode.Locked;
                                        Cursor.visible = false;
                                    }
                                }
                            }
                            catch { }
                            // (No periodic TVInterface exit here: repeated Close()/RemovePlayer calls can fight game state and break pause menu/ESC.)
                            // Keep composition/browser running in background (no visual pause), but do not process TV/browser interaction input.
                            // If feasible, allow basic media controls (arrows) to reach the browser while off-TV/video keeps playing.
                            try { HandleDetachedBrowserMediaKeys(); } catch { }
                            try { UpdateExternalMirrorTick(); } catch { }
                            if (!_externalMirrorMode) { try { _gpuCompositionCapture?.Tick(); } catch { } }
                            try { MaintainDetachedAppVisualState(); } catch { }
                            try { TryUpdateAdaptiveRendering(); } catch { }
                            try { TryLogAdaptiveDiagnostics(); } catch { }
                            try { TryUpdateDistanceBasedVolume(); } catch { }
                            try { UnderNetBrowserInputHooks.LogDiagnostics(); } catch { }
                            return;
                        }
                    }
                    catch
                    {
                        // On error, stay in detached-safe mode.
                        try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                        try { HandleDetachedBrowserMediaKeys(); } catch { }
                        try { _gpuCompositionCapture?.Tick(); } catch { }
                        try { MaintainDetachedAppVisualState(); } catch { }
                        try { TryUpdateAdaptiveRendering(); } catch { }
                        try { TryLogAdaptiveDiagnostics(); } catch { }
                        try { TryUpdateDistanceBasedVolume(); } catch { }
                        try { UnderNetBrowserInputHooks.LogDiagnostics(); } catch { }
                        return;
                    }
                }
                UpdateInputOwnershipState();
                // Keep suppression/hotbar-slot pinning active (covers unhooked paths like OnMove and alternate equip-slot changes). Throttled internally.
                try { TryEnsureGameInputSuppressed(); } catch { }
                try { TickReattachFocusRecovery(); } catch { }
                HandleClipboardPaste();
                HandleBrowserPointerInput();
                HandleCompositionKeyboardInput();
                try { SyncAddressBarFromBrowser(); } catch { }
                try
                {
                    // Hard fallback: address bar must never be blank/unusable at startup.
                    if (_urlInput != null && !_urlInput.isFocused && string.IsNullOrEmpty(_urlInput.text))
                    {
                        var homeDisplay = UnderNetBrowserConfig.HomeUrl?.Value?.Trim();
                        if (string.IsNullOrWhiteSpace(homeDisplay)) homeDisplay = "undernet:home";
                        if (homeDisplay.Equals("about:home", StringComparison.OrdinalIgnoreCase) || homeDisplay.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase)) homeDisplay = "undernet:home";
                        _urlInput.text = homeDisplay;
                        if (_urlInput.textComponent != null) _urlInput.textComponent.text = homeDisplay;
                        try { _urlInput.ForceLabelUpdate(); } catch { }
                    }
                    try { if (_urlInput != null && !_urlInput.isFocused) SetUrlDisplayText(string.IsNullOrEmpty(_urlInput.text) ? "undernet:home" : _urlInput.text); } catch { }
                }
                catch { }
                try { UnderNetBrowserInputHooks.LogDiagnostics(); } catch { }

                // F3 cursor toggle is handled globally in UnderNetBrowserMod (works inside/outside TV).
                if (Input.GetKeyDown(UnderNetBrowserConfig.GetKey(KeyCode.F5, UnderNetBrowserConfig.KeyReload)))
                {
                    _gpuCompositionCapture?.Reload();
                }
                if (Input.GetKeyDown(UnderNetBrowserConfig.GetKey(KeyCode.F6, UnderNetBrowserConfig.KeyGameInputMode)))
                {
                    SetGameInputMode(!_gameInputMode, "toggle");
                }

                // Right-click: exit TV mode but keep this app running/paused in background (do not close app).
                // In Game Input Mode with web focus, forward right-click to the browser game instead (predictable; F6 exits mode).
                if (Input.GetMouseButtonDown(1) && !IsPaused)
                {
                    bool urlFocusedNow = _urlInput != null && _urlInput.isFocused;
                    bool webFocusedNow = _compositionWebFocused && !urlFocusedNow;
                    bool ptrOverWeb = IsPointerOverVideoDisplay(out _, out _);
                    if (_gameInputMode && webFocusedNow && ptrOverWeb)
                    {
                        // Forwarded to browser via pointer handling below/this frame; do not detach.
                    }
                    else
                    {
                        TryDetachToGameplayKeepAppOpen();
                    }
                }

                // Animate loading hint (simple spinner/dots) when visible
                if (_hintText != null && _hintText.enabled && !string.IsNullOrEmpty(_hintBaseMessage))
                {
                    bool gameModeHint = _hintBaseMessage.StartsWith("Game Input ON", StringComparison.Ordinal);
                    if (!gameModeHint)
                    {
                        _hintAnimTime += Time.unscaledDeltaTime;
                        int dots = ((int)(_hintAnimTime * 2f) % 4);
                        string spinner = new[] { "◜", "◠", "◝", "◞", "◡", "◟" }[((int)(_hintAnimTime * 8f) % 6)];
                        _hintText.text = $"{_hintBaseMessage}\n\n<color=#7CFF6B>{spinner}</color>  <color=#DFFFE3>Connecting</color>{new string('.', dots)}";
                    }
                }

                if (Time.realtimeSinceStartup >= _repairNextAllowedRealtime)
                {
                    var diagGo = _videoDisplay != null ? (_videoDisplay.transform.parent?.gameObject ?? _videoDisplay.gameObject) : null;
                    if (diagGo != null) RepairS1APICanvasState(diagGo, "OnUpdate");
                }

                try { UpdateExternalMirrorTick(); } catch { }
                if (_externalMirrorMode)
                {
                    // External mirror (e.g. Discord window capture) is active; browser composition is paused/stopped.
                    try { if (_hintText != null) { _hintBaseMessage = ""; _hintText.enabled = false; _hintText.text = ""; } } catch { }
                }
                else if (_gpuCompositionCapture != null)
                {
                    // Intercept built-in shortcut routes from in-page navigation/clicks (e.g. homepage shortcuts).
                    try
                    {
                        var cur = _gpuCompositionCapture.CurrentUrl;
                        if (!string.IsNullOrWhiteSpace(cur))
                        {
                            var n = NormalizeUrl(cur);
                            if (n.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase) || IsDiscordMirrorRequestUrl(cur))
                            {
                                if (Time.realtimeSinceStartup - _lastExternalMirrorAttemptTime > 1.0f)
                                {
                                    if (!TryStartDiscordMirror(n))
                                    {
                                        try { _gpuCompositionCapture.Navigate(GetResolvedHomeUrl()); } catch { }
                                        try { SetHint("Discord mirror unavailable (is Discord running?)."); } catch { }
                                    }
                                }
                                return;
                            }
                            if (n.Equals("undernet:ai", StringComparison.OrdinalIgnoreCase))
                            {
                                try { SetUrl("https://www.google.com/ai"); } catch { }
                                return;
                            }
                        }
                    }
                    catch { }
                }

                if (_externalMirrorMode)
                {
                    // already handled above
                }
                else if (_gpuCompositionCapture != null)
                {
                    _gpuCompositionCapture.Tick();
                    try { TryUpdateAdaptiveRendering(); } catch { }
                    try { TryUpdateDistanceBasedVolume(); } catch { }
                    if (_gpuCompositionCapture.Texture != null && _videoDisplay != null && _videoDisplay.texture != _gpuCompositionCapture.Texture)
                        _videoDisplay.texture = _gpuCompositionCapture.Texture;

                    // Composition startup is async: keep loading hint visible until first rendered frame/content is ready.
                    try
                    {
                        int compSt = UnderNetBrowserGpuNative.CompGetStatus();
                        string compDiag = "";
                        try { compDiag = UnderNetBrowserGpuNative.GetCompDiagString() ?? ""; } catch { }
                        if (compSt == 2)
                            SetHint("Starting UnderNet browser (initializing WebView2/graphics)...");
                        else if (compSt < 0)
                            SetHint("Browser initialization failed. See MelonLoader log for diagnostics.");
                        else if (compSt == 1)
                        {
                            // Hide hint once we have evidence of successful frame copy/rendering.
                            bool hasFrame = compDiag.IndexOf("comp blit ok", StringComparison.OrdinalIgnoreCase) >= 0
                                || compDiag.IndexOf("running (async init complete)", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (hasFrame) { try { UnderNetBrowserDiagnostics.OnFirstRenderedFrame(); } catch { } }
                            if (hasFrame && _hintText != null && !_gameInputMode) { _hintBaseMessage = ""; _hintText.enabled = false; }
                            else if (_hintText != null && _hintText.enabled && string.IsNullOrEmpty(_hintBaseMessage) && !_gameInputMode)
                                SetHint("Loading page...");
                        }
                    }
                    catch { }

                    // Prime focus once content is rendering to help first interaction (best-effort)
                    if (!_compositionFocusPrimed && _gpuCompositionCapture.Texture != null)
                    {
                        _compositionFocusPrimed = true;
                        try { _gpuCompositionCapture.RequestFocus(); } catch { }
                    }

                    // Page fullscreen (e.g. YouTube): hide chrome and let content fill the full TV area.
                    try
                    {
                        bool fs = _gpuCompositionCapture.IsFullscreen;
                        if (fs != _isPageFullscreen)
                        {
                            _isPageFullscreen = fs;
                            UpdateChromeVisibility();
                        }
                    }
                    catch { }

                    // Evidence-based visual diagnostics (rate-limited): trace actual dimensions/scales across pipeline stages.
                    if (Time.unscaledTime - _lastVisualDiagLog > 5f)
                    {
                        _lastVisualDiagLog = Time.unscaledTime;
                        try
                        {
                            int tw = _gpuCompositionCapture.Texture != null ? _gpuCompositionCapture.Texture.width : 0;
                            int th = _gpuCompositionCapture.Texture != null ? _gpuCompositionCapture.Texture.height : 0;
                            int cw = 0, ch = 0, compSt = 0;
                            try { compSt = UnderNetBrowserGpuNative.CompGetStatus(); } catch { }
                            try { cw = _gpuCompositionCapture.ViewWidth; ch = _gpuCompositionCapture.ViewHeight; } catch { } // configured composition/target size
                            var vrt = _videoDisplay != null ? _videoDisplay.rectTransform : null;
                            var vrect = vrt != null ? vrt.rect : default;
                            var vlossy = vrt != null ? vrt.lossyScale : Vector3.one;
                            var canvas = _videoDisplay != null ? _videoDisplay.canvas : null;
                            float canvasScale = canvas != null ? canvas.scaleFactor : 0f;
                            string diag = "";
                            try { diag = UnderNetBrowserGpuNative.GetCompDiagString() ?? ""; } catch { }
                            float texAspect = th > 0 ? (float)tw / th : 0f;
                            float rectAspect = vrect.height > 0.01f ? vrect.width / vrect.height : 0f;
                            string arTier = _adaptiveCurrentTier.ToString();
                            int arInterval = _adaptiveAppliedCaptureIntervalMs;
                            float arFps = arInterval > 0 ? (1000f / arInterval) : 0f;
                            UnderNetBrowserConfig.Log($"[UnderNetBrowser][VisualDiag] compStatus={compSt} unityTex={tw}x{th} compFrame={cw}x{ch} videoRect={vrect.width:F1}x{vrect.height:F1} rectAspect={rectAspect:F3} texAspect={texAspect:F3} videoLossy=({vlossy.x:F3},{vlossy.y:F3},{vlossy.z:F3}) canvasScale={canvasScale:F3} texFilter={(_gpuCompositionCapture.Texture!=null?_gpuCompositionCapture.Texture.filterMode.ToString():"n/a")} adaptive(tier={arTier} reason='{_adaptiveLastReason}' intervalMs={arInterval} fps={(arFps>0?arFps.ToString("0.#"):"full")} standby={_adaptiveAppliedStandby} dist={_adaptiveLastDistance:0.##} tvVisible={_adaptiveLastTvVisible}) diag='{diag}'");
                        }
                        catch { }
                    }

                    SyncAddressBarFromBrowser();
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] OnUpdate error (non-fatal): {ex.Message}");
            }
        }

        // Legacy non-composition browser startup removed: UnderNet runs in composition mode only.

        private static string NormalizeUrl(string? input)
        {
            var text = (input ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return "https://www.google.com";

            // Internal command routes (handled by app, not navigated as web URLs)
            if (text.StartsWith("undernet:cmd/", StringComparison.OrdinalIgnoreCase))
                return text;

            // Built-in routes
            if (text.Equals("undernet:home", StringComparison.OrdinalIgnoreCase) || text.Equals("about:home", StringComparison.OrdinalIgnoreCase))
                return "undernet:home";
            if (text.Equals("home:home", StringComparison.OrdinalIgnoreCase) || text.Equals("home:homepage", StringComparison.OrdinalIgnoreCase))
                return "undernet:home";
            if (text.Equals("undernet:games", StringComparison.OrdinalIgnoreCase) || text.Equals("home:games", StringComparison.OrdinalIgnoreCase))
                return "undernet:games";
            if (text.Equals("undernet:settings", StringComparison.OrdinalIgnoreCase) || text.Equals("home:settings", StringComparison.OrdinalIgnoreCase))
                return "undernet:settings";
            if (text.Equals("undernet:notepad", StringComparison.OrdinalIgnoreCase) || text.Equals("home:notepad", StringComparison.OrdinalIgnoreCase))
                return "undernet:notepad";
            if (text.Equals("undernet:ai", StringComparison.OrdinalIgnoreCase) || text.Equals("home:ai", StringComparison.OrdinalIgnoreCase))
                return "undernet:ai";
            if (text.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase) || text.Equals("home:discord", StringComparison.OrdinalIgnoreCase))
                return "undernet:discord";

            // Intercept-friendly route URLs used by built-in homepage shortcuts (custom schemes may not navigate reliably in WebView2).
            if (text.Equals("https://undernet.route/ai", StringComparison.OrdinalIgnoreCase) || text.Equals("http://undernet.route/ai", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://undernet.route/ai?", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://undernet.route/ai?", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://undernet.route/ai/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://undernet.route/ai/", StringComparison.OrdinalIgnoreCase))
                return "undernet:ai";
            if (text.Equals("https://undernet.route/discord", StringComparison.OrdinalIgnoreCase) || text.Equals("http://undernet.route/discord", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://undernet.route/discord?", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://undernet.route/discord?", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://undernet.route/discord/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://undernet.route/discord/", StringComparison.OrdinalIgnoreCase))
                return "undernet:discord";

            // App routes (simple local UI host): home:<id>, undernet:<id>, undernet:app/<id>
            if (TryParseLocalAppRoute(text, out var appId))
                return "home:" + appId;

            if (!text.Contains("://") && !text.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;
            return text;
        }

        private string ResolveNavigationTarget(string? input)
        {
            var normalized = NormalizeUrl(input);
            if (normalized.Equals("undernet:home", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepageUrl();
            if (normalized.Equals("undernet:games", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepagePageUrl("games.html");
            if (normalized.Equals("undernet:settings", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepagePageUrl("settings.html");
            if (normalized.Equals("undernet:notepad", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepagePageUrl("notepad.html");
            if (normalized.Equals("undernet:ai", StringComparison.OrdinalIgnoreCase))
                return "https://www.google.com/ai";

            if (normalized.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase))
                return "undernet:discord";

            // Local app UI routes -> file:///.../root/Apps/<id>/index.html
            if (TryParseLocalAppRoute(normalized, out var appId))
            {
                var appUrl = TryResolveLocalAppUrl("home:" + appId);
                if (!string.IsNullOrWhiteSpace(appUrl))
                    return appUrl;
                // App not found: fall back to homepage (simple, safe behavior)
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] App route not found: home:{appId}");
                return GetOrCreateHomepageUrl();
            }

            // Migrate previous default to new built-in homepage experience
            if (normalized.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase) && string.Equals(UnderNetBrowserConfig.HomeUrl?.Value?.Trim(), "undernet:home", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepageUrl();
            // Normalize direct Google AI URLs to built-in AI route display context by allowing navigation but we keep URL as-is here? no, target stays google
            return normalized;
        }

        private string GetResolvedHomeUrl()
        {
            var home = UnderNetBrowserConfig.HomeUrl?.Value;
            if (string.IsNullOrWhiteSpace(home)) home = "undernet:home";
            home = home.Trim();
            if (home.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase))
                home = "undernet:home"; // migrate old default
            return ResolveNavigationTarget(home);
        }

        private static bool _webAppsFoundationEnsured;
        private static string? _cachedHomepageFileUrl;
        private const int ManagedHomepageVersion = 5;
        private const string ManagedHomepageMarkerPrefix = "UnderNetBrowser:managed-homepage version=";

        private static string GetUserDataRoot()
        {
            try
            {
                var ud = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
                if (!string.IsNullOrWhiteSpace(ud))
                    return Path.Combine(ud, "UnderNetBrowser");
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "UnderNetBrowser");
        }

        private static string GetWebContentRoot() => Path.Combine(GetUserDataRoot(), "root");
        private static string GetHomepageDir() => Path.Combine(GetWebContentRoot(), "Homepage");
        private static string GetHomepageFilePath() => Path.Combine(GetHomepageDir(), "index.html");
        private static string GetAppsRootDir() => Path.Combine(GetWebContentRoot(), "Apps");
        private static string GetLegacyHomepageFilePath() => Path.Combine(GetUserDataRoot(), "Home", "home.html");

        private static bool IsSafeAppId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();
            if (id.Length == 0 || id.Length > 64) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        private static bool IsPathUnderRoot(string fullPath, string rootFullPath)
        {
            try
            {
                var p = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var r = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TryParseLocalAppRoute(string? input, out string appId)
        {
            appId = "";
            try
            {
                var s = (input ?? "").Trim();
                if (s.Length == 0) return false;

                string? candidate = null;
                if (s.StartsWith("home:", StringComparison.OrdinalIgnoreCase))
                    candidate = s.Substring("home:".Length);
                else if (s.StartsWith("undernet:app/", StringComparison.OrdinalIgnoreCase))
                    candidate = s.Substring("undernet:app/".Length);
                else if (s.StartsWith("undernet:", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = s.Substring("undernet:".Length);
                    if (!rest.Equals("home", StringComparison.OrdinalIgnoreCase))
                        candidate = rest;
                }

                if (candidate == null) return false;
                candidate = candidate.Trim().Trim('/', '\\');
                if (candidate.Length == 0) return false;
                // Disallow nested paths for now (simple app-id routing)
                if (candidate.IndexOf('/') >= 0 || candidate.IndexOf('\\') >= 0) return false;

                if (!IsSafeAppId(candidate)) return false;
                // Reserve homepage tokens / built-in routes
                if (candidate.Equals("home", StringComparison.OrdinalIgnoreCase) || candidate.Equals("homepage", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals("games", StringComparison.OrdinalIgnoreCase) || candidate.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals("notepad", StringComparison.OrdinalIgnoreCase) || candidate.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals("discord", StringComparison.OrdinalIgnoreCase))
                    return false;

                appId = candidate.ToLowerInvariant();
                return true;
            }
            catch { appId = ""; return false; }
        }

        private static string? TryGetAppIndexPath(string appId)
        {
            try
            {
                if (!IsSafeAppId(appId)) return null;
                var appsRoot = Path.GetFullPath(GetAppsRootDir());
                var appDir = Path.Combine(appsRoot, appId);
                var index = Path.Combine(appDir, "index.html");
                if (!IsPathUnderRoot(appDir, appsRoot)) return null;
                return File.Exists(index) ? index : null;
            }
            catch { return null; }
        }

        private static string? TryResolveLocalAppUrl(string? routeInput)
        {
            try
            {
                if (!TryParseLocalAppRoute(routeInput, out var appId)) return null;
                EnsureWebAppsFoundation();
                // Safety net: ensure built-in example app exists when requested.
                if (string.Equals(appId, "example", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var exampleDir = Path.Combine(GetAppsRootDir(), "example");
                        Directory.CreateDirectory(exampleDir);
                        var exampleIndex = Path.Combine(exampleDir, "index.html");
                        if (!File.Exists(exampleIndex))
                            File.WriteAllText(exampleIndex, BuildExampleAppHtml());
                    }
                    catch { }
                }
                var indexPath = TryGetAppIndexPath(appId);
                if (string.IsNullOrWhiteSpace(indexPath)) return null;
                return new Uri(indexPath).AbsoluteUri;
            }
            catch { return null; }
        }

        private static void EnsureWebAppsFoundation()
        {
            try
            {
                Directory.CreateDirectory(GetHomepageDir());
                Directory.CreateDirectory(GetAppsRootDir());

                var homepagePath = GetHomepageFilePath();
                var legacyPath = GetLegacyHomepageFilePath();

                // Migrate legacy homepage once if new homepage does not exist yet.
                if (!File.Exists(homepagePath) && File.Exists(legacyPath))
                {
                    try
                    {
                        File.Copy(legacyPath, homepagePath, false);
                        UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Migrated legacy homepage to root/Homepage/index.html.");
                    }
                    catch (Exception ex)
                    {
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Legacy homepage migration failed (will generate default): {ex.Message}");
                    }
                }

                // Ensure built-in homepage pages exist. Use bundled/managed-file logic so fresh installs and
                // managed updates come from the same source of truth (Homepage/*.html) instead of ad-hoc writes.
                try
                {
                    var homeDir = GetHomepageDir();
                    EnsureHomepageFileFromBundle(Path.Combine(homeDir, "index.html"), "index.html");
                    EnsureHomepageFileFromBundle(Path.Combine(homeDir, "games.html"), "games.html");
                    EnsureHomepageFileFromBundle(Path.Combine(homeDir, "settings.html"), "settings.html");
                    EnsureHomepageFileFromBundle(Path.Combine(homeDir, "notepad.html"), "notepad.html");
                }
                catch { }

                // Ensure built-in example app exists (simple plug-and-play reference).
                try
                {
                    var exampleDir = Path.Combine(GetAppsRootDir(), "example");
                    Directory.CreateDirectory(exampleDir);
                    var exampleIndex = Path.Combine(exampleDir, "index.html");
                    if (!File.Exists(exampleIndex))
                    {
                        File.WriteAllText(exampleIndex, BuildExampleAppHtml());
                        UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Created example app at root/Apps/example/index.html.");
                    }
                }
                catch { }

                _webAppsFoundationEnsured = true;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] EnsureWebAppsFoundation failed (non-fatal): {ex.Message}");
            }
        }

        private string GetOrCreateHomepageUrl()
        {
            try
            {
                EnsureWebAppsFoundation();
                var path = GetHomepageFilePath();

                // Fast path: once ensured this session and file exists, avoid repeated filesystem checks/allocations.
                if (_webAppsFoundationEnsured && !string.IsNullOrEmpty(_cachedHomepageFileUrl) && File.Exists(path))
                    return _cachedHomepageFileUrl;

                if (!File.Exists(path))
                {
                    try { Directory.CreateDirectory(GetHomepageDir()); EnsureHomepageFileFromBundle(path, "index.html"); } catch { }
                }

                // Ensure companion pages exist when homepage is resolved/created.
                try
                {
                    var homeDir = GetHomepageDir();
                    var gamesPath = Path.Combine(homeDir, "games.html");
                    if (!File.Exists(gamesPath)) { try { EnsureHomepageFileFromBundle(gamesPath, "games.html"); } catch { } }
                    var settingsPath = Path.Combine(homeDir, "settings.html");
                    if (!File.Exists(settingsPath)) { try { EnsureHomepageFileFromBundle(settingsPath, "settings.html"); } catch { } }
                    var notepadPath = Path.Combine(homeDir, "notepad.html");
                    if (!File.Exists(notepadPath)) { try { EnsureHomepageFileFromBundle(notepadPath, "notepad.html"); } catch { } }
                }
                catch { }

                _cachedHomepageFileUrl = new Uri(path).AbsoluteUri;
                return _cachedHomepageFileUrl;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Homepage creation failed, falling back to Google: {ex.Message}");
                return "https://www.google.com";
            }
        }

        private string GetOrCreateHomepagePageUrl(string fileName)
        {
            try
            {
                EnsureWebAppsFoundation();
                var homeDir = GetHomepageDir();
                try { Directory.CreateDirectory(homeDir); } catch { }
                var path = Path.Combine(homeDir, fileName);
                if (!File.Exists(path))
                {
                    try { EnsureHomepageFileFromBundle(path, fileName); } catch { }
                }
                // Best-effort ensure common companions exist too
                try
                {
                    var idx = GetHomepageFilePath();
                    if (!File.Exists(idx)) { try { EnsureHomepageFileFromBundle(idx, "index.html"); } catch { } }
                }
                catch { }
                return new Uri(path).AbsoluteUri;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Homepage page creation failed for '{fileName}': {ex.Message}");
                return GetOrCreateHomepageUrl();
            }
        }

        private static string? _cachedDefaultExtensionSourceDir;

        private static void EnsureDefaultBrowserExtensionDeployed(string compositionUserDataRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(compositionUserDataRoot)) return;
                // Deploy default browser extension (uBlock Origin Lite) automatically if missing.
                const string extensionFolderName = "uBlockOriginLite";
                var dst = Path.Combine(compositionUserDataRoot, "Extensions", extensionFolderName);
                if (File.Exists(Path.Combine(dst, "manifest.json"))) return; // fast path: already deployed

                // Resolve source (best-effort), cached per session to avoid repeated filesystem probing.
                string? sourceDir = _cachedDefaultExtensionSourceDir;
                if (!string.IsNullOrWhiteSpace(sourceDir) && !File.Exists(Path.Combine(sourceDir, "manifest.json")))
                    sourceDir = null;

                if (string.IsNullOrWhiteSpace(sourceDir))
                {
                    string? asmLoc = null;
                    try { asmLoc = typeof(UnderNetBrowserApp).Assembly.Location; } catch { asmLoc = null; }
                    var asmDir = string.IsNullOrWhiteSpace(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
                    var candidates = new List<string>(8);
                    void Add(string? dir, params string[] parts)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(dir)) return;
                            var p = dir;
                            for (int i = 0; i < parts.Length; i++) p = Path.Combine(p, parts[i]);
                            candidates.Add(p);
                        }
                        catch { }
                    }
                    Add(asmDir, "BrowserExtensions", extensionFolderName);
                    Add(asmDir, "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName);
                    try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "BrowserExtensions", extensionFolderName); } catch { }
                    try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName); } catch { }
                    try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "BrowserExtensions", extensionFolderName); } catch { }
                    try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName); } catch { }

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        try
                        {
                            var c = candidates[i];
                            if (!string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c, "manifest.json")))
                            {
                                sourceDir = c;
                                break;
                            }
                        }
                        catch { }
                    }
                    _cachedDefaultExtensionSourceDir = sourceDir;
                }

                if (string.IsNullOrWhiteSpace(sourceDir)) return;

                // Deploy only to the active WebView2 composition profile to avoid redundant duplicate copies in UserData.
                try
                {
                    if (!File.Exists(Path.Combine(dst, "manifest.json")))
                    {
                        CopyDirectoryRecursive(sourceDir, dst);
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Auto-deployed default browser extension to '{dst}'.");
                    }
                }
                catch { }
            }
            catch { }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            try { Directory.CreateDirectory(destDir); } catch { }
            try
            {
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    try
                    {
                        var name = Path.GetFileName(file);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        File.Copy(file, Path.Combine(destDir, name), false);
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (var dir in Directory.GetDirectories(sourceDir))
                {
                    try
                    {
                        var name = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        CopyDirectoryRecursive(dir, Path.Combine(destDir, name));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string? TryGetBundledHomepageSourcePath(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName)) return null;
                string? asmLoc = null;
                try { asmLoc = typeof(UnderNetBrowserApp).Assembly.Location; } catch { asmLoc = null; }
                var asmDir = string.IsNullOrWhiteSpace(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
                var candidates = new List<string>();
                void Add(string? dir, params string[] parts)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(dir)) return;
                        var p = dir;
                        for (int i = 0; i < parts.Length; i++) p = Path.Combine(p, parts[i]);
                        candidates.Add(p);
                    }
                    catch { }
                }
                Add(asmDir, "Homepage", fileName);
                Add(asmDir, "UnderNetBrowser", "Homepage", fileName);
                Add(asmDir, "FireskyUnderNetBrowser", "Homepage", fileName);
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "UnderNetBrowser", "Homepage", fileName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "FireskyUnderNetBrowser", "Homepage", fileName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "UnderNetBrowser", "Homepage", fileName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "FireskyUnderNetBrowser", "Homepage", fileName); } catch { }
                for (int i = 0; i < candidates.Count; i++)
                {
                    try { var c = candidates[i]; if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c; } catch { }
                }
            }
            catch { }
            return null;
        }

        private static string ReadBundledHomepageHtmlOrFallback(string fileName, string fallbackHtml)
        {
            // 1) Prefer embedded resource (bundled inside the DLL; no loose Homepage folder needed).
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string? resName = null;
                try
                {
                    var names = asm.GetManifestResourceNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i] != null && names[i].EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                        { resName = names[i]; break; }
                    }
                }
                catch { resName = null; }
                if (!string.IsNullOrWhiteSpace(resName))
                {
                    using (var s = asm.GetManifestResourceStream(resName))
                    {
                        if (s != null)
                        {
                            using (var r = new System.IO.StreamReader(s))
                                return r.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Failed reading embedded homepage '{fileName}': {ex.Message}"); } catch { }
            }

            // 2) Backward-compatible: loose file next to mod / in Mods / game root.
            try
            {
                var src = TryGetBundledHomepageSourcePath(fileName);
                if (!string.IsNullOrWhiteSpace(src) && File.Exists(src))
                    return File.ReadAllText(src);
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Failed reading bundled homepage file '{fileName}': {ex.Message}"); } catch { }
            }
            return fallbackHtml ?? "";
        }

        private static int TryGetManagedHomepageVersionFromContent(string? content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return -1;
                var idx = content.IndexOf(ManagedHomepageMarkerPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return -1;
                idx += ManagedHomepageMarkerPrefix.Length;
                int end = idx;
                while (end < content.Length && char.IsDigit(content[end])) end++;
                if (end <= idx) return -1;
                if (int.TryParse(content.Substring(idx, end - idx), out int v)) return v;
            }
            catch { }
            return -1;
        }

        private static void EnsureHomepageFileFromBundle(string targetPath, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath)) return;
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                string bundled = "";
                try { bundled = ReadBundledHomepageHtmlOrFallback(fileName, ""); } catch { bundled = ""; }

                if (!File.Exists(targetPath))
                {
                    if (!string.IsNullOrWhiteSpace(bundled))
                    {
                        try { File.WriteAllText(targetPath, bundled); return; } catch { }
                    }

                    // Last-resort minimal fallback (source files should ship with mod). Mark as managed so future updates can auto-replace.
                    string fallback = fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                        ? "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Home</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>UnderNet Home</h1><p>Homepage source file missing. Reinstall mod files or restore <code>Homepage/index.html</code>.</p><p><a href=\"https://www.google.com/ai\" style=\"color:#7cff6b\">AI</a> | <a href=\"games.html\" style=\"color:#7cff6b\">Games</a> | <a href=\"settings.html\" style=\"color:#7cff6b\">Settings</a> | <a href=\"notepad.html\" style=\"color:#7cff6b\">Notepad</a></p></body></html>"
                        : fileName.Equals("games.html", StringComparison.OrdinalIgnoreCase)
                        ? "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Games</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>Games</h1><p>Games page source file missing. Reinstall mod files or restore <code>Homepage/games.html</code>.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>"
                        : fileName.Equals("settings.html", StringComparison.OrdinalIgnoreCase)
                        ? "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Settings</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>Settings</h1><p>Settings page source file missing. Reinstall mod files or restore <code>Homepage/settings.html</code>.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>"
                        : fileName.Equals("notepad.html", StringComparison.OrdinalIgnoreCase)
                        ? "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Notepad</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>Notepad</h1><p>Notepad page source file missing. Reinstall mod files or restore <code>Homepage/notepad.html</code>.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>"
                        : "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Page</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>UnderNet</h1><p>Page source file missing.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>";
                    try { File.WriteAllText(targetPath, fallback); } catch { }
                    return;
                }

                // Auto-update only managed built-in pages (version marker present). Preserve unmarked/customized files.
                try
                {
                    string existing = File.ReadAllText(targetPath);
                    int existingVersion = TryGetManagedHomepageVersionFromContent(existing);
                    if (existingVersion >= 0 && existingVersion < ManagedHomepageVersion && !string.IsNullOrWhiteSpace(bundled))
                    {
                        File.WriteAllText(targetPath, bundled);
                        try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Updated managed homepage file '{fileName}' ({existingVersion} -> {ManagedHomepageVersion})."); } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        private static string BuildDefaultHomepageHtml() => ReadBundledHomepageHtmlOrFallback("index.html", "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Home</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>UnderNet Home</h1><p>Homepage source file missing. Reinstall mod files or restore <code>Homepage/index.html</code>.</p><p><a href=\"https://www.google.com/ai\" style=\"color:#7cff6b\">AI</a> | <a href=\"games.html\" style=\"color:#7cff6b\">Games</a> | <a href=\"settings.html\" style=\"color:#7cff6b\">Settings</a> | <a href=\"notepad.html\" style=\"color:#7cff6b\">Notepad</a></p></body></html>");

        private static string BuildGamesPageHtml() => ReadBundledHomepageHtmlOrFallback("games.html", "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Games</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>Games</h1><p>Games page source file missing. Reinstall mod files or restore <code>Homepage/games.html</code>.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>");

        private static string BuildSettingsPageHtml() => ReadBundledHomepageHtmlOrFallback("settings.html", "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><!-- UnderNetBrowser:managed-homepage version=5 --><title>UnderNet Settings</title></head><body style=\"font-family:Segoe UI;background:#0b120f;color:#eaf8f0;padding:20px\"><h1>Settings</h1><p>Settings page source file missing. Reinstall mod files or restore <code>Homepage/settings.html</code>.</p><p><a href=\"index.html\" style=\"color:#7cff6b\">Home</a></p></body></html>");

        private static string BuildExampleAppHtml()
        {
            // Simple built-in example app to demonstrate plug-and-play local UI hosting.
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
<title>UnderNet Example App</title>
<style>
  :root { --bg:#07110d; --panel:#0f1b16; --edge:#214033; --text:#e9f7ee; --muted:#9bb7aa; --accent:#7cff6b; }
  *{box-sizing:border-box} html,body{height:100%;margin:0}
  body{font-family:'Segoe UI',Tahoma,Arial,sans-serif;background:radial-gradient(800px 300px at 20% 0%,rgba(124,255,107,.12),transparent 60%),var(--bg);color:var(--text);padding:24px}
  .card{max-width:900px;margin:0 auto;background:linear-gradient(180deg,rgba(18,29,23,.96),rgba(11,17,14,.96));border:1px solid var(--edge);border-radius:14px;padding:20px;box-shadow:0 16px 40px rgba(0,0,0,.35)}
  h1{margin:0 0 8px;font-size:28px} .sub{margin:0 0 16px;color:var(--muted)}
  .grid{display:grid;grid-template-columns:1fr 1fr;gap:12px}
  .box{border:1px solid rgba(255,255,255,.08);border-radius:10px;padding:12px;background:rgba(255,255,255,.02)}
  h2{margin:0 0 8px;font-size:14px;letter-spacing:.8px;text-transform:uppercase;color:#dcecdf}
  p,li{color:var(--muted);line-height:1.45} code{color:#dfffe3}
  a.btn{display:inline-block;margin-top:10px;padding:8px 12px;border-radius:8px;border:1px solid rgba(124,255,107,.35);color:#eaffea;text-decoration:none;background:rgba(124,255,107,.08)}
  a.btn:hover{background:rgba(124,255,107,.14)}
  @media (max-width:760px){.grid{grid-template-columns:1fr}}
</style>
</head>
<body>
  <div class=""card"">
    <h1>Example UnderNet App</h1>
    <p class=""sub"">This is a simple local HTML page hosted inside UnderNet Browser. Use it as a template for mod/app UIs.</p>
    <div class=""grid"">
      <div class=""box"">
        <h2>How it works</h2>
        <ul>
          <li>Folder: <code>UserData/UnderNetBrowser/root/Apps/example/</code></li>
          <li>Entry file: <code>index.html</code></li>
          <li>Open with: <code>home:example</code></li>
        </ul>
        <a class=""btn"" href=""undernet:home"">Back to Home</a>
      </div>
      <div class=""box"">
        <h2>Make your own</h2>
        <p>Create <code>root/Apps/&lt;app-id&gt;/index.html</code>, then navigate to <code>home:&lt;app-id&gt;</code>.</p>
        <p>This is ideal for simple interfaces (dashboards, launchers, tools, mod UIs) built with HTML/CSS/JS.</p>
      </div>
    </div>
  </div>
</body>
</html>";
        }

        private static string UrlDecodeLoose(string? s)
        {
            try { return Uri.UnescapeDataString((s ?? string.Empty).Replace("+", "%20")); } catch { return s ?? string.Empty; }
        }

        private static Dictionary<string, string> ParseSimpleQuery(string? query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(query)) return dict;
                var q = query;
                if (q.StartsWith("?")) q = q.Substring(1);
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    var k = UrlDecodeLoose(kv.Length > 0 ? kv[0] : "");
                    var v = UrlDecodeLoose(kv.Length > 1 ? kv[1] : "");
                    if (!string.IsNullOrEmpty(k)) dict[k] = v;
                }
            }
            catch { }
            return dict;
        }

        private static bool TryApplyConfigCommand(string key, string value, out string message)
        {
            message = "Unknown setting.";
            try
            {
                bool Save()
                {
                    try { MelonPreferences.Save(); } catch { try { MelonLoader.MelonPreferences.Save(); } catch { } }
                    return true;
                }
                bool ParseBool(string v, out bool b)
                {
                    b = false;
                    if (bool.TryParse(v, out b)) return true;
                    if (v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) { b = true; return true; }
                    if (v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase)) { b = false; return true; }
                    return false;
                }
                bool ParseFloat(string v, out float f) => float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) || float.TryParse(v, out f);
                bool ParseInt(string v, out int i) => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out i) || int.TryParse(v, out i);

                switch ((key ?? "").Trim())
                {
                    case "HomeUrl":
                        if (UnderNetBrowserConfig.HomeUrl != null) { UnderNetBrowserConfig.HomeUrl.Value = string.IsNullOrWhiteSpace(value) ? "undernet:home" : value.Trim(); Save(); message = "Home URL updated."; return true; }
                        break;
                    case "KeyToggleCursor":
                        if (UnderNetBrowserConfig.KeyToggleCursor != null) { UnderNetBrowserConfig.KeyToggleCursor.Value = string.IsNullOrWhiteSpace(value) ? "F3" : value.Trim(); Save(); message = "Cursor toggle key updated."; return true; }
                        break;
                    case "KeyReload":
                        if (UnderNetBrowserConfig.KeyReload != null) { UnderNetBrowserConfig.KeyReload.Value = string.IsNullOrWhiteSpace(value) ? "F5" : value.Trim(); Save(); message = "Reload key updated."; return true; }
                        break;
                    case "KeyGameInputMode":
                        if (UnderNetBrowserConfig.KeyGameInputMode != null) { UnderNetBrowserConfig.KeyGameInputMode.Value = string.IsNullOrWhiteSpace(value) ? "F6" : value.Trim(); Save(); message = "Game Input Mode key updated."; return true; }
                        break;
                    case "BasicAdBlockEnabled":
                        if (UnderNetBrowserConfig.BasicAdBlockEnabled != null && ParseBool(value, out var b1)) { UnderNetBrowserConfig.BasicAdBlockEnabled.Value = b1; Save(); message = "Basic ad/tracker blocking updated."; return true; }
                        break;
                    case "AutoConsentEnabled":
                        if (UnderNetBrowserConfig.AutoConsentEnabled != null && ParseBool(value, out var b2)) { UnderNetBrowserConfig.AutoConsentEnabled.Value = b2; Save(); message = "Auto-consent setting updated."; return true; }
                        break;
                    case "DistanceVolumeEnabled":
                        if (UnderNetBrowserConfig.DistanceVolumeEnabled != null && ParseBool(value, out var b3)) { UnderNetBrowserConfig.DistanceVolumeEnabled.Value = b3; Save(); message = "Distance volume setting updated."; return true; }
                        break;
                    case "DebugLogging":
                        if (UnderNetBrowserConfig.DebugLogging != null && ParseBool(value, out var b4)) { UnderNetBrowserConfig.DebugLogging.Value = b4; Save(); message = "Verbose debug logging updated."; return true; }
                        break;
                    case "ChromeHeightFraction":
                        if (UnderNetBrowserConfig.ChromeHeightFraction != null && ParseFloat(value, out var f1)) { UnderNetBrowserConfig.ChromeHeightFraction.Value = f1; Save(); message = "Chrome height updated."; return true; }
                        break;
                    case "GpuCompositionRenderWidth":
                        if (UnderNetBrowserConfig.GpuCompositionRenderWidth != null && ParseInt(value, out var i1)) { UnderNetBrowserConfig.GpuCompositionRenderWidth.Value = Math.Max(640, i1); Save(); message = "Render width updated (restart/reopen may be required)."; return true; }
                        break;
                    case "GpuCompositionRenderHeight":
                        if (UnderNetBrowserConfig.GpuCompositionRenderHeight != null && ParseInt(value, out var i2)) { UnderNetBrowserConfig.GpuCompositionRenderHeight.Value = Math.Max(360, i2); Save(); message = "Render height updated (restart/reopen may be required)."; return true; }
                        break;
                }
            }
            catch (Exception ex) { message = "Failed: " + ex.Message; return false; }
            return false;
        }

        private bool TryHandleUnderNetCommandUrl(string? rawInput, bool navigateBackToSettings = true)
        {
            try
            {
                var raw = (rawInput ?? string.Empty).Trim();
                if (raw.Length == 0) return false;
                if (!raw.StartsWith("undernet:cmd/", StringComparison.OrdinalIgnoreCase)) return false;
                var rest = raw.Substring("undernet:cmd/".Length);
                var qIdx = rest.IndexOf('?');
                var action = (qIdx >= 0 ? rest.Substring(0, qIdx) : rest).Trim().Trim('/').ToLowerInvariant();
                var query = qIdx >= 0 ? rest.Substring(qIdx) : "";
                var args = ParseSimpleQuery(query);
                string status = "err";
                string msg = "Unsupported command.";
                if (action == "set")
                {
                    var key = args.TryGetValue("key", out var k) ? k : (args.TryGetValue("name", out var n) ? n : "");
                    var value = args.TryGetValue("value", out var v) ? v : "";
                    var ok = TryApplyConfigCommand(key, value, out msg);
                    status = ok ? "ok" : "err";
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Settings cmd set key='{key}' ok={ok} msg='{msg}'");
                }
                else if (action == "save")
                {
                    try { MelonPreferences.Save(); } catch { }
                    status = "ok"; msg = "Preferences saved.";
                }
                else if (action == "load" || action == "refresh" || action == "getsettings")
                {
                    status = "ok"; msg = "Current settings loaded.";
                }

                if (navigateBackToSettings)
                {
                    var target = GetOrCreateHomepageUrl();
                    try
                    {
                        var settingsPath = Path.Combine(GetHomepageDir(), "settings.html");
                        if (File.Exists(settingsPath)) target = new Uri(settingsPath).AbsoluteUri;
                    }
                    catch { }
                    try { target += (target.Contains("?") ? "&" : "?") + "cfg_status=" + Uri.EscapeDataString(status) + "&cfg_msg=" + Uri.EscapeDataString(msg ?? ""); } catch { }
                    try { _gpuCompositionCapture?.Navigate(target); } catch { }
                    try { if (_urlInput != null) { _urlInput.text = "undernet:settings"; if (_urlInput.textComponent != null) _urlInput.textComponent.text = "undernet:settings"; _urlInput.ForceLabelUpdate(); } } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        private void SetUrl(string? input)
        {
            var raw = (input ?? string.Empty).Trim();
            if (TryHandleUnderNetCommandUrl(raw, navigateBackToSettings: true)) return;
            if (raw.StartsWith("undernet:cmd/", StringComparison.OrdinalIgnoreCase)) return;
            var normalizedRaw = NormalizeUrl(raw);
            var url = ResolveNavigationTarget(raw);

            // External window mirror (Discord): mirror-only (no web redirect/fallback).
            if (IsDiscordMirrorRequestUrl(raw) || IsDiscordMirrorRequestUrl(normalizedRaw) || IsDiscordMirrorRequestUrl(url))
            {
                if (!TryStartDiscordMirror(url))
                {
                    SetHint("Discord mirror unavailable (is Discord running?).");
                    try { SetUrlDisplayText("undernet:discord"); } catch { }
                }
                return;
            }
            else if (_externalMirrorMode)
            {
                // Leaving mirror mode to browse normally
                StopExternalWindowMirror("navigate_away");
                EnsureCompositionBrowserStarted(url);
            }

            // Keep address bar friendly for home token
            if (_urlInput != null)
            {
                if (raw.Equals("undernet:home", StringComparison.OrdinalIgnoreCase) || raw.Equals("about:home", StringComparison.OrdinalIgnoreCase))
                    _urlInput.text = "undernet:home";
                else if (!(IsDiscordMirrorRequestUrl(raw) || IsDiscordMirrorRequestUrl(normalizedRaw)))
                    _urlInput.text = raw.Length == 0 ? "undernet:home" : NormalizeUrl(raw);
            }

            if (_gpuCompositionCapture != null)
            {
                try { ReleaseAllGameKeys("navigate"); } catch { }
                _gpuCompositionCapture.Navigate(url);
                SetHint("");
                MelonLogger.Msg($"[Firesky UnderNet Browser] [UnderNetBrowser] Navigate (composition): {TruncateUrlForLog(url)}");
            }
            else
            {
                if (_externalMirrorMode) return;
                SetHint("Composition browser not available.");
            }
        }

        private void HardResetCompositionNavigate(string targetUrl, string reason)
        {
            if (_browserUiCriticalActionInProgress)
            {
                try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Hard reset ignored ({reason}): another browser UI action is in progress."); } catch { }
                return;
            }
            _browserUiCriticalActionInProgress = true;
            try
            {
                var url = string.IsNullOrWhiteSpace(targetUrl) ? GetResolvedHomeUrl() : targetUrl;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [UnderNetBrowser] Hard reset navigate ({reason}): {TruncateUrlForLog(url)}");

                // Recovery path: avoid sending script/navigation commands into a potentially hung WebView before teardown,
                // as they can block on native synchronization and freeze the game. Dispose performs native shutdown/cleanup.
                try { SetHint("Restarting browser..."); } catch { }
                try { _compositionWebFocused = false; _browserPointerDown = false; _browserPointerRightDown = false; } catch { }
                try { ReleaseAllGameKeys("hard_reset:" + reason); } catch { }
                try { UnderNetBrowserInputHooks.UpdateOwnership(IsOpen && !_detachedToGameplay, false, false, false); } catch { }

                var oldCapture = _gpuCompositionCapture;
                _gpuCompositionCapture = null;
                _gpuCompositionMode = false;
                try { oldCapture?.SendMouseUp(0, 0, false); } catch { }
                try { oldCapture?.SendMouseUp(0, 0, true); } catch { }
                try { oldCapture?.Dispose(); } catch (Exception ex) { try { MelonLogger.Warning($"[Firesky UnderNet Browser] [UnderNetBrowser] Hard reset dispose failed: {ex.Message}"); } catch { } }

                // Best-effort extra shutdown in case dispose could not complete native cleanup cleanly.
                try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }

                GetDesiredCompositionRenderSize(out int compW, out int compH);
                string compUserData = "";
                try { compUserData = System.IO.Path.Combine(GetUserDataRoot(), "WebView2Composition"); } catch { compUserData = ""; }
                try { EnsureDefaultBrowserExtensionDeployed(compUserData); } catch { }
                try { UnderNetBrowserDiagnostics.OnBrowserLoadingBegin(); UnderNetBrowserDiagnostics.OnWebViewInitStart(); } catch { }

                var newCapture = new UnderNetBrowserCompositionCapture();
                bool started = false;
                try { started = newCapture.TryStart(url, compW, compH, compUserData); }
                catch (Exception ex) { try { MelonLogger.Warning($"[Firesky UnderNet Browser] [UnderNetBrowser] Hard reset TryStart exception: {ex.Message}"); } catch { } started = false; }

                if (started)
                {
                    _gpuCompositionCapture = newCapture;
                    _gpuCompositionMode = true;
                    try { UnderNetBrowserDiagnostics.OnWebViewInitDone(); } catch { }
                    try { SetHint("Reloading browser..."); } catch { }
                    _compositionWebFocused = true;
                    try { if (_videoDisplay != null && _gpuCompositionCapture.Texture != null) _videoDisplay.texture = _gpuCompositionCapture.Texture; } catch { }
                }
                else
                {
                    try { SetHint("Composition browser restart failed. Check MelonLoader log / WebView2 runtime and native renderer."); } catch { }
                    try { newCapture.Dispose(); } catch { }
                    _gpuCompositionCapture = null;
                    _gpuCompositionMode = false;
                }
            }
            catch (Exception ex)
            {
                try { SetHint($"Browser reset failed: {ex.Message}"); } catch { }
                try { MelonLogger.Warning($"[Firesky UnderNet Browser] [UnderNetBrowser] HardResetCompositionNavigate failed: {ex.Message}"); } catch { }
            }
            finally
            {
                _browserUiCriticalActionInProgress = false;
            }
        }

        private void SetGameInputMode(bool enabled, string reason)
        {
            if (_gameInputMode == enabled)
            {
                try { if (_gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(_gameInputMode); } catch { }
                return;
            }
            _gameInputMode = enabled;
            if (!enabled)
            {
                ReleaseAllGameKeys("mode_off:" + reason);
            }
            else
            {
                // Smooth enable: if URL bar is focused, move focus to web view automatically instead of requiring a manual click.
                try
                {
                    if (_urlInput != null && _urlInput.isFocused)
                    {
                        _gameInputPrevUrlFocused = true;
                        try { _urlInput.DeactivateInputField(); } catch { }
                    }
                }
                catch { }
                try { if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null); } catch { }
                _compositionWebFocused = true;
                try { _gpuCompositionCapture?.RequestFocus(); } catch { }
                // Native click nudge at center (best-effort activation/focus for WebGPU/canvas pages; more "real" than JS-only events).
                try
                {
                    if (_gpuCompositionCapture != null)
                    {
                        int cx = Math.Max(1, _gpuCompositionCapture.ViewWidth / 2);
                        int cy = Math.Max(1, _gpuCompositionCapture.ViewHeight / 2);
                        _gpuCompositionCapture.SendMouseMove(cx, cy, false, false);
                        _gpuCompositionCapture.SendMouseDown(cx, cy, false);
                        _gpuCompositionCapture.SendMouseUp(cx, cy, false);
                    }
                }
                catch { }
                // Best-effort: focus canvas/document for browser games when enabling game input (retry briefly for async pages).
                // Includes click nudge for canvas/WebGPU games that require user-activation-like focus behavior (best-effort).
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var d=document; function pick(){try{var ae=d.activeElement; if(ae&&(ae.tagName==='INPUT'||ae.tagName==='TEXTAREA'||ae.isContentEditable)){try{ae.blur&&ae.blur();}catch(_){}}}catch(_){} var c=null; try{c=d.querySelector('canvas');}catch(_){} if(!c){try{c=d.querySelector('video, [tabindex], iframe, embed, object, body, html');}catch(_){}} return c;} function f(){try{try{window&&window.focus&&window.focus();}catch(_){} try{(d.body||d.documentElement)&&((d.body||d.documentElement).focus&&((d.body||d.documentElement).focus()));}catch(_){} var c=pick(); if(c){try{if(c.tabIndex==null||c.tabIndex<0)c.tabIndex=0;}catch(_){} try{c.focus&&c.focus({preventScroll:true});}catch(_){try{c.focus&&c.focus();}catch(__){}} try{c.click&&c.click();}catch(_){} try{var r=c.getBoundingClientRect?c.getBoundingClientRect():null; var x=r?Math.floor(r.left+r.width/2):Math.floor((window.innerWidth||800)/2); var y=r?Math.floor(r.top+r.height/2):Math.floor((window.innerHeight||600)/2); var opts={bubbles:true,cancelable:true,composed:true,clientX:x,clientY:y,button:0,buttons:1}; c.dispatchEvent(new PointerEvent('pointerdown',opts)); c.dispatchEvent(new MouseEvent('mousedown',opts)); c.dispatchEvent(new PointerEvent('pointerup',{bubbles:true,cancelable:true,composed:true,clientX:x,clientY:y,button:0,buttons:0})); c.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,cancelable:true,composed:true,clientX:x,clientY:y,button:0,buttons:0})); c.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true,composed:true,clientX:x,clientY:y,button:0,buttons:0}));}catch(_){} }}catch(_){}} f(); setTimeout(f,50); setTimeout(f,150); setTimeout(f,350); setTimeout(f,700); setTimeout(f,1200);}catch(_){}})();"); } catch { }
                try { _lastGameModeFocusNudge = Time.realtimeSinceStartup; } catch { }
                try { UnderNetBrowserInputHooks.SetGameInputModeActive(_gameInputMode); } catch { }
                try { UpdateInputOwnershipState(); } catch { }
            }
            try { UnderNetBrowserInputHooks.SetGameInputModeActive(_gameInputMode); } catch { }
            try { if (_gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(_gameInputMode); } catch { }
            try
            {
                // Keep hint area unobtrusive in game mode (chrome indicator is primary); do not force big overlay text.
                if (!_gameInputMode && _hintText != null && !string.IsNullOrEmpty(_hintBaseMessage) && _hintBaseMessage.StartsWith("Game Input ON", StringComparison.Ordinal))
                {
                    _hintBaseMessage = "";
                    _hintText.enabled = false;
                }
            }
            catch { }
            UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] GameInputMode={( _gameInputMode ? "ON" : "OFF" )} reason={reason}");
        }

        private void ReleaseAllGameKeys(string reason)
        {
            try
            {
                if (_gpuCompositionCapture == null || _gameKeysHeld.Count == 0) { _gameKeysHeld.Clear(); return; }
                const uint WM_KEYUP = 0x0101;
                foreach (var vk in _gameKeysHeld)
                {
                    try { _gpuCompositionCapture.SendKey(WM_KEYUP, (ulong)vk, MakeKeyLParam(vk, true)); } catch { }
                    try { TrySendGameInputDomKeyUpByVk(vk); } catch { }
                }
                _gameKeysHeld.Clear();
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Released all game keys ({reason}).");
            }
            catch { _gameKeysHeld.Clear(); }
        }

        private void TrySendGameInputDomKeyUpByVk(int vk)
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                if (!TryGetJsKeyEventPropsFromVk(vk, out var jsKey, out var jsCode, out var keyCode, out var location)) return;
                // Lightweight DOM event for game input compatibility (no DOM queries/focus work).
                string script =
                    "try{var e=new KeyboardEvent('keyup',{key:'" + EscapeJsString(jsKey) + "',code:'" + EscapeJsString(jsCode) + "',keyCode:" + keyCode + ",which:" + keyCode + ",location:" + location + ",bubbles:true,cancelable:true,repeat:false});window.dispatchEvent(e);document.dispatchEvent(e);}catch(_e){}";
                _gpuCompositionCapture.ExecuteScript(script);
            }
            catch { }
        }

        private static bool TryGetJsKeyEventPropsFromVk(int vk, out string key, out string code, out int keyCode, out int location)
        {
            key = ""; code = ""; keyCode = vk; location = 0;
            if (vk >= 0x41 && vk <= 0x5A) { char c = (char)vk; code = "Key" + c; key = ((char)(c - 'A' + 'a')).ToString(); keyCode = vk; return true; }
            if (vk >= 0x30 && vk <= 0x39) { char c = (char)vk; code = "Digit" + c; key = c.ToString(); keyCode = vk; return true; }
            if (vk >= 0x60 && vk <= 0x69) { int d = vk - 0x60; code = "Numpad" + d; key = d.ToString(); keyCode = vk; location = 3; return true; }
            if (vk >= 0x70 && vk <= 0x7B) { int n = vk - 0x70 + 1; code = "F" + n; key = "F" + n; keyCode = vk; return true; }
            switch (vk)
            {
                case 0x25: key = "ArrowLeft"; code = "ArrowLeft"; keyCode = 37; return true;
                case 0x26: key = "ArrowUp"; code = "ArrowUp"; keyCode = 38; return true;
                case 0x27: key = "ArrowRight"; code = "ArrowRight"; keyCode = 39; return true;
                case 0x28: key = "ArrowDown"; code = "ArrowDown"; keyCode = 40; return true;
                case 0x20: key = " "; code = "Space"; keyCode = 32; return true;
                case 0x0D: key = "Enter"; code = "Enter"; keyCode = 13; return true;
                case 0x09: key = "Tab"; code = "Tab"; keyCode = 9; return true;
                case 0x1B: key = "Escape"; code = "Escape"; keyCode = 27; return true;
                case 0x08: key = "Backspace"; code = "Backspace"; keyCode = 8; return true;
                case 0x2E: key = "Delete"; code = "Delete"; keyCode = 46; return true;
                case 0x2D: key = "Insert"; code = "Insert"; keyCode = 45; return true;
                case 0x24: key = "Home"; code = "Home"; keyCode = 36; return true;
                case 0x23: key = "End"; code = "End"; keyCode = 35; return true;
                case 0x21: key = "PageUp"; code = "PageUp"; keyCode = 33; return true;
                case 0x22: key = "PageDown"; code = "PageDown"; keyCode = 34; return true;
                case 0xA0: key = "Shift"; code = "ShiftLeft"; keyCode = 16; location = 1; return true;
                case 0xA1: key = "Shift"; code = "ShiftRight"; keyCode = 16; location = 2; return true;
                case 0xA2: key = "Control"; code = "ControlLeft"; keyCode = 17; location = 1; return true;
                case 0xA3: key = "Control"; code = "ControlRight"; keyCode = 17; location = 2; return true;
                case 0xA4: key = "Alt"; code = "AltLeft"; keyCode = 18; location = 1; return true;
                case 0xA5: key = "Alt"; code = "AltRight"; keyCode = 18; location = 2; return true;
                case 0x14: key = "CapsLock"; code = "CapsLock"; keyCode = 20; return true;
                case 0xDB: key = "["; code = "BracketLeft"; keyCode = 219; return true;
                case 0xDD: key = "]"; code = "BracketRight"; keyCode = 221; return true;
                case 0xDC: key = "\\\\"; code = "Backslash"; keyCode = 220; return true;
                case 0xBA: key = ";"; code = "Semicolon"; keyCode = 186; return true;
                case 0xDE: key = "'"; code = "Quote"; keyCode = 222; return true;
                case 0xBC: key = ","; code = "Comma"; keyCode = 188; return true;
                case 0xBE: key = "."; code = "Period"; keyCode = 190; return true;
                case 0xBF: key = "/"; code = "Slash"; keyCode = 191; return true;
                case 0xC0: key = "`"; code = "Backquote"; keyCode = 192; return true;
                case 0xBD: key = "-"; code = "Minus"; keyCode = 189; return true;
                case 0xBB: key = "="; code = "Equal"; keyCode = 187; return true;
                default: return false;
            }
        }

        private static bool TryGetGameInputVk(KeyCode kc, out int vk)
        {
            vk = 0;
            // Letters
            if (kc >= KeyCode.A && kc <= KeyCode.Z) { vk = 0x41 + (kc - KeyCode.A); return true; }
            // Digits
            if (kc >= KeyCode.Alpha0 && kc <= KeyCode.Alpha9) { vk = 0x30 + (kc - KeyCode.Alpha0); return true; }
            if (kc >= KeyCode.Keypad0 && kc <= KeyCode.Keypad9) { vk = 0x60 + (kc - KeyCode.Keypad0); return true; }
            // Function keys
            if (kc >= KeyCode.F1 && kc <= KeyCode.F12)
            {
                int idx = kc - KeyCode.F1;
                // Exclude app-reserved/configured keys (cursor toggle, reload, game mode toggle) from game-input forwarding.
                try
                {
                    var kCursor = UnderNetBrowserConfig.GetKey(KeyCode.F3, UnderNetBrowserConfig.KeyToggleCursor);
                    var kReload = UnderNetBrowserConfig.GetKey(KeyCode.F5, UnderNetBrowserConfig.KeyReload);
                    var kGame = UnderNetBrowserConfig.GetKey(KeyCode.F6, UnderNetBrowserConfig.KeyGameInputMode);
                    if (kc == kCursor || kc == kReload || kc == kGame) return false;
                }
                catch
                {
                    if (kc == KeyCode.F3 || kc == KeyCode.F5 || kc == KeyCode.F6) return false;
                }
                vk = 0x70 + idx;
                return true;
            }
            switch (kc)
            {
                case KeyCode.LeftArrow: vk = 0x25; return true;
                case KeyCode.UpArrow: vk = 0x26; return true;
                case KeyCode.RightArrow: vk = 0x27; return true;
                case KeyCode.DownArrow: vk = 0x28; return true;
                case KeyCode.Space: vk = 0x20; return true;
                case KeyCode.Return: vk = 0x0D; return true;
                case KeyCode.KeypadEnter: vk = 0x0D; return true;
                case KeyCode.Tab: vk = 0x09; return true;
                case KeyCode.Escape: vk = 0x1B; return true;
                case KeyCode.Backspace: vk = 0x08; return true;
                case KeyCode.Delete: vk = 0x2E; return true;
                case KeyCode.Insert: vk = 0x2D; return true;
                case KeyCode.Home: vk = 0x24; return true;
                case KeyCode.End: vk = 0x23; return true;
                case KeyCode.PageUp: vk = 0x21; return true;
                case KeyCode.PageDown: vk = 0x22; return true;
                case KeyCode.LeftShift: vk = 0xA0; return true;
                case KeyCode.RightShift: vk = 0xA1; return true;
                case KeyCode.LeftControl: vk = 0xA2; return true;
                case KeyCode.RightControl: vk = 0xA3; return true;
                case KeyCode.LeftAlt: vk = 0xA4; return true;
                case KeyCode.RightAlt: vk = 0xA5; return true;
                case KeyCode.CapsLock: vk = 0x14; return true;
                case KeyCode.LeftBracket: vk = 0xDB; return true;
                case KeyCode.RightBracket: vk = 0xDD; return true;
                case KeyCode.Backslash: vk = 0xDC; return true;
                case KeyCode.Semicolon: vk = 0xBA; return true;
                case KeyCode.Quote: vk = 0xDE; return true;
                case KeyCode.Comma: vk = 0xBC; return true;
                case KeyCode.Period: vk = 0xBE; return true;
                case KeyCode.Slash: vk = 0xBF; return true;
                case KeyCode.BackQuote: vk = 0xC0; return true;
                case KeyCode.Minus: vk = 0xBD; return true;
                case KeyCode.Equals: vk = 0xBB; return true;
                default: return false;
            }
        }

        // Keys tracked for down/up transitions in Game Input Mode (common gameplay + modifiers + function/navigation keys).
        private static readonly KeyCode[] s_gameInputTrackedKeys = new KeyCode[]
        {
            KeyCode.A,KeyCode.B,KeyCode.C,KeyCode.D,KeyCode.E,KeyCode.F,KeyCode.G,KeyCode.H,KeyCode.I,KeyCode.J,KeyCode.K,KeyCode.L,KeyCode.M,
            KeyCode.N,KeyCode.O,KeyCode.P,KeyCode.Q,KeyCode.R,KeyCode.S,KeyCode.T,KeyCode.U,KeyCode.V,KeyCode.W,KeyCode.X,KeyCode.Y,KeyCode.Z,
            KeyCode.Alpha0,KeyCode.Alpha1,KeyCode.Alpha2,KeyCode.Alpha3,KeyCode.Alpha4,KeyCode.Alpha5,KeyCode.Alpha6,KeyCode.Alpha7,KeyCode.Alpha8,KeyCode.Alpha9,
            KeyCode.Keypad0,KeyCode.Keypad1,KeyCode.Keypad2,KeyCode.Keypad3,KeyCode.Keypad4,KeyCode.Keypad5,KeyCode.Keypad6,KeyCode.Keypad7,KeyCode.Keypad8,KeyCode.Keypad9,
            KeyCode.LeftArrow,KeyCode.RightArrow,KeyCode.UpArrow,KeyCode.DownArrow,
            KeyCode.Space,KeyCode.LeftShift,KeyCode.RightShift,KeyCode.LeftControl,KeyCode.RightControl,KeyCode.LeftAlt,KeyCode.RightAlt,
            KeyCode.Return,KeyCode.KeypadEnter,KeyCode.Tab,KeyCode.Escape,KeyCode.Backspace,KeyCode.Delete,KeyCode.Insert,KeyCode.Home,KeyCode.End,KeyCode.PageUp,KeyCode.PageDown,KeyCode.CapsLock,
            KeyCode.LeftBracket,KeyCode.RightBracket,KeyCode.Backslash,KeyCode.Semicolon,KeyCode.Quote,KeyCode.Comma,KeyCode.Period,KeyCode.Slash,KeyCode.BackQuote,KeyCode.Minus,KeyCode.Equals,
            KeyCode.F1,KeyCode.F2,KeyCode.F4,KeyCode.F7,KeyCode.F8,KeyCode.F9,KeyCode.F10,KeyCode.F11,KeyCode.F12
        };

        private void UpdateGameInputModeTransitions(bool webFocused, bool urlFocused)
        {
            try
            {
                // URL bar typing must remain separate from game input.
                if (urlFocused)
                {
                    if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys("url_focused");
                    if (_gameInputMode) SetGameInputMode(false, "url_focused");
                    _gameInputPrevUrlFocused = true;
                    return;
                }
                _gameInputPrevUrlFocused = false;

                if (!_gameInputMode || !webFocused || _gpuCompositionCapture == null)
                {
                    if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys(webFocused ? "inactive" : "web_unfocused");
                    return;
                }

                // Forward real key down/up transitions to WebView2 via native Win32 messages.
                // Avoid per-frame/per-key ExecuteScript DOM injection and periodic focus/script nudges here:
                // they add overhead on the WebView2/page thread and can cause lag, delays, and audio issues in games.
                const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
                foreach (var kc in s_gameInputTrackedKeys)
                {
                    if (!TryGetGameInputVk(kc, out var vk)) continue;
                    bool down = Input.GetKey(kc);
                    bool wasHeld = _gameKeysHeld.Contains(vk);
                    if (down && !wasHeld)
                    {
                        _gameKeysHeld.Add(vk);
                        try { _gpuCompositionCapture.SendKey(WM_KEYDOWN, (ulong)vk, MakeKeyLParam(vk, false)); } catch { }
                        TrySendGameInputDomKeyEvent(kc, vk, true);
                    }
                    else if (!down && wasHeld)
                    {
                        _gameKeysHeld.Remove(vk);
                        try { _gpuCompositionCapture.SendKey(WM_KEYUP, (ulong)vk, MakeKeyLParam(vk, true)); } catch { }
                        TrySendGameInputDomKeyEvent(kc, vk, false);
                    }
                }
            }
            catch { }
        }

        private void TrySendGameInputDomKeyEvent(KeyCode kc, int vk, bool down)
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                if (!TryGetJsKeyEventProps(kc, vk, out var jsKey, out var jsCode, out var keyCode, out var location)) return;
                string type = down ? "keydown" : "keyup";
                // Lightweight DOM KeyboardEvent for browser-game compatibility (sent on transitions only, no DOM queries/focus work).
                string script =
                    "try{var e=new KeyboardEvent('" + type + "',{key:'" + EscapeJsString(jsKey) + "',code:'" + EscapeJsString(jsCode) + "',keyCode:" + keyCode + ",which:" + keyCode + ",location:" + location + ",bubbles:true,cancelable:true,repeat:false});window.dispatchEvent(e);document.dispatchEvent(e);}catch(_e){}";
                _gpuCompositionCapture.ExecuteScript(script);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private static ulong MakeKeyLParam(int vk, bool keyUp)
        {
            try
            {
                uint scan = MapVirtualKey((uint)vk, 0) & 0xFF;
                bool extended = vk == 0xA3 || vk == 0xA5 || // RCtrl, RAlt
                                vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 || // Ins, Del, Home, End
                                vk == 0x21 || vk == 0x22 || // PgUp, PgDn
                                (vk >= 0x25 && vk <= 0x28) || // arrows
                                vk == 0x6F; // numpad divide
                ulong lp = 1UL | ((ulong)scan << 16);
                if (extended) lp |= 1UL << 24;
                if (keyUp) lp |= (1UL << 30) | (1UL << 31);
                return lp;
            }
            catch { return keyUp ? 0xC0000001UL : 0x00000001UL; }
        }

        private static bool TryGetJsKeyEventProps(KeyCode kc, int vk, out string key, out string code, out int keyCode, out int location)
        {
            key = ""; code = ""; keyCode = vk; location = 0;
            // Letters
            if (kc >= KeyCode.A && kc <= KeyCode.Z)
            {
                char c = (char)('A' + (kc - KeyCode.A));
                code = "Key" + c;
                key = ((char)('a' + (kc - KeyCode.A))).ToString();
                keyCode = 'A' + (kc - KeyCode.A);
                return true;
            }
            // Digits
            if (kc >= KeyCode.Alpha0 && kc <= KeyCode.Alpha9)
            {
                int d = kc - KeyCode.Alpha0;
                code = "Digit" + d;
                key = d.ToString();
                keyCode = '0' + d;
                return true;
            }
            if (kc >= KeyCode.Keypad0 && kc <= KeyCode.Keypad9)
            {
                int d = kc - KeyCode.Keypad0;
                code = "Numpad" + d;
                key = d.ToString();
                keyCode = 96 + d; // VK_NUMPAD0+
                location = 3;
                return true;
            }
            if (kc >= KeyCode.F1 && kc <= KeyCode.F12)
            {
                int n = 1 + (kc - KeyCode.F1);
                code = "F" + n; key = "F" + n; keyCode = 0x70 + (n - 1);
                return true;
            }
            switch (kc)
            {
                case KeyCode.LeftArrow: key = "ArrowLeft"; code = "ArrowLeft"; keyCode = 37; return true;
                case KeyCode.UpArrow: key = "ArrowUp"; code = "ArrowUp"; keyCode = 38; return true;
                case KeyCode.RightArrow: key = "ArrowRight"; code = "ArrowRight"; keyCode = 39; return true;
                case KeyCode.DownArrow: key = "ArrowDown"; code = "ArrowDown"; keyCode = 40; return true;
                case KeyCode.Space: key = " "; code = "Space"; keyCode = 32; return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: key = "Enter"; code = kc == KeyCode.KeypadEnter ? "NumpadEnter" : "Enter"; keyCode = 13; location = kc == KeyCode.KeypadEnter ? 3 : 0; return true;
                case KeyCode.Tab: key = "Tab"; code = "Tab"; keyCode = 9; return true;
                case KeyCode.Escape: key = "Escape"; code = "Escape"; keyCode = 27; return true;
                case KeyCode.Backspace: key = "Backspace"; code = "Backspace"; keyCode = 8; return true;
                case KeyCode.Delete: key = "Delete"; code = "Delete"; keyCode = 46; return true;
                case KeyCode.Insert: key = "Insert"; code = "Insert"; keyCode = 45; return true;
                case KeyCode.Home: key = "Home"; code = "Home"; keyCode = 36; return true;
                case KeyCode.End: key = "End"; code = "End"; keyCode = 35; return true;
                case KeyCode.PageUp: key = "PageUp"; code = "PageUp"; keyCode = 33; return true;
                case KeyCode.PageDown: key = "PageDown"; code = "PageDown"; keyCode = 34; return true;
                case KeyCode.LeftShift: key = "Shift"; code = "ShiftLeft"; keyCode = 16; location = 1; return true;
                case KeyCode.RightShift: key = "Shift"; code = "ShiftRight"; keyCode = 16; location = 2; return true;
                case KeyCode.LeftControl: key = "Control"; code = "ControlLeft"; keyCode = 17; location = 1; return true;
                case KeyCode.RightControl: key = "Control"; code = "ControlRight"; keyCode = 17; location = 2; return true;
                case KeyCode.LeftAlt: key = "Alt"; code = "AltLeft"; keyCode = 18; location = 1; return true;
                case KeyCode.RightAlt: key = "Alt"; code = "AltRight"; keyCode = 18; location = 2; return true;
                case KeyCode.CapsLock: key = "CapsLock"; code = "CapsLock"; keyCode = 20; return true;
                case KeyCode.LeftBracket: key = "["; code = "BracketLeft"; keyCode = 219; return true;
                case KeyCode.RightBracket: key = "]"; code = "BracketRight"; keyCode = 221; return true;
                case KeyCode.Backslash: key = "\\\\"; code = "Backslash"; keyCode = 220; return true;
                case KeyCode.Semicolon: key = ";"; code = "Semicolon"; keyCode = 186; return true;
                case KeyCode.Quote: key = "'"; code = "Quote"; keyCode = 222; return true;
                case KeyCode.Comma: key = ","; code = "Comma"; keyCode = 188; return true;
                case KeyCode.Period: key = "."; code = "Period"; keyCode = 190; return true;
                case KeyCode.Slash: key = "/"; code = "Slash"; keyCode = 191; return true;
                case KeyCode.BackQuote: key = "`"; code = "Backquote"; keyCode = 192; return true;
                case KeyCode.Minus: key = "-"; code = "Minus"; keyCode = 189; return true;
                case KeyCode.Equals: key = "="; code = "Equal"; keyCode = 187; return true;
                default:
                    // Fallback: use vk where possible
                    if (vk > 0) { key = ""; code = ""; keyCode = vk; return true; }
                    return false;
            }
        }

        private void HandleCompositionKeyboardInput()
        {
            try
            {
                if (!_gpuCompositionMode || _gpuCompositionCapture == null || !IsOpen) return;

                bool urlFocused = _urlInput != null && _urlInput.isFocused;
                bool webFocused = _compositionWebFocused && !urlFocused;
                bool ptrOver = IsPointerOverVideoDisplay(out _, out _);

                // URL bar typing must remain separate from game input.
                if (urlFocused)
                {
                    if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys("url_focused");
                    if (_gameInputMode) SetGameInputMode(false, "url_focused");
                }

                // Fallback zoom hotkeys for users without a working mouse wheel (e.g. remapped via AHK to keys).
                // Do not intercept while typing in the URL bar or while Game Input Mode forwards keys to the page/game.
                if (!urlFocused && !(_gameInputMode && webFocused))
                {
                    bool ctrlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                    if (ctrlDown)
                    {
                        if (TryHandleBrowserZoomHotkeys()) return;
                    }
                }

                // Toggle handled in OnUpdate (F6). Keep deterministic ownership suppression while app is open.
                bool numberKeyDown = false;
                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) { numberKeyDown = true; break; }
                }
                if (numberKeyDown)
                {
                    try { UnderNetBrowserInputHooks.NotifyNumberKeyIntent(); } catch { }
                    try { TrySuppressHotbarScroll(); } catch { }
                }

                // Game Input Mode: forward real key down/up transitions (held keys) to WebView2.
                // F6 is the reliable exit; Escape/right-click are forwarded to the game when this mode is active (predictable).
                if (_gameInputMode && webFocused && !urlFocused)
                {
                    UpdateGameInputModeTransitions(webFocused, urlFocused);
                    return;
                }

                // Not in active game mode: ensure no stuck keys and use normal browser text/input behavior.
                if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys("inactive_or_unfocused");

                // ESC during page fullscreen must exit web fullscreen (not close app), even if web focus tracking is stale.
                if (!webFocused && (Input.GetKeyDown(KeyCode.Escape)))
                {
                    bool isFullscreenNow = _isPageFullscreen;
                    try { isFullscreenNow = isFullscreenNow || _gpuCompositionCapture.IsFullscreen; } catch { }
                    if (isFullscreenNow)
                    {
                        SuppressAppCloseBriefly(2.5f);
                        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
                        try { _gpuCompositionCapture.SendKey(WM_KEYDOWN, 0x1B, 0); _gpuCompositionCapture.SendKey(WM_KEYUP, 0x1B, 0); } catch { }
                        try
                        {
                            _gpuCompositionCapture.ExecuteScript(
                                "(function(){try{var d=document; var fs=d.fullscreenElement||d.webkitFullscreenElement||d.mozFullScreenElement||d.msFullscreenElement; if(fs){try{(d.exitFullscreen||d.webkitExitFullscreen||d.mozCancelFullScreen||d.msExitFullscreen||function(){}).call(d);}catch(_){} try{if(fs.exitFullscreen)fs.exitFullscreen();}catch(_){} }" +
                                "var e=d.activeElement||d.body; var k={key:'Escape',code:'Escape',keyCode:27,which:27,bubbles:true,cancelable:true,composed:true}; try{e.dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{e.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} try{window.dispatchEvent(new KeyboardEvent('keydown',k)); window.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} " +
                                "try{var v=d.querySelector('video.html5-main-video, video'); if(v){v.dispatchEvent(new KeyboardEvent('keydown',k)); v.dispatchEvent(new KeyboardEvent('keyup',k));}}catch(_){} }catch(_){}})();");
                        }
                        catch { }
                        LogCompositionTypingDiag($"esc-fullscreen fallback (webFocused=false) fs={isFullscreenNow}");
                        return;
                    }
                }

                if (!webFocused)
                {
                    if (!urlFocused && ptrOver)
                    {
                        // Media/seek/volume keys: forward to web when pointer is over video/content even if web focus tracking is stale.
                        bool arrowHeld = Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow);
                        bool arrowDown = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow);
                        bool spaceDown = Input.GetKeyDown(KeyCode.Space);
                        if (arrowHeld || arrowDown || spaceDown)
                        {
                            try
                            {
                                _compositionWebFocused = true;
                                try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                                UpdateInputOwnershipState();
                                if (arrowDown || spaceDown)
                                {
                                    try { _gpuCompositionCapture.RequestFocus(); } catch { }
                                }
                                const uint wmKd = 0x0100, wmKu = 0x0101, wmCh = 0x0102;
                                if (spaceDown)
                                {
                                    try { SendSpacePlayPauseScriptFallback(); } catch { }
                                    try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Space handled by smart script (stale-focus path) ptr={ptrOver}"); } catch { }
                                    LogCompositionTypingDiag($"space(stale-focus smart script) urlFocused={urlFocused} ptr={ptrOver}");
                                }
                                if (arrowHeld || arrowDown)
                                {
                                    TryHandleMediaArrowRepeat(
                                        Input.GetKey(KeyCode.LeftArrow), Input.GetKey(KeyCode.RightArrow),
                                        Input.GetKey(KeyCode.UpArrow), Input.GetKey(KeyCode.DownArrow),
                                        sendVkTaps: true, sendScriptFallbacks: true);
                                    if (arrowDown)
                                        LogCompositionTypingDiag($"arrow(media fallback) urlFocused={urlFocused} ptr={ptrOver}");
                                }
                            }
                            catch { }
                        }
                    }
                    return;
                }

                // Normal web mode: number keys as discrete events (text/controls), plus existing text-entry behavior.
                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
                    {
                        char ch = (char)('1' + i);
                        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
                        ulong vk = (ulong)(0x31 + i); // '1'..'9'
                        _gpuCompositionCapture.SendKey(WM_KEYDOWN, vk, 0);
                        _gpuCompositionCapture.SendKey(WM_CHAR, ch, 0);
                        _gpuCompositionCapture.SendKey(WM_KEYUP, vk, 0);
                        MarkWebTextEntryActivity("numkey");
                        LogCompositionTypingDiag($"numkey='{ch}' urlFocused={urlFocused} webFocused={webFocused} ptr={ptrOver}");
                    }
                }

                const uint WM_KD = 0x0100, WM_KU = 0x0101, WM_CH = 0x0102;
                int sentChars = 0, sentCtrl = 0, sentVks = 0;
                // Forward chars (printable text, incl. punctuation) to webpage.
                // Primary: inject text into focused DOM element via ExecuteScript (reliable for composition-hosted WebView2
                // when WM message delivery is not consumed by web inputs). Also send key events as best-effort fallback.
                var s = Input.inputString;
                if (!string.IsNullOrEmpty(s))
                {
                    System.Text.StringBuilder? textBatch = null;
                    foreach (char c in s)
                    {
                        if (char.IsControl(c))
                        {
                            // Flush pending printable text before control keys to preserve order.
                            if (textBatch != null && textBatch.Length > 0)
                            {
                                try { _gpuCompositionCapture.SendText(textBatch.ToString()); MarkWebTextEntryActivity("textBatch"); } catch { }
                                textBatch.Clear();
                            }
                            if (c == '\b') { _gpuCompositionCapture.SendKey(WM_KD, 0x08, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x08, 0); sentCtrl++; MarkWebTextEntryActivity("backspace"); }
                            else if (c == '\r' || c == '\n') { _gpuCompositionCapture.SendKey(WM_KD, 0x0D, 0); _gpuCompositionCapture.SendKey(WM_CH, 0x0D, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x0D, 0); sentCtrl++; MarkWebTextEntryActivity("enter"); }
                            else if (c == '\t') { _gpuCompositionCapture.SendKey(WM_KD, 0x09, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x09, 0); sentCtrl++; MarkWebTextEntryActivity("tab"); }
                            else if (c == 27) { _gpuCompositionCapture.SendKey(WM_KD, 0x1B, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x1B, 0); sentCtrl++; }
                            continue;
                        }
                        // Space is handled by explicit KeyDown path + smart DOM script (typing vs media toggle).
                        if (c == ' ')
                        {
                            continue;
                        }

                        // Best-effort VK for WM_KEYDOWN/WM_KEYUP; actual character comes from WM_CHAR / script injection.
                        ulong vk = 0;
                        if (c >= 'a' && c <= 'z') vk = (ulong)(char.ToUpperInvariant(c));
                        else if (c >= 'A' && c <= 'Z') vk = (ulong)c;
                        else if (c >= '0' && c <= '9') vk = (ulong)c;

                        _gpuCompositionCapture.SendKey(WM_KD, vk, 0);
                        _gpuCompositionCapture.SendKey(WM_CH, c, 0);
                        _gpuCompositionCapture.SendKey(WM_KU, vk, 0);
                        if (textBatch == null) textBatch = new System.Text.StringBuilder();
                        textBatch.Append(c);
                        sentChars++;
                    }
                    if (textBatch != null && textBatch.Length > 0)
                    {
                        try { _gpuCompositionCapture.SendText(textBatch.ToString()); MarkWebTextEntryActivity("textBatchFlush"); } catch { }
                    }
                    if (sentChars > 0 || sentCtrl > 0)
                        LogCompositionTypingDiag($"inputString len={s.Length} chars={sentChars} ctrl={sentCtrl} (textInject+keys) urlFocused={urlFocused} webFocused={webFocused} ptr={ptrOver}");
                }

                void SendVk(ulong vk, bool asChar = false, char ch = '\0')
                {
                    _gpuCompositionCapture.SendKey(WM_KD, vk, 0);
                    if (asChar && ch != '\0') _gpuCompositionCapture.SendKey(WM_CH, ch, 0);
                    _gpuCompositionCapture.SendKey(WM_KU, vk, 0);
                    sentVks++;
                }

                // Non-char/navigation keys (ensure they reach web content)
                if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    SendVk(0x08);
                    try
                    {
                        // Fallback for composition key delivery: mutate focused editable element and emit events.
                        _gpuCompositionCapture.ExecuteScript(
                            "(function(){try{var e=document.activeElement; if(!e||e===document.body||e===document.documentElement)return; var k={key:'Backspace',code:'Backspace',keyCode:8,which:8,bubbles:true,cancelable:true}; try{e.dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} " +
                            "var tag=(e.tagName||'').toUpperCase(); if(tag==='INPUT'||tag==='TEXTAREA'){var v=e.value||''; var s=(typeof e.selectionStart==='number')?e.selectionStart:v.length; var en=(typeof e.selectionEnd==='number')?e.selectionEnd:s; if(s!==en){e.value=v.slice(0,s)+v.slice(en); try{e.setSelectionRange(s,s);}catch(_){}} else if(s>0){e.value=v.slice(0,s-1)+v.slice(en); try{e.setSelectionRange(s-1,s-1);}catch(_){}} try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} try{e.dispatchEvent(new Event('change',{bubbles:true}));}catch(_){}} " +
                            "else if(e.isContentEditable){try{document.execCommand('deleteBackward');}catch(_){} try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){}} " +
                            "try{e.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){}}catch(_){}})();");
                    }
                    catch { }
                }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    MarkWebTextEntryActivity("enterKey");
                    try { UnderNetBrowserInputHooks.SuppressEnterForGameBriefly(0.2f); } catch { }
                    SendVk(0x0D, true, '\r');
                    try
                    {
                        // Fallback for composition key delivery: dispatch Enter and attempt form submission/search submit.
                        // Includes site-specific fallbacks (e.g. YouTube search button) since synthetic key events may be ignored.
                        _gpuCompositionCapture.ExecuteScript(
                            "(function(){try{var e=document.activeElement||document.body; function fireK(t,el){try{el.dispatchEvent(new KeyboardEvent(t,{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true,composed:true}));}catch(_){}} fireK('keydown',e); fireK('keypress',e); fireK('keyup',e);" +
                            "var el=e; try{var yts=document.querySelector('button#search-icon-legacy, ytd-searchbox button#search-icon-legacy, ytd-masthead #search-icon-legacy'); if(yts){yts.click();}}catch(_){} " +
                            "var f=el&&el.form?el.form:(el&&el.closest?el.closest('form'):null); if(f){try{if(f.requestSubmit)f.requestSubmit(); else f.submit();}catch(_){}} " +
                            "try{var root=(el&&el.closest)?(el.closest('form, ytd-searchbox, ytm-searchbox, form')||document):document; var btn=root.querySelector('button[type=\\\"submit\\\"],input[type=\\\"submit\\\"],button[aria-label=\\\"Search\\\"],button#search-icon-legacy'); if(btn){btn.click();}}catch(_){} " +
                            "try{if(el&&el.tagName==='INPUT'){el.dispatchEvent(new Event('change',{bubbles:true}));}}catch(_){} }catch(_){}})();");
                    }
                    catch { }
                }
                if (Input.GetKeyDown(KeyCode.Tab)) SendVk(0x09);
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    bool isFullscreenNow = _isPageFullscreen;
                    try { isFullscreenNow = isFullscreenNow || (_gpuCompositionCapture != null && _gpuCompositionCapture.IsFullscreen); } catch { }

                    if (isFullscreenNow)
                    {
                        // ESC in page fullscreen: exit web fullscreen only (stay in app).
                        SuppressAppCloseBriefly(2.5f);
                        SendVk(0x1B);
                        try
                        {
                            _gpuCompositionCapture.ExecuteScript(
                                "(function(){try{var d=document; var fs=d.fullscreenElement||d.webkitFullscreenElement||d.mozFullScreenElement||d.msFullscreenElement; if(fs){try{(d.exitFullscreen||d.webkitExitFullscreen||d.mozCancelFullScreen||d.msExitFullscreen||function(){}).call(d);}catch(_){} try{if(fs.exitFullscreen)fs.exitFullscreen();}catch(_){} }" +
                                "var e=d.activeElement||d.body; var k={key:'Escape',code:'Escape',keyCode:27,which:27,bubbles:true,cancelable:true,composed:true}; try{e.dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{e.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} try{window.dispatchEvent(new KeyboardEvent('keydown',k)); window.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} " +
                                "try{var v=d.querySelector('video.html5-main-video, video'); if(v){v.dispatchEvent(new KeyboardEvent('keydown',k)); v.dispatchEvent(new KeyboardEvent('keyup',k));}}catch(_){} }catch(_){}})();");
                        }
                        catch { }
                    }
                    else
                    {
                        if (_gameInputMode)
                        {
                            // In Game Input Mode, keep ESC for the browser game/page (e.g. in-game menus). Do not detach/close.
                            try { SuppressAppCloseBriefly(2.5f); } catch { }
                            try { UnderNetBrowserConfig.Log("[UnderNetBrowser] ESC in Game Input Mode: forwarded to page (app close/detach suppressed)."); } catch { }
                        }
                        else
                        {
                            // ESC while in app (non-fullscreen, normal mode): warm-close the app and keep browser session warm for fast reopen.
                            // (RMB remains the detach-to-gameplay path that keeps the app running in background.)
                            try { RequestWarmClose("esc_keyboard"); } catch { }
                        }
                    }
                }
                TryHandleMediaArrowRepeat(
                    Input.GetKey(KeyCode.LeftArrow), Input.GetKey(KeyCode.RightArrow),
                    Input.GetKey(KeyCode.UpArrow), Input.GetKey(KeyCode.DownArrow),
                    sendVkTaps: true, sendScriptFallbacks: true);
                if (Input.GetKeyDown(KeyCode.Delete)) { SendVk(0x2E); MarkWebTextEntryActivity("deleteKey"); }
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    // Let DOM-aware script handle space to avoid double actions:
                    // - editable/search/text fields: insert a space
                    // - otherwise: toggle media playback (video/YouTube)
                    try { SendSpacePlayPauseScriptFallback(); } catch { }
                    try { UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Space handled by smart script"); } catch { }
                }
                if (sentVks > 0)
                    LogCompositionTypingDiag($"navKeys={sentVks} urlFocused={urlFocused} webFocused={webFocused} ptr={ptrOver} fs={_isPageFullscreen}");
            }
            catch { }
        }

        private float _lastCompTypingDiagLog;
        private string _lastCompTypingDiagMsg = "";
        private void LogCompositionTypingDiag(string msg)
        {
            try
            {
                if (UnderNetBrowserConfig.DebugLogging?.Value != true && UnderNetBrowserConfig.AlwaysLogDiagnostics?.Value != true)
                    return;
                // Throttle identical messages to avoid spam while still capturing relevant events.
                bool same = string.Equals(_lastCompTypingDiagMsg, msg, StringComparison.Ordinal);
                if (same && Time.realtimeSinceStartup - _lastCompTypingDiagLog < 0.5f) return;
                _lastCompTypingDiagMsg = msg;
                _lastCompTypingDiagLog = Time.realtimeSinceStartup;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [CompInput] {msg}");
            }
            catch { }
        }

                private bool TryHandleBrowserZoomHotkeys()
        {
            try
            {
                if (!_gpuCompositionMode || _gpuCompositionCapture == null || !IsOpen) return false;

                int dir = 0;
                bool reset = false;
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Plus))
                    dir = 1;
                else if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                    dir = -1;
                else if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
                    reset = true;

                if (dir == 0 && !reset) return false;

                double z = _gpuCompositionCapture.GetZoomFactor();
                if (!(z > 0.0)) z = 1.0;
                double nz = reset ? 1.0 : Mathf.Clamp((float)(z + (dir > 0 ? 0.10 : -0.10)), 0.25f, 5.0f);
                _gpuCompositionCapture.SetZoomFactor(nz);
                try { _gpuCompositionCapture.RequestFocus(); } catch { }
                try { TrySuppressHotbarScroll(); } catch { }
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Web zoom -> {nz:0.00} (ctrl+hotkey{(reset ? ", reset" : dir > 0 ? ", in" : ", out")}, requested)");
                return true;
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Web zoom hotkey failed (restart game after native update?): {ex.Message}"); } catch { }
                return true; // consume key intent when ctrl+zoom combo was pressed
            }
        }

        private void SendSpacePlayPauseScriptFallback()
        {
            try
            {
                if (!_gpuCompositionMode || _gpuCompositionCapture == null) return;
                // Smart space handling (DOM-aware):
                // - if focus is in editable/search/text field: insert a space
                // - otherwise: toggle media playback (YouTube/player/video)
                _gpuCompositionCapture.ExecuteScript(
                    "(function(){try{var d=document;" +
                    "function deepActive(root){var a=root&&root.activeElement; try{while(a&&a.shadowRoot&&a.shadowRoot.activeElement){a=a.shadowRoot.activeElement;}}catch(_){} return a||null;}" +
                    "var ae=deepActive(d); if(!ae) ae=d.activeElement;" +
                    "function isEditable(el){if(!el) return false; try{if(el.isContentEditable) return true;}catch(_){} var t=el.tagName?el.tagName.toUpperCase():''; if(t==='INPUT'||t==='TEXTAREA'||t==='SELECT') return true; try{var ce=el.getAttribute&&el.getAttribute('contenteditable'); if(ce!==null && String(ce).toLowerCase()!=='false') return true;}catch(_){} try{var r=el.getAttribute&&el.getAttribute('role'); if(r&&/textbox|searchbox|combobox/i.test(r)) return true;}catch(_){} try{if(el.closest&&el.closest('[contenteditable]:not([contenteditable=\"false\"]),[role=\"textbox\"],[role=\"searchbox\"],[role=\"combobox\"]')) return true;}catch(_){} return false;}" +
                    "var editEl=isEditable(ae)?ae:null; if(!editEl){try{var c=ae&&ae.closest?ae.closest('[contenteditable]:not([contenteditable=\"false\"]),[role=\"textbox\"],[role=\"searchbox\"],[role=\"combobox\"]'):null; if(c) editEl=c;}catch(_){} }" +
                    "if(editEl){" +
                    "  try{if(editEl.tagName){var t2=editEl.tagName.toUpperCase(); if(t2==='INPUT'||t2==='TEXTAREA'){var v=editEl.value||''; var s=(typeof editEl.selectionStart==='number')?editEl.selectionStart:v.length; var e=(typeof editEl.selectionEnd==='number')?editEl.selectionEnd:s; editEl.value=v.slice(0,s)+' '+v.slice(e); try{editEl.setSelectionRange(s+1,s+1);}catch(_){} try{editEl.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} try{editEl.dispatchEvent(new Event('change',{bubbles:true}));}catch(_){} return;}}}catch(_){} " +
                    "  try{var ok=false; try{ok=!!d.execCommand('insertText',false,' ');}catch(_){} if(!ok){try{var sel=window.getSelection&&window.getSelection(); if(sel&&sel.rangeCount){var r=sel.getRangeAt(0); r.deleteContents(); var n=d.createTextNode(' '); r.insertNode(n); r.setStartAfter(n); r.collapse(true); sel.removeAllRanges(); sel.addRange(r); ok=true;}else if(editEl.isContentEditable){editEl.textContent=(editEl.textContent||'')+' '; ok=true;} }catch(_){} } try{editEl.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} }catch(_){} " +
                    "  return;" +
                    "}" +
                    // Perform a single media toggle action to avoid double pause/unpause (e.g. player click + button click).
                    "try{var ytBtn=d.querySelector('.ytp-play-button, button.ytp-play-button'); if(ytBtn){try{ytBtn.focus();}catch(_){} ytBtn.click(); return;}}catch(_){} " +
                    "var v=null; try{v=d.querySelector('video.html5-main-video');}catch(_){} if(!v){try{var vids=d.querySelectorAll('video'); for(var i=0;i<vids.length;i++){var vv=vids[i]; if(!vv) continue; var r=vv.getBoundingClientRect?vv.getBoundingClientRect():null; if(r && r.width>20 && r.height>20){v=vv; break;} if(!v) v=vv;}}catch(_){}} " +
                    "if(v){try{if(v.paused||v.ended){var pr=v.play(); if(pr&&pr.catch)pr.catch(function(){});} else {v.pause();}}catch(_){ } return;}" +
                    "try{var btn2=d.querySelector('button[aria-label*=\"Play\" i], button[aria-label*=\"Pause\" i], button[title*=\"Play\" i], button[title*=\"Pause\" i]'); if(btn2){btn2.click(); return;}}catch(_){} " +
                    "try{var p=d.querySelector('#movie_player,.html5-video-player'); if(p){try{p.focus();}catch(_){} try{p.click();}catch(_){} }}catch(_){}" +
                    "}catch(_){}})();");
            }
            catch { }
        }
        private void SendArrowMediaKeyFallback(string dir)
        {
            try
            {
                if (!_gpuCompositionMode || _gpuCompositionCapture == null) return;
                string script = dir switch
                {
                    // Left/right: dispatch keys and keep direct seek fallback for reliable media control.
                    "left" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var k={key:'ArrowLeft',code:'ArrowLeft',keyCode:37,which:37,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} var v=d.querySelector('video'); if(v){try{v.currentTime=Math.max(0,(v.currentTime||0)-5);}catch(_){}} }catch(_){}})();",
                    "right" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var k={key:'ArrowRight',code:'ArrowRight',keyCode:39,which:39,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} var v=d.querySelector('video'); if(v){try{var dur=isFinite(v.duration)?v.duration:(v.currentTime||0)+5; v.currentTime=Math.min(dur,(v.currentTime||0)+5);}catch(_){}} }catch(_){}})();",
                    // Up/down: dispatch keys for page/search suggestion/result navigation; avoid forcing volume changes that break UI navigation.
                    "up" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var k={key:'ArrowUp',code:'ArrowUp',keyCode:38,which:38,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} }catch(_){}})();",
                    "down" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var k={key:'ArrowDown',code:'ArrowDown',keyCode:40,which:40,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} }catch(_){}})();",
                    _ => ""
                };
                if (!string.IsNullOrEmpty(script)) _gpuCompositionCapture.ExecuteScript(script);
            }
            catch { }
        }

        private void HandleDetachedBrowserMediaKeys()
        {
            try
            {
                if (!_detachedToGameplay || _gpuCompositionCapture == null) return;
                // Don't interfere while typing in other Unity UI or when paused menus likely need input.
                try { if (Time.timeScale <= 0.01f) return; } catch { }
                // Respect distance-based volume range for remote detached controls: if the browser is beyond the silent/max distance,
                // do not accept remote arrow media controls until back in range.
                try
                {
                    if (UnderNetBrowserConfig.DistanceVolumeEnabled?.Value == true && _lastDistanceVolumeApplied <= 0.001f)
                        return;
                }
                catch { }
                // Best-effort: forward arrow keys for video seek/volume control while browser/video continues off-TV.
                // (Game reportedly doesn't use these keys.) Supports holding via repeat (VK taps + JS fallbacks).
                TryHandleMediaArrowRepeat(
                    Input.GetKey(KeyCode.LeftArrow), Input.GetKey(KeyCode.RightArrow),
                    Input.GetKey(KeyCode.UpArrow), Input.GetKey(KeyCode.DownArrow),
                    sendVkTaps: true, sendScriptFallbacks: true);
            }
            catch { }
        }

        private void SendDetachedWebVkTap(ulong vk)
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                const uint WM_KD = 0x0100, WM_KU = 0x0101;
                _gpuCompositionCapture.SendKey(WM_KD, vk, 0);
                _gpuCompositionCapture.SendKey(WM_KU, vk, 0);
            }
            catch { }
        }

        private bool TryHandleMediaArrowRepeat(bool left, bool right, bool up, bool down, bool sendVkTaps, bool sendScriptFallbacks, bool allowWhenUnfocusedPointerOver = false)
        {
            // Returns true if any arrow was handled this frame (press/repeat), so callers can early-return when needed.
            bool handled = false;
            try
            {
                float now = 0f;
                try { now = Time.realtimeSinceStartup; } catch { now = 0f; }
                void Handle(KeyCode kc, ref float nextTime, ulong vk, string dir)
                {
                    bool downNow = false;
                    bool downEdge = false;
                    try { downNow = Input.GetKey(kc); downEdge = Input.GetKeyDown(kc); } catch { downNow = false; downEdge = false; }
                    if (!downNow)
                    {
                        nextTime = -1f;
                        return;
                    }
                    bool fire = false;
                    if (downEdge || nextTime < 0f)
                    {
                        fire = true;
                        nextTime = now + 0.35f; // initial delay before repeat
                    }
                    else if (now >= nextTime)
                    {
                        fire = true;
                        nextTime = now + 0.18f; // repeat interval
                    }
                    if (!fire) { handled = handled || downNow; return; }

                    handled = true;
                    try
                    {
                        if (sendVkTaps && _gpuCompositionCapture != null)
                            SendDetachedWebVkTap(vk);
                        if (sendScriptFallbacks)
                            SendArrowMediaKeyFallback(dir);
                    }
                    catch { }
                }

                if (left || Input.GetKey(KeyCode.LeftArrow)) Handle(KeyCode.LeftArrow, ref _nextMediaArrowRepeatLeft, 0x25, "left");
                else _nextMediaArrowRepeatLeft = -1f;
                if (right || Input.GetKey(KeyCode.RightArrow)) Handle(KeyCode.RightArrow, ref _nextMediaArrowRepeatRight, 0x27, "right");
                else _nextMediaArrowRepeatRight = -1f;
                if (up || Input.GetKey(KeyCode.UpArrow)) Handle(KeyCode.UpArrow, ref _nextMediaArrowRepeatUp, 0x26, "up");
                else _nextMediaArrowRepeatUp = -1f;
                if (down || Input.GetKey(KeyCode.DownArrow)) Handle(KeyCode.DownArrow, ref _nextMediaArrowRepeatDown, 0x28, "down");
                else _nextMediaArrowRepeatDown = -1f;

                _ = allowWhenUnfocusedPointerOver; // reserved for caller clarity
            }
            catch { }
            return handled;
        }

        private void MarkWebTextEntryActivity(string reason = "")
        {
            try
            {
                if (_urlInput != null && _urlInput.isFocused) return;
                if (!_compositionWebFocused) return;
                _lastWebTextEntryActivityRealtime = Time.realtimeSinceStartup;
                try { UnderNetBrowserInputHooks.SetWebTextEntryActive(true); } catch { }
                _ = reason;
            }
            catch { }
        }

        private void RefreshWebTextEntryState()
        {
            try
            {
                bool urlFocused = _urlInput != null && _urlInput.isFocused;
                bool webFocused = _compositionWebFocused && !urlFocused && !_detachedToGameplay && IsOpen;
                bool active = webFocused && (Time.realtimeSinceStartup - _lastWebTextEntryActivityRealtime <= 2.5f);
                try { UnderNetBrowserInputHooks.SetWebTextEntryActive(active); } catch { }
            }
            catch { }
        }

        private void UpdateInputOwnershipState()
        {
            try
            {
                // While detached to gameplay, the TV app/browser may remain "open" in background but must not suppress
                // gameplay movement/hotbar selection. Detach/reattach flows manage explicit ownership transitions.
                if (_detachedToGameplay)
                {
                    _lastWebTextEntryActivityRealtime = -999f;
                    try { UnderNetBrowserInputHooks.SetGameInputModeActive(false); } catch { }
                    UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false);
                    try { UnderNetBrowserInputHooks.SetWebTextEntryActive(false); } catch { }
                    return;
                }

                bool urlFocused = _urlInput != null && _urlInput.isFocused;
                bool webFocused = _compositionWebFocused && !urlFocused;
                bool pointerOver = IsPointerOverVideoDisplay(out _, out _);
                try { UnderNetBrowserInputHooks.SetGameInputModeActive(_gameInputMode && webFocused); } catch { }
                // Clicking browser/URL bar area should not cause hotbar switching per spec; focus ownership tracks url/web focus.
                // Pointer alone is not treated as ownership unless focused, so clicking outside restores immediately on focus loss.
                UnderNetBrowserInputHooks.UpdateOwnership(IsOpen, urlFocused, webFocused, pointerOver);
                RefreshWebTextEntryState();
            }
            catch { }
        }

        private bool IsPointerOverVideoDisplay(out int px, out int py)
        {
            px = 0; py = 0;
            try
            {
                if (_videoDisplay == null) return false;
                var rt = _videoDisplay.rectTransform;
                if (rt == null) return false;
                var canvas = _videoDisplay.canvas;
                var cam = canvas != null ? canvas.worldCamera : null;
                if (cam == null) cam = Camera.main;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out var local)) return false;
                var rect = rt.rect;
                if (rect.width <= 0.01f || rect.height <= 0.01f) return false;
                float nx = (local.x - rect.xMin) / rect.width;
                float ny = (rect.yMax - local.y) / rect.height;
                if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return false;
                int vw = _gpuCompositionCapture != null ? Math.Max(1, _gpuCompositionCapture.ViewWidth) : 1600;
                int vh = _gpuCompositionCapture != null ? Math.Max(1, _gpuCompositionCapture.ViewHeight) : 900;
                px = Mathf.Clamp(Mathf.RoundToInt(nx * (vw - 1)), 0, vw - 1);
                py = Mathf.Clamp(Mathf.RoundToInt(ny * (vh - 1)), 0, vh - 1);
                return true;
            }
            catch { return false; }
        }

        private void HandleClipboardPaste()
        {
            if (_urlInput == null || !IsOpen) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            // In-page clipboard shortcuts (web focused): forward Ctrl+C/Ctrl+V to WebView2 and apply best-effort fallbacks.
            bool urlFocusedNow = _urlInput.isFocused;
            bool webFocusedNow = _compositionWebFocused && !urlFocusedNow;
            if (ctrl && _gpuCompositionMode && _gpuCompositionCapture != null && webFocusedNow)
            {
                if (Input.GetKeyDown(KeyCode.A))
                {
                    try
                    {
                        SendCtrlComboToWeb(0x41); // 'A'
                        try { TrySelectAllInWeb(); } catch { }
                        LogCompositionTypingDiag("Ctrl+A forwarded to web");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+A failed: {ex.Message}"); }
                    return;
                }
                if (Input.GetKeyDown(KeyCode.C))
                {
                    try
                    {
                        // Best-effort: let the page/browser handle copy; also try to mirror selection into system clipboard.
                        SendCtrlComboToWeb(0x43); // 'C'
                        TryCopyWebSelectionToClipboard();
                        LogCompositionTypingDiag("Ctrl+C forwarded to web");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+C failed: {ex.Message}"); }
                    return;
                }
                if (Input.GetKeyDown(KeyCode.X))
                {
                    try
                    {
                        // Best-effort: mirror selection to clipboard, then perform cut in editable contexts and forward keys.
                        try { TryCopyWebSelectionToClipboard(); } catch { }
                        try { TryCutInWeb(); } catch { }
                        SendCtrlComboToWeb(0x58); // 'X'
                        LogCompositionTypingDiag("Ctrl+X forwarded to web");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+X failed: {ex.Message}"); }
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Z))
                {
                    try
                    {
                        try { TryUndoRedoInWeb(false); } catch { }
                        SendCtrlComboToWeb(0x5A); // 'Z'
                        LogCompositionTypingDiag("Ctrl+Z forwarded to web");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+Z failed: {ex.Message}"); }
                    return;
                }
                if (Input.GetKeyDown(KeyCode.Y))
                {
                    try
                    {
                        try { TryUndoRedoInWeb(true); } catch { }
                        SendCtrlComboToWeb(0x59); // 'Y'
                        LogCompositionTypingDiag("Ctrl+Y forwarded to web");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+Y failed: {ex.Message}"); }
                    return;
                }
                if (Input.GetKeyDown(KeyCode.V))
                {
                    try
                    {
                        // Best-effort: inject clipboard text into focused editable element in page.
                        // Use a single deterministic path to avoid duplicate pastes (script injection + browser Ctrl+V + native text injection).
                        bool injected = TryPasteClipboardTextIntoWeb();
                        if (injected) MarkWebTextEntryActivity("pasteInjected");
                        LogCompositionTypingDiag($"Ctrl+V to web injected={injected}");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+V failed: {ex.Message}"); }
                    return;
                }
            }

            bool urlSelected = false;
            try
            {
                var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                urlSelected = sel != null && _urlInput != null && (sel == _urlInput.gameObject || sel.transform.IsChildOf(_urlInput.transform));
            }
            catch { }
            bool urlBarPasteTarget = ctrl && (Input.GetKey(KeyCode.V) || Input.GetKeyDown(KeyCode.V)) && (urlSelected || (_urlInput != null && _urlInput.isFocused) || !_compositionWebFocused);
            if (urlBarPasteTarget)
            {
                float nowPaste = 0f;
                try { nowPaste = Time.realtimeSinceStartup; } catch { nowPaste = 0f; }
                bool isDown = false;
                try { isDown = Input.GetKeyDown(KeyCode.V); } catch { isDown = false; }
                bool fireUrlPaste = isDown || _nextUrlBarPasteRepeatTime < 0f || nowPaste >= _nextUrlBarPasteRepeatTime;
                if (fireUrlPaste)
                {
                    // Support hold-repeat for URL bar paste (polish). Repeat replaces content (no append) to avoid runaway duplication.
                    _nextUrlBarPasteRepeatTime = nowPaste + (isDown ? 0.35f : 0.12f);
                    try
                    {
                        var clip = GUIUtility.systemCopyBuffer;
                        if (!string.IsNullOrEmpty(clip))
                        {
                            var text = clip.Replace("\r", "").Replace("\n", " ").Replace("\t", " ").Trim();
                            const int maxUrlPasteChars = 2048;
                            if (text.Length > maxUrlPasteChars) text = text.Substring(0, maxUrlPasteChars);
                            _urlInput.text = text;
                            _compositionWebFocused = false;
                            try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(_urlInput.gameObject); } catch { }
                            try { _urlInput.ActivateInputField(); } catch { }
                            try { _urlInput.Select(); } catch { }
                            try
                            {
                                int end = (_urlInput.text ?? "").Length;
                                _urlInput.caretPosition = end;
                                _urlInput.selectionAnchorPosition = end;
                                _urlInput.selectionFocusPosition = end;
                            }
                            catch { }
                            try { if (_urlInput.textComponent != null) _urlInput.textComponent.text = _urlInput.text; } catch { }
                            try { _urlInput.ForceLabelUpdate(); } catch { }
                            try { SetUrlDisplayText(_urlInput.text ?? ""); } catch { }
                            // Keep URL bar as the active target after paste (prevent web/game focus stealing on repeated Ctrl+V).
                            _compositionWebFocused = false;
                        }
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Paste failed: {ex.Message}"); }
                }
                return;
            }
            else
            {
                _nextUrlBarPasteRepeatTime = -1f;
            }

            // Non-composition fallback web-focused paste guard (rare): avoid hijacking into address bar.
            if (ctrl && Input.GetKeyDown(KeyCode.V) && _compositionWebFocused && !_urlInput.isFocused)
            {
                LogCompositionTypingDiag($"Ctrl+V ignored for address bar while webFocused (non-composition path)");
                return;
            }

            if (_urlInput.isFocused && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                try { UnderNetBrowserInputHooks.SuppressEnterForGameBriefly(0.2f); } catch { }
                var text = _urlInput.text?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SetUrl(text);
                    _compositionWebFocused = true; // navigating to web content
                    try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                    try { _urlInput.DeactivateInputField(); } catch { }
                }
            }
        }

        private void SendCtrlComboToWeb(int vkLetter)
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                const uint WM_KD = 0x0100, WM_KU = 0x0101;
                _gpuCompositionCapture.SendKey(WM_KD, 0x11, 0); // VK_CONTROL
                _gpuCompositionCapture.SendKey(WM_KD, (ulong)vkLetter, 0);
                _gpuCompositionCapture.SendKey(WM_KU, (ulong)vkLetter, 0);
                _gpuCompositionCapture.SendKey(WM_KU, 0x11, 0);
            }
            catch { }
        }

        private static float _nextHomepageGameTimeSyncRealtime = -999f;
        private static string _lastHomepageGameTimePayload = string.Empty;

        private static string EscapeJsSingleQuoted(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
        }

        private static bool TryGetScheduleOneGameTime(out string timeText, out string dayText)
        {
            timeText = "--:--";
            dayText = "Time unavailable";
            try
            {
                Type? tmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { tmType = asm.GetType("Il2CppScheduleOne.GameTime.TimeManager", false); } catch { tmType = null; }
                    if (tmType != null) break;
                }
                if (tmType == null) return false;

                object? tm = null;
                try
                {
                    var instExistsProp = tmType.GetProperty("InstanceExists", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                    if (instExistsProp != null)
                    {
                        var existsObj = instExistsProp.GetValue(null);
                        if (existsObj is bool b && !b) return false;
                    }
                }
                catch { }
                try
                {
                    var instProp = tmType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                    if (instProp != null) tm = instProp.GetValue(null);
                }
                catch { tm = null; }
                if (tm == null) return false;

                int currentTime = -1;
                try
                {
                    var ctProp = tmType.GetProperty("CurrentTime", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (ctProp != null)
                    {
                        var v = ctProp.GetValue(tm);
                        if (v != null) currentTime = Convert.ToInt32(v);
                    }
                }
                catch { currentTime = -1; }

                // Prefer game's formatted 12-hour time if available.
                string? formatted = null;
                try
                {
                    var m = tmType.GetMethod("Get12HourTime", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(float), typeof(bool) }, null);
                    if (m != null && currentTime >= 0)
                    {
                        var o = m.Invoke(null, new object[] { (float)currentTime, true });
                        if (o is string s && !string.IsNullOrWhiteSpace(s)) formatted = s.Trim();
                    }
                }
                catch { formatted = null; }
                if (string.IsNullOrWhiteSpace(formatted) && currentTime >= 0)
                {
                    int mins = ((currentTime % 1440) + 1440) % 1440;
                    int h24 = mins / 60;
                    int mm = mins % 60;
                    int h12 = h24 % 12; if (h12 == 0) h12 = 12;
                    formatted = $"{h12}:{mm:00} {(h24 >= 12 ? "PM" : "AM")}";
                }
                if (!string.IsNullOrWhiteSpace(formatted)) timeText = formatted;

                string dayName = "";
                try
                {
                    var dayProp = tmType.GetProperty("CurrentDay", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (dayProp != null)
                    {
                        var dv = dayProp.GetValue(tm);
                        if (dv != null) dayName = dv.ToString() ?? "";
                    }
                }
                catch { dayName = ""; }

                int dayIndex = -1, elapsedDays = -1;
                try { var p = tmType.GetProperty("DayIndex", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); if (p != null) { var v = p.GetValue(tm); if (v != null) dayIndex = Convert.ToInt32(v); } } catch { }
                try { var p = tmType.GetProperty("ElapsedDays", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); if (p != null) { var v = p.GetValue(tm); if (v != null) elapsedDays = Convert.ToInt32(v); } } catch { }

                if (!string.IsNullOrWhiteSpace(dayName))
                {
                    dayText = dayName;
                    int dayNum = elapsedDays >= 0 ? (elapsedDays + 1) : (dayIndex >= 0 ? (dayIndex + 1) : -1);
                    if (dayNum > 0) dayText += $" · Day {dayNum}";
                }
                else if (elapsedDays >= 0 || dayIndex >= 0)
                {
                    int dayNum = elapsedDays >= 0 ? (elapsedDays + 1) : (dayIndex + 1);
                    dayText = $"Day {dayNum}";
                }

                return !string.IsNullOrWhiteSpace(timeText) || !string.IsNullOrWhiteSpace(dayName);
            }
            catch
            {
                return false;
            }
        }

        private static string JsBool(bool v) => v ? "true" : "false";
        private static string JsStr(string? v) => "'" + EscapeJsSingleQuoted(v ?? string.Empty) + "'";

        private static float _nextAiPageCustomizationRealtime = -999f;
        private static string _lastAiCustomizationUrl = string.Empty;
        private static string? _cachedSteamPersonaName;

        private static string GetLocalPlayerDisplayNameBestEffort()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_cachedSteamPersonaName)) return _cachedSteamPersonaName!;
                string? name = null;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            var tn = t?.FullName ?? t?.Name ?? "";
                            // Common Steamworks.NET / Facepunch / Il2Cpp wrappers
                            if (tn.EndsWith(".SteamFriends", StringComparison.Ordinal) || tn.EndsWith(".SteamUser", StringComparison.Ordinal) || tn == "SteamFriends" || tn == "SteamUser")
                            {
                                try
                                {
                                    foreach (var mn in new[] { "GetPersonaName", "GetFriendPersonaName", "get_Name" })
                                    {
                                        var m = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                                        if (m == null || m.GetParameters().Length != 0) continue;
                                        object? inst = null;
                                        if (!m.IsStatic)
                                        {
                                            var p = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) ?? t.GetProperty("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                            if (p != null) try { inst = p.GetValue(null); } catch { inst = null; }
                                            if (inst == null)
                                            {
                                                var f = t.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) ?? t.GetField("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                                if (f != null) try { inst = f.GetValue(null); } catch { inst = null; }
                                            }
                                            if (inst == null) continue;
                                        }
                                        var v = m.Invoke(inst, null);
                                        var s = v as string;
                                        if (!string.IsNullOrWhiteSpace(s)) { name = s.Trim(); break; }
                                    }
                                }
                                catch { }
                            }
                            if (!string.IsNullOrWhiteSpace(name)) break;
                        }
                        if (!string.IsNullOrWhiteSpace(name)) break;
                    }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = Environment.UserName; } catch { name = null; }
                }
                if (string.IsNullOrWhiteSpace(name)) name = "Player";
                _cachedSteamPersonaName = name;
                return name!;
            }
            catch { return "Player"; }
        }

        private void TryApplyAiPageCustomization(string? currentUrl)
        {
            try
            {
                if (!IsOpen || _detachedToGameplay || _externalMirrorMode || _gpuCompositionCapture == null) return;
                var url = (currentUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(url)) return;
                bool isAi = false;
                try
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var host = (uri.Host ?? "").ToLowerInvariant();
                        var path = (uri.AbsolutePath ?? "").TrimEnd('/').ToLowerInvariant();
                        var q = (uri.Query ?? "").ToLowerInvariant();
                        isAi = host.EndsWith("google.com") && (path == "/ai" || path.StartsWith("/ai/") || q.Contains("udm=50"));
                    }
                }
                catch { isAi = false; }
                if (!isAi) { _lastAiCustomizationUrl = string.Empty; return; }

                float now = Time.realtimeSinceStartup;
                // Reapply periodically in case SPA rerenders; also reapply on URL changes.
                if (!string.Equals(_lastAiCustomizationUrl, url, StringComparison.Ordinal)) _nextAiPageCustomizationRealtime = now;
                if (now < _nextAiPageCustomizationRealtime) return;
                _nextAiPageCustomizationRealtime = now + 1.25f;
                _lastAiCustomizationUrl = url;

                var playerName = GetLocalPlayerDisplayNameBestEffort();
                var greeting = $"Hello {playerName}, how can I help?";
                var script = @"
(function(){
  try{
    var g=" + JsStr(greeting) + @";
    if(!window.__undernetAiApplyCount) window.__undernetAiApplyCount=0;
    window.__undernetAiApplyCount++;
    var cssId='undernet-ai-style';
    if(!document.getElementById(cssId)){
      var st=document.createElement('style'); st.id=cssId; st.textContent=''
        +'html,body{background:#07110d !important;color:#eefbf3 !important;}'
        +'body,button,input,textarea,select{font-family:""Segoe UI Variable"",""Segoe UI"",Tahoma,Arial,sans-serif !important;}'
        +'header,[role=""banner""],footer,[role=""contentinfo""],#gb,#searchform,form[role=""search""]{display:none !important;}'
        +'a[href*=""tbm=""],a[href*=""/imghp""],a[href*=""/maps""],a[href*=""/shopping""],a[href*=""/news""],a[href*=""/videohp""]{display:none !important;}'
        +'[aria-label*=""Google apps"" i],[aria-label*=""Sign in"" i],[aria-label*=""Account"" i],[aria-label*=""Try"" i]{display:none !important;}'
        +'.undernet-ai-badge{position:fixed;top:10px;left:12px;z-index:2147483647;padding:8px 12px;border-radius:10px;background:rgba(12,20,17,.94);border:1px solid rgba(124,255,107,.28);color:#eaffea;font:600 13px/1.2 Segoe UI,sans-serif;box-shadow:0 8px 24px rgba(0,0,0,.35);pointer-events:none}'
        +'.undernet-ai-note{position:fixed;top:10px;left:132px;z-index:2147483647;padding:8px 10px;border-radius:10px;background:rgba(8,13,10,.82);border:1px solid rgba(255,255,255,.08);color:#9db6ab;font:500 12px/1.2 Segoe UI,sans-serif;pointer-events:none}';
      (document.head||document.documentElement).appendChild(st);
    }
    if(!document.getElementById('undernet-ai-badge')){
      var b=document.createElement('div'); b.id='undernet-ai-badge'; b.className='undernet-ai-badge'; b.textContent='UnderNet AI';
      (document.body||document.documentElement).appendChild(b);
    }
    if(!document.getElementById('undernet-ai-note')){
      var n=document.createElement('div'); n.id='undernet-ai-note'; n.className='undernet-ai-note'; n.textContent='Powered by Google AI';
      (document.body||document.documentElement).appendChild(n);
    }
    // Personalize greeting-like text if visible
    try{
      var candidates = Array.from(document.querySelectorAll('h1,h2,h3,p,div,span'));
      for(var i=0;i<candidates.length && i<1800;i++){
        var el=candidates[i]; if(!el || !el.textContent) continue;
        var t=(el.textContent||'').trim();
        if(t.length<4 || t.length>140) continue;
        if(/^(hi|hello|hey|good (morning|afternoon|evening)|what can i help|how can i help).*/i.test(t) && !/undernet/i.test(t)){
          var cs=null; try{ cs=window.getComputedStyle(el);}catch(_){}
          if(cs && (cs.display==='none' || cs.visibility==='hidden' || parseFloat(cs.opacity||'1')===0)) continue;
          el.textContent = g;
          break;
        }
      }
    }catch(_){}
    // Try setting composer placeholder hint if present
    try{
      var inputs = Array.from(document.querySelectorAll('textarea,[contenteditable=""true""],[role=""textbox""]'));
      for(var j=0;j<inputs.length;j++){
        var inp=inputs[j]; if(!inp) continue;
        var ph = (inp.getAttribute && (inp.getAttribute('placeholder')||inp.getAttribute('aria-label')||'')) || '';
        if(/ask|message|prompt|enter/i.test(ph) || inp.tagName==='TEXTAREA'){
          try{ inp.setAttribute('data-undernet-ai','1'); }catch(_){}
          break;
        }
      }
    }catch(_){}
    try{ if(document.title && !/UnderNet AI/i.test(document.title)) document.title='UnderNet AI'; }catch(_){}
  }catch(e){}
})();";
                try { _gpuCompositionCapture.ExecuteScript(script); } catch { }
            }
            catch { }
        }
        private static float _nextSettingsPageSyncRealtime = -999f;
        private static string _lastSettingsPagePayload = string.Empty;

        private static string BuildCurrentSettingsJsObject()
        {
            try
            {
                string home = UnderNetBrowserConfig.HomeUrl?.Value ?? "undernet:home";
                string kCursor = UnderNetBrowserConfig.KeyToggleCursor?.Value ?? "F3";
                string kReload = UnderNetBrowserConfig.KeyReload?.Value ?? "F5";
                string kGame = UnderNetBrowserConfig.KeyGameInputMode?.Value ?? "F6";
                bool adblock = UnderNetBrowserConfig.BasicAdBlockEnabled?.Value ?? true;
                bool consent = UnderNetBrowserConfig.AutoConsentEnabled?.Value ?? false;
                bool distVol = UnderNetBrowserConfig.DistanceVolumeEnabled?.Value ?? true;
                bool dbg = UnderNetBrowserConfig.DebugLogging?.Value ?? false;
                float chrome = UnderNetBrowserConfig.ChromeHeightFraction?.Value ?? 0.085f;
                int rw = UnderNetBrowserConfig.GpuCompositionRenderWidth?.Value ?? 1920;
                int rh = UnderNetBrowserConfig.GpuCompositionRenderHeight?.Value ?? 1080;
                return "{"
                    + "HomeUrl:" + JsStr(home) + ","
                    + "KeyToggleCursor:" + JsStr(kCursor) + ","
                    + "KeyReload:" + JsStr(kReload) + ","
                    + "KeyGameInputMode:" + JsStr(kGame) + ","
                    + "BasicAdBlockEnabled:" + JsBool(adblock) + ","
                    + "AutoConsentEnabled:" + JsBool(consent) + ","
                    + "DistanceVolumeEnabled:" + JsBool(distVol) + ","
                    + "DebugLogging:" + JsBool(dbg) + ","
                    + "ChromeHeightFraction:" + chrome.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                    + "GpuCompositionRenderWidth:" + rw.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                    + "GpuCompositionRenderHeight:" + rh.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "}";
            }
            catch { return "{}"; }
        }

        internal static void TryUpdateSettingsPageState(bool force = false)
        {
            try
            {
                var app = _activeInstance;
                if (app == null || !app.IsOpen) return;
                float now = Time.realtimeSinceStartup;
                if (!force && now < _nextSettingsPageSyncRealtime) return;
                _nextSettingsPageSyncRealtime = now + 1.0f;

                if (app._gpuCompositionCapture == null) return;
                var cur = "";
                try { cur = app._gpuCompositionCapture.CurrentUrl ?? ""; } catch { cur = ""; }
                if (string.IsNullOrWhiteSpace(cur)) return;
                var curNorm = cur.Replace("\\", "/");
                if (curNorm.IndexOf("/UnderNetBrowser/root/Homepage/settings.html", StringComparison.OrdinalIgnoreCase) < 0 &&
                    curNorm.IndexOf("/root/Homepage/settings.html", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                var payload = BuildCurrentSettingsJsObject();
                if (!force && string.Equals(payload, _lastSettingsPagePayload, StringComparison.Ordinal)) return;
                _lastSettingsPagePayload = payload;
                var script = "(function(){try{if(window.__underNetApplySettings)window.__underNetApplySettings(" + payload + ");}catch(e){}})();";
                try { app._gpuCompositionCapture.ExecuteScript(script); } catch { }
            }
            catch { }
        }

        internal static void TryUpdateHomepageGameTime()
        {
            try
            {
                var app = _activeInstance;
                if (app == null || !app.IsOpen) return;
                float now = Time.realtimeSinceStartup;
                if (now < _nextHomepageGameTimeSyncRealtime) return;
                _nextHomepageGameTimeSyncRealtime = now + 1.0f;

                if (app._gpuCompositionCapture == null) return;
                var cur = "";
                try { cur = app._gpuCompositionCapture.CurrentUrl ?? ""; } catch { cur = ""; }
                if (string.IsNullOrWhiteSpace(cur)) return;
                var curNorm = cur.Replace("\\", "/");
                if (curNorm.IndexOf("/UnderNetBrowser/root/Homepage/index.html", StringComparison.OrdinalIgnoreCase) < 0 &&
                    curNorm.IndexOf("/root/Homepage/index.html", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                if (!TryGetScheduleOneGameTime(out var timeText, out var dayText))
                {
                    timeText = "--:--";
                    dayText = "Time unavailable";
                }
                var payload = timeText + "|" + dayText;
                if (string.Equals(payload, _lastHomepageGameTimePayload, StringComparison.Ordinal)) return;
                _lastHomepageGameTimePayload = payload;
                var script = "(function(){try{if(window.__underNetSetGameTime)window.__underNetSetGameTime('" + EscapeJsSingleQuoted(timeText) + "','" + EscapeJsSingleQuoted(dayText) + "');}catch(e){}})();";
                try { app._gpuCompositionCapture.ExecuteScript(script); } catch { }
            }
            catch { }
        }

        private static string EscapeJsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private bool TryPasteClipboardTextIntoWeb()
        {
            try
            {
                if (_gpuCompositionCapture == null) return false;
                var clip = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(clip)) return false;
                string jsText = EscapeJsString(clip);
                string script =
                    "(function(){try{var t=\"" + jsText + "\"; if(!t) return;" +
                    "var e=document.activeElement; if(e && (e.tagName==='INPUT' || e.tagName==='TEXTAREA')){" +
                    "var start=(typeof e.selectionStart==='number')?e.selectionStart:(e.value?e.value.length:0);" +
                    "var end=(typeof e.selectionEnd==='number')?e.selectionEnd:start;" +
                    "var v=e.value||''; e.value=v.slice(0,start)+t+v.slice(end);" +
                    "try{var p=start+t.length; e.setSelectionRange(p,p);}catch(_){} " +
                    "try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} try{e.dispatchEvent(new Event('change',{bubbles:true}));}catch(_){} return;" +
                    "}" +
                    "if(e && e.isContentEditable){try{document.execCommand('insertText', false, t);}catch(_){try{e.textContent=(e.textContent||'')+t;}catch(__){}} try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} return;}" +
                    "try{document.execCommand('insertText', false, t);}catch(_){}" +
                    "}catch(_){}})();";
                _gpuCompositionCapture.ExecuteScript(script);
                MarkWebTextEntryActivity("pasteScript");
                return true;
            }
            catch { return false; }
        }

        private void TrySelectAllInWeb()
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                _gpuCompositionCapture.ExecuteScript(
                    "(function(){try{var e=document.activeElement;" +
                    "if(e&&(e.tagName==='INPUT'||e.tagName==='TEXTAREA')&&typeof e.select==='function'){try{e.focus();}catch(_){} try{e.select();}catch(_){} try{e.setSelectionRange(0,(e.value||'').length);}catch(_){} return;}" +
                    "if(e&&e.isContentEditable){try{var r=document.createRange(); r.selectNodeContents(e); var s=window.getSelection(); s.removeAllRanges(); s.addRange(r);}catch(_){} try{document.execCommand('selectAll');}catch(_){} return;}" +
                    "try{document.execCommand('selectAll');}catch(_){try{var r2=document.createRange(); r2.selectNodeContents(document.body||document.documentElement); var s2=window.getSelection(); s2.removeAllRanges(); s2.addRange(r2);}catch(__){}}" +
                    "}catch(_){}})();");
            }
            catch { }
        }

        private void TryCopyWebSelectionToClipboard()
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                // Best-effort: ask page to copy selection via execCommand (works in many contexts even when async clipboard APIs are restricted).
                _gpuCompositionCapture.ExecuteScript("(function(){try{document.execCommand('copy');}catch(_){} try{var e=document.activeElement; if(e&&e.select&&document.queryCommandEnabled&&document.queryCommandEnabled('copy')){document.execCommand('copy');}}catch(_){} })();");
            }
            catch { }
        }

        private void TryCutInWeb()
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                _gpuCompositionCapture.ExecuteScript(
                    "(function(){try{var e=document.activeElement;" +
                    "if(e&&(e.tagName==='INPUT'||e.tagName==='TEXTAREA')){" +
                    "try{if(document.queryCommandSupported&&document.queryCommandSupported('cut')){document.execCommand('cut'); return;}}catch(_){} " +
                    "try{var s=(typeof e.selectionStart==='number')?e.selectionStart:0; var n=(typeof e.selectionEnd==='number')?e.selectionEnd:s; if(n>s){var v=e.value||''; e.value=v.slice(0,s)+v.slice(n); try{e.setSelectionRange(s,s);}catch(__){} try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(__){} try{e.dispatchEvent(new Event('change',{bubbles:true}));}catch(__){}}}catch(_){} return;}" +
                    "if(e&&e.isContentEditable){try{if(document.queryCommandSupported&&document.queryCommandSupported('cut')){document.execCommand('cut'); return;}}catch(_){} try{document.execCommand('delete');}catch(_){} try{e.dispatchEvent(new Event('input',{bubbles:true}));}catch(_){} return;}" +
                    "}catch(_){}})();");
            }
            catch { }
        }

        private void TryUndoRedoInWeb(bool redo)
        {
            try
            {
                if (_gpuCompositionCapture == null) return;
                string cmd = redo ? "redo" : "undo";
                _gpuCompositionCapture.ExecuteScript(
                    "(function(){try{var cmd='" + cmd + "';" +
                    "try{if(document.queryCommandSupported&&document.queryCommandSupported(cmd)){document.execCommand(cmd); return;}}catch(_){}" +
                    "try{document.execCommand(cmd);}catch(_){}" +
                    "}catch(_){}})();");
            }
            catch { }
        }

        private void HandleBrowserPointerInput()
        {
            if (_videoDisplay == null || !IsOpen || _gpuCompositionCapture == null)
            {
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                if (_gameMouseCaptured) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } _gameMouseCaptured = false; }
                try { if (_browserHostCursorHidden) { _browserHostCursorHidden = false; Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } } catch { }
                return;
            }
            // Brief carry-over suppression after reattach, but never block user recovery clicks while focus recovery is active.
            // If the user clicks/taps during recovery, nudge focus and continue processing input.
            try
            {
                float nowRt = Time.realtimeSinceStartup;
                bool inRecovery = nowRt < _reattachFocusRecoveryUntilRealtime;
                bool inSuppress = nowRt < _reattachInteractionSuppressUntilRealtime;
                if (inSuppress && !inRecovery)
                {
                    _browserPointerDown = false;
                    _browserPointerRightDown = false;
                    return;
                }
                if (inRecovery)
                {
                    bool anyPointerIntent = false;
                    try { anyPointerIntent = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButton(0) || Input.GetMouseButton(1); } catch { }
                    if (inSuppress || anyPointerIntent)
                    {
                        try { EnsureReattachFocus("pointer_recovery"); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var rt = _videoDisplay.rectTransform;
                if (rt == null) return;
                var canvas = _videoDisplay.canvas;
                var cam = canvas != null ? canvas.worldCamera : null;
                if (cam == null) cam = Camera.main;

                bool mouseHeld = Input.GetMouseButton(0);
                bool mouseDown = Input.GetMouseButtonDown(0);
                bool mouseUp = Input.GetMouseButtonUp(0);
                bool rHeld = Input.GetMouseButton(1);
                bool rDown = Input.GetMouseButtonDown(1);
                bool rUp = Input.GetMouseButtonUp(1);

                int vw = Math.Max(1, _gpuCompositionCapture.ViewWidth);
                int vh = Math.Max(1, _gpuCompositionCapture.ViewHeight);

                bool urlFocusedNowChk = _urlInput != null && _urlInput.isFocused;
                bool gameModeActive = _gameInputMode && _compositionWebFocused && !urlFocusedNowChk;

                bool hasLocal = RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out var local);
                if (!hasLocal)
                {
                    if (_gameMouseCaptured) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } _gameMouseCaptured = false; }
                    try { if (_browserHostCursorHidden) { _browserHostCursorHidden = false; Cursor.visible = true; Cursor.lockState = CursorLockMode.None; } } catch { }
                    if (_browserPointerDown || _browserPointerRightDown)
                    {
                        int edgeX = Mathf.Clamp(_lastSentPointerX, 0, vw - 1);
                        int edgeY = Mathf.Clamp(_lastSentPointerY, 0, vh - 1);
                        if (mouseHeld || rHeld)
                        {
                            bool due = (Time.unscaledTime - _lastPointerMoveSendTime) >= 0.012f;
                            if (due)
                            {
                                _lastSentPointerX = edgeX; _lastSentPointerY = edgeY; _lastPointerMoveSendTime = Time.unscaledTime;
                                try { _gpuCompositionCapture.SendMouseMove(edgeX, edgeY, _browserPointerDown && mouseHeld, _browserPointerRightDown && rHeld); } catch { }
                            }
                        }
                        if (_browserPointerDown && (mouseUp || !mouseHeld)) { try { _gpuCompositionCapture.SendMouseUp(edgeX, edgeY, false); } catch { } _browserPointerDown = false; }
                        if (_browserPointerRightDown && (rUp || !rHeld)) { try { _gpuCompositionCapture.SendMouseUp(edgeX, edgeY, true); } catch { } _browserPointerRightDown = false; }
                        return;
                    }
                    if (mouseDown)
                    {
                        bool wasWebFocused = _compositionWebFocused;
                        _compositionWebFocused = false;
                        if (wasWebFocused)
                        {
                            LogCompositionTypingDiag("webFocus cleared by click outside browser viewport");
                            UpdateInputOwnershipState();
                        }
                        try
                        {
                            if (_urlInput != null && _urlInput.gameObject.activeInHierarchy && _urlInput.interactable)
                            {
                                var urlRt = _urlInput.GetComponent<RectTransform>();
                                if (urlRt != null && RectTransformUtility.RectangleContainsScreenPoint(urlRt, Input.mousePosition, cam))
                                {
                                    _urlInput.ActivateInputField();
                                    try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(_urlInput.gameObject); } catch { }
                                    try { _urlInput.caretPosition = (_urlInput.text ?? "").Length; _urlInput.selectionAnchorPosition = _urlInput.caretPosition; _urlInput.selectionFocusPosition = _urlInput.caretPosition; } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    return;
                }
                var rect = rt.rect;
                if (rect.width <= 0.01f || rect.height <= 0.01f) return;
                float nx = (local.x - rect.xMin) / rect.width;
                float ny = (rect.yMax - local.y) / rect.height;
                bool inside = nx >= 0f && nx <= 1f && ny >= 0f && ny <= 1f;
                int px = Mathf.Clamp(Mathf.RoundToInt(nx * (vw - 1)), 0, vw - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt(ny * (vh - 1)), 0, vh - 1);

                // In game mode, avoid Unity cursor locking/virtual-mouse capture (can conflict with in-page cursors/pointer lock).
                // Keep normal absolute pointer mapping so the website/game can handle its own cursor behavior.
                if (_gameMouseCaptured)
                {
                    try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                    _gameMouseCaptured = false;
                }

                // Cursor policy: keep host cursor visible for normal web browsing (WebView2 composition doesn't render OS cursor in texture).
                // Hide host cursor only in Game Input Mode over web content, where the browser game/page typically provides its own cursor/capture UX.
                try
                {
                    bool urlFocusedNow2 = _urlInput != null && _urlInput.isFocused;
                    bool hideHostCursor = inside && IsOpen && !_detachedToGameplay && !urlFocusedNow2 && _gameInputMode && _compositionWebFocused;
                    if (hideHostCursor != _browserHostCursorHidden)
                    {
                        _browserHostCursorHidden = hideHostCursor;
                        Cursor.visible = !hideHostCursor;
                        Cursor.lockState = CursorLockMode.None;
                    }
                    else if (hideHostCursor)
                    {
                        if (Cursor.visible) Cursor.visible = false;
                        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                    }
                    else
                    {
                        // Ensure visible when not intentionally hidden (avoid getting stuck hidden).
                        if (!Cursor.visible && !_gameMouseCaptured) Cursor.visible = true;
                        if (Cursor.lockState != CursorLockMode.None && !_gameMouseCaptured) Cursor.lockState = CursorLockMode.None;
                    }
                }
                catch { }
                if (!inside)
                {
                    if (mouseDown && !(_browserPointerDown || _browserPointerRightDown))
                    {
                        bool wasWebFocused = _compositionWebFocused;
                        _compositionWebFocused = false;
                        if (wasWebFocused)
                        {
                            LogCompositionTypingDiag("webFocus cleared by click outside browser content area");
                            UpdateInputOwnershipState();
                        }
                    }

                    if (_browserPointerDown || _browserPointerRightDown)
                    {
                        bool due = (Time.unscaledTime - _lastPointerMoveSendTime) >= 0.012f;
                        if (due)
                        {
                            _lastSentPointerX = px; _lastSentPointerY = py; _lastPointerMoveSendTime = Time.unscaledTime;
                            try { _gpuCompositionCapture.SendMouseMove(px, py, _browserPointerDown && mouseHeld, _browserPointerRightDown && rHeld); } catch { }
                        }
                        if (_browserPointerDown && (mouseUp || !mouseHeld)) { try { _gpuCompositionCapture.SendMouseUp(px, py, false); } catch { } _browserPointerDown = false; }
                        if (_browserPointerRightDown && (rUp || !rHeld)) { try { _gpuCompositionCapture.SendMouseUp(px, py, true); } catch { } _browserPointerRightDown = false; }
                        return;
                    }

                    try
                    {
                        var scrollOutside = Input.mouseScrollDelta.y;
                        bool ctrlOutside = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                        if (ctrlOutside && Mathf.Abs(scrollOutside) > 0.001f)
                            UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Ctrl+wheel ignored: pointer outside browser content (nx={nx:0.###} ny={ny:0.###})");
                    }
                    catch { }

                    return;
                }

                bool urlFocusedNow = _urlInput != null && _urlInput.isFocused;
                bool webFocusedNow = _compositionWebFocused && !urlFocusedNow;
                bool allowRightClickToWeb = _gameInputMode && webFocusedNow;

                // Only start a browser drag/click on an actual press inside the view.
                if (mouseDown)
                {
                    _browserPointerDown = true;
                    bool wasWebFocused = _compositionWebFocused;
                    _compositionWebFocused = true;
                    try
                    {
                        if (_urlInput != null && _urlInput.isFocused)
                        {
                            try { _urlInput.DeactivateInputField(); } catch { }
                            try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                        }
                    }
                    catch { }
                    UpdateInputOwnershipState();
                    LogCompositionTypingDiag($"webClick inside px={px} py={py} wasWebFocused={wasWebFocused} urlFocused={(_urlInput != null && _urlInput.isFocused)}");
                    // Request native web focus on click when transitioning, and also periodically if focus may be stale.
                    try { _gpuCompositionCapture.RequestFocus(); } catch { }
                    // In game mode, help focus the actual game canvas/element so keyboard events reach it.
                    if (_gameInputMode)
                    {
                        try
                        {
                            _gpuCompositionCapture.ExecuteScript("(function(){try{var d=document,c=d.querySelector('canvas'); if(c){try{if(c.tabIndex==null||c.tabIndex<0)c.tabIndex=0;}catch(_){} try{if(typeof c.focus==='function') c.focus();}catch(_){}} try{window&&window.focus&&window.focus();}catch(_){} try{d.body&&d.body.focus&&d.body.focus();}catch(_){} }catch(_){}})();");
                        }
                        catch { }
                    }
                    _gpuCompositionCapture.SendMouseMove(px, py, false, _browserPointerRightDown && rHeld);
                    _gpuCompositionCapture.SendMouseDown(px, py, false);
                }
                else if (rDown && allowRightClickToWeb)
                {
                    _browserPointerRightDown = true;
                    bool wasWebFocused = _compositionWebFocused;
                    _compositionWebFocused = true;
                    try
                    {
                        if (_urlInput != null && _urlInput.isFocused)
                        {
                            try { _urlInput.DeactivateInputField(); } catch { }
                            try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                        }
                    }
                    catch { }
                    UpdateInputOwnershipState();
                    if (!wasWebFocused) { try { _gpuCompositionCapture.RequestFocus(); } catch { } }
                    _gpuCompositionCapture.SendMouseMove(px, py, _browserPointerDown && mouseHeld, false);
                    _gpuCompositionCapture.SendMouseDown(px, py, true);
                }
                else
                {
                    bool moved = (px != _lastSentPointerX || py != _lastSentPointerY);
                    bool due = (Time.unscaledTime - _lastPointerMoveSendTime) >= 0.012f; // ~83Hz cap to reduce input flooding
                    if (moved || due)
                    {
                        _lastSentPointerX = px;
                        _lastSentPointerY = py;
                        _lastPointerMoveSendTime = Time.unscaledTime;
                        _gpuCompositionCapture.SendMouseMove(px, py, _browserPointerDown && mouseHeld, _browserPointerRightDown && rHeld);
                    }
                }

                // Always release tracked button state when Unity reports mouse-up (or button no longer held) to avoid stuck web drags/selections.
                if (_browserPointerDown && (mouseUp || !mouseHeld))
                {
                    try { _gpuCompositionCapture.SendMouseUp(px, py, false); } catch { }
                    _browserPointerDown = false;
                }
                if (_browserPointerRightDown && (rUp || !rHeld))
                {
                    try { _gpuCompositionCapture.SendMouseUp(px, py, true); } catch { }
                    _browserPointerRightDown = false;
                }

                var scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.001f)
                {
                    bool ctrlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
                    if (ctrlDown)
                    {
                        try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Ctrl+wheel detected over browser (scroll={scroll:0.###}, inside=True, webFocused={_compositionWebFocused}, urlFocused={_urlInput != null && _urlInput.isFocused})"); } catch { }
                        try
                        {
                            double z = _gpuCompositionCapture.GetZoomFactor();
                            if (!(z > 0.0)) z = 1.0;
                            double step = scroll > 0 ? 0.10 : -0.10;
                            // Slightly larger step for fast wheel gestures.
                            if (Mathf.Abs(scroll) >= 2f) step *= 1.5;
                            double nz = Mathf.Clamp((float)(z + step), 0.25f, 5.0f);
                            _gpuCompositionCapture.SetZoomFactor(nz);
                            try { _gpuCompositionCapture.RequestFocus(); } catch { }
                            UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Web zoom -> {nz:0.00} (ctrl+wheel, requested)");
                        }
                        catch (Exception ex) { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Web zoom failed (restart game after native update?): {ex.Message}"); }
                        // Ctrl+wheel is consumed for zoom; suppress hotbar/game wheel side effects.
                        try { TrySuppressHotbarScroll(); } catch { }
                    }
                    else
                    {
                        int delta = Mathf.RoundToInt(scroll * 120f);
                        try { _gpuCompositionCapture.RequestFocus(); } catch { }
                        _gpuCompositionCapture.SendMouseWheel(px, py, delta);
                        // Wheel is forwarded to webpage; hotbar switching is blocked by InputHooks when ownership active.
                        if (!UnderNetBrowserInputHooks.Installed)
                        {
                            try { TrySuppressHotbarScroll(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Pointer input error: {ex.Message}"); }
        }

        private void BuildUI(GameObject container)
        {
            var bgGo = new GameObject("UnderNetBrowser_Background");
            bgGo.transform.SetParent(container.transform, false);
            var bgRT = bgGo.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.06f, 0.06f, 0.08f, 1f);
            bgImage.raycastTarget = true;

            // Browser content (fills most of screen; adjusted by chrome/fullscreen state)
            var videoGo = new GameObject("UnderNetBrowser_VideoDisplay");
            videoGo.transform.SetParent(container.transform, false);
            _videoRT = videoGo.AddComponent<RectTransform>();
            _videoDisplay = videoGo.AddComponent<RawImage>();
            _videoDisplay.color = Color.white;
            _videoDisplay.raycastTarget = false;

            var hintGo = new GameObject("UnderNetBrowser_HintText");
            hintGo.transform.SetParent(videoGo.transform, false);
            var hintRT = hintGo.AddComponent<RectTransform>();
            hintRT.anchorMin = Vector2.zero; hintRT.anchorMax = Vector2.one; hintRT.offsetMin = Vector2.zero; hintRT.offsetMax = Vector2.zero;
            _hintText = hintGo.AddComponent<Text>();
            _hintText.font = GetUIFont();
            _hintText.text = "UnderNet Browser\nLoading...";
            _hintText.fontSize = 24;
            _hintText.color = new Color(0.95f, 0.98f, 0.96f, 1f);
            _hintText.alignment = TextAnchor.MiddleCenter;
            _hintText.raycastTarget = false;
            _hintText.supportRichText = true;
            try
            {
                var shadow = hintGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.75f);
                shadow.effectDistance = new Vector2(1f, -1f);
            }
            catch { }

            // Top browser chrome (always visible except page fullscreen)
            _chromeRoot = new GameObject("UnderNetBrowser_Chrome");
            _chromeRoot.transform.SetParent(container.transform, false);
            _chromeRT = _chromeRoot.AddComponent<RectTransform>();
            ApplyChromeLayout();
            _chromeRT.offsetMin = Vector2.zero; _chromeRT.offsetMax = Vector2.zero;
            var chromeBg = _chromeRoot.AddComponent<Image>();
            chromeBg.color = new Color(0.090f, 0.096f, 0.112f, 0.985f);
            chromeBg.raycastTarget = true;
            try
            {
                var topLine = _chromeRoot.AddComponent<Outline>();
                topLine.effectColor = new Color(1f, 1f, 1f, 0.04f);
                topLine.effectDistance = new Vector2(0f, -1f);
            }
            catch { }

            var chromeLayout = _chromeRoot.AddComponent<HorizontalLayoutGroup>();
            chromeLayout.spacing = 8f;
            chromeLayout.childAlignment = TextAnchor.MiddleCenter;
            chromeLayout.childForceExpandWidth = false;
            chromeLayout.childForceExpandHeight = false;
            chromeLayout.childControlWidth = true;
            chromeLayout.childControlHeight = true;
            chromeLayout.padding = new RectOffset(10, 10, 6, 6);

            var navBg = new Color(0.165f, 0.176f, 0.198f, 1f);
            var accentBg = new Color(0.150f, 0.210f, 0.235f, 1f);
            var homeBg = new Color(0.150f, 0.205f, 0.175f, 1f);
            var dangerBg = new Color(0.38f, 0.16f, 0.18f, 1f);

            _backButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_BackBtn", "←", new Vector2(34, 30), navBg, () => {
                if (_externalMirrorMode)
                {
                    var ret = _externalMirrorReturnUrl;
                    try { StopExternalWindowMirror("back_button"); } catch { }
                    try { EnsureCompositionBrowserStarted(string.IsNullOrWhiteSpace(ret) ? GetResolvedHomeUrl() : ret); } catch { }
                    return;
                }
                _gpuCompositionCapture?.GoBack();
            }, 18);
            _forwardButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_FwdBtn", "→", new Vector2(34, 30), navBg, () => {
                if (_externalMirrorMode) { SetHint("Discord mirror active: Back/Home to return."); return; }
                _gpuCompositionCapture?.GoForward();
            }, 18);
            _reloadButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_ReloadBtn", "↻", new Vector2(34, 30), accentBg, () => {
                if (_externalMirrorMode)
                {
                    try { StopExternalWindowMirror("reload_button"); } catch { }
                    if (!TryStartDiscordMirror("undernet:discord"))
                    {
                        SetHint("Discord mirror unavailable (is Discord running?). Back/Home to return.");
                        try { EnsureCompositionBrowserStarted(string.IsNullOrWhiteSpace(_externalMirrorReturnUrl) ? GetResolvedHomeUrl() : _externalMirrorReturnUrl); } catch { }
                    }
                    return;
                }
                _gpuCompositionCapture?.Reload();
            }, 17);
            _homeButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_HomeBtn", "⌂", new Vector2(34, 30), homeBg, () => {
                try { ReleaseAllGameKeys("home_button"); } catch { }
                if (_externalMirrorMode)
                {
                    try { StopExternalWindowMirror("home_button"); } catch { }
                    try { EnsureCompositionBrowserStarted(GetResolvedHomeUrl()); } catch { }
                    try { _compositionWebFocused = true; UpdateInputOwnershipState(); } catch { }
                    return;
                }
                var raw = "undernet:home";
                var home = GetResolvedHomeUrl();
                if (_urlInput != null)
                {
                    _urlInput.text = "undernet:home";
                    if (_urlInput.textComponent != null) { _urlInput.textComponent.text = "undernet:home"; _urlInput.textComponent.enabled = true; }
                    try { _urlInput.ForceLabelUpdate(); } catch { }
                }
                try { SetUrlDisplayText("undernet:home"); } catch { }
                if (_gpuCompositionCapture != null)
                    HardResetCompositionNavigate(home, "home_button");
                else
                    SetUrl(raw);
            }, 17);

            _urlInput = CreateInput(_chromeRoot.transform, "UnderNetBrowser_UrlInput", new Vector2(240, 30), "Search or enter address");
            var urlLayout = _urlInput.GetComponent<LayoutElement>() ?? _urlInput.gameObject.AddComponent<LayoutElement>();
            urlLayout.flexibleWidth = 1f;
            urlLayout.minWidth = 90f;
            urlLayout.preferredWidth = 240f;
            urlLayout.minHeight = 24f;
            urlLayout.preferredHeight = 30f;
            urlLayout.flexibleHeight = 0f;
            // Reliable visible URL label overlay (InputField text rendering has been unreliable in this TV UI context).
            try
            {
                var overlayGo = new GameObject("UnderNetBrowser_UrlDisplayOverlay");
                overlayGo.transform.SetParent(_urlInput.transform, false);
                var ort = overlayGo.AddComponent<RectTransform>();
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one;
                ort.offsetMin = new Vector2(34, 2); ort.offsetMax = new Vector2(-14, -2);
                _urlDisplayText = overlayGo.AddComponent<Text>();
                _urlDisplayText.font = GetUIFont();
                _urlDisplayText.fontSize = 14;
                _urlDisplayText.color = new Color(0.97f, 0.98f, 1f, 1f);
                _urlDisplayText.alignment = TextAnchor.MiddleLeft;
                _urlDisplayText.supportRichText = false;
                _urlDisplayText.raycastTarget = false;
                _urlDisplayText.horizontalOverflow = HorizontalWrapMode.Overflow;
                _urlDisplayText.verticalOverflow = VerticalWrapMode.Truncate;
                _urlDisplayText.text = "undernet:home";
            }
            catch { }
            try
            {
                var homeDisplay = UnderNetBrowserConfig.HomeUrl?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(homeDisplay)) homeDisplay = "undernet:home";
                if (homeDisplay.Equals("about:home", StringComparison.OrdinalIgnoreCase) || homeDisplay.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase)) homeDisplay = "undernet:home";
                _urlInput.text = homeDisplay;
                if (_urlDisplayText != null) _urlDisplayText.text = homeDisplay;
                if (_urlInput.textComponent != null) { _urlInput.textComponent.text = homeDisplay; _urlInput.textComponent.color = new Color(0.97f, 0.98f, 1f, 1f); }
                if (_urlInput.placeholder is Text ph) ph.enabled = string.IsNullOrEmpty(homeDisplay);
                _lastSyncedUrl = homeDisplay;
                _urlInput.interactable = true;
            }
            catch { }
            // Do not navigate on end-edit/focus loss (e.g. clicking into the web page) - that would auto-submit the URL.
            // Navigation is explicit via Enter/submit or the Go/home/reload UI actions.
            try { EventHelper.AddListener((string _) => SetUrl(_urlInput?.text), _urlInput.onSubmit); } catch { }

            // Visual indicator for Game Input Mode (F6)
            try
            {
                _gameModeIndicatorRoot = new GameObject("UnderNetBrowser_GameModeIndicator");
                _gameModeIndicatorRoot.transform.SetParent(_chromeRoot.transform, false);
                var irt = _gameModeIndicatorRoot.AddComponent<RectTransform>();
                irt.sizeDelta = new Vector2(118, 24);
                var ile = _gameModeIndicatorRoot.AddComponent<LayoutElement>();
                ile.preferredWidth = 118; ile.minWidth = 88; ile.preferredHeight = 24; ile.minHeight = 22; ile.flexibleWidth = 0; ile.flexibleHeight = 0;
                var ibg = _gameModeIndicatorRoot.AddComponent<Image>();
                ibg.color = new Color(0.16f, 0.62f, 0.34f, 0.98f);
                ibg.raycastTarget = false;
                try
                {
                    var ibgOutline = _gameModeIndicatorRoot.AddComponent<Outline>();
                    ibgOutline.effectColor = new Color(1f, 1f, 1f, 0.10f);
                    ibgOutline.effectDistance = new Vector2(1f, -1f);
                }
                catch { }
                var igo = new GameObject("Text");
                igo.transform.SetParent(_gameModeIndicatorRoot.transform, false);
                var itrt = igo.AddComponent<RectTransform>();
                itrt.anchorMin = Vector2.zero; itrt.anchorMax = Vector2.one; itrt.offsetMin = new Vector2(10, 2); itrt.offsetMax = new Vector2(-10, -2);
                _gameModeIndicatorText = igo.AddComponent<Text>();
                _gameModeIndicatorText.font = GetUIFont();
                _gameModeIndicatorText.fontSize = 12;
                _gameModeIndicatorText.color = new Color(0.98f, 1f, 0.99f, 1f);
                _gameModeIndicatorText.alignment = TextAnchor.MiddleCenter;
                _gameModeIndicatorText.supportRichText = false;
                _gameModeIndicatorText.raycastTarget = false;
                _gameModeIndicatorText.fontStyle = FontStyle.Bold;
                _gameModeIndicatorText.text = "● GAME MODE · F6";
                _gameModeIndicatorRoot.SetActive(false);
            }
            catch { }

            _closeButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_CloseBtn", "✕", new Vector2(34, 30), dangerBg, CloseAppToTvHome, 16);
            // Opaque cap behind the close button region to hide URL overlay overflow under/around the X (RectMask2D on the input broke text visibility).
            try
            {
                if (_chromeRoot != null && _closeButton != null)
                {
                    var cap = new GameObject("UnderNetBrowser_ChromeRightCap");
                    cap.transform.SetParent(_chromeRoot.transform, false);
                    cap.transform.SetSiblingIndex(_closeButton.transform.GetSiblingIndex()); // place before close button so X draws on top
                    var capImg = cap.AddComponent<UnityEngine.UI.Image>();
                    capImg.color = new Color(0.090f, 0.096f, 0.112f, 0.985f);
                    capImg.raycastTarget = false;
                    var capLE = cap.AddComponent<UnityEngine.UI.LayoutElement>();
                    capLE.ignoreLayout = true;
                    var capRT = cap.GetComponent<RectTransform>();
                    capRT.anchorMin = new Vector2(1f, 0f);
                    capRT.anchorMax = new Vector2(1f, 1f);
                    capRT.pivot = new Vector2(1f, 0.5f);
                    capRT.sizeDelta = new Vector2(52f, 0f); // cover right padding + close button area
                    capRT.anchoredPosition = Vector2.zero;
                }
            }
            catch { }

            _isPageFullscreen = false;
            UpdateChromeVisibility();

            try
            {
                Canvas.ForceUpdateCanvases();
                if (_chromeRT != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_chromeRT);
                var urlRt = _urlInput?.GetComponent<RectTransform>();
                if (urlRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(urlRt);
                if (_urlInput != null)
                {
                    if (string.IsNullOrEmpty(_urlInput.text)) _urlInput.text = "undernet:home";
                    if (_urlInput.textComponent != null)
                    {
                        _urlInput.textComponent.text = _urlInput.text;
                        _urlInput.textComponent.enabled = true;
                        _urlInput.textComponent.color = new Color(0.95f, 0.96f, 0.98f, 1f);
                        _urlInput.textComponent.gameObject.SetActive(true);
                    }
                    try { _urlInput.ForceLabelUpdate(); } catch { }
                }
                try { SetUrlDisplayText(_urlInput?.text ?? "undernet:home"); } catch { }
                Canvas.ForceUpdateCanvases();
                try
                {
                    var f = GetUIFont();
                    var ugo = _urlInput?.gameObject;
                    var ur = _urlInput?.GetComponent<RectTransform>()?.rect;
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] UrlBarDiag(build): input={(ugo!=null)} active={ugo?.activeInHierarchy} text='{_urlInput?.text}' disp='{_urlDisplayText?.text}' dispActive={_urlDisplayText?.gameObject?.activeInHierarchy} rect={(ur.HasValue ? $"{ur.Value.width:0.#}x{ur.Value.height:0.#}" : "null")} font={(f!=null ? f.name : "null")}");
                }
                catch { }
            }
            catch { }
        }

        private void UpdateChromeVisibility()
        {
            try
            {
                bool showChrome = !_isPageFullscreen;
                if (_chromeRoot != null && _chromeRoot.activeSelf != showChrome) _chromeRoot.SetActive(showChrome);
                ApplyChromeLayout();
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] UpdateChromeVisibility failed: {ex.Message}"); }
        }

        private void ApplyChromeLayout()
        {
            try
            {
                float chromeHFrac = UnderNetBrowserConfig.GetChromeHeightFraction();
                float topYMin = Mathf.Clamp01(1f - chromeHFrac);
                bool showChrome = !_isPageFullscreen;

                if (_chromeRT != null)
                {
                    _chromeRT.anchorMin = new Vector2(0f, topYMin);
                    _chromeRT.anchorMax = new Vector2(1f, 1f);
                    _chromeRT.offsetMin = Vector2.zero;
                    _chromeRT.offsetMax = Vector2.zero;

                    // Scale chrome controls/padding to better match configured height (stable with ChromeHeightFraction config).
                    try
                    {
                        float containerH = 1080f;
                        try
                        {
                            var rootRt = _chromeRT.root as RectTransform;
                            if (rootRt != null && rootRt.rect.height > 10f) containerH = rootRt.rect.height;
                            else if (_videoRT != null && _videoRT.parent is RectTransform prt && prt.rect.height > 10f) containerH = prt.rect.height;
                        }
                        catch { }
                        float chromePx = Mathf.Clamp(containerH * chromeHFrac, 24f, 220f);
                        int padV = Mathf.Clamp(Mathf.RoundToInt(chromePx * 0.14f), 3, 14);
                        int padH = Mathf.Clamp(Mathf.RoundToInt(chromePx * 0.22f), 6, 22);
                        int padTop = Mathf.Clamp(padV + 2, 3, 16);
                        int padBottom = Mathf.Max(2, padV - 1);
                        float innerH = Mathf.Max(16f, chromePx - padTop - padBottom - 1f);
                        float controlH = Mathf.Clamp(innerH, 16f, 56f);
                        if (chromePx < 38f) controlH = Mathf.Max(14f, chromePx - (padTop + padBottom + 1f));
                        float btnW = Mathf.Clamp(controlH * 1.05f, 24f, 64f);
                        int font = Mathf.Clamp(Mathf.RoundToInt(controlH * 0.58f), 11, 26);
                        int urlFont = Mathf.Clamp(Mathf.RoundToInt(controlH * 0.46f), 10, 22);

                        var hlg = _chromeRoot != null ? _chromeRoot.GetComponent<HorizontalLayoutGroup>() : null;
                        if (hlg != null)
                        {
                            hlg.childAlignment = TextAnchor.MiddleLeft;
                            // Extra top padding reduces content clipping under TV screen/canvas edge masks.
                            hlg.padding = new RectOffset(padH, padH, padTop, padBottom);
                            hlg.spacing = Mathf.Clamp(chromePx * 0.10f, 4f, 12f);
                        }

                        void ApplyButtonSizing(Button? b)
                        {
                            if (b == null) return;
                            try
                            {
                                var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
                                le.preferredWidth = btnW; le.minWidth = Mathf.Max(22f, btnW * 0.9f);
                                le.preferredHeight = controlH; le.minHeight = controlH;
                                var t = b.GetComponentInChildren<Text>(true);
                                if (t != null) t.fontSize = font;
                            }
                            catch { }
                        }

                        ApplyButtonSizing(_backButton);
                        ApplyButtonSizing(_forwardButton);
                        ApplyButtonSizing(_reloadButton);
                        ApplyButtonSizing(_homeButton);
                        ApplyButtonSizing(_closeButton);

                        float containerW = 685f;
                        try
                        {
                            if (_chromeRT != null && _chromeRT.rect.width > 10f) containerW = _chromeRT.rect.width;
                            else
                            {
                                var rootRt = _chromeRT != null ? _chromeRT.root as RectTransform : null;
                                if (rootRt != null && rootRt.rect.width > 10f) containerW = rootRt.rect.width;
                            }
                        }
                        catch { }

                        if (_urlInput != null)
                        {
                            try
                            {
                                var le = _urlInput.GetComponent<LayoutElement>() ?? _urlInput.gameObject.AddComponent<LayoutElement>();
                                le.flexibleWidth = 1f;
                                le.preferredHeight = controlH; le.minHeight = controlH;
                                // Keep URL bar shrinkable on small in-game TV canvases (e.g. ~685px wide).
                                le.minWidth = Mathf.Clamp(containerW * 0.22f, 90f, 180f);
                                le.preferredWidth = Mathf.Clamp(containerW * 0.42f, 140f, 520f);
                                if (_urlInput.textComponent != null) _urlInput.textComponent.fontSize = urlFont;
                                if (_urlInput.placeholder is Text ph) ph.fontSize = Mathf.Max(10, urlFont - 1);
                                if (_urlDisplayText != null) _urlDisplayText.fontSize = urlFont;
                            }
                            catch { }
                        }

                        if (_gameModeIndicatorRoot != null)
                        {
                            try
                            {
                                var ile = _gameModeIndicatorRoot.GetComponent<LayoutElement>() ?? _gameModeIndicatorRoot.AddComponent<LayoutElement>();
                                ile.preferredHeight = Mathf.Max(22f, controlH * 0.92f);
                                ile.minHeight = Mathf.Max(20f, controlH * 0.85f);
                                // Compact status pill so it doesn't crowd small widths.
                                ile.preferredWidth = Mathf.Clamp(controlH * 4.2f, 96f, 150f);
                                ile.minWidth = Mathf.Max(88f, ile.preferredWidth * 0.85f);
                                if (_gameModeIndicatorText != null)
                                {
                                    _gameModeIndicatorText.fontSize = Mathf.Clamp(Mathf.RoundToInt(controlH * 0.34f), 9, 14);
                                    _gameModeIndicatorText.resizeTextForBestFit = true;
                                    _gameModeIndicatorText.resizeTextMinSize = 8;
                                    _gameModeIndicatorText.resizeTextMaxSize = Mathf.Clamp(Mathf.RoundToInt(controlH * 0.34f), 9, 14);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (_videoRT != null)
                {
                    if (showChrome)
                    {
                        // Flush join to chrome strip to avoid visible seam/gap artifacts.
                        _videoRT.anchorMin = new Vector2(0f, 0f);
                        _videoRT.anchorMax = new Vector2(1f, topYMin);
                    }
                    else
                    {
                        _videoRT.anchorMin = new Vector2(0f, 0f);
                        _videoRT.anchorMax = new Vector2(1f, 1f);
                    }
                    _videoRT.offsetMin = Vector2.zero;
                    _videoRT.offsetMax = Vector2.zero;
                }
            }
            catch { }
        }

        // Composition mode polls page fullscreen state during OnUpdate to hide/show chrome.

        private void CloseAppToTvHome()
        {
            if (_browserUiCriticalActionInProgress)
            {
                try { UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Close requested (X) ignored: browser UI action in progress."); } catch { }
                return;
            }
            bool invokedFrameworkClose = false;
            _browserUiCriticalActionInProgress = true;
            try
            {
                MelonLogger.Msg("[Firesky UnderNet Browser] [UnderNetBrowser] Close requested (X).");
                _explicitCloseRequested = true;
                _warmCloseRequested = false;
                AnyDetachActive = false;
                _detachRequested = false;
                // Try common S1API/TVApp close methods without assuming a specific signature.
                var t = GetType();
                string[] names = { "Close", "ForceClose", "Exit", "CloseApp", "ReturnToHome", "GoHome", "Back" };
                foreach (var n in names)
                {
                    try
                    {
                        var m = t.GetMethod(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (m != null)
                        {
                            invokedFrameworkClose = true;
                            m.Invoke(this, null);
                            return;
                        }
                    }
                    catch { invokedFrameworkClose = false; }
                }
                // Fallback: treat as close callback (will at least stop our browser/HUD restore) if framework doesn't expose explicit close.
                try { OnClosed(); } catch { }
                UnderNetBrowserConfig.Log("[UnderNetBrowser] No TVApp close method found via reflection; performed local close cleanup only.");
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Close failed: {ex.Message}"); }
            finally
            {
                // If framework close was invoked, OnClosed may run later and must see explicit-close intent.
                // It will clear the flag; clear here only for immediate/fallback paths.
                if (!invokedFrameworkClose) _explicitCloseRequested = false;
                _warmCloseRequested = false;
                _browserUiCriticalActionInProgress = false;
            }
        }

        private void RequestWarmClose(string reason)
        {
            if (_browserUiCriticalActionInProgress)
            {
                try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Warm close requested ({reason}) ignored: browser UI action in progress."); } catch { }
                return;
            }
            bool invokedFrameworkClose = false;
            _browserUiCriticalActionInProgress = true;
            try
            {
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Warm close requested ({reason}).");
                _warmCloseRequested = true;
                _explicitCloseRequested = false;
                _detachRequested = false;
                _detachedToGameplay = false;
                AnyDetachActive = false;
                try { ClearReattachFocusRecoveryState(); } catch { }
                try { RestoreDetachedHomeScreenObjects(restoreHiddenObjects: false); } catch { }
                try { SetAppInteractionEnabled(true); } catch { }

                var t = GetType();
                string[] names = { "Close", "ForceClose", "Exit", "CloseApp", "ReturnToHome", "GoHome", "Back" };
                foreach (var n in names)
                {
                    try
                    {
                        var m = t.GetMethod(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (m != null)
                        {
                            invokedFrameworkClose = true;
                            m.Invoke(this, null);
                            return;
                        }
                    }
                    catch { invokedFrameworkClose = false; }
                }
                // Fallback: local close callback path (treat as normal/warm close, not explicit X shutdown).
                try { OnClosed(); } catch { }
                UnderNetBrowserConfig.Log("[UnderNetBrowser] No TVApp close method found for warm close; performed local close cleanup only.");
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Warm close failed: {ex.Message}"); }
            finally
            {
                if (!invokedFrameworkClose) _warmCloseRequested = false;
                _browserUiCriticalActionInProgress = false;
            }
        }

        private bool _detachRequested;
        private float _detachRequestedTime;
        private float _reattachInteractionSuppressUntilRealtime;
        private bool _explicitCloseRequested;
        private bool _warmCloseRequested;
        private float _lastDetachedReattachCheckRealtime;
        private bool _detachedTvInterfaceWasOpen;
        private float _lastDetachedVisualMaintainRealtime;
        private float _lastDetachedExitEnsureRealtime;
        private float _lastTvScreenMirrorAttemptRealtime;
        // Rapid detach/reattach safeguards + focus recovery
        private float _lastDetachRequestRealtime = -999f;
        private float _lastDetachExitInvokeRealtime = -999f;
        private float _reattachBlockedUntilRealtime = -999f;
        private float _lastReattachRealtime = -999f;
        private float _reattachFocusRecoveryUntilRealtime = -999f;
        private float _nextReattachFocusRetryRealtime = -999f;
        private int _reattachFocusRetryCount;
        private int _lastLoggedReattachBlockBucket = int.MinValue;
        private readonly List<(GameObject go, bool wasActive)> _detachedHiddenHomeScreenObjects = new List<(GameObject, bool)>();
        private readonly List<(Renderer renderer, bool wasEnabled)> _detachedHiddenClockRenderers = new List<(Renderer, bool)>();
        private readonly List<(Renderer renderer, Material? materialInstance, Texture? originalMainTexture, string? originalShaderName)> _tvScreenMirrorStates = new List<(Renderer, Material?, Texture?, string?)>();
        internal static volatile bool AnyDetachActive;

        // Harmony prefix target (patched from MelonMod.OnInitializeMelon). Blocks S1API TVApp.Close/ForceClose during detach
        // or when ESC is being used to exit web/page fullscreen (so the game doesn't also close the TV app).
        private static float _suppressAppCloseUntilRealtime;
        internal static void SuppressAppCloseBriefly(float seconds = 1.5f)
        {
            try
            {
                float until = Time.realtimeSinceStartup + Math.Max(0.1f, seconds);
                if (until > _suppressAppCloseUntilRealtime) _suppressAppCloseUntilRealtime = until;
            }
            catch { }
        }

        // When detached to gameplay, do not consume S1API TVApp exit handling and allow ESC to reach pause menu/game.
        // When attached, consume ESC ourselves (mark ExitAction.Used) to prevent pause menu, and prevent S1API TV exit/close side effects.
        internal static bool TVAppHandleExitPrefix(object __instance, object __0)
        {
            try
            {
                if (__instance is UnderNetBrowserApp app)
                {
                    if (app._detachedToGameplay)
                        return false; // skip HandleExit so ESC can reach pause menu/game while app stays running

                    if (app.IsOpen && !app.IsPaused)
                    {
                        bool esc = false;
                        try { esc = Input.GetKeyDown(KeyCode.Escape) || Input.GetKey(KeyCode.Escape); } catch { }
                        if (esc)
                        {
                            bool fs = app._isPageFullscreen;
                            try { fs = fs || (app._gpuCompositionCapture != null && app._gpuCompositionCapture.IsFullscreen); } catch { }
                            if (app._gameInputMode || fs)
                            {
                                try { SuppressAppCloseBriefly(2.5f); } catch { }
                                TryMarkExitActionUsed(__0);
                                return false; // consume ESC for web/game-input handling; prevent pause menu and TV exit/close side effects
                            }

                            // Normal ESC (non-fullscreen, not game input): warm-close the app (keep browser session warm), not detach-to-gameplay.
                            if (!app._warmCloseRequested)
                            {
                                try { app.RequestWarmClose("esc"); } catch { }
                            }
                            TryMarkExitActionUsed(__0);
                            return false; // consume ESC to prevent pause menu; warm close will proceed via our close path
                        }
                    }
                }
            }
            catch { }
            return true;
        }

        private static void TryMarkExitActionUsed(object exitAction)
        {
            if (exitAction == null) return;
            try
            {
                var t = exitAction.GetType();
                var p = t.GetProperty("Used", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(exitAction, true);
                    return;
                }
            }
            catch { }
            try
            {
                var t = exitAction.GetType();
                var f = t.GetField("Used", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                     ?? t.GetField("used", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                     ?? t.GetField("_used", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null) f.SetValue(exitAction, true);
            }
            catch { }
            try
            {
                var m = exitAction.GetType().GetMethod("Use", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(exitAction, null);
            }
            catch { }
        }

        internal static bool TVAppClosePrefix(object __instance)
        {
            try
            {
                if (__instance is UnderNetBrowserApp app)
                {
                    // Explicit X-button close or intentional warm close should proceed.
                    if (app._explicitCloseRequested || app._warmCloseRequested) return true;

                    // If detaching (or recently detached), keep app alive/open instead of closing it.
                    if (app._detachRequested || (AnyDetachActive && (Time.realtimeSinceStartup - app._detachRequestedTime) < 3.0f))
                    {
                        UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Blocked TVApp.Close/ForceClose during detach (keeping app open).");
                        return false; // skip original close
                    }

                    // If app is stably detached to gameplay, keep it running in background (don't let game close it).
                    // Do not re-invoke TVInterface exit here: ESC/close attempts can cause feedback loops and break pause menu.
                    if (app._detachedToGameplay)
                    {
                        UnderNetBrowserConfig.Log("[UnderNetBrowser] Blocked TVApp.Close/ForceClose while detached (keeping app running in background).");
                        return false;
                    }

                    // Heuristic: right-click during open app is detach intent (exit TV to gameplay, keep app paused).
                    // Game/S1API may process RMB as back/exit and call Close() before/without our OnUpdate handler running.
                    try
                    {
                        bool rmb = false;
                        try { rmb = Input.GetMouseButtonDown(1) || Input.GetMouseButton(1); } catch { }
                        if (rmb && app.IsOpen && !app.IsPaused)
                        {
                            UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] TVApp.Close/ForceClose intercepted during RMB: converting to detach.");
                            try { app.TryDetachToGameplayKeepAppOpen(); } catch { }
                            // Ensure detach flags are set even if detach attempt failed mid-way.
                            app._detachRequested = true;
                            app._detachRequestedTime = Time.realtimeSinceStartup;
                            AnyDetachActive = true;
                            return false;
                        }
                    }
                    catch { }

                    // Heuristic: prevent ESC from closing the app when it should be handled by web content.
                    // - Game Input Mode: games/pages need ESC for menus (do not close/detach).
                    // - Page fullscreen: ESC must exit fullscreen (stay in app), preserve existing behavior.
                    try
                    {
                        bool esc = false;
                        try { esc = Input.GetKeyDown(KeyCode.Escape) || Input.GetKey(KeyCode.Escape); } catch { }
                        if (esc)
                        {
                            bool fs = app._isPageFullscreen;
                            try { fs = fs || (app._gpuCompositionCapture != null && app._gpuCompositionCapture.IsFullscreen); } catch { }
                            if (app._gameInputMode || fs)
                            {
                                SuppressAppCloseBriefly(2.5f);
                                try
                                {
                                    app._gpuCompositionCapture?.SendKey(0x0100, 0x1B, 0);
                                    app._gpuCompositionCapture?.SendKey(0x0101, 0x1B, 0);
                                    app._gpuCompositionCapture?.ExecuteScript("(function(){try{var d=document; var fs=d.fullscreenElement||d.webkitFullscreenElement||d.mozFullScreenElement||d.msFullscreenElement; if(fs){try{(d.exitFullscreen||d.webkitExitFullscreen||d.mozCancelFullScreen||d.msExitFullscreen||function(){}).call(d);}catch(_){} try{if(fs.exitFullscreen)fs.exitFullscreen();}catch(_){} } var k={key:'Escape',code:'Escape',keyCode:27,which:27,bubbles:true,cancelable:true,composed:true}; try{(d.activeElement||d.body).dispatchEvent(new KeyboardEvent('keydown',k)); (d.activeElement||d.body).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} try{window.dispatchEvent(new KeyboardEvent('keydown',k)); window.dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} try{var v=d.querySelector('video.html5-main-video, video'); if(v){v.dispatchEvent(new KeyboardEvent('keydown',k)); v.dispatchEvent(new KeyboardEvent('keyup',k));}}catch(_){} try{var c=d.querySelector('canvas'); if(c){c.dispatchEvent(new KeyboardEvent('keydown',k)); c.dispatchEvent(new KeyboardEvent('keyup',k));}}catch(_){} }catch(_){}})();");
                                }
                                catch { }
                                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Blocked TVApp.Close/ForceClose on ESC (gameMode=" + app._gameInputMode + ", fs=" + fs + ").");
                                return false;
                            }
                        }
                    }
                    catch { }

                    if (Time.realtimeSinceStartup < _suppressAppCloseUntilRealtime)
                    {
                        UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Blocked TVApp.Close/ForceClose during fullscreen-escape/app-close suppression.");
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Right-click behavior override: leave TV interaction/mode and resume gameplay while keeping this app
        /// running in the background. The app should continue to exist on the TV and retain its state;
        /// it is closed only via the app's X button.
        /// </summary>
        private void TryDetachToGameplayKeepAppOpen()
        {
            try
            {
                if (!IsOpen) return;
                float nowRt = Time.realtimeSinceStartup;

                // Debounce rapid detach requests to avoid TV/S1API/WebView focus races when user toggles quickly.
                if (_detachedToGameplay && (nowRt - _lastDetachRequestRealtime) < 0.5f)
                {
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                        UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach ignored: already detached (cooldown)");
                    return;
                }
                if ((nowRt - _lastDetachRequestRealtime) < 0.25f && (_detachRequested || AnyDetachActive))
                {
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                        UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach ignored: rapid duplicate request");
                    return;
                }

                _lastDetachRequestRealtime = nowRt;
                _detachRequested = true;
                _detachRequestedTime = nowRt;
                AnyDetachActive = true;
                _detachedToGameplay = true;
                // Block immediate false-positive reattach while TV/game state settles after detach.
                _reattachBlockedUntilRealtime = Mathf.Max(_reattachBlockedUntilRealtime, nowRt + 0.45f);
                _lastLoggedReattachBlockBucket = int.MinValue;
                _reattachFocusRecoveryUntilRealtime = -999f;
                _nextReattachFocusRetryRealtime = -999f;
                _reattachFocusRetryCount = 0;

                // Keep rendering active while detached so video playback remains visible on TV (audio already continues).
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
                _compositionWebFocused = false;
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                try { ReleaseAllGameKeys("detach_to_gameplay"); } catch { }
                try { UnderNetBrowserDiagnostics.OnDetachRequested(); } catch { }
                try { UnderNetBrowserDiagnostics.SetBrowserIntentionalAlive(true); } catch { }
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Right-click detach: keeping app open and attempting to exit TV mode (gameplay restore).");

                // Release gameplay/UI ownership immediately (similar to paused state) without pausing app rendering.
                // Pausing the S1API TV app can freeze visual updates while audio continues.
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { TryRestoreGameInput(); } catch { }
                try { TryRestoreGameHud(); } catch { }
                try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }

                // Best-effort: exit TV interaction/interface so player returns to gameplay/camera/controls.
                // Throttled to avoid hammering game APIs during rapid input/toggle sequences.
                if (nowRt - _lastDetachExitInvokeRealtime >= 0.5f)
                {
                    _lastDetachExitInvokeRealtime = nowRt;
                    TryExitTvInterfaceBestEffort();
                }

                // Keep gameplay released after detach even if S1API/game still considers app open.
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { MaintainDetachedAppVisualState(force: true); } catch { }
                try { TryRestoreGameInput(); } catch { }
                try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }
                try { UnderNetBrowserDiagnostics.OnGameplayResumed(); } catch { }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Right-click detach failed (non-fatal): {ex.Message}");
            }
        }

        private void TryExitTvInterfaceBestEffort()
        {
            try
            {
                bool invoked = false;
                object? localPlayer = FindGameLocalPlayer();

                // Fast, targeted detach: avoid full-scene scans/reflection-heavy discovery (caused massive hitches/freezes).
                // Verified game API (knowledge/_ilspy_ASC/Il2CppScheduleOne.TV/TVInterface.cs): RemovePlayer(Player), Close(), IsOpen etc.
                UnityEngine.Object? tvInterfaceObj = null; Type? tvInterfaceType = null;
                UnityEngine.Object? tvInteractableObj = null; Type? tvInteractableType = null;

                // 1) Resolve known runtime types directly on likely GameObjects.
                TryResolveTvComponentOnGameObject("TVInterface", ref tvInterfaceObj, ref tvInterfaceType, ref tvInteractableObj, ref tvInteractableType);
                if (tvInterfaceObj == null)
                    TryResolveTvInterfaceUnderNamedRoots(ref tvInterfaceObj, ref tvInterfaceType, ref tvInteractableObj, ref tvInteractableType);

                // 2) Best-effort: remove local player and close TV interface to reliably restore gameplay/camera/controls.
                // Note: TVInterface.Close() typically returns TV to home/lock screen visually (app/browser state remains alive in background).
                bool removed = false, closed = false;
                if (tvInterfaceObj != null && tvInterfaceType != null)
                    removed = TryRemoveLocalPlayerFromTvInterface(tvInterfaceType, tvInterfaceObj, localPlayer);

                if (tvInterfaceObj != null && tvInterfaceType != null)
                    closed = TryInvokeTvInterfaceClose(tvInterfaceType, tvInterfaceObj);
                invoked |= (removed || closed);

                // 4) Fallback via TVInteractable.Interface.
                if (!invoked && tvInteractableObj != null && tvInteractableType != null)
                {
                    if (TryGetTvInterfaceFromInteractable(tvInteractableType, tvInteractableObj, out var ifaceType2, out var ifaceObj2) && ifaceType2 != null && ifaceObj2 != null)
                    {
                        removed |= TryRemoveLocalPlayerFromTvInterface(ifaceType2, ifaceObj2, localPlayer);
                        closed |= TryInvokeTvInterfaceClose(ifaceType2, ifaceObj2);
                        invoked |= (removed || closed);
                    }
                }

                // 5) Last-resort: behavior-based invoke on components of the TVInterface GO only.
                if (!removed && !closed)
                    invoked |= TryInvokeExitLikeOnTvInterfaceGameObject(localPlayer);

                if (tvInterfaceObj == null && tvInteractableObj == null)
                {
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach: TVInterface/TVInteractable not resolved by targeted lookup (skipping full scan to avoid hitch).");
                    LogTvDetachDiscovery();
                }

                if (!invoked)
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach: no suitable TVInterface/TVInteractable exit method invoked (best-effort, targeted).");
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryExitTvInterfaceBestEffort failed: {ex.Message}");
            }
        }

        private static object? FindGameLocalPlayer()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? playerType = null;
                    try { playerType = asm.GetType("Il2CppScheduleOne.PlayerScripts.Player", false) ?? asm.GetType("ScheduleOne.PlayerScripts.Player", false); } catch { }
                    if (playerType == null) continue;
                    try
                    {
                        var p = playerType.GetProperty("Local", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (p != null)
                        {
                            var v = p.GetValue(null);
                            if (v != null) return v;
                        }
                    }
                    catch { }
                    try
                    {
                        var f = playerType.GetField("Local", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (f != null)
                        {
                            var v = f.GetValue(null);
                            if (v != null) return v;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private bool TryGetTvInterfaceFromInteractable(Type interactableType, UnityEngine.Object interactable, out Type? ifaceType, out UnityEngine.Object? ifaceObj)
        {
            ifaceType = null; ifaceObj = null;
            try
            {
                object? val = null;
                var p = interactableType.GetProperty("Interface", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (p != null) { try { val = p.GetValue(interactable); } catch { } }
                if (val == null)
                {
                    var f = interactableType.GetField("Interface", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (f != null) { try { val = f.GetValue(interactable); } catch { } }
                }
                if (val is UnityEngine.Object uo)
                {
                    ifaceObj = uo;
                    ifaceType = uo.GetType();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool TryRemoveLocalPlayerFromTvInterface(Type ifaceType, UnityEngine.Object ifaceObj, object? localPlayer)
        {
            try
            {
                if (localPlayer == null)
                {
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach: local player not found; cannot call TVInterface.RemovePlayer(Player).");
                    return false;
                }

                // Prefer instance's actual runtime type and only invoke methods compatible with the target object to avoid IL2CPP proxy mismatches.
                Type actualType = ifaceObj.GetType();
                var candidateTypes = new List<Type>();
                if (actualType != null) candidateTypes.Add(actualType);
                if (ifaceType != null && ifaceType != actualType) candidateTypes.Add(ifaceType);

                foreach (var t in candidateTypes)
                {
                    try
                    {
                        if (t == null) continue;
                        var isOpenProp = t.GetProperty("IsOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (isOpenProp != null && (isOpenProp.DeclaringType == null || isOpenProp.DeclaringType.IsInstanceOfType(ifaceObj)))
                        {
                            try
                            {
                                var v = isOpenProp.GetValue(ifaceObj);
                                if (v is bool b && !b && UnderNetBrowserConfig.DebugLogging?.Value == true)
                                    UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach: TVInterface.IsOpen is false (continuing best-effort RemovePlayer).");
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try
                    {
                        var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        foreach (var mi in methods)
                        {
                            if (mi == null || mi.Name != "RemovePlayer") continue;
                            var ps = mi.GetParameters();
                            if (ps.Length != 1) continue;
                            if (mi.DeclaringType != null && !mi.DeclaringType.IsInstanceOfType(ifaceObj)) continue;

                            bool argOk = false;
                            try
                            {
                                var pType = ps[0].ParameterType;
                                argOk = pType.IsInstanceOfType(localPlayer) || pType.IsAssignableFrom(localPlayer.GetType());
                            }
                            catch { argOk = false; }

                            try
                            {
                                mi.Invoke(ifaceObj, new[] { localPlayer });
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {t.FullName ?? t.Name}.RemovePlayer(localPlayer) for detach (argMatch={argOk}).");
                                return true;
                            }
                            catch (Exception ex)
                            {
                                if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: {t.FullName ?? t.Name}.RemovePlayer invoke attempt failed (argMatch={argOk}): {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryRemoveLocalPlayerFromTvInterface failed: {ex.Message}");
            }
            return false;
        }

        private bool TryInvokeTvInterfaceClose(Type ifaceType, UnityEngine.Object ifaceObj)
        {
            try
            {
                Type actualType = ifaceObj.GetType();
                var candidateTypes = new List<Type>();
                if (actualType != null) candidateTypes.Add(actualType);
                if (ifaceType != null && ifaceType != actualType) candidateTypes.Add(ifaceType);

                foreach (var t in candidateTypes)
                {
                    try
                    {
                        if (t == null) continue;
                        var mi = t.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (mi == null) continue;
                        if (mi.DeclaringType != null && !mi.DeclaringType.IsInstanceOfType(ifaceObj)) continue;
                        mi.Invoke(ifaceObj, null);
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {t.FullName ?? t.Name}.Close() for detach (game interaction exit).");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                            UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: {t.FullName ?? t.Name}.Close() invoke attempt failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryInvokeTvInterfaceClose failed: {ex.Message}");
            }
            return false;
        }

        private static void TryResolveTvComponentOnGameObject(string goName, ref UnityEngine.Object? tvInterfaceObj, ref Type? tvInterfaceType, ref UnityEngine.Object? tvInteractableObj, ref Type? tvInteractableType)
        {
            try
            {
                var go = GameObject.Find(goName);
                if (go == null) return;

                // Try to obtain strongly-typed IL2CPP components first (handles cases where GetComponents returns base UnityEngine.Component).
                if (tvInterfaceObj == null)
                {
                    var (obj, t) = TryGetIl2CppComponentByTypeNames(go, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                    if (obj != null && t != null) { tvInterfaceObj = obj; tvInterfaceType = t; }
                }
                if (tvInteractableObj == null)
                {
                    var (obj, t) = TryGetIl2CppComponentByTypeNames(go, "Il2CppScheduleOne.TV.TVInteractable", "ScheduleOne.TV.TVInteractable");
                    if (obj != null && t != null) { tvInteractableObj = obj; tvInteractableType = t; }
                }

                Component[]? comps = null;
                try { comps = go.GetComponents<Component>(); } catch { comps = null; }
                if (comps != null)
                {
                    foreach (var c in comps)
                    {
                        if (c == null) continue;

                        // Try dynamic TryCast to known TV types if component is Il2CppObjectBase but surfaced as generic Component.
                        if (tvInterfaceObj == null)
                        {
                            var casted = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                            if (casted.obj != null && casted.type != null) { tvInterfaceObj = casted.obj; tvInterfaceType = casted.type; }
                        }
                        if (tvInteractableObj == null)
                        {
                            var casted = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInteractable", "ScheduleOne.TV.TVInteractable");
                            if (casted.obj != null && casted.type != null) { tvInteractableObj = casted.obj; tvInteractableType = casted.type; }
                        }

                        Type? t = null;
                        try { t = c.GetType(); } catch { t = null; }
                        if (t != null)
                        {
                            string full = t.FullName ?? t.Name ?? "";
                            if (tvInterfaceObj == null && (string.Equals(full, "Il2CppScheduleOne.TV.TVInterface", StringComparison.Ordinal) || full.EndsWith(".TVInterface", StringComparison.Ordinal) || LooksLikeTvInterface(t)))
                            { tvInterfaceObj = c; tvInterfaceType = t; }
                            if (tvInteractableObj == null && (string.Equals(full, "Il2CppScheduleOne.TV.TVInteractable", StringComparison.Ordinal) || full.EndsWith(".TVInteractable", StringComparison.Ordinal) || LooksLikeTvInteractable(t)))
                            { tvInteractableObj = c; tvInteractableType = t; }
                        }

                        if (tvInterfaceObj != null && tvInteractableObj != null) break;
                    }
                }

                if (tvInterfaceObj != null || tvInteractableObj != null)
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: targeted resolve on '{goName}' iface={(tvInterfaceType != null ? tvInterfaceType.FullName : "null")} interact={(tvInteractableType != null ? tvInteractableType.FullName : "null")}");
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach targeted resolve '{goName}' failed (non-fatal): {ex.Message}");
            }
        }

        private static void TryResolveTvInterfaceUnderNamedRoots(ref UnityEngine.Object? tvInterfaceObj, ref Type? tvInterfaceType, ref UnityEngine.Object? tvInteractableObj, ref Type? tvInteractableType)
        {
            try
            {
                string[] roots = { "TV_Built(Clone)", "TV_Built" };
                foreach (var rname in roots)
                {
                    GameObject? root = null;
                    try { root = GameObject.Find(rname); } catch { root = null; }
                    if (root == null) continue;
                    Transform? child = null;
                    try { child = root.transform.Find("TVInterface"); } catch { child = null; }
                    if (child == null) continue;
                    var go = child.gameObject;
                    if (go == null) continue;

                    if (tvInterfaceObj == null)
                    {
                        var (obj, t) = TryGetIl2CppComponentByTypeNames(go, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                        if (obj != null && t != null) { tvInterfaceObj = obj; tvInterfaceType = t; }
                    }
                    if (tvInteractableObj == null)
                    {
                        var (obj, t) = TryGetIl2CppComponentByTypeNames(go, "Il2CppScheduleOne.TV.TVInteractable", "ScheduleOne.TV.TVInteractable");
                        if (obj != null && t != null) { tvInteractableObj = obj; tvInteractableType = t; }
                    }

                    Component[]? comps = null;
                    try { comps = go.GetComponents<Component>(); } catch { comps = null; }
                    if (comps != null)
                    {
                        foreach (var c in comps)
                        {
                            if (c == null) continue;
                            if (tvInterfaceObj == null)
                            {
                                var casted = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                                if (casted.obj != null && casted.type != null) { tvInterfaceObj = casted.obj; tvInterfaceType = casted.type; }
                            }
                            if (tvInteractableObj == null)
                            {
                                var casted = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInteractable", "ScheduleOne.TV.TVInteractable");
                                if (casted.obj != null && casted.type != null) { tvInteractableObj = casted.obj; tvInteractableType = casted.type; }
                            }

                            Type? t = null; try { t = c.GetType(); } catch { t = null; }
                            if (t != null)
                            {
                                string full = t.FullName ?? t.Name ?? "";
                                if (tvInterfaceObj == null && (string.Equals(full, "Il2CppScheduleOne.TV.TVInterface", StringComparison.Ordinal) || full.EndsWith(".TVInterface", StringComparison.Ordinal) || LooksLikeTvInterface(t))) { tvInterfaceObj = c; tvInterfaceType = t; }
                                if (tvInteractableObj == null && (string.Equals(full, "Il2CppScheduleOne.TV.TVInteractable", StringComparison.Ordinal) || full.EndsWith(".TVInteractable", StringComparison.Ordinal) || LooksLikeTvInteractable(t))) { tvInteractableObj = c; tvInteractableType = t; }
                            }
                        }
                    }

                    if (tvInterfaceObj != null)
                    {
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: resolved via '{rname}/TVInterface' iface={(tvInterfaceType != null ? tvInterfaceType.FullName : "null")} interact={(tvInteractableType != null ? tvInteractableType.FullName : "null")}");
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool TryInvokeExitLikeOnTvInterfaceGameObject(object? localPlayer)
        {
            bool invoked = false;
            try
            {
                var go = GameObject.Find("TVInterface");
                if (go == null) return false;
                Component[]? comps = null;
                try { comps = go.GetComponents<Component>(); } catch { comps = null; }
                if (comps == null) return false;

                foreach (var c in comps)
                {
                    if (c == null) continue;

                    var candidates = new List<UnityEngine.Object> { c };
                    try
                    {
                        var castIface = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                        if (castIface.obj != null) candidates.Add(castIface.obj);
                        var castInter = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInteractable", "ScheduleOne.TV.TVInteractable");
                        if (castInter.obj != null) candidates.Add(castInter.obj);
                    }
                    catch { }

                    foreach (var obj in candidates)
                    {
                        Type? t = null; try { t = obj.GetType(); } catch { t = null; }
                        if (t == null) continue;

                        bool didRemove = false;
                        bool didClose = false;
                        if (localPlayer != null)
                        {
                            try
                            {
                                var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                foreach (var mi in methods)
                                {
                                    if (mi == null || mi.Name != "RemovePlayer") continue;
                                    var ps = mi.GetParameters();
                                    if (ps.Length != 1) continue;
                                    if (mi.DeclaringType != null && !mi.DeclaringType.IsInstanceOfType(obj)) continue;
                                    bool argOk = false;
                                    try
                                    {
                                        var pType = ps[0].ParameterType;
                                        argOk = pType.IsInstanceOfType(localPlayer) || pType.IsAssignableFrom(localPlayer.GetType());
                                    }
                                    catch { argOk = false; }
                                    try
                                    {
                                        mi.Invoke(obj, new[] { localPlayer });
                                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: invoked {t.FullName ?? t.Name}.RemovePlayer(localPlayer) (behavior scan, argMatch={argOk}).");
                                        didRemove = true; break;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }

                        try
                        {
                            var miClose = t.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (miClose != null && (miClose.DeclaringType == null || miClose.DeclaringType.IsInstanceOfType(obj)))
                            {
                                miClose.Invoke(obj, null);
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: invoked {t.FullName ?? t.Name}.Close() (behavior scan).");
                                didClose = true;
                            }
                        }
                        catch { }

                        invoked |= (didRemove || didClose);
                        if (didRemove || didClose) break;
                    }
                    if (invoked) break;
                }
            }
            catch { }
            return invoked;
        }

        private void EnsureReattachFocus(string reason)
        {
            try
            {
                if (_detachedToGameplay || !IsOpen) return;

                bool verbose = UnderNetBrowserConfig.DebugLogging?.Value == true;
                if (verbose)
                {
                    try
                    {
                        bool over = false; int px = -1, py = -1;
                        try { over = IsPointerOverVideoDisplay(out px, out py); } catch { }
                        bool compTexReady = false;
                        try { compTexReady = _gpuCompositionCapture?.Texture != null; } catch { compTexReady = false; }
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] ReattachFocus: reason={reason} webFocused={_compositionWebFocused} urlFocused={(_urlInput != null && _urlInput.isFocused)} ptrOver={over} ptrPx={px} ptrPy={py} comp={(_gpuCompositionCapture != null)} compTex={compTexReady} suspended={(_gpuCompositionCapture?.RenderingSuspended ?? false)}");
                    }
                    catch { }
                }

                // On reattach the browser must always be the interaction target.
                try
                {
                    if (_urlInput != null && _urlInput.isFocused)
                    {
                        try { _urlInput.DeactivateInputField(); } catch { }
                    }
                }
                catch { }
                try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }

                _compositionWebFocused = true;
                try { UpdateInputOwnershipState(); } catch { }
                try { SetAppInteractionEnabled(true); } catch { }
                try { EnsureGraphicRaycastersEnabledForApp(reason); } catch { }
                try
                {
                    // Best-effort focus restoration; if composition texture is not ready yet, keep retry window active.
                    if (_gpuCompositionCapture != null)
                    {
                        _gpuCompositionCapture.RequestFocus();
                        if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                        {
                            bool compTexReady = false;
                            try { compTexReady = _gpuCompositionCapture.Texture != null; } catch { compTexReady = false; }
                            if (!compTexReady && reason != null && (reason.StartsWith("reattach", StringComparison.OrdinalIgnoreCase) || reason.StartsWith("retry#", StringComparison.OrdinalIgnoreCase) || reason.IndexOf("pointer_recovery", StringComparison.OrdinalIgnoreCase) >= 0))
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] ReattachFocus: composition texture not ready yet ({reason}); continuing recovery.");
                        }
                    }
                }
                catch { }
                try
                {
                    _gpuCompositionCapture?.ExecuteScript(
                        "(function(){try{" +
                        "try{window.focus&&window.focus();}catch(_){}" +
                        "try{document.body&&document.body.focus&&document.body.focus();}catch(_){}" +
                        "try{var ae=document.activeElement;" +
                        "if(!ae||ae===document.body||ae===document.documentElement){" +
                        "  var c=document.querySelector('video,canvas,button,[role=\"button\"],a,input,textarea,select,[tabindex]');" +
                        "  if(c&&c.focus){try{c.focus({preventScroll:true});}catch(_){try{c.focus();}catch(__){}}}" +
                        "}}catch(_){}" +
                        "try{var evs=['pointerdown','mousedown','mouseup','click'];" +
                        "for(var i=0;i<evs.length;i++){try{document.dispatchEvent(new Event(evs[i],{bubbles:true}));}catch(_){}}}catch(_){}" +
                        "try{var v=document.querySelector('video'); if(v){try{v.setAttribute('tabindex', v.getAttribute('tabindex')||'-1');}catch(_){} try{v.focus&&v.focus();}catch(_){}}" +
                        "catch(_){}" +
                        "}catch(_){}})();");
                }
                catch { }

                // Keep short retry window alive while user is interacting after reattach.
                float nowRt = Time.realtimeSinceStartup;
                if (_reattachFocusRecoveryUntilRealtime > nowRt)
                    _nextReattachFocusRetryRealtime = Mathf.Min(_nextReattachFocusRetryRealtime <= 0f ? (nowRt + 0.12f) : _nextReattachFocusRetryRealtime, nowRt + 0.12f);
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] EnsureReattachFocus failed ({reason}, non-fatal): {ex.Message}"); } catch { }
            }
        }

        private void ClearReattachFocusRecoveryState()
        {
            if (UnderNetBrowserConfig.DebugLogging?.Value == true && _reattachFocusRecoveryUntilRealtime > Time.realtimeSinceStartup)
                UnderNetBrowserConfig.Log("[UnderNetBrowser] ReattachFocus: recovery cleared");
            _reattachInteractionSuppressUntilRealtime = -999f;
            _reattachFocusRecoveryUntilRealtime = -999f;
            _nextReattachFocusRetryRealtime = -999f;
            _reattachFocusRetryCount = 0;
        }

        private void BeginReattachFocusRecovery(string reason)
        {
            try
            {
                // Reattach/open/resume recovery means gameplay/browser interaction is transitioning back to active TV context.
                try
                {
                    if (reason != null && (reason.IndexOf("detach", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("reattach", StringComparison.OrdinalIgnoreCase) >= 0))
                        UnderNetBrowserDiagnostics.SetBrowserIntentionalAlive(false);
                }
                catch { }
                float nowRt = Time.realtimeSinceStartup;
                _lastReattachRealtime = nowRt;
                _reattachInteractionSuppressUntilRealtime = nowRt + 0.12f; // short carry-over guard only; clicks during recovery are handled
                _reattachFocusRecoveryUntilRealtime = nowRt + 1.75f;
                _nextReattachFocusRetryRealtime = nowRt;
                _reattachFocusRetryCount = 0;
                if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] ReattachFocus: begin reason={reason} window=1.75s");
                EnsureReattachFocus(reason + ":begin");
            }
            catch { }
        }

        private void TickReattachFocusRecovery()
        {
            try
            {
                if (_detachedToGameplay || !IsOpen) return;
                float nowRt = Time.realtimeSinceStartup;
                if (nowRt >= _reattachFocusRecoveryUntilRealtime)
                {
                    // One-time debug marker when window expires (gated).
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true && _reattachFocusRetryCount > 0 && _nextReattachFocusRetryRealtime > -900f)
                    {
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] ReattachFocus: window elapsed after {_reattachFocusRetryCount} retry(ies)");
                        _nextReattachFocusRetryRealtime = -999f; // prevent repeated "elapsed" logs
                    }
                    return;
                }
                if (nowRt < _nextReattachFocusRetryRealtime) return;

                _reattachFocusRetryCount++;
                EnsureReattachFocus($"retry#{_reattachFocusRetryCount}");

                // Retry aggressively at first, then back off slightly.
                float interval = _reattachFocusRetryCount <= 6 ? 0.10f : 0.20f;
                _nextReattachFocusRetryRealtime = nowRt + interval;

                // Keep raycasters/canvas state healthy during recovery in case S1API/TV UI settles late.
                if ((_reattachFocusRetryCount % 3) == 0)
                {
                    try { SetAppInteractionEnabled(true); } catch { }
                    try { EnsureGraphicRaycastersEnabledForApp($"retry#{_reattachFocusRetryCount}"); } catch { }
                }
            }
            catch { }
        }

        private bool TryReattachToTvIfResumedWhileDetached()
        {
            try
            {
                if (!_detachedToGameplay || !IsOpen) return false;
                float nowRt = Time.realtimeSinceStartup;
                if (nowRt < _reattachBlockedUntilRealtime)
                {
                    // State-aware debug signal: throttled so debug runs remain useful without flooding during settle windows.
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                    {
                        float remain = _reattachBlockedUntilRealtime - nowRt;
                        int bucket = Mathf.Clamp((int)Mathf.Ceil(remain * 2f), 0, 20); // ~0.5s buckets
                        if (bucket != _lastLoggedReattachBlockBucket)
                        {
                            _lastLoggedReattachBlockBucket = bucket;
                            UnderNetBrowserConfig.Log($"[UnderNetBrowser] Reattach probe blocked (settle): {remain:0.00}s");
                        }
                    }
                    return false;
                }
                if (nowRt - _lastDetachedReattachCheckRealtime < 0.20f) return false;
                _lastDetachedReattachCheckRealtime = nowRt;

                // Detect game TV interaction resumed.
                // After RemovePlayer-only detach the interface may remain open/visible; prefer player-presence over IsOpen alone.
                try { UnderNetBrowserDiagnostics.SetBrowserIntentionalAlive(false); } catch { }
                bool shouldReattach = false;
                try
                {
                    var go = GameObject.Find("TVInterface");
                    if (go != null)
                    {
                        var (ifaceObj, ifaceType) = TryGetIl2CppComponentByTypeNames(go, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                        if (ifaceObj == null || ifaceType == null)
                        {
                            // fallback: try resolved instance on components
                            Component[]? comps = null;
                            try { comps = go.GetComponents<Component>(); } catch { comps = null; }
                            if (comps != null)
                            {
                                foreach (var c in comps)
                                {
                                    if (c == null) continue;
                                    var casted = TryCastIl2CppObject(c, "Il2CppScheduleOne.TV.TVInterface", "ScheduleOne.TV.TVInterface");
                                    if (casted.obj != null && casted.type != null) { ifaceObj = casted.obj; ifaceType = casted.type; break; }
                                }
                            }
                        }

                        if (ifaceObj != null && ifaceType != null)
                        {
                            bool tvOpen = false;
                            try
                            {
                                var isOpenProp = ifaceType.GetProperty("IsOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (isOpenProp != null)
                                {
                                    var v = isOpenProp.GetValue(ifaceObj);
                                    if (v is bool b) tvOpen = b;
                                }
                            }
                            catch { }

                            bool localInPlayers = IsLocalPlayerInTvInterface(ifaceType, ifaceObj);
                            if (localInPlayers)
                                shouldReattach = true;
                            else if (tvOpen)
                            {
                                // If we cannot confirm player presence, be conservative: only reattach when IsOpen transitions from false->true.
                                // (When detaching via Close(), IsOpen becomes false then true on re-interact. When detaching via RemovePlayer-only,
                                // IsOpen may stay true, so this avoids immediate false reattach.)
                                if (!_detachedTvInterfaceWasOpen)
                                    shouldReattach = true;
                            }
                            _detachedTvInterfaceWasOpen = tvOpen;
                        }
                    }
                }
                catch { }

                if (!shouldReattach) return false;

                // Re-acquire browser/TV input ownership while keeping app/browser state running.
                _detachedToGameplay = false;
                _detachRequested = false;
                AnyDetachActive = false;
                _compositionFocusPrimed = false;
                _compositionWebFocused = true; // default back to web interaction after reattach
                if (_gameInputMode && UnderNetBrowserConfig.DebugLogging?.Value == true)
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] Reattach: resetting GameInputMode to OFF for deterministic focus/ownership.");
                _gameInputMode = false;
                try { if (_gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(false); } catch { }
                _nextMediaArrowRepeatLeft = _nextMediaArrowRepeatRight = _nextMediaArrowRepeatUp = _nextMediaArrowRepeatDown = -1f;
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Re-attaching to TV interaction after detached gameplay resume (restoring input ownership).");

                // Do not reactivate hidden TV home/clock objects on reattach; app remains active and restoring them can surface vanilla lock/home UI.
                try { RestoreDetachedHomeScreenObjects(restoreHiddenObjects: false); } catch { }
                try { TryForegroundS1AppForReattach(); } catch { }
                try { SetAppInteractionEnabled(true); } catch { }
                try { EnsureGraphicRaycastersEnabledForApp("reattach_detected_pre"); } catch { }
                try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                try { TryHideGameHud(); } catch { }
                try { TryDisableGameInput(); } catch { }
                try
                {
                    bool fs = false;
                    try { fs = _gpuCompositionCapture != null && _gpuCompositionCapture.IsFullscreen; } catch { fs = false; }
                    _isPageFullscreen = fs;
                    UpdateChromeVisibility();
                }
                catch { }
                try { UpdateInputOwnershipState(); } catch { }

                // Strong focus recovery: reattach must always focus web interaction, even with quick detach/reattach or late UI settle.
                try
                {
                    if (_urlInput != null && _urlInput.isFocused)
                    {
                        try { _urlInput.DeactivateInputField(); } catch { }
                    }
                }
                catch { }
                try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                BeginReattachFocusRecovery("reattach_detected");

                try
                {
                    bool over = false; int px = -1, py = -1;
                    try { over = IsPointerOverVideoDisplay(out px, out py); } catch { }
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Reattach state: webFocused={_compositionWebFocused} gameInput={_gameInputMode} urlFocused={(_urlInput != null && _urlInput.isFocused)} ptrOver={over} ptrPx={px} ptrPy={py} compTex={(_gpuCompositionCapture?.Texture != null)} suspended={(_gpuCompositionCapture?.RenderingSuspended ?? false)}");
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryReattachToTvIfResumedWhileDetached failed (non-fatal): {ex.Message}");
                return false;
            }
        }

        private void MaintainDetachedAppVisualState(bool force = false)
        {
            try
            {
                if (!_detachedToGameplay) return;
                if (!force && Time.realtimeSinceStartup - _lastDetachedVisualMaintainRealtime < 0.25f) return;
                _lastDetachedVisualMaintainRealtime = Time.realtimeSinceStartup;

                // Keep our app canvas/UI visible on the TV while player is in gameplay (TVInterface.Close() may show home/lock screen).
                GameObject? appGo = null;
                try
                {
                    if (_videoDisplay != null)
                    {
                        var t = _videoDisplay.transform;
                        // Walk up to find app root container if present.
                        for (int i = 0; i < 8 && t != null; i++)
                        {
                            if (t.name == "UnderNetBrowser_TVApp" || t.name.IndexOf("UnderNetBrowser", StringComparison.OrdinalIgnoreCase) >= 0)
                            { appGo = t.gameObject; break; }
                            t = t.parent;
                        }
                        if (appGo == null) appGo = _videoDisplay.gameObject;
                    }
                }
                catch { }
                if (appGo == null)
                {
                    try { appGo = GameObject.Find("UnderNetBrowser_TVApp"); } catch { appGo = null; }
                }

                if (appGo != null)
                {
                    try
                    {
                        // Activate app and key ancestors under TV interface so it can render on the TV surface.
                        var cur = appGo.transform;
                        int depth = 0;
                        while (cur != null && depth++ < 6)
                        {
                            try { if (!cur.gameObject.activeSelf) cur.gameObject.SetActive(true); } catch { }
                            // Stop climbing too far above TV interface container area.
                            if (cur.name == "TVInterface" || cur.name == "Container") break;
                            cur = cur.parent;
                        }
                    }
                    catch { }

                    try
                    {
                        var cg = appGo.GetComponent<CanvasGroup>() ?? appGo.GetComponentInParent<CanvasGroup>();
                        if (cg != null)
                        {
                            cg.alpha = 1f;
                            // Keep non-interactive while detached to gameplay.
                            cg.interactable = false;
                            cg.blocksRaycasts = false;
                        }
                    }
                    catch { }

                    try
                    {
                        var canvas = appGo.GetComponent<Canvas>() ?? appGo.GetComponentInParent<Canvas>();
                        if (canvas != null)
                        {
                            if (!canvas.enabled) canvas.enabled = true;
                            // Ensure app renders above TV home/lock visuals while detached.
                            try { canvas.overrideSorting = true; canvas.sortingOrder = 32760; } catch { }
                        }
                    }
                    catch { }

                    try { appGo.transform.SetAsLastSibling(); } catch { }
                }

                // Best-effort: mirror composition texture onto TV physical screen surface while detached
                // (inspired by CustomTV rendering directly to TV display objects), so visuals can persist even if
                // TVInterface.Close()/home-screen UI hides the app canvas.
                try { TryApplyDetachedTvScreenMirror(force); } catch { }

                // Best-effort: hide TV home/lock screen UI and clock/day renderers while detached so our app remains visible.
                // Re-enforce tracked hidden elements cheaply; do expensive hierarchy discovery once per detached session.
                try
                {
                    // Re-hide previously tracked home-screen objects if TV reactivated them.
                    for (int i = 0; i < _detachedHiddenHomeScreenObjects.Count; i++)
                    {
                        var item = _detachedHiddenHomeScreenObjects[i];
                        try { if (item.go != null && item.go.activeSelf) item.go.SetActive(false); } catch { }
                    }
                    // Re-disable previously tracked clock/day renderers if re-enabled.
                    for (int i = 0; i < _detachedHiddenClockRenderers.Count; i++)
                    {
                        var item = _detachedHiddenClockRenderers[i];
                        try { if (item.renderer != null && item.renderer.enabled) item.renderer.enabled = false; } catch { }
                    }

                    if (!_detachedVisualTargetsScanned)
                    {
                        var tvIfaceGo = GameObject.Find("TVInterface");
                        if (tvIfaceGo != null)
                        {
                            var transforms = tvIfaceGo.GetComponentsInChildren<Transform>(true);
                            if (transforms != null)
                            {
                                foreach (var tr in transforms)
                                {
                                    if (tr == null) continue;
                                    var go = tr.gameObject;
                                    if (go == null) continue;
                                    string n = "";
                                    try { n = go.name ?? ""; } catch { n = ""; }

                                    bool looksTvHome = n.IndexOf("HomeScreen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       n.IndexOf("TVHome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       n.IndexOf("Home Screen", StringComparison.OrdinalIgnoreCase) >= 0;
                                    if (!looksTvHome) continue;

                                    bool isTvHomeComponent = false;
                                    try
                                    {
                                        var comps = go.GetComponents<Component>();
                                        if (comps != null)
                                        {
                                            foreach (var c in comps)
                                            {
                                                if (c == null) continue;
                                                string tn = "";
                                                try { tn = c.GetType().FullName ?? c.GetType().Name ?? ""; } catch { tn = ""; }
                                                if (tn.IndexOf("TV.TVHomeScreen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    tn.IndexOf("Il2CppScheduleOne.TV.TVHomeScreen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    tn.IndexOf("ScheduleOne.TV.TVHomeScreen", StringComparison.OrdinalIgnoreCase) >= 0)
                                                { isTvHomeComponent = true; break; }
                                            }
                                        }
                                    }
                                    catch { }

                                    if (!isTvHomeComponent && !string.Equals(n, "HomeScreen", StringComparison.OrdinalIgnoreCase)) continue;

                                    bool wasActive = false;
                                    try { wasActive = go.activeSelf; } catch { }
                                    if (wasActive)
                                    {
                                        try { go.SetActive(false); } catch { }
                                        try
                                        {
                                            bool exists = false;
                                            for (int i = 0; i < _detachedHiddenHomeScreenObjects.Count; i++)
                                                if (_detachedHiddenHomeScreenObjects[i].go == go) { exists = true; break; }
                                            if (!exists) _detachedHiddenHomeScreenObjects.Add((go, true));
                                        }
                                        catch { }
                                    }
                                }
                            }

                            // Hide TV lock-screen clock/day 3D label renderers (active "Time"/"Day" objects seen on TV) while detached.
                            try
                            {
                                var clockRenderers = tvIfaceGo.GetComponentsInChildren<Renderer>(true);
                                if (clockRenderers != null)
                                {
                                    foreach (var r in clockRenderers)
                                    {
                                        if (r == null) continue;
                                        string rn = "";
                                        try { rn = r.gameObject != null ? (r.gameObject.name ?? "") : ""; } catch { rn = ""; }
                                        if (!string.Equals(rn, "Time", StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(rn, "Day", StringComparison.OrdinalIgnoreCase))
                                            continue;

                                        bool wasEnabled = false;
                                        try { wasEnabled = r.enabled; } catch { wasEnabled = false; }
                                        if (!wasEnabled) continue;

                                        try { r.enabled = false; } catch { }
                                        try
                                        {
                                            bool exists = false;
                                            for (int i = 0; i < _detachedHiddenClockRenderers.Count; i++)
                                                if (_detachedHiddenClockRenderers[i].renderer == r) { exists = true; break; }
                                            if (!exists) _detachedHiddenClockRenderers.Add((r, true));
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }

                            _detachedVisualTargetsScanned = true;
                        }
                        // If TVInterface not found yet, keep scanned=false so we can retry on next maintain tick.
                    }
                }
                catch { }
            }
            catch { }
        }

        private void RestoreDetachedHomeScreenObjects(bool restoreHiddenObjects = true)
        {
            if (restoreHiddenObjects)
            {
                try
                {
                    for (int i = _detachedHiddenHomeScreenObjects.Count - 1; i >= 0; i--)
                    {
                        var item = _detachedHiddenHomeScreenObjects[i];
                        try { if (item.go != null) item.go.SetActive(item.wasActive); } catch { }
                    }
                }
                catch { }

                try
                {
                    for (int i = _detachedHiddenClockRenderers.Count - 1; i >= 0; i--)
                    {
                        var item = _detachedHiddenClockRenderers[i];
                        try { if (item.renderer != null) item.renderer.enabled = item.wasEnabled; } catch { }
                    }
                }
                catch { }

                try { _detachedHiddenHomeScreenObjects.Clear(); } catch { }
                try { _detachedHiddenClockRenderers.Clear(); } catch { }

                // Reset scan state after a full restore so future detach sessions can rediscover/hide as needed.
                _detachedVisualTargetsScanned = false;
            }
            else
            {
                // App remains active/detached-visual context: keep hidden-object tracking so a later full close/shutdown
                // can restore the TV lock screen (time/day) correctly. Do not mark as scanned-false here either.
            }

            try { RestoreTvScreenMirror(); } catch { }
        }

        private bool _tvScreenMirrorNoCandidateLogged;
        private float _tvScreenMirrorNoCandidateLogRealtime;
        private float _lastHotbarScanSkipLogRealtime;
        private bool _detachedVisualTargetsScanned;
        private void TryApplyDetachedTvScreenMirror(bool force = false)
        {
            try
            {
                if (!_detachedToGameplay) return;
                if (!force && Time.realtimeSinceStartup - _lastTvScreenMirrorAttemptRealtime < 0.5f) return;
                _lastTvScreenMirrorAttemptRealtime = Time.realtimeSinceStartup;

                var tex = _gpuCompositionCapture?.Texture;
                if (tex == null) return;

                // If already mirroring valid renderers, just ensure texture stays applied.
                if (_tvScreenMirrorStates.Count > 0)
                {
                    bool anyValid = false;
                    for (int i = 0; i < _tvScreenMirrorStates.Count; i++)
                    {
                        var st = _tvScreenMirrorStates[i];
                        if (st.renderer == null) continue;
                        try
                        {
                            var mat = st.renderer.material;
                            if (mat == null) continue;
                            if (mat.mainTexture != tex) mat.mainTexture = tex;
                            anyValid = true;
                        }
                        catch { }
                    }
                    if (anyValid) return;
                    _tvScreenMirrorStates.Clear();
                }

                // No candidates found in this detached session; avoid expensive hierarchy rescans.
                if (_tvScreenMirrorNoCandidateLogged) return;

                GameObject? tvIfaceGo = null;
                try { tvIfaceGo = GameObject.Find("TVInterface"); } catch { tvIfaceGo = null; }
                if (tvIfaceGo == null)
                {
                    try
                    {
                        string[] roots = { "TV_Built(Clone)/TVInterface", "TVInterface" };
                        foreach (var path in roots)
                        {
                            try { var go = GameObject.Find(path); if (go != null) { tvIfaceGo = go; break; } } catch { }
                        }
                    }
                    catch { }
                }

                // Avoid mirroring onto our own app UI objects.
                Transform? appRoot = null;
                try
                {
                    if (_videoDisplay != null)
                    {
                        var t = _videoDisplay.transform;
                        for (int i = 0; i < 10 && t != null; i++)
                        {
                            if (t.name == "UnderNetBrowser_TVApp") { appRoot = t; break; }
                            t = t.parent;
                        }
                    }
                }
                catch { appRoot = null; }

                // Collect renderers from likely TV roots (TVInterface may only contain UI labels; screen mesh can be elsewhere on TV).
                var rendererList = new List<Renderer>();
                var seen = new HashSet<int>();
                void AddRenderers(GameObject? root)
                {
                    try
                    {
                        if (root == null) return;
                        var rs = root.GetComponentsInChildren<Renderer>(true);
                        if (rs == null) return;
                        foreach (var r in rs)
                        {
                            if (r == null) continue;
                            int id = 0;
                            try { id = r.GetInstanceID(); } catch { id = 0; }
                            if (id != 0 && !seen.Add(id)) continue;
                            rendererList.Add(r);
                        }
                    }
                    catch { }
                }

                AddRenderers(tvIfaceGo);
                try
                {
                    if (tvIfaceGo != null)
                    {
                        // Search up to TV_Built(Clone)/TV root and include siblings/children (screen mesh often separate from TVInterface UI).
                        var t = tvIfaceGo.transform;
                        for (int i = 0; i < 6 && t != null; i++)
                        {
                            AddRenderers(t.gameObject);
                            string n = "";
                            try { n = t.name ?? ""; } catch { n = ""; }
                            if (n.IndexOf("TV_Built", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(n, "TV", StringComparison.OrdinalIgnoreCase))
                                break;
                            t = t.parent;
                        }
                    }
                }
                catch { }
                try
                {
                    // Extra fallback roots by name.
                    string[] extraRoots = { "TV_Built(Clone)", "TV_Built", "TV", "Barn Contents Container/TV_Built(Clone)" };
                    foreach (var p in extraRoots) { try { AddRenderers(GameObject.Find(p)); } catch { } }
                }
                catch { }

                if (rendererList.Count == 0)
                {
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                        UnderNetBrowserConfig.Log("[UnderNetBrowser] TV screen mirror: no Renderer components found under TV hierarchy.");
                    return;
                }
                var renderers = rendererList.ToArray();

                var applied = new List<string>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    try
                    {
                        if (appRoot != null && r.transform.IsChildOf(appRoot)) continue;
                    }
                    catch { }

                    GameObject? go = null;
                    try { go = r.gameObject; } catch { go = null; }
                    if (go == null) continue;

                    string name = "";
                    try { name = go.name ?? ""; } catch { name = ""; }
                    string lower = name.ToLowerInvariant();

                    // Avoid UI canvas renderers and text labels entirely (they break clock/display text).
                    bool isUiGraphic = false;
                    try
                    {
                        var comps = go.GetComponents<Component>();
                        if (comps != null)
                        {
                            foreach (var c in comps)
                            {
                                if (c == null) continue;
                                string tn = "";
                                try { tn = c.GetType().Name ?? ""; } catch { tn = ""; }
                                if (tn.IndexOf("TextMeshProUGUI", StringComparison.OrdinalIgnoreCase) >= 0) { isUiGraphic = true; break; }
                                if (string.Equals(tn, "TextMeshPro", StringComparison.OrdinalIgnoreCase) ||
                                    tn.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) >= 0) { isUiGraphic = true; break; }
                                if (tn.IndexOf("Canvas", StringComparison.OrdinalIgnoreCase) >= 0) { isUiGraphic = true; break; }
                                if (tn.IndexOf("Image", StringComparison.OrdinalIgnoreCase) >= 0 && tn.IndexOf("RawImage", StringComparison.OrdinalIgnoreCase) >= 0) { isUiGraphic = true; break; }
                            }
                        }
                    }
                    catch { }
                    if (isUiGraphic) continue;

                    // Never target clock/time/day labels.
                    if (lower == "time" || lower.Contains("timelabel") || lower.Contains("daylabel") || lower == "daylabel" || lower.Contains("clock"))
                        continue;

                    string matName = "";
                    try { var m = r.sharedMaterial != null ? r.sharedMaterial : r.material; if (m != null) matName = (m.name ?? "").ToLowerInvariant(); } catch { matName = ""; }

                    // Prefer actual screen/display surfaces only.
                    bool nameCandidate =
                        lower.Contains("screen") || lower.Contains("display") || lower.Contains("monitor") ||
                        lower.Contains("tvscreen") || lower.Contains("quad") || lower.Contains("plane");
                    bool matCandidate = matName.Contains("screen") || matName.Contains("display") || matName.Contains("monitor") || matName.Contains("tvscreen");

                    bool meshRenderer = false;
                    try { meshRenderer = r.GetType().Name.IndexOf("MeshRenderer", StringComparison.OrdinalIgnoreCase) >= 0; } catch { meshRenderer = false; }
                    bool skinned = false;
                    try { skinned = r.GetType().Name.IndexOf("SkinnedMeshRenderer", StringComparison.OrdinalIgnoreCase) >= 0; } catch { skinned = false; }

                    if (!nameCandidate && !matCandidate && (meshRenderer || skinned))
                    {
                        // Conservative fallback for common TV model screen part names.
                        if (lower.Contains("panel") || lower.Contains("glass") || lower.Contains("front"))
                            nameCandidate = true;
                    }
                    if (!nameCandidate && !matCandidate) continue;

                    try
                    {
                        var mat = r.material; // instance
                        if (mat == null) continue;
                        Texture? original = null;
                        try { original = mat.mainTexture; } catch { original = null; }
                        string? shaderName = null;
                        try { shaderName = mat.shader != null ? mat.shader.name : null; } catch { shaderName = null; }

                        // Best-effort shader choice for visibility; prefer unlit-like if available.
                        try
                        {
                            var unlit = Shader.Find("Unlit/Texture") ?? Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
                            if (unlit != null) mat.shader = unlit;
                        }
                        catch { }

                        mat.mainTexture = tex;
                        _tvScreenMirrorStates.Add((r, mat, original, shaderName));
                        applied.Add(name);
                    }
                    catch (Exception ex)
                    {
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] TV screen mirror candidate '{name}' failed: {ex.Message}");
                    }
                }

                if (applied.Count > 0)
                {
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] TV screen mirror (detached): applied composition texture to {applied.Count} renderer(s): {string.Join(", ", applied)}");
                }
                else
                {
                    // Benign/frequent no-op on some setups; trace only when debug logging is enabled.
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                    {
                        float nowRt = Time.realtimeSinceStartup;
                        if (!_tvScreenMirrorNoCandidateLogged || (nowRt - _tvScreenMirrorNoCandidateLogRealtime) >= 10f)
                        {
                            _tvScreenMirrorNoCandidateLogged = true;
                            _tvScreenMirrorNoCandidateLogRealtime = nowRt;
                            UnderNetBrowserConfig.Log("[UnderNetBrowser] TV screen mirror: no suitable screen renderer candidates found.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TV screen mirror failed (non-fatal): {ex.Message}");
            }
        }

        private void RestoreTvScreenMirror()
        {
            try
            {
                for (int i = _tvScreenMirrorStates.Count - 1; i >= 0; i--)
                {
                    var st = _tvScreenMirrorStates[i];
                    try
                    {
                        if (st.renderer == null) continue;
                        var mat = st.materialInstance ?? st.renderer.material;
                        if (mat == null) continue;
                        try { mat.mainTexture = st.originalMainTexture; } catch { }
                        // Shader restore is best-effort only if we can find it again; skip to avoid guessing wrong shader object.
                    }
                    catch { }
                }
            }
            catch { }
            finally { _tvScreenMirrorStates.Clear(); }
            _tvScreenMirrorNoCandidateLogged = false;
            _tvScreenMirrorNoCandidateLogRealtime = 0f;
            _detachedVisualTargetsScanned = false;
        }

        private void TryForegroundS1AppForReattach()
        {
            try
            {
                // Best-effort: re-foreground this S1API TV app after the player re-enters TV interaction.
                // RMB detach may have left TV/home state in a way where re-interaction shows vanilla home/lock screen.
                // We avoid calling OnOpened() (would reinit/navigate browser) and instead restore S1API app UI/state and hide home screen.
                var s1Type = typeof(S1API.TVApp.TVApp);

                // Ensure S1API app state indicates open/not paused.
                try { SetInstanceFieldOrProperty(s1Type, this, "_isOpen", true); } catch { }
                try { SetInstanceFieldOrProperty(s1Type, this, "_isPaused", false); } catch { }

                // Activate app root/canvas/container and make visible/interactable.
                GameObject? appRoot = null;
                try { appRoot = GetInstanceFieldOrPropertyValue(s1Type, this, "_appRoot") as GameObject; } catch { appRoot = null; }
                if (appRoot != null)
                {
                    try { if (!appRoot.activeSelf) appRoot.SetActive(true); } catch { }
                }

                try
                {
                    var container = GetInstanceFieldOrPropertyValue(s1Type, this, "_container") as GameObject;
                    if (container != null && !container.activeSelf) container.SetActive(true);
                }
                catch { }

                try
                {
                    var canvasGroup = GetInstanceFieldOrPropertyValue(s1Type, this, "_canvasGroup") as CanvasGroup;
                    if (canvasGroup != null)
                    {
                        canvasGroup.alpha = 1f;
                        canvasGroup.interactable = true;
                        canvasGroup.blocksRaycasts = true;
                    }
                }
                catch { }

                // Try to close/hide the TV home screen so our app is foregrounded (similar intent to S1API button click flow).
                try
                {
                    var home = GetInstanceFieldOrPropertyValue(s1Type, this, "_homeScreen");
                    if (home != null)
                    {
                        TrySetS1ApiSkipInterfaceClose(true);
                        try
                        {
                            // Prefer Close() if available on the home screen object.
                            var ht = home.GetType();
                            var closeMi = ht.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (closeMi != null)
                            {
                                closeMi.Invoke(home, null);
                            }
                            else
                            {
                                // Best-effort: deactivate its GameObject if it's a Unity component/object.
                                if (home is UnityEngine.Component hc) { try { hc.gameObject.SetActive(false); } catch { } }
                                else if (home is UnityEngine.Object huo) { try { (huo as GameObject)?.SetActive(false); } catch { } }
                            }
                        }
                        finally
                        {
                            // Patch/helper may consume the flag; clear best-effort.
                            TrySetS1ApiSkipInterfaceClose(false);
                        }
                    }
                }
                catch { }

                // Ensure our app stays visually on top in TV hierarchy where possible.
                try
                {
                    if (appRoot != null) appRoot.transform.SetAsLastSibling();
                }
                catch { }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryForegroundS1AppForReattach failed (non-fatal): {ex.Message}");
            }
        }

        private static void TrySetS1ApiSkipInterfaceClose(bool value)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetType("S1API.Internal.Patches.TVHomeScreen_Close_Patch", false); } catch { t = null; }
                    if (t == null) continue;
                    var p = t.GetProperty("SkipInterfaceClose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (p != null && p.CanWrite) { try { p.SetValue(null, value); return; } catch { } }
                    var f = t.GetField("SkipInterfaceClose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (f != null) { try { f.SetValue(null, value); return; } catch { } }
                    var backing = t.GetField("<SkipInterfaceClose>k__BackingField", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (backing != null) { try { backing.SetValue(null, value); return; } catch { } }
                }
            }
            catch { }
        }

        private static object? GetInstanceFieldOrPropertyValue(Type declaringType, object instance, string name)
        {
            try
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                Type? t = declaringType;
                while (t != null)
                {
                    var f = t.GetField(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(instance);
                    var p = t.GetProperty(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                    if (p != null && p.CanRead) return p.GetValue(instance);
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static void SetInstanceFieldOrProperty(Type declaringType, object instance, string name, object? value)
        {
            try
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                Type? t = declaringType;
                while (t != null)
                {
                    var f = t.GetField(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) { f.SetValue(instance, value); return; }
                    var p = t.GetProperty(name, flags | System.Reflection.BindingFlags.DeclaredOnly);
                    if (p != null && p.CanWrite) { p.SetValue(instance, value); return; }
                    t = t.BaseType;
                }
            }
            catch { }
        }

        private void SetAppInteractionEnabled(bool enabled)
        {
            try
            {
                GameObject? appGo = null;
                if (_videoDisplay != null)
                {
                    var t = _videoDisplay.transform;
                    for (int i = 0; i < 10 && t != null; i++)
                    {
                        if (t.name == "UnderNetBrowser_TVApp") { appGo = t.gameObject; break; }
                        t = t.parent;
                    }
                    if (appGo == null)
                    {
                        try { appGo = _videoDisplay.transform.parent != null ? _videoDisplay.transform.parent.gameObject : null; } catch { appGo = null; }
                    }
                }
                if (appGo == null)
                {
                    try { appGo = GameObject.Find("UnderNetBrowser_TVApp"); } catch { appGo = null; }
                }
                if (appGo == null) return;

                if (enabled)
                {
                    try
                    {
                        // Reactivate app and ancestors in case TV/app state left them inactive on some clients.
                        var cur = appGo.transform;
                        int depth = 0;
                        while (cur != null && depth++ < 8)
                        {
                            try { if (!cur.gameObject.activeSelf) cur.gameObject.SetActive(true); } catch { }
                            if (cur.name == "TVInterface") break;
                            cur = cur.parent;
                        }
                    }
                    catch { }
                }

                try
                {
                    var cg = appGo.GetComponent<CanvasGroup>() ?? appGo.GetComponentInParent<CanvasGroup>();
                    if (cg != null)
                    {
                        cg.alpha = 1f;
                        cg.interactable = enabled;
                        cg.blocksRaycasts = enabled;
                    }
                }
                catch { }

                try
                {
                    var canvas = appGo.GetComponent<Canvas>() ?? appGo.GetComponentInParent<Canvas>();
                    if (canvas != null)
                    {
                        canvas.enabled = true;
                        if (enabled)
                        {
                            // Best-effort reset from detached-visual override; normal app uses default ordering.
                            try { canvas.overrideSorting = false; } catch { }
                            try { canvas.sortingOrder = 0; } catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        private int _lastLoggedRaycasterCount = -1;
        private int _lastLoggedRaycasterEnabled = -1;
        private float _lastLoggedRaycasterTime = -999f;

        private void EnsureGraphicRaycastersEnabledForApp(string reason = "")
        {
            try
            {
                GameObject? appGo = null;
                if (_videoDisplay != null)
                {
                    var t = _videoDisplay.transform;
                    for (int i = 0; i < 10 && t != null; i++)
                    {
                        if (t.name == "UnderNetBrowser_TVApp") { appGo = t.gameObject; break; }
                        t = t.parent;
                    }
                    if (appGo == null) appGo = _videoDisplay.gameObject;
                }
                if (appGo == null) return;

                var raycasters = appGo.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true);
                if (raycasters == null) return;
                int enabledCount = 0;
                for (int i = 0; i < raycasters.Length; i++)
                {
                    var gr = raycasters[i];
                    if (gr == null) continue;
                    try { if (!gr.enabled) gr.enabled = true; } catch { }
                    try { if (gr.enabled) enabledCount++; } catch { }
                }

                bool verbose = UnderNetBrowserConfig.DebugLogging?.Value == true || UnderNetBrowserConfig.AlwaysLogDiagnostics?.Value == true;
                bool changed = raycasters.Length != _lastLoggedRaycasterCount || enabledCount != _lastLoggedRaycasterEnabled;
                bool periodic = (Time.realtimeSinceStartup - _lastLoggedRaycasterTime) >= 5f;
                if (verbose || changed || periodic)
                {
                    _lastLoggedRaycasterCount = raycasters.Length;
                    _lastLoggedRaycasterEnabled = enabledCount;
                    _lastLoggedRaycasterTime = Time.realtimeSinceStartup;
                    string suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" reason={reason}";
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Reattach raycasters: count={raycasters.Length} enabled={enabledCount}{suffix}");
                }
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] EnsureGraphicRaycastersEnabledForApp failed: {ex.Message}"); } catch { }
            }
        }

        private void LogTvDetachDiscovery()
        {
            // Disabled by default: full-scene discovery caused massive hitches/freezes on IL2CPP.
            // Keep a cheap targeted probe only.
            try
            {
                string[] goNames = { "TVInterface", "TV_Built(Clone)" };
                foreach (var name in goNames)
                {
                    GameObject? go = null;
                    try { go = GameObject.Find(name); } catch { go = null; }
                    if (go == null) continue;
                    try
                    {
                        var compsOnGo = go.GetComponents<Component>();
                        int n = compsOnGo != null ? compsOnGo.Length : 0;
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach probe: GO '{name}' active={go.activeInHierarchy} path={GetTransformPath(go.transform)} components={n}");
                        if (compsOnGo != null)
                        {
                            foreach (var cc in compsOnGo)
                            {
                                if (cc == null) continue;
                                string tname = "?";
                                try { tname = cc.GetType().FullName ?? cc.GetType().Name ?? "?"; } catch { }
                                bool likeIface = false, likeInter = false;
                                try { var ct = cc.GetType(); likeIface = LooksLikeTvInterface(ct); likeInter = LooksLikeTvInteractable(ct); } catch { }
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach probe:   '{name}' component='{tname}' likeInterface={likeIface} likeInteractable={likeInter}");
                            }
                        }
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach probe GO '{name}' failed: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach probe failed (non-fatal): {ex.Message}");
            }
        }

        private static (Type? type, string name) GetBestComponentTypeInfo(Component c)
        {
            // Keep lightweight: avoid expensive IL2CPP reflection/assembly scans on hot paths.
            try
            {
                Type managedType = c.GetType();
                string managedName = managedType.FullName ?? managedType.Name ?? "";
                return (managedType, managedName);
            }
            catch
            {
                return (typeof(Component), "?");
            }
        }

        private static readonly Dictionary<string, Type?> _typeLookupCache = new Dictionary<string, Type?>(StringComparer.Ordinal);
        private static bool _hotbarLikeScanCompletedNoTargets;

        private static (UnityEngine.Object? obj, Type? type) TryGetIl2CppComponentByTypeNames(GameObject go, params string[] typeNames)
        {
            try
            {
                foreach (var name in typeNames)
                {
                    var managedType = FindLoadedTypeByName(name);
                    if (managedType == null) continue;

                    // Try Il2CppSystem.Type GetComponent overload via Il2CppType.From(managedType)
                    try
                    {
                        var il2cppTypeUtil = Type.GetType("Il2CppInterop.Runtime.Il2CppType, Il2CppInterop.Runtime");
                        var miFrom = il2cppTypeUtil?.GetMethod("From", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Type) }, null);
                        if (miFrom != null)
                        {
                            var il2cppType = miFrom.Invoke(null, new object[] { managedType });
                            if (il2cppType != null)
                            {
                                var goType = go.GetType();
                                var miGetComp = goType.GetMethod("GetComponent", new[] { il2cppType.GetType() });
                                if (miGetComp != null)
                                {
                                    var comp = miGetComp.Invoke(go, new[] { il2cppType }) as UnityEngine.Object;
                                    if (comp != null) return (comp, managedType);
                                }
                            }
                        }
                    }
                    catch { }

                    // No safe managed-Type fallback here (IL2CPP Unity expects Il2CppSystem.Type).
                }
            }
            catch { }
            return (null, null);
        }

        private static (UnityEngine.Object? obj, Type? type) TryCastIl2CppObject(Component c, params string[] typeNames)
        {
            try
            {
                // Only meaningful for IL2CPP objects
                var il2cppBase = Type.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, Il2CppInterop.Runtime");
                if (il2cppBase == null || !il2cppBase.IsInstanceOfType(c)) return (null, null);

                foreach (var name in typeNames)
                {
                    var targetType = FindLoadedTypeByName(name);
                    if (targetType == null) continue;
                    try
                    {
                        // Try generic TryCast<T>() instance method
                        var methods = c.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        foreach (var mi in methods)
                        {
                            if (mi.Name != "TryCast" || !mi.IsGenericMethodDefinition) continue;
                            var gmi = mi.MakeGenericMethod(targetType);
                            var res = gmi.Invoke(c, null) as UnityEngine.Object;
                            if (res != null) return (res, targetType);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return (null, null);
        }

        private static bool IsLocalPlayerInTvInterface(Type ifaceType, UnityEngine.Object ifaceObj)
        {
            try
            {
                var localPlayer = FindGameLocalPlayer();
                if (localPlayer == null) return false;

                // Try Players list/collection property/field
                object? playersObj = null;
                try
                {
                    var p = ifaceType.GetProperty("Players", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (p != null) playersObj = p.GetValue(ifaceObj);
                }
                catch { }
                if (playersObj == null)
                {
                    try
                    {
                        var f = ifaceType.GetField("Players", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (f != null) playersObj = f.GetValue(ifaceObj);
                    }
                    catch { }
                }
                if (playersObj == null) return false;

                // Try Contains(localPlayer)
                try
                {
                    var miContains = playersObj.GetType().GetMethod("Contains", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (miContains != null)
                    {
                        var r = miContains.Invoke(playersObj, new[] { localPlayer });
                        if (r is bool b) return b;
                    }
                }
                catch { }

                // Fallback: enumerate and compare by reference/equality
                try
                {
                    if (playersObj is System.Collections.IEnumerable en)
                    {
                        foreach (var item in en)
                        {
                            if (item == null) continue;
                            if (ReferenceEquals(item, localPlayer)) return true;
                            try { if (item.Equals(localPlayer)) return true; } catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
            return false;
        }

        private static Type? FindLoadedTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            try
            {
                if (_typeLookupCache.TryGetValue(fullName, out var cached)) return cached;
            }
            catch { }

            Type? found = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(fullName, false);
                        if (t != null) { found = t; break; }
                    }
                    catch { }
                }
            }
            catch { }

            try { _typeLookupCache[fullName] = found; } catch { }
            return found;
        }

        // (GetTransformPath already defined elsewhere in class)

        private static bool LooksLikeTvInterface(Type t)
        {
            try
            {
                // Verified TVInterface surface: RemovePlayer(Player), AddPlayer(Player), Open(), Close(), CanOpen(), IsOpen
                var hasRemove = t.GetMethod("RemovePlayer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;
                var hasAdd = t.GetMethod("AddPlayer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;
                var hasOpen = t.GetMethod("Open", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
                var hasClose = t.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
                var isOpenProp = t.GetProperty("IsOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return (hasRemove && (hasClose || hasOpen)) || (hasAdd && hasRemove && (isOpenProp != null || hasOpen));
            }
            catch { return false; }
        }

        private static bool LooksLikeTvInteractable(Type t)
        {
            try
            {
                // Verified TVInteractable surface: Interacted(), Interface field/property
                var hasInteracted = t.GetMethod("Interacted", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
                if (!hasInteracted) return false;
                var ifaceProp = t.GetProperty("Interface", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var ifaceField = t.GetField("Interface", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return ifaceProp != null || ifaceField != null;
            }
            catch { return false; }
        }

        private bool TryInvokePreferredMethodsOnInstance(Type ownerType, UnityEngine.Object instance, string[] methodPreference, bool preferFullExit)
        {
            try
            {
                var methods = ownerType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                foreach (var wanted in methodPreference)
                {
                    foreach (var mi in methods)
                    {
                        if (!string.Equals(mi.Name, wanted, StringComparison.Ordinal)) continue;
                        if (TryInvokeExitLikeMethod(ownerType, instance, mi, preferFullExit))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryInvokePreferredMethodsOnInstance failed for {ownerType.Name}: {ex.Message}");
            }
            return false;
        }

        private bool TryInvokeExitOnComponentsByName(string[] nameHints, bool preferInteractable)
        {
            bool invoked = false;
            try
            {
                var comps = Resources.FindObjectsOfTypeAll<Component>();
                if (comps == null) return false;
                foreach (var c in comps)
                {
                    try
                    {
                        if (c == null) continue;
                        var t = c.GetType();
                        var full = t.FullName ?? t.Name ?? "";
                        if (string.IsNullOrEmpty(full)) continue;

                        bool isInteractable = full.IndexOf("TVInteractable", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isInterface = full.IndexOf("TVInterface", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isInteractable && !isInterface) continue;

                        // Prefer interactable EndInteract to restore player control/camera without going to TV home.
                        string[] prefs = isInteractable
                            ? new[] { "EndInteract", "StopInteract", "EndInteraction", "StopInteraction", "Exit", "Close" }
                            : new[] { "Exit", "End", "Leave", "Close", "EndInteract", "StopInteract" };

                        // If we prefer interactable pass, try interactables first in this scan order naturally; invoke when matched.
                        var methods = t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        foreach (var wanted in prefs)
                        {
                            foreach (var mi in methods)
                            {
                                if (!string.Equals(mi.Name, wanted, StringComparison.Ordinal)) continue;
                                // Avoid calling TVApp close-like methods that target app/home; focus on interaction exit.
                                if (TryInvokeExitLikeMethod(t, c, mi, preferFullExit: true))
                                {
                                    invoked = true;
                                    // For interactables, one successful EndInteract is usually enough; continue for interfaces only if needed.
                                    if (isInteractable) return true;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryInvokeExitOnComponentsByName failed (non-fatal): {ex.Message}");
            }
            return invoked;
        }

        private bool TryInvokeTvExitOnType(string il2cppName, string monoName, string[] methodPreference, bool preferFullExit)
        {
            try
            {
                Type? tvType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { tvType = asm.GetType(il2cppName, false) ?? asm.GetType(monoName, false); if (tvType != null) break; } catch { }
                }
                if (tvType == null) return false;

                UnityEngine.Object? instance = FindUnityObjectInstanceOfType(tvType);
                if (instance == null) instance = FindStaticSingletonInstance(tvType);
                if (instance == null)
                {
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Detach: instance not found for {tvType.FullName}.");
                    return false;
                }

                var methods = tvType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                foreach (var wanted in methodPreference)
                {
                    foreach (var mi in methods)
                    {
                        if (!string.Equals(mi.Name, wanted, StringComparison.Ordinal)) continue;
                        if (TryInvokeExitLikeMethod(tvType, instance, mi, preferFullExit))
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryInvokeTvExitOnType failed for {il2cppName}: {ex.Message}");
            }
            return false;
        }

        private static UnityEngine.Object? FindUnityObjectInstanceOfType(Type tvType)
        {
            try
            {
                // Prefer Unity's typed lookup when possible (overload/signature varies across Mono/IL2CPP bindings)
                try
                {
                    var methods = typeof(Resources).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    foreach (var m in methods)
                    {
                        if (m.Name != "FindObjectsOfTypeAll") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        object? arg = null;
                        var pt = ps[0].ParameterType;
                        if (pt == typeof(Type)) arg = tvType;
                        else if (pt.Name.Contains("Type"))
                        {
                            try
                            {
                                var from = pt.GetMethod("From", new[] { typeof(Type) });
                                if (from != null) arg = from.Invoke(null, new object[] { tvType });
                            }
                            catch { }
                        }
                        if (arg == null) continue;
                        try
                        {
                            var found = m.Invoke(null, new[] { arg }) as System.Collections.IEnumerable;
                            if (found == null) continue;
                            foreach (var o in found)
                            {
                                if (o is UnityEngine.Object uo) return uo;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Targeted fallback: scan Components/Behaviours by type identity/name (faster than scanning all UnityEngine.Objects/GameObjects)
                try
                {
                    var comps = Resources.FindObjectsOfTypeAll<Component>();
                    if (comps != null)
                    {
                        string tvName = tvType.Name ?? "";
                        string tvFull = tvType.FullName ?? "";
                        foreach (var c in comps)
                        {
                            try
                            {
                                if (c == null) continue;
                                var ct = c.GetType();
                                if (ct == tvType || tvType.IsAssignableFrom(ct)) return c;
                                var ctn = ct.FullName ?? ct.Name ?? "";
                                if (!string.IsNullOrEmpty(tvFull) && string.Equals(ctn, tvFull, StringComparison.Ordinal)) return c;
                                if (!string.IsNullOrEmpty(tvName) && (ctn.EndsWith("." + tvName, StringComparison.Ordinal) || ctn == tvName || ctn.IndexOf(tvName, StringComparison.Ordinal) >= 0))
                                    return c;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
            return null;
        }

        private static UnityEngine.Object? FindStaticSingletonInstance(Type tvType)
        {
            foreach (var name in new[] { "Instance", "instance", "Singleton", "Current" })
            {
                try
                {
                    var p = tvType.GetProperty(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (p != null && p.GetValue(null) is UnityEngine.Object po) return po;
                }
                catch { }
                try
                {
                    var f = tvType.GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f != null && f.GetValue(null) is UnityEngine.Object fo) return fo;
                }
                catch { }
            }
            return null;
        }

        private bool TryInvokeExitLikeMethod(Type ownerType, UnityEngine.Object instance, System.Reflection.MethodInfo mi, bool preferFullExit)
        {
            try
            {
                var ps = mi.GetParameters();
                if (ps.Length == 0)
                {
                    mi.Invoke(instance, null);
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {ownerType.Name}.{mi.Name}() for detach.");
                    return true;
                }

                if (ps.Length == 1)
                {
                    var pt = ps[0].ParameterType;
                    object? arg = null;

                    if (pt.IsEnum)
                    {
                        // Prefer values that indicate full exit/close/end over home/back navigation when detaching to gameplay.
                        var names = Enum.GetNames(pt);
                        string[] preferredFull = { "Exit", "Close", "End", "Leave", "Stop", "Quit" };
                        string[] avoidIfPossible = { "Home", "Menu", "Back" };
                        object? chosen = null;

                        if (preferFullExit)
                        {
                            foreach (var pref in preferredFull)
                            {
                                foreach (var en in names)
                                {
                                    if (en.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        !ContainsAny(en, avoidIfPossible))
                                    { chosen = Enum.Parse(pt, en); break; }
                                }
                                if (chosen != null) break;
                            }
                        }

                        if (chosen == null)
                        {
                            foreach (var en in names)
                            {
                                if (en.IndexOf("Exit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    en.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    en.IndexOf("Back", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    en.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                                { chosen = Enum.Parse(pt, en); break; }
                            }
                        }

                        arg = chosen ?? Enum.ToObject(pt, 0);
                    }
                    else if (pt == typeof(bool)) arg = true;
                    else if (pt == typeof(int)) arg = 0;
                    else return false; // unknown arg type; skip

                    mi.Invoke(instance, new[] { arg });
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {ownerType.Name}.{mi.Name}({pt.Name}={arg}) for detach.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] {ownerType.Name}.{mi.Name} invoke failed: {ex.Message}");
            }
            return false;
        }

        private static bool ContainsAny(string text, string[] needles)
        {
            if (string.IsNullOrEmpty(text) || needles == null) return false;
            foreach (var n in needles)
            {
                if (!string.IsNullOrEmpty(n) && text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private float _lastUrlSyncTime;
        private string _lastSyncedUrl = "";
        private float _lastVisualDiagLog;

        private void SetUrlDisplayText(string text)
        {
            try
            {
                if (_urlDisplayText == null) return;
                _urlDisplayText.text = text ?? "";
                _urlDisplayText.enabled = true;
                if (!_urlDisplayText.gameObject.activeSelf) _urlDisplayText.gameObject.SetActive(true);
            }
            catch { }
        }

        private static string CleanDisplayUrl(string? url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return url ?? "";
                var raw = url.Trim();
                if (raw.Equals("undernet:home", StringComparison.OrdinalIgnoreCase) || raw.Equals("about:home", StringComparison.OrdinalIgnoreCase))
                    return "undernet:home";
                if (TryParseLocalAppRoute(raw, out var appIdRaw))
                    return "home:" + appIdRaw;
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

                var host = uri.Host ?? "";
                bool isYouTube = host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
                bool isGoogle = host.Equals("google.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("google.", StringComparison.OrdinalIgnoreCase);
                // Simple google host matcher for common domains (google.com, www.google.com, google.co.uk etc.)
                if (!isGoogle)
                {
                    try
                    {
                        var labels = host.Split('.');
                        if (labels.Length >= 2)
                        {
                            // e.g. www.google.co.uk -> labels contain "google"
                            for (int i = 0; i < labels.Length - 1; i++)
                                if (labels[i].Equals("google", StringComparison.OrdinalIgnoreCase)) { isGoogle = true; break; }
                        }
                    }
                    catch { }
                }
                if (isGoogle)
                {
                    try
                    {
                        var q = ParseQueryString(uri.Query);
                        bool changed = false;
                        foreach (var k in new[] { "zx", "sei" })
                        {
                            if (q.Remove(k)) changed = true;
                        }
                        var path = uri.AbsolutePath ?? "/";
                        if (string.IsNullOrEmpty(path)) path = "/";
                        // Build cleaned URL for Google (display): drop ephemeral params like zx.
                        var ub = new UriBuilder(uri) { Query = "" };
                        if (q.Count > 0)
                        {
                            var parts = new System.Collections.Generic.List<string>();
                            foreach (var kv in q)
                            {
                                if (kv.Value == null) continue;
                                foreach (var v in kv.Value)
                                    parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(v ?? ""));
                            }
                            ub.Query = string.Join("&", parts);
                        }
                        var cleanedGoogle = ub.Uri.ToString();
                        // Normalize bare root to trailing slash form
                        if (q.Count == 0 && (path == "/" || path == "") && string.IsNullOrEmpty(ub.Uri.Query))
                        {
                            var portPart = ub.Uri.IsDefaultPort ? "" : ":" + ub.Port;
                            cleanedGoogle = $"{ub.Scheme}://{ub.Host}{portPart}/";
                        }
                        return changed ? cleanedGoogle : (q.Count == 0 && (path == "/" || path == "") ? cleanedGoogle : raw);
                    }
                    catch { return raw; }
                }
                if (!isYouTube) return raw;

                // youtu.be/<id> -> keep id (+ optional t/start)
                if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    var idPath = (uri.AbsolutePath ?? "").Trim('/');
                    if (string.IsNullOrEmpty(idPath)) return raw;
                    var q = ParseQueryString(uri.Query);
                    string? t = GetFirstQueryValue(q, "t") ?? GetFirstQueryValue(q, "start") ?? GetFirstQueryValue(q, "time_continue");
                    var cleanShort = "https://youtu.be/" + idPath;
                    if (!string.IsNullOrWhiteSpace(t)) cleanShort += "?t=" + Uri.EscapeDataString(t);
                    return cleanShort;
                }

                // youtube.com/watch?v=... -> keep v and optional time params only (drop list/start_radio/index/pp/etc.)
                if ((uri.AbsolutePath ?? "").Equals("/watch", StringComparison.OrdinalIgnoreCase))
                {
                    var q = ParseQueryString(uri.Query);
                    string? v = GetFirstQueryValue(q, "v");
                    if (string.IsNullOrWhiteSpace(v)) return raw;
                    string? t = GetFirstQueryValue(q, "t") ?? GetFirstQueryValue(q, "start") ?? GetFirstQueryValue(q, "time_continue");
                    var cleanWatch = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(v);
                    if (!string.IsNullOrWhiteSpace(t)) cleanWatch += "&t=" + Uri.EscapeDataString(t);
                    return cleanWatch;
                }

                return raw;
            }
            catch { return url ?? ""; }
        }

        private static Dictionary<string, List<string>> ParseQueryString(string? query)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(query)) return dict;
                var q = query;
                if (q.StartsWith("?", StringComparison.Ordinal)) q = q.Substring(1);
                if (q.Length == 0) return dict;
                foreach (var part in q.Split('&'))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    var kv = part.Split(new[] { '=' }, 2);
                    var key = Uri.UnescapeDataString(kv[0] ?? "");
                    if (string.IsNullOrEmpty(key)) continue;
                    var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1] ?? "") : "";
                    if (!dict.TryGetValue(key, out var list)) { list = new List<string>(); dict[key] = list; }
                    list.Add(val);
                }
            }
            catch { }
            return dict;
        }

        private static string? GetFirstQueryValue(Dictionary<string, List<string>> query, string key)
        {
            try
            {
                if (query != null && query.TryGetValue(key, out var list) && list != null && list.Count > 0)
                    return list[0];
            }
            catch { }
            return null;
        }

        private void SyncAddressBarFromBrowser()
        {
            try
            {
                if (_urlInput == null) return;
                bool focused = _urlInput.isFocused;
                // Keep reliable visible overlay in sync (InputField label can be invisible in this UI context).
                try { SetUrlDisplayText(_urlInput.text ?? ""); } catch { }
                if (focused) return;
                // Throttle P/Invoke + string allocations; URL changes are infrequent.
                if (Time.unscaledTime - _lastUrlSyncTime < 0.25f) return;
                _lastUrlSyncTime = Time.unscaledTime;

                string GetHomeDisplay()
                {
                    var homeDisplay = UnderNetBrowserConfig.HomeUrl?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(homeDisplay)) homeDisplay = "undernet:home";
                    if (homeDisplay.Equals("about:home", StringComparison.OrdinalIgnoreCase) || homeDisplay.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase)) homeDisplay = "undernet:home";
                    return homeDisplay;
                }

                if (_externalMirrorMode)
                {
                    try { SetUrlDisplayText("undernet:discord"); } catch { }
                    _lastSyncedUrl = "undernet:discord";
                    return;
                }

                string? url = null;
                try { url = _gpuCompositionCapture?.CurrentUrl; } catch { url = null; }
                if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("undernet:cmd/", StringComparison.OrdinalIgnoreCase))
                {
                    try { TryHandleUnderNetCommandUrl(url, navigateBackToSettings: true); } catch { }
                    return;
                }

                // Intercept in-page navigations to built-in undernet routes (custom scheme or intercept-friendly https routes).
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var u = url.Trim();
                    var n = NormalizeUrl(u);
                    if (n.Equals("undernet:home", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("undernet:games", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("undernet:settings", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("undernet:notepad", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("undernet:ai", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase))
                    {
                        try { SetUrl(n); } catch { }
                        return;
                    }
                }

                // Startup/early init or unavailable URL: keep bar populated and usable.
                if (string.IsNullOrWhiteSpace(url))
                {
                    if (string.IsNullOrEmpty(_urlInput.text))
                    {
                        var homeDisplay = GetHomeDisplay();
                        _urlInput.text = homeDisplay;
                        if (_urlInput.textComponent != null) _urlInput.textComponent.text = homeDisplay;
                        try { _urlInput.ForceLabelUpdate(); } catch { }
                        SetUrlDisplayText(homeDisplay);
                        _lastSyncedUrl = homeDisplay;
                    }
                    else
                    {
                        SetUrlDisplayText(_urlInput.text);
                    }
                    return;
                }

                // Keep friendly labels for local homepage/app routes.
                var display = url;
                try
                {
                    if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        var local = Path.GetFullPath(new Uri(url).LocalPath);

                        var homeCandidates = new[] { GetHomepageFilePath(), GetLegacyHomepageFilePath() };
                        foreach (var hc in homeCandidates)
                        {
                            try
                            {
                                if (string.Equals(local, Path.GetFullPath(hc), StringComparison.OrdinalIgnoreCase))
                                { display = "undernet:home"; break; }
                            }
                            catch { }
                        }

                        // Built-in homepage companion pages
                        if (!string.Equals(display, "undernet:home", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var homeDir = Path.GetFullPath(GetHomepageDir());
                                string MapHomePage(string file, string token)
                                {
                                    try { if (string.Equals(local, Path.GetFullPath(Path.Combine(homeDir, file)), StringComparison.OrdinalIgnoreCase)) return token; } catch { }
                                    return "";
                                }
                                var g = MapHomePage("games.html", "undernet:games"); if (!string.IsNullOrEmpty(g)) display = g;
                                if (string.IsNullOrEmpty(g))
                                {
                                    var s = MapHomePage("settings.html", "undernet:settings"); if (!string.IsNullOrEmpty(s)) display = s;
                                    if (string.IsNullOrEmpty(s))
                                    {
                                        var n = MapHomePage("notepad.html", "undernet:notepad"); if (!string.IsNullOrEmpty(n)) display = n;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!string.Equals(display, "undernet:home", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(display, "undernet:games", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(display, "undernet:settings", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(display, "undernet:notepad", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var appsRoot = Path.GetFullPath(GetAppsRootDir()) + Path.DirectorySeparatorChar;
                                if (local.StartsWith(appsRoot, StringComparison.OrdinalIgnoreCase))
                                {
                                    var rel = local.Substring(appsRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                    var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length >= 2 && string.Equals(parts[1], "index.html", StringComparison.OrdinalIgnoreCase) && IsSafeAppId(parts[0]))
                                        display = "home:" + parts[0].ToLowerInvariant();
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                display = CleanDisplayUrl(display);
                // AI page (Google AI) keeps its real URL in the address bar; no undernet:ai alias display.
                try { } catch { }
                if (string.IsNullOrWhiteSpace(display)) display = GetHomeDisplay();
                if (string.Equals(_lastSyncedUrl, display, StringComparison.Ordinal) && !string.IsNullOrEmpty(_urlInput.text))
                {
                    SetUrlDisplayText(display);
                    return;
                }
                bool urlChanged = !string.Equals(_lastSyncedUrl, display, StringComparison.Ordinal);
                _lastSyncedUrl = display;
                if (!string.Equals(_urlInput.text, display, StringComparison.Ordinal))
                {
                    _urlInput.text = display;
                    if (_urlInput.textComponent != null) _urlInput.textComponent.text = display;
                    try { _urlInput.ForceLabelUpdate(); } catch { }
                }
                SetUrlDisplayText(display);
                try { TryApplyAiPageCustomization(url); } catch { }

                // After navigation/URL change, keep web content focused for immediate interaction (unless URL bar is focused).
                if (urlChanged && IsOpen && !_detachedToGameplay && (_urlInput == null || !_urlInput.isFocused))
                {
                    try { _compositionWebFocused = true; } catch { }
                    try { UpdateInputOwnershipState(); } catch { }
                    try { _gpuCompositionCapture?.RequestFocus(); } catch { }
                }
            }
            catch { }
        }

        private void SetHint(string message)
        {
            try
            {
                if (_hintText == null) return;
                if (string.IsNullOrEmpty(message)) { _hintText.enabled = false; _hintBaseMessage = ""; return; }
                _hintBaseMessage = message;
                _hintAnimTime = 0f;
                _hintText.supportRichText = true;
                _hintText.text = message;
                _hintText.enabled = true;
            }
            catch { }
        }

        private bool IsPointerOverBrowser(out int px, out int py)
        {
            // Composition-only path: same hit-test as video display mapping.
            return IsPointerOverVideoDisplay(out px, out py);
        }

        private void TrySuppressHotbarScroll()
        {
            // Best-effort: if known suppression states were captured, reapply them.
            if (_savedInputStates.Count > 0) { TryEnsureGameInputSuppressed(); return; }
            // If we couldn't capture states, try to disable common hotbar/inventory toggles opportunistically.
            try { TryDisableHotbarOnInventoryLikeTypes(captureState: false); } catch { }
            try { TryRestoreEquippedSlotIndex(); } catch { }
        }

        private int _lastKnownHotbarSlot = -1;
        private object? _cachedHotbarSlotTarget;
        private Type? _cachedHotbarSlotDeclaringType;
        private System.Reflection.MemberInfo? _cachedHotbarSlotMember;
        private bool _cachedHotbarSlotIsStatic;
        private bool _hotbarSlotAccessorResolved;
        private float _lastHotbarSlotResolveAttempt;

        private void TrySuppressHotbarNumberKeys()
        {
            bool pressed = false;
            for (int i = 0; i < 9; i++) { if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) { pressed = true; break; } }
            if (!pressed) return;
            try { TrySuppressHotbarScroll(); } catch { }
            // Also try to immediately restore previously known slot (best-effort, runtime-discovered)
            try { TryRestoreEquippedSlotIndex(forceRestore: true); } catch { }
        }

        private void TryRestoreEquippedSlotIndex(bool forceRestore = false)
        {
            try
            {
                // Prefer verified PlayerInventory.Instance.EquippedSlotIndex (IL2CPP Schedule I) when available.
                // Cache accessor to avoid expensive all-assembly scans every frame/tick.
                if (!_hotbarSlotAccessorResolved || (_cachedHotbarSlotMember == null && Time.realtimeSinceStartup - _lastHotbarSlotResolveAttempt > 2f))
                {
                    TryResolveHotbarSlotAccessor();
                }

                if (_cachedHotbarSlotMember != null)
                {
                    try
                    {
                        if (TryReadCachedHotbarSlot(out int cur))
                        {
                            if (_lastKnownHotbarSlot < 0) _lastKnownHotbarSlot = cur;
                            if (forceRestore && _lastKnownHotbarSlot >= 0 && cur != _lastKnownHotbarSlot)
                            {
                                TryWriteCachedHotbarSlot(_lastKnownHotbarSlot);
                                return;
                            }
                            if (!forceRestore) _lastKnownHotbarSlot = cur;
                            return;
                        }
                    }
                    catch
                    {
                        // accessor went stale; retry resolve later
                        _cachedHotbarSlotMember = null;
                        _hotbarSlotAccessorResolved = false;
                    }
                }

                // Fallback: bounded opportunistic scan (only when accessor unresolved and throttled)
                if (Time.realtimeSinceStartup - _lastHotbarSlotResolveAttempt < 1.0f) return;
                TryResolveHotbarSlotAccessor(forceScan: true);
                if (_cachedHotbarSlotMember != null && TryReadCachedHotbarSlot(out int cur2))
                {
                    if (_lastKnownHotbarSlot < 0) _lastKnownHotbarSlot = cur2;
                    if (forceRestore && _lastKnownHotbarSlot >= 0 && cur2 != _lastKnownHotbarSlot) TryWriteCachedHotbarSlot(_lastKnownHotbarSlot);
                    else if (!forceRestore) _lastKnownHotbarSlot = cur2;
                }
            }
            catch { }
        }

        private void TryResolveHotbarSlotAccessor(bool forceScan = false)
        {
            _lastHotbarSlotResolveAttempt = Time.realtimeSinceStartup;
            try
            {
                // 1) Verified/preferred: Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance.EquippedSlotIndex
                foreach (var tn in new[] { "Il2CppScheduleOne.PlayerScripts.PlayerInventory", "ScheduleOne.PlayerScripts.PlayerInventory" })
                {
                    Type? invType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { invType = asm.GetType(tn, false); if (invType != null) break; } catch { } }
                    if (invType == null) continue;
                    object? inv = null;
                    foreach (var sm in new[] { "Instance", "instance", "Singleton", "Local" })
                    {
                        try
                        {
                            var sp = invType.GetProperty(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                            if (sp != null) inv = sp.GetValue(null);
                            if (inv == null)
                            {
                                var sf = invType.GetField(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                                if (sf != null) inv = sf.GetValue(null);
                            }
                            if (inv != null) break;
                        }
                        catch { }
                    }
                    if (inv == null) continue;
                    foreach (var name in new[] { "EquippedSlotIndex", "_equippedSlotIndex", "equippedSlotIndex" })
                    {
                        if (TryCacheSlotMember(inv, inv.GetType(), name, isStatic: false)) { _hotbarSlotAccessorResolved = true; return; }
                    }
                }

                if (!forceScan)
                {
                    _hotbarSlotAccessorResolved = true; // don't rescan constantly if preferred path unavailable now
                    return;
                }

                // 2) Bounded fallback scan (only ScheduleOne/S1API-ish inventory/hotbar/player types)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        try
                        {
                            var tn = t.FullName ?? t.Name ?? "";
                            if (tn.IndexOf("ScheduleOne", StringComparison.OrdinalIgnoreCase) < 0 && tn.IndexOf("S1API", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (tn.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) < 0 && tn.IndexOf("Hotbar", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            object? inst = null;
                            foreach (var sm in new[] { "Instance", "instance", "Local", "local", "Singleton", "Current" })
                            {
                                try
                                {
                                    var sp = t.GetProperty(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (sp != null) inst = sp.GetValue(null);
                                    if (inst == null) { var sf = t.GetField(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); if (sf != null) inst = sf.GetValue(null); }
                                    if (inst != null) break;
                                }
                                catch { }
                            }
                            object?[] targets = inst != null ? new object?[] { inst } : new object?[] { null };
                            foreach (var target in targets)
                            {
                                var targetType = target?.GetType() ?? t;
                                foreach (var name in new[] { "EquippedSlotIndex", "equippedSlotIndex", "SelectedSlotIndex", "selectedSlotIndex", "CurrentSlotIndex", "currentSlotIndex" })
                                {
                                    if (TryCacheSlotMember(target, targetType, name, isStatic: target == null)) { _hotbarSlotAccessorResolved = true; return; }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally { _hotbarSlotAccessorResolved = true; }
        }

        private bool TryCacheSlotMember(object? target, Type type, string name, bool isStatic)
        {
            try
            {
                var flags = (isStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance) | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var p = type.GetProperty(name, flags);
                if (p != null && p.PropertyType == typeof(int) && p.CanRead)
                {
                    _cachedHotbarSlotTarget = target;
                    _cachedHotbarSlotDeclaringType = type;
                    _cachedHotbarSlotMember = p;
                    _cachedHotbarSlotIsStatic = isStatic;
                    return true;
                }
                var f = type.GetField(name, flags);
                if (f != null && f.FieldType == typeof(int))
                {
                    _cachedHotbarSlotTarget = target;
                    _cachedHotbarSlotDeclaringType = type;
                    _cachedHotbarSlotMember = f;
                    _cachedHotbarSlotIsStatic = isStatic;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private bool TryReadCachedHotbarSlot(out int value)
        {
            value = -1;
            if (_cachedHotbarSlotMember == null) return false;
            try
            {
                object? target = _cachedHotbarSlotIsStatic ? null : _cachedHotbarSlotTarget;
                // refresh instance from singleton if possible when stale null
                if (!_cachedHotbarSlotIsStatic && target == null && _cachedHotbarSlotDeclaringType != null)
                {
                    // best-effort: cannot reliably recover; force re-resolve
                    return false;
                }
                if (_cachedHotbarSlotMember is System.Reflection.PropertyInfo p)
                {
                    var v = p.GetValue(target);
                    if (v is int i) { value = i; return true; }
                }
                else if (_cachedHotbarSlotMember is System.Reflection.FieldInfo f)
                {
                    var v = f.GetValue(target);
                    if (v is int i) { value = i; return true; }
                }
            }
            catch { }
            return false;
        }

        private void TryWriteCachedHotbarSlot(int value)
        {
            if (_cachedHotbarSlotMember == null) return;
            try
            {
                object? target = _cachedHotbarSlotIsStatic ? null : _cachedHotbarSlotTarget;
                if (_cachedHotbarSlotMember is System.Reflection.PropertyInfo p && p.CanWrite) p.SetValue(target, value);
                else if (_cachedHotbarSlotMember is System.Reflection.FieldInfo f) f.SetValue(target, value);
            }
            catch { }
        }

        private void TryDisableHotbarOnInventoryLikeTypes(bool captureState = true)
        {
            // This broad GetTypes() scan is expensive; if a prior full scan found no actionable targets on this runtime, skip repeats.
            if (_hotbarLikeScanCompletedNoTargets)
            {
                try
                {
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                    {
                        float nowRt = Time.realtimeSinceStartup;
                        if (nowRt - _lastHotbarScanSkipLogRealtime >= 10f)
                        {
                            _lastHotbarScanSkipLogRealtime = nowRt;
                            UnderNetBrowserConfig.Log($"[HotbarScan] skipped: prior scan found no actionable targets (capture={captureState})");
                        }
                    }
                }
                catch { }
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int assembliesTotal = 0, assembliesScanned = 0, assembliesSkipped = 0, typesEnumerated = 0, typesMatched = 0;
            int staticHits = 0, instanceHits = 0, capturedStatesAdded = 0;
            int savedBefore = 0;
            bool verbose = false;
            try { verbose = UnderNetBrowserConfig.DebugLogging?.Value == true; } catch { verbose = false; }
            try { savedBefore = _savedInputStates.Count; } catch { savedBefore = 0; }

            try
            {
                string[] memberNames = { "CanChangeEquippedItem", "canChangeEquippedItem", "EquippingEnabled", "equippingEnabled", "HotbarEnabled", "hotbarEnabled", "ScrollEnabled", "scrollEnabled" };
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                assembliesTotal = assemblies?.Length ?? 0;

                foreach (var asm in assemblies)
                {
                    string an = "";
                    try { an = asm.GetName().Name ?? ""; } catch { an = ""; }
                    // Limit expensive GetTypes() scan to relevant assemblies only.
                    if (an.IndexOf("ScheduleOne", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("S1API", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Il2Cpp", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        assembliesSkipped++;
                        continue;
                    }

                    assembliesScanned++;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    typesEnumerated += types.Length;

                    foreach (var t in types)
                    {
                        try
                        {
                            var tn = t.FullName ?? t.Name ?? "";
                            if (tn.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) < 0 &&
                                tn.IndexOf("Hotbar", StringComparison.OrdinalIgnoreCase) < 0 &&
                                tn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (tn.IndexOf("ScheduleOne", StringComparison.OrdinalIgnoreCase) < 0 &&
                                tn.IndexOf("S1API", StringComparison.OrdinalIgnoreCase) < 0 &&
                                tn.IndexOf("Il2CppScheduleOne", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            typesMatched++;

                            // Try static members
                            foreach (var m in memberNames)
                            {
                                try
                                {
                                    var p = t.GetProperty(m, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(bool))
                                    {
                                        object? prev = null;
                                        try { prev = p.GetValue(null); } catch { prev = null; }
                                        bool prevBool = prev is bool b && b;
                                        if (captureState)
                                        {
                                            int before = _savedInputStates.Count;
                                            TrySetStaticBoolByTypeName(tn, m, false);
                                            if (_savedInputStates.Count > before)
                                            {
                                                staticHits++;
                                                if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] static hit: asm='{an}' type='{tn}' member='{m}' (prop) prev={prevBool} -> false (captured)");
                                            }
                                        }
                                        else if (prevBool)
                                        {
                                            try { p.SetValue(null, false); staticHits++; if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] static apply: asm='{an}' type='{tn}' member='{m}' (prop) prev=true -> false"); } catch { }
                                        }
                                        continue;
                                    }

                                    var f = t.GetField(m, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (f != null && f.FieldType == typeof(bool))
                                    {
                                        object? prev = null;
                                        try { prev = f.GetValue(null); } catch { prev = null; }
                                        bool prevBool = prev is bool b && b;
                                        if (captureState)
                                        {
                                            int before = _savedInputStates.Count;
                                            TrySetStaticBoolByTypeName(tn, m, false);
                                            if (_savedInputStates.Count > before)
                                            {
                                                staticHits++;
                                                if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] static hit: asm='{an}' type='{tn}' member='{m}' (field) prev={prevBool} -> false (captured)");
                                            }
                                        }
                                        else if (prevBool)
                                        {
                                            try { f.SetValue(null, false); staticHits++; if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] static apply: asm='{an}' type='{tn}' member='{m}' (field) prev=true -> false"); } catch { }
                                        }
                                    }
                                }
                                catch { }
                            }

                            // Try singleton-like instances (Instance/instance/Local)
                            object? inst = null;
                            string instSource = "";
                            foreach (var sm in new[] { "Instance", "instance", "Local", "local", "Singleton" })
                            {
                                try
                                {
                                    var sp = t.GetProperty(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (sp != null) inst = sp.GetValue(null);
                                    if (inst == null)
                                    {
                                        var sf = t.GetField(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                        if (sf != null) inst = sf.GetValue(null);
                                    }
                                    if (inst != null) { instSource = sm; break; }
                                }
                                catch { }
                            }
                            if (inst == null) continue;

                            foreach (var m in memberNames)
                            {
                                try
                                {
                                    var it = inst.GetType();
                                    var p = it.GetProperty(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(bool))
                                    {
                                        object? prev = null;
                                        try { prev = p.GetValue(inst); } catch { prev = null; }
                                        bool prevBool = prev is bool b && b;
                                        if (captureState)
                                        {
                                            int before = _savedInputStates.Count;
                                            TrySetBoolMember(inst, m, false);
                                            if (_savedInputStates.Count > before)
                                            {
                                                instanceHits++;
                                                if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] instance hit: asm='{an}' type='{tn}' singleton='{instSource}' member='{m}' (prop) prev={prevBool} -> false (captured)");
                                            }
                                        }
                                        else if (prevBool)
                                        {
                                            try { p.SetValue(inst, false); instanceHits++; if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] instance apply: asm='{an}' type='{tn}' singleton='{instSource}' member='{m}' (prop) prev=true -> false"); } catch { }
                                        }
                                        continue;
                                    }

                                    var f = it.GetField(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (f != null && f.FieldType == typeof(bool))
                                    {
                                        object? prev = null;
                                        try { prev = f.GetValue(inst); } catch { prev = null; }
                                        bool prevBool = prev is bool b && b;
                                        if (captureState)
                                        {
                                            int before = _savedInputStates.Count;
                                            TrySetBoolMember(inst, m, false);
                                            if (_savedInputStates.Count > before)
                                            {
                                                instanceHits++;
                                                if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] instance hit: asm='{an}' type='{tn}' singleton='{instSource}' member='{m}' (field) prev={prevBool} -> false (captured)");
                                            }
                                        }
                                        else if (prevBool)
                                        {
                                            try { f.SetValue(inst, false); instanceHits++; if (verbose) UnderNetBrowserConfig.Log($"[HotbarScan] instance apply: asm='{an}' type='{tn}' singleton='{instSource}' member='{m}' (field) prev=true -> false"); } catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { UnderNetBrowserConfig.Log($"[HotbarScan] failed (non-fatal): {ex.Message}"); } catch { }
            }
            finally
            {
                try
                {
                    sw.Stop();
                    capturedStatesAdded = Math.Max(0, _savedInputStates.Count - savedBefore);
                    int totalHits = staticHits + instanceHits;
                    // Summary for narrowing/perf analysis (gated to debug logging to avoid normal-log noise).
                    UnderNetBrowserConfig.Log($"[HotbarScan] summary: capture={captureState} elapsedMs={sw.ElapsedMilliseconds} asmTotal={assembliesTotal} asmScanned={assembliesScanned} asmSkipped={assembliesSkipped} typesEnumerated={typesEnumerated} typesMatched={typesMatched} staticHits={staticHits} instanceHits={instanceHits} totalHits={totalHits} capturedStatesAdded={capturedStatesAdded}");
                    if (totalHits == 0)
                        _hotbarLikeScanCompletedNoTargets = true;
                }
                catch { }
            }
        }

        private void TryDisableGameInput()
        {
            try
            {
                _savedInputStates.Clear();
                // 1) Verified game API path (IL2CPP Schedule I): PlayerInventory.Instance controls hotbar/equipping.
                //    Research/knowledge: EquippedSlotIndex/setter, SetEquippedSlotIndex, EquippingEnabled, HotbarEnabled,
                //    SetEquippingEnabled(bool), SetInventoryEnabled(bool), IndexAllSlots exist on PlayerInventory.
                TryApplyVerifiedPlayerInventorySuppression(disable: true, captureState: true);

                // 2) Movement suppression (best-effort, evidence-based): no verified player CanMove/OnMove API found in inspected dumps for this build.
                //    Try disabling Unity Behaviour.enabled on Il2CppScheduleOne.PlayerScripts.PlayerMovement.Instance (verified component type exists)
                //    and any S1API CanMove members if present in this S1API version.
                TryApplyMovementSuppression(disable: true, captureState: true);

                // 3) Best-effort, reflection-based attempts against plausible S1API/game APIs (only if present in this build).
                //    We never assume APIs exist; failures are non-fatal and restored on close.
                TrySetMemberByTypeName("S1API.Entities.Player", "Local", null, childMember: "InventoryEnabled", setValue: false);
                TrySetMemberByTypeName("S1API.Player.Player", "Local", null, childMember: "InventoryEnabled", setValue: false);
                TrySetStaticBoolByTypeName("S1API.Player.PlayerInventory", "HotbarEnabled", false);
                TrySetStaticBoolByTypeName("S1API.Gameplay.PlayerInventory", "HotbarEnabled", false);
                string[] invTypeNames = {
                    "ScheduleOne.PlayerScripts.PlayerInventory",
                    "Il2CppScheduleOne.PlayerScripts.PlayerInventory",
                    "ScheduleOne.PlayerScripts.PlayerInventoryManager",
                    "Il2CppScheduleOne.PlayerScripts.PlayerInventoryManager"
                };
                foreach (var tn in invTypeNames)
                {
                    foreach (var instName in new[] { "Instance", "instance", "Local", "local", "Singleton" })
                    {
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "CanChangeEquippedItem", false);
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "canChangeEquippedItem", false);
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "EquippingEnabled", false);
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "equippingEnabled", false);
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "HotbarEnabled", false);
                        TrySetInstanceBoolOnSingletonLikeType(tn, instName, "hotbarEnabled", false);
                    }
                    TrySetStaticBoolByTypeName(tn, "HotbarEnabled", false);
                    TrySetStaticBoolByTypeName(tn, "CanChangeEquippedItem", false);
                    TrySetStaticBoolByTypeName(tn, "EquippingEnabled", false);
                }
                TrySetInstanceBoolOnSingletonLikeType("ScheduleOne.UI.HUD", "Instance", "crosshairVisible", false);
                TrySetInstanceBoolOnSingletonLikeType("ScheduleOne.UI.HUD", "instance", "crosshairVisible", false);
                TrySetInstanceBoolOnSingletonLikeType("Il2CppScheduleOne.UI.HUD", "Instance", "crosshairVisible", false);
                TrySetInstanceBoolOnSingletonLikeType("Il2CppScheduleOne.UI.HUD", "instance", "crosshairVisible", false);
                // Additional attempts by scanning loaded assemblies for inventory-like types (best-effort, throttled scope avoided here since this runs on open)
                TryDisableHotbarOnInventoryLikeTypes();
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Input suppression: {_savedInputStates.Count} state(s) captured (best-effort).");
            }
            catch (Exception ex) { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Input suppression failed (non-fatal): {ex.Message}"); }
        }

        private void TrySetInstanceBoolOnSingletonLikeType(string typeName, string staticMember, string childMember, bool setValue)
        {
            TrySetMemberByTypeName(typeName, staticMember, null, childMember, setValue);
        }

        private void TryEnsureGameInputSuppressed()
        {
            // Re-apply more frequently while open to counter focus/alt-tab/game systems re-enabling input or unhooked hotbar changes.
            if (Time.realtimeSinceStartup - _lastInputBlockAttempt < 0.1f) return;
            _lastInputBlockAttempt = Time.realtimeSinceStartup;
            // Re-apply known toggles if some game system re-enables them while app is open.
            try
            {
                foreach (var s in _savedInputStates)
                {
                    try
                    {
                        if (s.StaticType != null)
                        {
                            if (s.IsProperty)
                            {
                                var p = s.StaticType.GetProperty(s.Member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(null, false);
                            }
                            else
                            {
                                var f = s.StaticType.GetField(s.Member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (f != null && f.FieldType == typeof(bool)) f.SetValue(null, false);
                            }
                            continue;
                        }
                        if (s.Target == null) continue;
                        if (s.IsProperty)
                        {
                            var p = s.Target.GetType().GetProperty(s.Member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(s.Target, false);
                        }
                        else
                        {
                            var f = s.Target.GetType().GetField(s.Member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (f != null && f.FieldType == typeof(bool)) f.SetValue(s.Target, false);
                        }
                    }
                    catch { }
                }
                // Re-apply verified PlayerInventory suppression (in case game re-enabled flags or instance became available later).
                try { TryApplyVerifiedPlayerInventorySuppression(disable: true, captureState: false); } catch { }
                // Re-apply movement suppression (best-effort) in case component was re-enabled by game/focus transitions.
                try { TryApplyMovementSuppression(disable: true, captureState: false); } catch { }
                // Learn/pin equipped slot while app is open to counter unhooked hotbar changes.
                // If slot drifted, restore to last known slot (best-effort).
                TryRestoreEquippedSlotIndex(forceRestore: IsOpen);
            }
            catch { }
        }

        private void TryRestoreGameInput()
        {
            try
            {
                for (int i = _savedInputStates.Count - 1; i >= 0; i--)
                {
                    var s = _savedInputStates[i];
                    try
                    {
                        if (s.StaticType != null)
                        {
                            if (s.IsProperty)
                            {
                                var p = s.StaticType.GetProperty(s.Member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (p != null && p.CanWrite) p.SetValue(null, s.Prev);
                            }
                            else
                            {
                                var f = s.StaticType.GetField(s.Member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (f != null) f.SetValue(null, s.Prev);
                            }
                            continue;
                        }
                        if (s.Target == null) continue;
                        if (s.IsProperty)
                        {
                            var p = s.Target.GetType().GetProperty(s.Member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (p != null && p.CanWrite) p.SetValue(s.Target, s.Prev);
                        }
                        else
                        {
                            var f = s.Target.GetType().GetField(s.Member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (f != null) f.SetValue(s.Target, s.Prev);
                        }
                    }
                    catch { }
                }
                // If capture missed (e.g. instance unavailable at open), explicitly re-enable via verified APIs as a best-effort restore.
                try { TryApplyVerifiedPlayerInventorySuppression(disable: false, captureState: false); } catch { }
                try { TryApplyMovementSuppression(disable: false, captureState: false); } catch { }
                if (_savedInputStates.Count > 0) UnderNetBrowserConfig.Log($"[UnderNetBrowser] Input suppression restored {_savedInputStates.Count} state(s).");
            }
            catch { }
            finally { _savedInputStates.Clear(); }
        }

        /// <summary>
        /// Uses verified Schedule I IL2CPP APIs on PlayerInventory when available:
        /// EquippingEnabled/HotbarEnabled properties and SetEquippingEnabled/SetInventoryEnabled methods.
        /// This is the preferred hotbar/equipping suppression path (reflection-discovered, no invented members).
        /// </summary>
        private void TryApplyVerifiedPlayerInventorySuppression(bool disable, bool captureState)
        {
            try
            {
                Type? invType = FindTypeInLoadedAssemblies("Il2CppScheduleOne.PlayerScripts.PlayerInventory")
                             ?? FindTypeInLoadedAssemblies("ScheduleOne.PlayerScripts.PlayerInventory");
                if (invType == null)
                {
                    // fallback scan (expensive, but rare) - avoid repeated full scans by caching null result not possible here; keep best-effort
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            var fn = t.FullName ?? "";
                            if (fn == "Il2CppScheduleOne.PlayerScripts.PlayerInventory" || fn == "ScheduleOne.PlayerScripts.PlayerInventory") { invType = t; break; }
                        }
                        if (invType != null) break;
                    }
                }
                if (invType == null) return;

                object? inv = null;
                foreach (var sm in new[] { "Instance", "instance", "Singleton", "Local" })
                {
                    try
                    {
                        var sp = invType.GetProperty(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                        if (sp != null) inv = sp.GetValue(null);
                        if (inv == null)
                        {
                            var sf = invType.GetField(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                            if (sf != null) inv = sf.GetValue(null);
                        }
                        if (inv != null) break;
                    }
                    catch { }
                }
                if (invType == null) return;

                if (inv != null)
                {
                    if (disable)
                    {
                        // Capture/disable via properties (verified)
                        if (captureState) TrySetBoolMember(inv, "EquippingEnabled", false); else TrySetBoolMemberNoCapture(inv, "EquippingEnabled", false);
                        if (captureState) TrySetBoolMember(inv, "HotbarEnabled", false); else TrySetBoolMemberNoCapture(inv, "HotbarEnabled", false);
                        // Call verified methods if present (best-effort; some builds may route through them)
                        TryInvokeInstanceBoolMethod(inv, "SetEquippingEnabled", false);
                        TryInvokeInstanceBoolMethod(inv, "SetInventoryEnabled", false);
                    }
                    else
                    {
                        // Restore handled by saved state replay; still try to re-enable via methods/properties if we have no saved state match.
                        TrySetBoolMemberNoCapture(inv, "EquippingEnabled", true);
                        TrySetBoolMemberNoCapture(inv, "HotbarEnabled", true);
                        TryInvokeInstanceBoolMethod(inv, "SetEquippingEnabled", true);
                        TryInvokeInstanceBoolMethod(inv, "SetInventoryEnabled", true);
                    }
                }
            }
            catch { }
        }

        private bool _movementSuppressionDiagLogged;
        /// <summary>
        /// Best-effort movement suppression. No verified player CanMove/OnMove API was found in inspected IL2CPP dumps for this build,
        /// so this uses only safe, inspectable approaches: disable/restore Unity Behaviour.enabled on PlayerMovement/PlayerMotor-like
        /// components when discovered, and keep reflection fallbacks for S1API/game members if they exist.
        /// </summary>
        private void TryApplyMovementSuppression(bool disable, bool captureState)
        {
            try
            {
                // Try likely game movement components by verified/known type names and singleton instances.
                string[] movementTypeNames = {
                    "Il2CppScheduleOne.PlayerScripts.PlayerMovement",
                    "ScheduleOne.PlayerScripts.PlayerMovement",
                    "Il2CppScheduleOne.PlayerScripts.FirstPersonController",
                    "ScheduleOne.PlayerScripts.FirstPersonController",
                    "Il2CppScheduleOne.PlayerScripts.PlayerController",
                    "ScheduleOne.PlayerScripts.PlayerController"
                };
                foreach (var tn in movementTypeNames)
                {
                    var t = FindTypeInLoadedAssemblies(tn);
                    if (t == null) continue;
                    object? inst = TryGetSingletonLikeInstance(t, new[] { "Instance", "instance", "Local", "local", "Singleton", "Current" });
                    if (inst == null) continue;

                    // Unity Behaviour.enabled is a verified engine API (if component derives from Behaviour).
                    if (captureState) TrySetBoolMember(inst, "enabled", disable ? false : true);
                    else TrySetBoolMemberNoCapture(inst, "enabled", disable ? false : true);

                    // If a build exposes CanMove/canMove on the movement component, honor it (no invented names beyond common existing ones).
                    if (captureState) TrySetBoolMember(inst, "CanMove", disable ? false : true); else TrySetBoolMemberNoCapture(inst, "CanMove", disable ? false : true);
                    if (captureState) TrySetBoolMember(inst, "canMove", disable ? false : true); else TrySetBoolMemberNoCapture(inst, "canMove", disable ? false : true);

                    if (!_movementSuppressionDiagLogged)
                    {
                        try { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Movement suppression target: type={t.FullName} hasEnabled={inst.GetType().GetProperty("enabled") != null} hasCanMove={inst.GetType().GetProperty("CanMove") != null || inst.GetType().GetProperty("canMove") != null}"); } catch { }
                    }
                }

                // S1API/player-level fallbacks (only if members exist in this build)
                if (captureState)
                {
                    TrySetMemberByTypeName("S1API.Entities.Player", "Local", null, childMember: "CanMove", setValue: disable ? false : true);
                    TrySetMemberByTypeName("S1API.Player.Player", "Local", null, childMember: "CanMove", setValue: disable ? false : true);
                }
                else
                {
                    // no-capture reapply/restore best-effort
                    TrySetS1ApiLocalCanMoveNoCapture(disable ? false : true);
                }

                _movementSuppressionDiagLogged = true;
            }
            catch { }
        }

        private static Type? FindTypeInLoadedAssemblies(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            try
            {
                if (_typeLookupCache.TryGetValue(typeName, out var cached))
                    return cached;
            }
            catch { }

            Type? found = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { found = asm.GetType(typeName, false); if (found != null) break; } catch { }
                }
            }
            catch { found = null; }

            try { _typeLookupCache[typeName] = found; } catch { }
            return found;
        }

        private void TrySetS1ApiLocalCanMoveNoCapture(bool value)
        {
            foreach (var tn in new[] { "S1API.Entities.Player", "S1API.Player.Player" })
            {
                try
                {
                    var t = FindTypeInLoadedAssemblies(tn);
                    if (t == null) continue;
                    var local = TryGetSingletonLikeInstance(t, new[] { "Local", "local", "Instance", "instance" });
                    if (local == null) continue;
                    TrySetBoolMemberNoCapture(local, "CanMove", value);
                    TrySetBoolMemberNoCapture(local, "canMove", value);
                }
                catch { }
            }
        }

        private static object? TryGetSingletonLikeInstance(Type t, string[] memberNames)
        {
            foreach (var sm in memberNames)
            {
                try
                {
                    var sp = t.GetProperty(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                    if (sp != null) { var v = sp.GetValue(null); if (v != null) return v; }
                    var sf = t.GetField(sm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
                    if (sf != null) { var v = sf.GetValue(null); if (v != null) return v; }
                }
                catch { }
            }
            return null;
        }

        private void TrySetBoolMemberNoCapture(object target, string member, bool setValue)
        {
            try
            {
                var tt = target.GetType();
                var p = tt.GetProperty(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) { p.SetValue(target, setValue); return; }
                var f = tt.GetField(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool)) { f.SetValue(target, setValue); }
            }
            catch { }
        }

        private void TryInvokeInstanceBoolMethod(object target, string methodName, bool arg)
        {
            try
            {
                var m = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                if (m != null) m.Invoke(target, new object[] { arg });
            }
            catch { }
        }

        private void TrySetMemberByTypeName(string typeName, string staticMember, string? instanceMember, string childMember, bool setValue)
        {
            try
            {
                Type? t = FindTypeInLoadedAssemblies(typeName);
                if (t == null) return;
                object? target = null;
                var p = t.GetProperty(staticMember, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var f = t.GetField(staticMember, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null) target = p.GetValue(null); else if (f != null) target = f.GetValue(null);
                if (target == null) return;
                TrySetBoolMember(target, childMember, setValue);
            }
            catch { }
        }

        private void TrySetStaticBoolByTypeName(string typeName, string member, bool setValue)
        {
            try
            {
                Type? t = FindTypeInLoadedAssemblies(typeName);
                if (t == null) return;
                var p = t.GetProperty(member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    var prev = p.GetValue(null);
                    p.SetValue(null, setValue);
                    _savedInputStates.Add(new InputStateSnapshot { StaticType = t, Member = member, Prev = prev, IsProperty = true });
                    return;
                }
                var fi = t.GetField(member, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (fi != null && fi.FieldType == typeof(bool))
                {
                    var prev = fi.GetValue(null);
                    fi.SetValue(null, setValue);
                    _savedInputStates.Add(new InputStateSnapshot { StaticType = t, Member = member, Prev = prev, IsProperty = false });
                }
            }
            catch { }
        }

        private void TrySetBoolMember(object target, string member, bool setValue)
        {
            try
            {
                var tt = target.GetType();
                var p = tt.GetProperty(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null && p.CanRead && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    var prev = p.GetValue(target);
                    if (prev is bool b && b == setValue) return;
                    p.SetValue(target, setValue);
                    _savedInputStates.Add(new InputStateSnapshot { Target = target, Member = member, Prev = prev, IsProperty = true });
                    return;
                }
                var f = tt.GetField(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool))
                {
                    var prev = f.GetValue(target);
                    if (prev is bool b && b == setValue) return;
                    f.SetValue(target, setValue);
                    _savedInputStates.Add(new InputStateSnapshot { Target = target, Member = member, Prev = prev, IsProperty = false });
                }
            }
            catch { }
        }

        private Button CreateButton(Transform parent, string name, string label, Vector2 size, Color bg, Action onClick, int fontSize = 16)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = size;
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = size.x; le.preferredHeight = size.y; le.minWidth = size.x; le.minHeight = size.y;

            var img = go.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = true;

            // Subtle depth/stroke so flat UI primitives feel premium
            try
            {
                var outline = go.AddComponent<Outline>();
                outline.effectColor = new Color(1f, 1f, 1f, 0.07f);
                outline.effectDistance = new Vector2(1f, -1f);
            }
            catch { }
            try
            {
                var shadow = go.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.28f);
                shadow.effectDistance = new Vector2(0f, -1f);
            }
            catch { }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = bg;
            colors.highlightedColor = Color.Lerp(bg, Color.white, 0.16f);
            colors.pressedColor = Color.Lerp(bg, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(bg.r * 0.6f, bg.g * 0.6f, bg.b * 0.6f, 0.55f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.07f;
            btn.colors = colors;
            EventHelper.AddListener(onClick, btn.onClick);

            var txt = CreateText(go, label, fontSize, new Color(0.94f, 0.96f, 0.99f, 1f));
            txt.alignment = TextAnchor.MiddleCenter;
            txt.fontStyle = FontStyle.Bold;
            txt.resizeTextForBestFit = true;
            txt.resizeTextMinSize = Mathf.Max(10, fontSize - 4);
            txt.resizeTextMaxSize = fontSize;
            return btn;
        }

        private InputField CreateInput(Transform parent, string name, Vector2 size, string placeholder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.145f, 0.155f, 0.175f, 1f);
            img.raycastTarget = true;
            // NOTE: RectMask2D on the input root caused address bar text/overlay visibility regressions in this UI setup.
            // TODO: implement clipping via a dedicated masked text viewport without hiding InputField/overlay content.
            try
            {
                var outline = go.AddComponent<Outline>();
                outline.effectColor = new Color(1f, 1f, 1f, 0.07f);
                outline.effectDistance = new Vector2(1, -1);
            }
            catch { }
            try
            {
                var shadow = go.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.22f);
                shadow.effectDistance = new Vector2(0f, -1f);
            }
            catch { }

            var input = go.AddComponent<InputField>();
            input.targetGraphic = img;
            input.transition = Selectable.Transition.ColorTint;
            var colors = input.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1.02f);
            colors.pressedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            input.colors = colors;
            input.interactable = true;
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = InputField.ContentType.Standard;
            input.keyboardType = TouchScreenKeyboardType.Default;
            input.characterLimit = 2048;
            input.caretColor = new Color(0.58f, 0.78f, 1f, 1f);
            input.customCaretColor = true;
            input.selectionColor = new Color(0.28f, 0.52f, 0.95f, 0.40f);
            input.caretBlinkRate = 0.85f;
            input.caretWidth = 2;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(34, 2); trt.offsetMax = new Vector2(-14, -2);
            var txt = textGo.AddComponent<Text>();
            txt.font = GetUIFont(); txt.fontSize = 14; txt.color = new Color(0.97f, 0.98f, 1f, 1f); txt.alignment = TextAnchor.MiddleLeft; txt.supportRichText = false; txt.raycastTarget = false; txt.horizontalOverflow = HorizontalWrapMode.Overflow; txt.verticalOverflow = VerticalWrapMode.Truncate;
            input.textComponent = txt;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var prt = phGo.AddComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.offsetMin = new Vector2(34, 2); prt.offsetMax = new Vector2(-14, -2);
            var ptxt = phGo.AddComponent<Text>();
            ptxt.font = GetUIFont(); ptxt.fontSize = 13; ptxt.fontStyle = FontStyle.Italic; ptxt.color = new Color(0.60f, 0.66f, 0.74f, 1f); ptxt.alignment = TextAnchor.MiddleLeft; ptxt.supportRichText = false; ptxt.raycastTarget = false; ptxt.text = placeholder ?? "Search or enter address";
            input.placeholder = ptxt;

            // Leading search/site glyph for a browser-like omnibox feel
            try
            {
                var leadGo = new GameObject("LeadingIcon");
                leadGo.transform.SetParent(go.transform, false);
                var lrt = leadGo.AddComponent<RectTransform>();
                lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0f, 1f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = new Vector2(8f, 0f);
                lrt.sizeDelta = new Vector2(22f, 0f);
                var lt = leadGo.AddComponent<Text>();
                lt.font = GetUIFont();
                lt.text = "⌕";
                lt.fontSize = 15;
                lt.color = new Color(0.62f, 0.74f, 0.95f, 0.95f);
                lt.alignment = TextAnchor.MiddleCenter;
                lt.raycastTarget = false;
                lt.supportRichText = false;
            }
            catch { }

            input.text = "";
            try { input.ForceLabelUpdate(); } catch { }
            return input;
        }

        private Text CreateText(GameObject parent, string text, int fontSize, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.AddComponent<Text>();
            t.font = GetUIFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            t.supportRichText = false;
            return t;
        }

        private void TryHideGameHud()
        {
            try
            {
                TryRestoreGameHud();

                bool useCache = false;
                try
                {
                    // Reuse previously discovered HUD candidates to avoid expensive Resources.FindObjectsOfTypeAll<Canvas>() on reattach/reopen.
                    // Rescan periodically or if cache is empty/stale in case HUD objects are recreated.
                    if (_cachedHudHideCandidates.Count > 0 && (Time.realtimeSinceStartup - _lastHudCandidateScanRealtime) < 300f)
                    {
                        int valid = 0;
                        for (int i = 0; i < _cachedHudHideCandidates.Count; i++)
                        {
                            var go = _cachedHudHideCandidates[i];
                            if (go == null) continue;
                            try { if (!go.scene.IsValid()) continue; } catch { }
                            valid++;
                        }
                        useCache = valid > 0;
                    }
                }
                catch { useCache = false; }

                if (useCache)
                {
                    var seen = new HashSet<int>();
                    for (int i = 0; i < _cachedHudHideCandidates.Count; i++)
                    {
                        var go = _cachedHudHideCandidates[i];
                        if (go == null) continue;
                        try { if (!go.scene.IsValid()) continue; } catch { continue; }
                        int id = 0;
                        try { id = go.GetInstanceID(); } catch { id = 0; }
                        if (id != 0 && !seen.Add(id)) continue;
                        bool wasActive = false;
                        try { wasActive = go.activeSelf; } catch { wasActive = false; }
                        _hiddenHudObjects.Add((go, wasActive));
                        try { if (wasActive) go.SetActive(false); } catch { }
                    }
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] HUD hide: {_hiddenHudObjects.Count} object(s) hidden via cache.");
                    return;
                }

                _cachedHudHideCandidates.Clear();
                var seenScan = new HashSet<int>();
                var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
                foreach (var c in canvases)
                {
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    try
                    {
                        var tr = c.transform;
                        for (int i = 0; i < tr.childCount; i++)
                        {
                            var child = tr.GetChild(i);
                            if (child == null) continue;
                            var cn = child.name ?? string.Empty;
                            if (cn.IndexOf("UnderNet", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            bool match = cn.IndexOf("Hotbar", StringComparison.OrdinalIgnoreCase) >= 0
                                || cn.Equals("InventoryBar", StringComparison.OrdinalIgnoreCase)
                                || cn.Equals("ItemSlots", StringComparison.OrdinalIgnoreCase)
                                || cn.IndexOf("Crosshair", StringComparison.OrdinalIgnoreCase) >= 0
                                || cn.IndexOf("Reticle", StringComparison.OrdinalIgnoreCase) >= 0
                                || cn.IndexOf("CursorDot", StringComparison.OrdinalIgnoreCase) >= 0
                                || cn.Equals("Dot", StringComparison.OrdinalIgnoreCase)
                                || cn.Equals("CenterDot", StringComparison.OrdinalIgnoreCase);
                            if (!match) continue;
                            var go = child.gameObject;
                            int id = go.GetInstanceID();
                            if (!seenScan.Add(id)) continue;
                            _cachedHudHideCandidates.Add(go);
                            _hiddenHudObjects.Add((go, go.activeSelf));
                            if (go.activeSelf) go.SetActive(false);
                            UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Hid HUD object candidate: {cn} (canvas={c.gameObject.name})");
                        }
                    }
                    catch { }
                }
                _lastHudCandidateScanRealtime = Time.realtimeSinceStartup;
                // Skip expensive full-scene GameObject scan on normal paths (canvas-child scan above is sufficient and cheaper).
                // Full scan caused noticeable hitches on reattach/open in some scenes. Keep only if explicitly needed in future.
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] HUD hide: {_hiddenHudObjects.Count} object(s) tracked (scan).");
            }
            catch (Exception ex) { UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] HUD hide failed (non-fatal): {ex.Message}"); }
        }

        private void TryRestoreGameHud()
        {
            try
            {
                for (int i = _hiddenHudObjects.Count - 1; i >= 0; i--)
                {
                    var item = _hiddenHudObjects[i];
                    try { if (item.go != null) item.go.SetActive(item.wasActive); } catch { }
                }
            }
            catch { }
            finally
            {
                if (_hiddenHudObjects.Count > 0) UnderNetBrowserConfig.Log($"[UnderNetBrowser] HUD restore: restored {_hiddenHudObjects.Count} object(s).");
                _hiddenHudObjects.Clear();
            }
        }

        private void DumpUIDiagnostics(GameObject from, string label)
        {
            try
            {
                if (from == null) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: from=null"); return; }
                var go = from;
                var appRoot = go.transform.parent != null ? go.transform.parent.gameObject : go;
                var canvas = appRoot.GetComponent<Canvas>() ?? go.GetComponentInParent<Canvas>();
                var cg = appRoot.GetComponent<CanvasGroup>() ?? go.GetComponentInParent<CanvasGroup>();
                var rootRt = appRoot.GetComponent<RectTransform>();
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: from='{go.name}' active={go.activeInHierarchy} path={GetTransformPath(go.transform)}");
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: appRoot='{appRoot.name}' active={appRoot.activeInHierarchy} pos={appRoot.transform.position} lscale={appRoot.transform.localScale} lossy={appRoot.transform.lossyScale}");
                if (rootRt != null) UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: rootRT size={rootRt.rect.size} sd={rootRt.sizeDelta}");
                if (canvas != null) UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: canvas renderMode={canvas.renderMode} sortingOrder={canvas.sortingOrder} worldCamera={(canvas.worldCamera != null ? canvas.worldCamera.name : "null")}");
                if (cg != null) UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label}: canvasGroup alpha={cg.alpha} interact={cg.interactable} blocks={cg.blocksRaycasts}");
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] UI diag {label} failed: {ex.Message}"); }
        }

        private static string TruncateForLog(string? value, int maxLen = 240)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (maxLen < 16) maxLen = 16;
            if (value.Length <= maxLen) return value;
            return value.Substring(0, maxLen - 3) + "...";
        }

        private static string TruncateUrlForLog(string? url, int maxLen = 240)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            try
            {
                int frag = url.IndexOf('#');
                if (frag >= 0 && frag < url.Length - 1)
                    url = url.Substring(0, frag) + "#…";
            }
            catch { }
            return TruncateForLog(url, maxLen);
        }

        private string GetTransformPath(Transform t)
        {
            try
            {
                var parts = new List<string>();
                var cur = t;
                int depth = 0;
                while (cur != null && depth++ < 12) { parts.Add(cur.name); cur = cur.parent; }
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return t != null ? t.name : "(null)"; }
        }

        private void RepairS1APICanvasState(GameObject from, string label)
        {
            try
            {
                float nowRt = Time.realtimeSinceStartup;
                // Healthy-state fast path: throttle checks to reduce per-update overhead.
                _repairNextAllowedRealtime = nowRt + (_repairBackoffActive ? 5f : 1f);

                if (from == null) return;
                var go = from;
                var appRoot = go.transform.parent != null ? go.transform.parent.gameObject : go;
                var canvas = appRoot.GetComponent<Canvas>() ?? go.GetComponentInParent<Canvas>();
                var rootRt = appRoot.GetComponent<RectTransform>();

                float scaleMag = appRoot.transform.lossyScale.sqrMagnitude;
                bool badScale = scaleMag < 0.0001f;
                bool badSize = rootRt != null && rootRt.rect.width < 1f && rootRt.rect.height < 1f;
                bool badCam = canvas != null && canvas.renderMode == RenderMode.WorldSpace && canvas.worldCamera == null;

                // S1API TVApp spawns UI with correct TV sizing; avoid donor-based overrides that can break fit.
                // Only fix critical cases (missing world camera, or truly degenerate scale/size).
                if (badCam && canvas != null && Camera.main != null)
                {
                    try { canvas.worldCamera = Camera.main; } catch { }
                    if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: set worldCamera=Camera.main");
                }

                if (!badScale && !badSize)
                {
                    if (_repairStateLoggedBad || _repairBackoffActive)
                    {
                        if (UnderNetBrowserConfig.DebugLogging?.Value == true)
                            UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: state recovered.");
                    }
                    _repairStateLoggedBad = false;
                    _repairBackoffActive = false;
                    _lastRepairStateSignature = string.Empty;
                    _repairNextAllowedRealtime = nowRt + 1f;
                    return;
                }

                // Bad state: apply conservative repairs, then back off if upstream/parent transform remains irrecoverably degenerate.
                bool verbose = UnderNetBrowserConfig.DebugLogging?.Value == true;
                string path = "";
                Vector3 pLossy = Vector3.one;
                try { path = GetTransformPath(appRoot.transform); } catch { path = appRoot.name; }
                try { var p = appRoot.transform.parent; pLossy = p != null ? p.lossyScale : Vector3.one; } catch { }

                bool parentZeroish = Math.Abs(pLossy.x) < 0.0001f || Math.Abs(pLossy.y) < 0.0001f || Math.Abs(pLossy.z) < 0.0001f;
                string sig = $"{(badScale ? 1 : 0)}:{(badSize ? 1 : 0)}:{path}:{(int)(pLossy.x * 1000f)},{(int)(pLossy.y * 1000f)},{(int)(pLossy.z * 1000f)}";
                bool sigChanged = !string.Equals(sig, _lastRepairStateSignature, StringComparison.Ordinal);
                _lastRepairStateSignature = sig;

                // Throttle repetitive debug logging for persistent known-degenerate states (especially when upstream parent scale is zero),
                // while still surfacing state transitions and periodic status.
                float logMinInterval = 30f;
                if (verbose && !(badScale && parentZeroish && _repairBackoffActive && !sigChanged)) logMinInterval = 5f;
                if (!_repairStateLoggedBad || sigChanged || (nowRt - _lastRepairStateLogRealtime >= logMinInterval))
                {
                    _lastRepairStateLogRealtime = nowRt;
                    _repairStateLoggedBad = true;
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: badScale={badScale}(mag={scaleMag:F6}) badSize={badSize} rootRT={(rootRt != null ? rootRt.rect.size.ToString() : "null")} rootLocal={appRoot.transform.localScale} rootLossy={appRoot.transform.lossyScale} parentLossy={pLossy} path='{path}'");
                }

                if (badSize && rootRt != null)
                {
                    try
                    {
                        if (rootRt.rect.width < 1f || rootRt.rect.height < 1f)
                            rootRt.sizeDelta = new Vector2(685f, 400f);
                    }
                    catch { }
                }

                int fixedAncestors = 0;
                bool stillDegenerate = false;

                if (badScale)
                {
                    try { appRoot.transform.localScale = Vector3.one; } catch { }
                    try
                    {
                        var p = appRoot.transform.parent;
                        int depth = 0;
                        while (p != null && depth++ < 4 && fixedAncestors < 2)
                        {
                            Vector3 pLossyNow = Vector3.one;
                            try { pLossyNow = p.lossyScale; } catch { pLossyNow = Vector3.one; }
                            bool nodeZeroish = Math.Abs(pLossyNow.x) < 0.0001f || Math.Abs(pLossyNow.y) < 0.0001f || Math.Abs(pLossyNow.z) < 0.0001f;
                            if (!nodeZeroish) break;

                            string pn = p.name ?? "";
                            bool allowAncestorFix = pn.IndexOf("UnderNetBrowser", StringComparison.OrdinalIgnoreCase) >= 0
                                || string.Equals(pn, "Container", StringComparison.OrdinalIgnoreCase)
                                || fixedAncestors == 0;

                            if (!allowAncestorFix) break;
                            try
                            {
                                var ls = p.localScale;
                                bool localZeroish = Math.Abs(ls.x) < 0.0001f || Math.Abs(ls.y) < 0.0001f || Math.Abs(ls.z) < 0.0001f;
                                if (localZeroish || nodeZeroish)
                                {
                                    p.localScale = Vector3.one;
                                    fixedAncestors++;
                                }
                            }
                            catch { break; }
                            p = p.parent;
                        }
                    }
                    catch { }

                    try
                    {
                        var lossy = appRoot.transform.lossyScale;
                        var parentNow = appRoot.transform.parent;
                        var pLossyNow = parentNow != null ? parentNow.lossyScale : Vector3.one;
                        bool rootZeroish = Math.Abs(lossy.x) < 0.0001f || Math.Abs(lossy.y) < 0.0001f || Math.Abs(lossy.z) < 0.0001f;
                        bool parentStillZeroish = Math.Abs(pLossyNow.x) < 0.0001f || Math.Abs(pLossyNow.y) < 0.0001f || Math.Abs(pLossyNow.z) < 0.0001f;
                        stillDegenerate = rootZeroish;
                        // If upstream parent remains degenerate, local fixes cannot restore a valid lossy scale; back off aggressively.
                        if (stillDegenerate && (parentStillZeroish || parentZeroish))
                        {
                            _repairBackoffActive = true;
                            _repairNextAllowedRealtime = nowRt + 5f;
                            if (sigChanged || (nowRt - _lastRepairStateLogRealtime >= 30f))
                            {
                                _lastRepairStateLogRealtime = nowRt;
                                _repairStateLoggedBad = true;
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: badScale persists (rootLossy={lossy} parentLossy={pLossyNow} fixedAncestors={fixedAncestors}); upstream transform remains degenerate, backing off checks.");
                            }
                            return;
                        }
                    }
                    catch { stillDegenerate = badScale; }

                    if (stillDegenerate)
                    {
                        _repairBackoffActive = true;
                        _repairNextAllowedRealtime = nowRt + 5f;
                    }
                    else
                    {
                        _repairBackoffActive = false;
                        _repairNextAllowedRealtime = nowRt + 1f;
                    }
                }
                else
                {
                    _repairBackoffActive = false;
                    _repairNextAllowedRealtime = nowRt + 1f;
                }
            }
            catch (Exception ex) { try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label} failed: {ex.Message}"); } catch { } }
        }

        private static Font GetUIFont()
        {
            try
            {
                if (_cachedFont != null) return _cachedFont;
                string[] names = { "Arial.ttf", "LegacyRuntime.ttf", "Arial", "Liberation Sans" };
                foreach (var n in names)
                {
                    try { var f = Resources.GetBuiltinResource<Font>(n); if (f != null) { _cachedFont = f; return f; } } catch { }
                }
                try { var f = Font.CreateDynamicFontFromOSFont("Arial", 16); if (f != null) { _cachedFont = f; return f; } } catch { }
                try { var f = Font.CreateDynamicFontFromOSFont("Segoe UI", 16); if (f != null) { _cachedFont = f; return f; } } catch { }
                try { var f = Resources.GetBuiltinResource<Font>("Arial.ttf"); if (f != null) { _cachedFont = f; return f; } } catch { }
            }
            catch { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { return null!; }
        }

        private Sprite? LoadOrCreateIcon()
        {
            try
            {
                // Prefer embedded icon (no loose file required)
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string? resName = null;
                    try
                    {
                        var names = asm.GetManifestResourceNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            if (names[i] != null && names[i].EndsWith("UnderNetIcon.png", StringComparison.OrdinalIgnoreCase))
                            { resName = names[i]; break; }
                        }
                    }
                    catch { resName = null; }
                    if (!string.IsNullOrWhiteSpace(resName))
                    {
                        using (var s = asm.GetManifestResourceStream(resName))
                        {
                            if (s != null)
                            {
                                using (var ms = new System.IO.MemoryStream())
                                {
                                    s.CopyTo(ms);
                                    var bytes = ms.ToArray();
                                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                                    if (ImageConversion.LoadImage(tex, bytes, false))
                                    {
                                        tex.filterMode = FilterMode.Bilinear;
                                        tex.wrapMode = TextureWrapMode.Clamp;
                                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                                    }
                                    UnityEngine.Object.Destroy(tex);
                                }
                            }
                        }
                    }
                }
                catch { }

                // Backward-compatible fallback: loose file next to mod / in Mods
                var asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var asmDir = string.IsNullOrEmpty(asmPath) ? "" : System.IO.Path.GetDirectoryName(asmPath);
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string[] candidates = new[]
                {
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "UnderNetBrowser", "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "FireskyUnderNetBrowser", "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "..", "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "..", "..", "UnderNetIcon.png"),
                    System.IO.Path.Combine(baseDir, "Mods", "UnderNetIcon.png"),
                    System.IO.Path.Combine(baseDir, "Mods", "UnderNetBrowser", "UnderNetIcon.png"),
                    System.IO.Path.Combine(baseDir, "Mods", "FireskyUnderNetBrowser", "UnderNetIcon.png"),
                    System.IO.Path.Combine(baseDir, "UnderNetIcon.png")
                };
                foreach (var p in candidates)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p)) continue;
                        var bytes = System.IO.File.ReadAllBytes(p);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, bytes, false)) { UnityEngine.Object.Destroy(tex); continue; }
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    }
                    catch { }
                }
            }
            catch { }
            return CreateDefaultIcon();
        }

        private Sprite? CreateDefaultIcon()
        {
            try
            {
                const int size = 64;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color32[size * size];
                var bg = new Color32(16, 20, 28, 255);
                var border = new Color32(58, 78, 96, 255);
                var accent = new Color32(80, 220, 140, 255);
                var accentSoft = new Color32(60, 170, 120, 255);
                var textCol = new Color32(235, 245, 255, 255);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int dx = x - 31;
                    int dy = y - 31;
                    int ax = dx >= 0 ? dx : -dx;
                    int ay = dy >= 0 ? dy : -dy;
                    bool isBorder = x <= 3 || x >= 60 || y <= 3 || y >= 60;
                    bool inner = x >= 6 && x <= 57 && y >= 6 && y <= 57;
                    var c = isBorder ? border : bg;

                    // Subtle monitor/browser frame
                    if (inner)
                    {
                        bool frame = x <= 8 || x >= 55 || y <= 10 || y >= 53;
                        bool topBar = y >= 48 && y <= 53 && x >= 9 && x <= 54;
                        if (frame) c = new Color32(34, 44, 58, 255);
                        if (topBar) c = new Color32(28, 36, 48, 255);
                        // address dots / bar accent in top bar
                        if (topBar && y >= 50 && y <= 51 && x >= 12 && x <= 16) c = accentSoft;
                        if (topBar && y >= 50 && y <= 51 && x >= 18 && x <= 22) c = new Color32(90, 120, 150, 255);
                        if (topBar && y >= 50 && y <= 51 && x >= 24 && x <= 44) c = new Color32(44, 56, 72, 255);
                    }

                    // Stylized "U" (UnderNet) in center
                    bool inU = (x >= 22 && x <= 41 && y >= 18 && y <= 42);
                    if (inU)
                    {
                        bool stroke = (x >= 22 && x <= 25) || (x >= 38 && x <= 41) || (y >= 18 && y <= 21);
                        bool curveCut = (y <= 20 && x >= 26 && x <= 37); // soften bottom corners slightly
                        bool innerCut = (x >= 26 && x <= 37 && y >= 22 && y <= 42);
                        if (stroke && !curveCut) c = textCol;
                        else if (innerCut) c = (inner ? new Color32(34, 44, 58, 255) : bg);
                    }

                    // Green status/accent dot (network/live feel)
                    int dotDx = x - 46;
                    int dotDy = y - 39;
                    if ((dotDx * dotDx + dotDy * dotDy) <= 9) c = accent;
                    else if ((dotDx * dotDx + dotDy * dotDy) <= 14 && c.r == bg.r && c.g == bg.g && c.b == bg.b) c = accentSoft;

                    pixels[y * size + x] = c;
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
                try { sprite.name = "FireskyUnderNetBrowser_Icon"; } catch { }
                return sprite;
            }
            catch { return null; }
        }

        private void OnDestroy()
        {
            try { ShutdownBrowserRuntime("destroy"); } catch { }
            try { TryRestoreGameHud(); } catch { }
            try { _sync.Shutdown(); } catch { }
        }

        // --- Stage 3/4 PoC: spatial TV audio playback (WASAPI process-loopback capture -> Unity 3D AudioSource) ---
        private void GetDesiredCompositionRenderSize(out int width, out int height)
        {
            // Prefer player's native display resolution as a quality cap, fitted to the TV/video viewport aspect ratio.
            int nativeW = 1920;
            int nativeH = 1080;
            try
            {
                int rw = 0, rh = 0;
                try { rw = Screen.currentResolution.width; rh = Screen.currentResolution.height; } catch { rw = 0; rh = 0; }
                if (rw <= 0 || rh <= 0) { try { rw = Screen.width; rh = Screen.height; } catch { rw = 0; rh = 0; } }
                if (rw > 0) nativeW = rw;
                if (rh > 0) nativeH = rh;
            }
            catch { nativeW = 1920; nativeH = 1080; }

            // Respect configured values as an upper bound/cap when explicitly set within valid range.
            int capW = nativeW;
            int capH = nativeH;
            try
            {
                int cfgW = UnderNetBrowserConfig.GpuCompositionRenderWidth?.Value ?? 1920;
                int cfgH = UnderNetBrowserConfig.GpuCompositionRenderHeight?.Value ?? 1080;
                if (cfgW > 0) capW = Mathf.Min(capW, Mathf.Clamp(cfgW, 854, 3840));
                if (cfgH > 0) capH = Mathf.Min(capH, Mathf.Clamp(cfgH, 480, 2160));
            }
            catch { }

            float targetAspect = 16f / 9f;
            try
            {
                var vrt = _videoDisplay != null ? _videoDisplay.rectTransform : null;
                if (vrt != null)
                {
                    var r = vrt.rect;
                    if (r.height > 1f && r.width > 1f)
                        targetAspect = Mathf.Clamp(r.width / r.height, 0.5f, 4f);
                }
            }
            catch { targetAspect = 16f / 9f; }

            int w, h;
            if ((float)capW / (float)capH > targetAspect)
            {
                w = capW;
                h = Mathf.RoundToInt(w / targetAspect);
            }
            else
            {
                h = capH;
                w = Mathf.RoundToInt(h * targetAspect);
            }

            // Align to even dimensions (encoder/capture friendliness) and clamp.
            if ((w & 1) == 1) w--;
            if ((h & 1) == 1) h--;
            w = Mathf.Clamp(w, 854, 3840);
            h = Mathf.Clamp(h, 480, 2160);

            width = w;
            height = h;
        }

        private void TryUpdateAdaptiveRendering(bool force = false)
        {
            try
            {
                // Safety: do not race with teardown or apply when no active composition session.
                if (_gpuCompositionCapture == null || !_gpuCompositionMode)
                {
                    _adaptiveCurrentTier = AdaptiveVisualTier.Full;
                    _adaptivePendingTier = AdaptiveVisualTier.Full;
                    _adaptivePendingTierSince = -999f;
                    _adaptiveLastReason = "inactive";
                    _adaptiveAppliedCaptureIntervalMs = 0;
                    _adaptiveAppliedStandby = false;
                    return;
                }

                bool master = UnderNetBrowserConfig.AdaptiveRenderingEnabled?.Value == true;
                if (!master)
                {
                    ApplyAdaptiveTier(AdaptiveVisualTier.Full, "disabled", forceApply: force);
                    _adaptivePendingTier = AdaptiveVisualTier.Full;
                    _adaptivePendingTierSince = -999f;
                    return;
                }

                // If rendering is explicitly suspended for lifecycle reasons (warm-close background, etc.), do not fight it.
                try { if (_gpuCompositionCapture.RenderingSuspended) { _adaptiveLastReason = "suspended"; return; } } catch { }

                float now = 0f;
                try { now = Time.realtimeSinceStartup; } catch { now = 0f; }
                if (!force && now > 0f && (now - _adaptiveLastEvalTime) < 0.25f)
                    return;
                _adaptiveLastEvalTime = now;

                // Resolve listener (camera/player view) and TV position (reuse distance-volume TV transform cache when possible)
                Camera? cam = null;
                try { cam = Camera.main; } catch { cam = null; }
                bool hasListener = cam != null;
                bool hasTv = false;
                Vector3 listenerPos = default;
                Vector3 tvPos = default;
                Transform? tvTransform = null;
                if (hasListener)
                {
                    try { listenerPos = cam!.transform.position; } catch { hasListener = false; }
                }

                try
                {
                    // Reuse cached TV transform from distance-volume logic if available; refresh opportunistically.
                    tvTransform = _distanceVolumeTvTransform;
                    bool needResolve = tvTransform == null;
                    if (!needResolve)
                    {
                        try { if (!tvTransform!.gameObject.activeInHierarchy) needResolve = true; } catch { needResolve = true; }
                    }
                    if (needResolve || (now - _lastDistanceVolumeTvResolveTime) > 2.0f)
                    {
                        // Minimal inline resolve compatible with TryUpdateDistanceBasedVolume.
                        try
                        {
                            _lastDistanceVolumeTvResolveTime = now;
                            Transform? t = null;
                            GameObject? go = null;
                            try { go = GameObject.Find("TVInterface"); } catch { go = null; }
                            if (go == null) { try { go = GameObject.Find("TV_Built(Clone)/TVInterface"); } catch { go = null; } }
                            if (go == null) { try { go = GameObject.Find("TV_Built/TVInterface"); } catch { go = null; } }
                            if (go == null) { try { go = GameObject.Find("TV_Built(Clone)"); } catch { go = null; } }
                            if (go == null) { try { go = GameObject.Find("TV_Built"); } catch { go = null; } }
                            if (go != null) t = go.transform;
                            if (t != null) { _distanceVolumeTvTransform = t; tvTransform = t; }
                        }
                        catch { }
                    }
                    if (tvTransform != null)
                    {
                        try { tvPos = tvTransform.position; hasTv = true; } catch { hasTv = false; }
                    }
                }
                catch { hasTv = false; }

                float dist = -1f;
                if (hasListener && hasTv)
                {
                    try { dist = Vector3.Distance(listenerPos, tvPos); } catch { dist = -1f; }
                }
                _adaptiveLastDistance = dist;

                // Cheap visibility check (frustum/approx). Only used conservatively to avoid upgrading and to allow downgrade when clearly not visible.
                bool tvVisible = true;
                try
                {
                    if (cam != null && tvTransform != null)
                    {
                        // Prefer renderer bounds test if available, else viewport-point test.
                        Renderer? rend = null;
                        try { rend = tvTransform.GetComponentInChildren<Renderer>(); } catch { rend = null; }
                        if (rend != null)
                        {
                            try
                            {
                                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                                tvVisible = GeometryUtility.TestPlanesAABB(planes, rend.bounds);
                            }
                            catch { tvVisible = true; }
                        }
                        else
                        {
                            try
                            {
                                var vp = cam.WorldToViewportPoint(tvPos);
                                tvVisible = vp.z > 0f && vp.x >= -0.1f && vp.x <= 1.1f && vp.y >= -0.1f && vp.y <= 1.1f;
                            }
                            catch { tvVisible = true; }
                        }
                    }
                }
                catch { tvVisible = true; }
                _adaptiveLastTvVisible = tvVisible;

                // Compute desired tier from distance (independent of audio). If distance unknown, stay Full.
                AdaptiveVisualTier desired = AdaptiveVisualTier.Full;
                string reason = "full";
                bool distanceScaling = UnderNetBrowserConfig.AdaptiveDistanceQualityScaling?.Value == true;
                bool standbyEnabled = UnderNetBrowserConfig.AdaptiveVisualStandbyEnabled?.Value == true;

                float dFull = Mathf.Max(0f, UnderNetBrowserConfig.AdaptiveFullDistance?.Value ?? 7f);
                float dReduced = Mathf.Max(dFull + 0.1f, UnderNetBrowserConfig.AdaptiveReducedDistance?.Value ?? 14f);
                float dLow = Mathf.Max(dReduced + 0.1f, UnderNetBrowserConfig.AdaptiveLowDistance?.Value ?? 22f);
                float dStandby = Mathf.Max(dLow + 0.1f, UnderNetBrowserConfig.AdaptiveStandbyDistance?.Value ?? 30f);
                float hyst = Mathf.Clamp(UnderNetBrowserConfig.AdaptiveHysteresisDistance?.Value ?? 1f, 0f, 10f);
                float dwell = Mathf.Clamp(UnderNetBrowserConfig.AdaptiveTransitionDelaySeconds?.Value ?? 0.5f, 0f, 10f);

                if (distanceScaling && dist >= 0f)
                {
                    // Base by distance with simple hysteresis-aware thresholds relative to current tier.
                    // Upgrades restore quickly; downgrades respect hysteresis margins.
                    AdaptiveVisualTier byDist = AdaptiveVisualTier.Full;
                    if (dist >= dStandby) byDist = AdaptiveVisualTier.VisualStandby;
                    else if (dist >= dLow) byDist = AdaptiveVisualTier.Low;
                    else if (dist >= dReduced) byDist = AdaptiveVisualTier.Reduced;
                    else byDist = AdaptiveVisualTier.Full;

                    // Apply hysteresis around boundaries to avoid oscillation.
                    var cur = _adaptiveCurrentTier;
                    if (byDist > cur)
                    {
                        desired = byDist;
                        reason = byDist == AdaptiveVisualTier.VisualStandby ? "distance-standby" : (byDist == AdaptiveVisualTier.Low ? "distance-low" : "distance-reduced");
                    }
                    else if (byDist < cur)
                    {
                        // Upgrade: allow when comfortably inside better tier (distance minus hysteresis below boundary) for fast restore.
                        desired = byDist;
                        if (cur == AdaptiveVisualTier.VisualStandby && dist > dStandby - hyst) desired = cur;
                        else if (cur == AdaptiveVisualTier.Low && dist > dLow - hyst) desired = cur;
                        else if (cur == AdaptiveVisualTier.Reduced && dist > dReduced - hyst) desired = cur;
                        reason = "distance-restore";
                    }
                    else
                    {
                        desired = cur;
                        reason = "steady";
                    }
                }
                else
                {
                    desired = AdaptiveVisualTier.Full;
                    reason = (!distanceScaling || dist < 0f) ? "distance-unavailable/full" : "full";
                }

                // Cheap visibility can conservatively bias toward lower visual work (never force higher quality).
                if (!tvVisible && desired < AdaptiveVisualTier.Reduced)
                {
                    desired = AdaptiveVisualTier.Reduced;
                    reason = "not-visible";
                }
                else if (!tvVisible && desired == AdaptiveVisualTier.Reduced)
                {
                    reason = _adaptiveCurrentTier == desired ? "not-visible-steady" : "not-visible";
                }

                // Alt-tab / unfocused game window: only consider when not actively in TV/browser session.
                // Avoid freezing the in-game TV due to unreliable focus reporting while the app is open/interactive.
                bool appFocused = true;
                try { appFocused = Application.isFocused; } catch { appFocused = true; }
                bool allowUnfocusedStandby = !IsOpen || _detachedToGameplay;
                if (!appFocused && allowUnfocusedStandby)
                {
                    if (_adaptiveUnfocusedSinceRealtime < 0f) _adaptiveUnfocusedSinceRealtime = now;
                    if (now - _adaptiveUnfocusedSinceRealtime >= 1.5f)
                    {
                        var unfocusedTarget = standbyEnabled ? AdaptiveVisualTier.VisualStandby : AdaptiveVisualTier.Low;
                        if (desired < unfocusedTarget)
                        {
                            desired = unfocusedTarget;
                            reason = "app-unfocused";
                        }
                        else if (desired == unfocusedTarget && _adaptiveCurrentTier == desired)
                        {
                            reason = "app-unfocused-steady";
                        }
                    }
                }
                else
                {
                    _adaptiveUnfocusedSinceRealtime = -1f;
                }

                if (desired == AdaptiveVisualTier.VisualStandby && !standbyEnabled)
                {
                    desired = AdaptiveVisualTier.Low;
                    reason = "standby-disabled";
                }

                // Hysteresis/dwell: downgrades wait; upgrades apply quickly.
                if (desired > _adaptiveCurrentTier)
                {
                    if (_adaptivePendingTier != desired) { _adaptivePendingTier = desired; _adaptivePendingTierSince = now; }
                    if (now - _adaptivePendingTierSince >= dwell || force)
                    {
                        ApplyAdaptiveTier(desired, reason, forceApply: force);
                    }
                }
                else if (desired < _adaptiveCurrentTier)
                {
                    _adaptivePendingTier = desired;
                    _adaptivePendingTierSince = now;
                    ApplyAdaptiveTier(desired, reason, forceApply: true); // fast restore
                }
                else
                {
                    _adaptivePendingTier = desired;
                    _adaptivePendingTierSince = now;
                    // Keep applied state in sync if config changed.
                    ApplyAdaptiveTier(desired, reason, forceApply: false);
                }
            }
            catch { }
        }

        private void ApplyAdaptiveTier(AdaptiveVisualTier tier, string reason, bool forceApply)
        {
            try
            {
                int intervalMs = 0;
                bool standby = false;
                switch (tier)
                {
                    case AdaptiveVisualTier.Full:
                        intervalMs = 0; standby = false; break;
                    case AdaptiveVisualTier.Reduced:
                        {
                            int fps = Mathf.Clamp(UnderNetBrowserConfig.AdaptiveReducedCaptureFps?.Value ?? 15, 1, 60);
                            intervalMs = Mathf.Max(1, Mathf.RoundToInt(1000f / fps));
                            standby = false;
                            break;
                        }
                    case AdaptiveVisualTier.Low:
                        {
                            int fps = Mathf.Clamp(UnderNetBrowserConfig.AdaptiveLowCaptureFps?.Value ?? 8, 1, 60);
                            intervalMs = Mathf.Max(1, Mathf.RoundToInt(1000f / fps));
                            standby = false;
                            break;
                        }
                    case AdaptiveVisualTier.VisualStandby:
                        intervalMs = 0; standby = true; break;
                }

                bool changed = forceApply || tier != _adaptiveCurrentTier || intervalMs != _adaptiveAppliedCaptureIntervalMs || standby != _adaptiveAppliedStandby;
                _adaptiveCurrentTier = tier;
                _adaptiveLastReason = reason ?? "";

                if (changed && _gpuCompositionCapture != null)
                {
                    try { UnderNetBrowserGpuNative.CompSetAdaptiveVisualState(intervalMs, standby ? 1 : 0); } catch { }
                    _adaptiveAppliedCaptureIntervalMs = intervalMs;
                    _adaptiveAppliedStandby = standby;
                    try
                    {
                        string tname = tier.ToString();
                        float arFps = intervalMs > 0 ? (1000f / intervalMs) : 0f;
                        UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser][Adaptive] tier={tname} reason='{_adaptiveLastReason}' intervalMs={intervalMs} fps={(arFps>0?arFps.ToString("0.#"):"full")} standby={standby} dist={_adaptiveLastDistance:0.##} tvVisible={_adaptiveLastTvVisible}");
                    }
                    catch { }
                }

                // Resolution scaling intentionally conservative in v1: disabled by default and not applied dynamically to avoid session churn/risk.
            }
            catch { }
        }

        private void ResetAdaptiveRenderingState(string reason)
        {
            try
            {
                _adaptiveCurrentTier = AdaptiveVisualTier.Full;
                _adaptivePendingTier = AdaptiveVisualTier.Full;
                _adaptivePendingTierSince = -999f;
                _adaptiveLastEvalTime = -999f;
                _adaptiveAppliedCaptureIntervalMs = 0;
                _adaptiveAppliedStandby = false;
                _adaptiveLastReason = reason ?? "reset";
                _adaptiveLastDiagLogTime = -999f;
                try { UnderNetBrowserGpuNative.CompSetAdaptiveVisualState(0, 0); } catch { }
            }
            catch { }
        }

        private void TryLogAdaptiveDiagnostics(bool force = false)
        {
            try
            {
                if (UnderNetBrowserConfig.AdaptiveRenderingEnabled?.Value != true) return;
                if (_gpuCompositionCapture == null || !_gpuCompositionMode) return;

                float now = 0f;
                try { now = Time.realtimeSinceStartup; } catch { now = 0f; }
                if (!force && now > 0f && (now - _adaptiveLastDiagLogTime) < 5f) return;
                _adaptiveLastDiagLogTime = now;

                string tname = _adaptiveCurrentTier.ToString();
                float arFps = _adaptiveAppliedCaptureIntervalMs > 0 ? (1000f / _adaptiveAppliedCaptureIntervalMs) : 0f;
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser][AdaptiveDiag] tier={tname} reason='{_adaptiveLastReason}' intervalMs={_adaptiveAppliedCaptureIntervalMs} fps={(arFps > 0 ? arFps.ToString("0.#") : "full")} standby={_adaptiveAppliedStandby} dist={_adaptiveLastDistance:0.##} tvVisible={_adaptiveLastTvVisible}");
            }
            catch { }
        }
        // --- Stage 3/4 PoC: spatial TV audio playback (WASAPI process-loopback capture -> Unity 3D AudioSource) ---
        private void TryUpdateDistanceBasedVolume(bool force = false)
        {
            try
            {
                bool enabled = UnderNetBrowserConfig.DistanceVolumeEnabled?.Value == true;
                if (!enabled)
                {
                    if (_distanceVolumeWasEnabled || Math.Abs(_lastDistanceVolumeApplied - 1f) > 0.001f)
                    {
                        try { UnderNetBrowserGpuNative.CompSetDistanceAudioVolume(0, 1f); } catch { }
                        _lastDistanceVolumeApplied = 1f;
                        _distanceVolumeWasEnabled = false;
                    }
                    return;
                }

                _distanceVolumeWasEnabled = true;
                if (_gpuCompositionCapture == null || !_gpuCompositionMode)
                {
                    // No active composition/browser to attenuate.
                    return;
                }

                float now = 0f;
                try { now = Time.realtimeSinceStartup; } catch { now = 0f; }
                if (!force && now > 0f && (now - _lastDistanceVolumeUpdateTime) < 0.25f)
                    return;
                _lastDistanceVolumeUpdateTime = now;

                // Resolve listener position (camera preferred)
                Vector3 listenerPos;
                bool hasListener = false;
                try
                {
                    var cam = Camera.main;
                    if (cam != null) { listenerPos = cam.transform.position; hasListener = true; }
                    else
                    {
                        listenerPos = default;
                        hasListener = false;
                    }
                }
                catch { listenerPos = default; hasListener = false; }

                if (!hasListener)
                {
                    try { UnderNetBrowserGpuNative.CompSetDistanceAudioVolume(1, 1f); } catch { }
                    _lastDistanceVolumeApplied = 1f;
                    return;
                }

                // Resolve/cached TV position
                Vector3 tvPos = default;
                bool hasTv = false;
                try
                {
                    bool needResolve = _distanceVolumeTvTransform == null;
                    if (!needResolve)
                    {
                        try { if (!_distanceVolumeTvTransform!.gameObject.activeInHierarchy) needResolve = true; } catch { needResolve = true; }
                    }
                    if (needResolve || (now - _lastDistanceVolumeTvResolveTime) > 2.0f)
                    {
                        _lastDistanceVolumeTvResolveTime = now;
                        Transform? t = null;
                        try
                        {
                            GameObject? go = null;
                            try { go = GameObject.Find("TVInterface"); } catch { go = null; }
                            if (go == null)
                            {
                                try { go = GameObject.Find("TV_Built(Clone)/TVInterface"); } catch { go = null; }
                            }
                            if (go == null)
                            {
                                try { go = GameObject.Find("TV_Built/TVInterface"); } catch { go = null; }
                            }
                            if (go == null)
                            {
                                try { go = GameObject.Find("TV_Built(Clone)"); } catch { go = null; }
                            }
                            if (go == null)
                            {
                                try { go = GameObject.Find("TV_Built"); } catch { go = null; }
                            }
                            if (go != null) t = go.transform;
                        }
                        catch { t = null; }
                        if (t != null) _distanceVolumeTvTransform = t;
                    }

                    if (_distanceVolumeTvTransform != null)
                    {
                        tvPos = _distanceVolumeTvTransform.position;
                        hasTv = true;
                    }
                }
                catch { hasTv = false; }

                if (!hasTv)
                {
                    try { UnderNetBrowserGpuNative.CompSetDistanceAudioVolume(1, 1f); } catch { }
                    _lastDistanceVolumeApplied = 1f;
                    return;
                }

                float full = 7.5f;
                float silent = 35.0f;
                try { if (UnderNetBrowserConfig.DistanceVolumeFullDistance != null) full = Mathf.Max(0f, UnderNetBrowserConfig.DistanceVolumeFullDistance.Value); } catch { }
                try { if (UnderNetBrowserConfig.DistanceVolumeSilentDistance != null) silent = Mathf.Max(0.01f, UnderNetBrowserConfig.DistanceVolumeSilentDistance.Value); } catch { }
                if (silent <= full + 0.01f) silent = full + 0.01f;

                float dist = 0f;
                try { dist = Vector3.Distance(listenerPos, tvPos); } catch { dist = 0f; }
                float vol = 1f;
                if (dist <= full) vol = 1f;
                else if (dist >= silent) vol = 0f;
                else vol = 1f - Mathf.InverseLerp(full, silent, dist);
                vol = Mathf.Clamp01(vol);
                // Snap near-silent residuals to true zero (avoids ~1-2% process volume at/beyond effective silent range).
                if (vol <= 0.025f || dist >= (silent - 0.05f)) vol = 0f;

                if (!force && Mathf.Abs(vol - _lastDistanceVolumeApplied) < 0.02f)
                    return;

                try { UnderNetBrowserGpuNative.CompSetDistanceAudioVolume(1, vol); } catch { }
                _lastDistanceVolumeApplied = vol;
            }
            catch { }
        }

        private void HandleExternalMirrorPointerInput()
        {
            if (_videoDisplay == null || !IsOpen || !_externalMirrorMode || _externalMirrorHwnd == IntPtr.Zero)
            {
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                return;
            }
            try
            {
                var rt = _videoDisplay.rectTransform;
                if (rt == null) return;
                var canvas = _videoDisplay.canvas;
                var cam = canvas != null ? canvas.worldCamera : null;
                if (cam == null) cam = Camera.main;

                bool mouseHeld = Input.GetMouseButton(0);
                bool mouseDown = Input.GetMouseButtonDown(0);
                bool mouseUp = Input.GetMouseButtonUp(0);
                bool rHeld = Input.GetMouseButton(1);
                bool rDown = Input.GetMouseButtonDown(1);
                bool rUp = Input.GetMouseButtonUp(1);

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out var local))
                {
                    if (mouseDown) { _compositionWebFocused = false; UpdateInputOwnershipState(); }
                    if (_browserPointerDown && mouseUp) _browserPointerDown = false; else if (!mouseHeld) _browserPointerDown = false;
                    if (_browserPointerRightDown && rUp) _browserPointerRightDown = false; else if (!rHeld) _browserPointerRightDown = false;
                    return;
                }
                var rect = rt.rect;
                if (rect.width <= 0.01f || rect.height <= 0.01f) return;
                float nx = (local.x - rect.xMin) / rect.width;
                float ny = (rect.yMax - local.y) / rect.height;
                bool inside = nx >= 0f && nx <= 1f && ny >= 0f && ny <= 1f;
                if (!inside)
                {
                    if (mouseDown) { _compositionWebFocused = false; UpdateInputOwnershipState(); }
                    if (_browserPointerDown && mouseUp) _browserPointerDown = false; else if (!mouseHeld) _browserPointerDown = false;
                    if (_browserPointerRightDown && rUp) _browserPointerRightDown = false; else if (!rHeld) _browserPointerRightDown = false;
                    return;
                }

                if (!UnderNetBrowserExternalWindowMirror.GetClientRectScreen(_externalMirrorHwnd, out var crect)) return;
                int cw = Math.Max(1, crect.Right - crect.Left);
                int ch = Math.Max(1, crect.Bottom - crect.Top);
                int px = Mathf.Clamp(Mathf.RoundToInt(nx * (cw - 1)), 0, cw - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt(ny * (ch - 1)), 0, ch - 1);

                if (mouseDown)
                {
                    _browserPointerDown = true;
                    _compositionWebFocused = true;
                    try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                    UpdateInputOwnershipState();
                    UnderNetBrowserExternalWindowMirror.SendMouseMove(_externalMirrorHwnd, px, py);
                    UnderNetBrowserExternalWindowMirror.SendMouseDown(_externalMirrorHwnd, px, py, false);
                }
                else if (rDown)
                {
                    _browserPointerRightDown = true;
                    _compositionWebFocused = true;
                    UpdateInputOwnershipState();
                    UnderNetBrowserExternalWindowMirror.SendMouseMove(_externalMirrorHwnd, px, py);
                    UnderNetBrowserExternalWindowMirror.SendMouseDown(_externalMirrorHwnd, px, py, true);
                }
                else
                {
                    bool moved = (px != _lastSentPointerX || py != _lastSentPointerY);
                    bool due = (Time.unscaledTime - _lastPointerMoveSendTime) >= 0.012f;
                    if (moved && (due || mouseHeld || rHeld || _browserPointerDown || _browserPointerRightDown))
                    {
                        _lastSentPointerX = px; _lastSentPointerY = py; _lastPointerMoveSendTime = Time.unscaledTime;
                        UnderNetBrowserExternalWindowMirror.SendMouseMove(_externalMirrorHwnd, px, py);
                    }
                }

                if (mouseUp && _browserPointerDown)
                {
                    _browserPointerDown = false;
                    UnderNetBrowserExternalWindowMirror.SendMouseUp(_externalMirrorHwnd, px, py, false);
                }
                if (rUp && _browserPointerRightDown)
                {
                    _browserPointerRightDown = false;
                    UnderNetBrowserExternalWindowMirror.SendMouseUp(_externalMirrorHwnd, px, py, true);
                }

                float scroll = Input.mouseScrollDelta.y;
                if (Math.Abs(scroll) > 0.001f)
                {
                    int delta = (int)(scroll * 120f);
                    UnderNetBrowserExternalWindowMirror.SendMouseWheel(_externalMirrorHwnd, px, py, delta);
                }
            }
            catch { }
        }

        private void HandleExternalMirrorKeyboardInput()
        {
            if (!_externalMirrorMode || _externalMirrorHwnd == IntPtr.Zero) return;
            try
            {
                bool urlFocused = _urlInput != null && _urlInput.isFocused;
                if (urlFocused) { if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys("url_focused_mirror"); return; }
                bool webFocused = _compositionWebFocused && !urlFocused;
                if (!webFocused) { if (_gameKeysHeld.Count > 0) ReleaseAllGameKeys("web_unfocused_mirror"); return; }

                // Forward tracked/game-mode keys and common typing keys to external window.
                const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
                foreach (var kc in s_gameInputTrackedKeys)
                {
                    if (!TryGetGameInputVk(kc, out var vk)) continue;
                    bool down = Input.GetKey(kc);
                    bool wasHeld = _gameKeysHeld.Contains(vk);
                    if (down && !wasHeld)
                    {
                        _gameKeysHeld.Add(vk);
                        UnderNetBrowserExternalWindowMirror.SendKey(_externalMirrorHwnd, WM_KEYDOWN, (ulong)vk, 0);
                        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || vk == 0x20)
                        {
                            char ch = vk == 0x20 ? ' ' : (vk >= 0x41 && vk <= 0x5A ? (char)('a' + (vk - 0x41)) : (char)vk);
                            UnderNetBrowserExternalWindowMirror.SendKey(_externalMirrorHwnd, WM_CHAR, ch, 0);
                        }
                    }
                    else if (!down && wasHeld)
                    {
                        _gameKeysHeld.Remove(vk);
                        UnderNetBrowserExternalWindowMirror.SendKey(_externalMirrorHwnd, WM_KEYUP, (ulong)vk, 0);
                    }
                }
                // Text input for chat
                try
                {
                    var input = Input.inputString;
                    if (!string.IsNullOrEmpty(input))
                    {
                        foreach (char c in input)
                        {
                            if (c == '\b' || c == '\n' || c == '\r' || c == '\t') continue;
                            UnderNetBrowserExternalWindowMirror.SendKey(_externalMirrorHwnd, WM_CHAR, c, 0);
                        }
                    }
                }
                catch { }
            }
            catch { }
        }
        private bool IsDiscordMirrorRequestUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var u = url.Trim();
            return u.IndexOf("discord.com/app", StringComparison.OrdinalIgnoreCase) >= 0
                || u.IndexOf("discord.com/channels", StringComparison.OrdinalIgnoreCase) >= 0
                || u.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase)
                || u.Equals("home:discord", StringComparison.OrdinalIgnoreCase)
                || u.Equals("https://undernet.route/discord", StringComparison.OrdinalIgnoreCase)
                || u.Equals("http://undernet.route/discord", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://undernet.route/discord?", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("http://undernet.route/discord?", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://undernet.route/discord/", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("http://undernet.route/discord/", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryStartDiscordMirror(string? requestedUrl)
        {
            try
            {
                if (Time.realtimeSinceStartup - _lastExternalMirrorAttemptTime < 1.0f) return _externalMirrorMode;
                _lastExternalMirrorAttemptTime = Time.realtimeSinceStartup;

                if (!UnderNetBrowserExternalWindowMirror.TryFindDiscordWindow(out var target) || target.Hwnd == IntPtr.Zero)
                    return false;

                // Stop current external mirror if switching targets
                if (_externalMirrorMode) StopExternalWindowMirror("restart");

                _externalMirrorReturnUrl = _gpuCompositionCapture?.CurrentUrl;
                if (string.IsNullOrWhiteSpace(_externalMirrorReturnUrl)) _externalMirrorReturnUrl = GetResolvedHomeUrl();

                // Shut down composition browser (WGC HWND capture and comp browser share native target/render path).
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var ms=document.querySelectorAll('video,audio'); for(var i=0;i<ms.length;i++){try{ms[i].pause();}catch(_){}}}catch(_){}})();"); } catch { }
                try { _gpuCompositionCapture?.Dispose(); } catch { }
                _gpuCompositionCapture = null;
                try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                _gpuCompositionMode = false;

                _externalWindowCapture = new UnderNetBrowserGpuCapture();
                int w = Math.Max(2, _videoDisplay != null ? Mathf.RoundToInt(_videoDisplay.rectTransform.rect.width) : 1280);
                int h = Math.Max(2, _videoDisplay != null ? Mathf.RoundToInt(_videoDisplay.rectTransform.rect.height) : 720);
                if (w < 640 || h < 360) { w = 1280; h = 720; }
                if (!_externalWindowCapture.TryStart(target.Hwnd, w, h))
                {
                    try { _externalWindowCapture.Dispose(); } catch { }
                    _externalWindowCapture = null;
                    SetHint("Discord found, but window capture failed.");
                    // Try to restore browser to previous page
                    try { EnsureCompositionBrowserStarted(string.IsNullOrWhiteSpace(_externalMirrorReturnUrl) ? GetResolvedHomeUrl() : _externalMirrorReturnUrl); } catch { }
                    return false;
                }

                _externalMirrorHwnd = target.Hwnd;
                _externalMirrorMode = true;
                _externalMirrorLabel = string.IsNullOrWhiteSpace(target.Title) ? "Discord" : target.Title;
                try { if (_videoDisplay != null && _externalWindowCapture.Texture != null) _videoDisplay.texture = _externalWindowCapture.Texture; } catch { }
                try { SetUrlDisplayText("undernet:discord"); } catch { }
                try { if (_hintText != null) { _hintBaseMessage = ""; _hintText.enabled = false; } } catch { }
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] External mirror started: hwnd=0x{target.Hwnd.ToInt64():X} title='{_externalMirrorLabel}' pid={target.ProcessId}");
                return true;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] TryStartDiscordMirror failed: {ex.Message}");
                try { StopExternalWindowMirror("start_failed"); } catch { }
                return false;
            }
        }

        private void StopExternalWindowMirror(string reason)
        {
            try
            {
                if (!_externalMirrorMode && _externalWindowCapture == null) return;
                try { _externalWindowCapture?.Dispose(); } catch { }
                _externalWindowCapture = null;
                _externalMirrorMode = false;
                _externalMirrorHwnd = IntPtr.Zero;
                _externalMirrorLabel = "";
                try { if (_videoDisplay != null) _videoDisplay.texture = null; } catch { }
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] External mirror stopped ({reason}).");
            }
            catch { }
        }

        private void EnsureCompositionBrowserStarted(string? targetUrl = null)
        {
            try
            {
                if (_gpuCompositionCapture != null) return;
                _gpuCompositionMode = false;
                // Reuse same composition profile/path as normal startup to avoid creating a duplicate WebView2 user data folder.
                var home = string.IsNullOrWhiteSpace(targetUrl) ? GetResolvedHomeUrl() : ResolveNavigationTarget(targetUrl);
                string compUserData = "";
                try { compUserData = Path.Combine(GetUserDataRoot(), "WebView2Composition"); } catch { compUserData = ""; }
                try { EnsureDefaultBrowserExtensionDeployed(compUserData); } catch { }
                _gpuCompositionCapture = new UnderNetBrowserCompositionCapture();
                GetDesiredCompositionRenderSize(out int compW, out int compH);
                bool ok = false;
                try { ok = _gpuCompositionCapture.TryStart(home, compW, compH, compUserData); } catch { ok = false; }
                if (ok)
                {
                    _gpuCompositionMode = true;
                    try { if (_videoDisplay != null && _gpuCompositionCapture.Texture != null) _videoDisplay.texture = _gpuCompositionCapture.Texture; } catch { }
                    try { SetHint(""); } catch { }
                }
                else
                {
                    try { _gpuCompositionCapture.Dispose(); } catch { }
                    _gpuCompositionCapture = null;
                    _gpuCompositionMode = false;
                    SetHint("Failed to restart browser after mirror.");
                }
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] EnsureCompositionBrowserStarted failed: {ex.Message}");
            }
        }

        private void UpdateExternalMirrorTick()
        {
            if (!_externalMirrorMode || _externalWindowCapture == null) return;
            try
            {
                try { if (_hintText != null) { _hintBaseMessage = ""; _hintText.enabled = false; _hintText.text = ""; } } catch { }
                _externalWindowCapture.Tick();
                if (_videoDisplay != null && _externalWindowCapture.Texture != null && _videoDisplay.texture != _externalWindowCapture.Texture)
                    _videoDisplay.texture = _externalWindowCapture.Texture;

                // If Discord window is gone, stop mirror and fall back.
                if (_externalMirrorHwnd != IntPtr.Zero)
                {
                    bool alive = false;
                    try { alive = IsWindow(_externalMirrorHwnd); } catch { alive = false; }
                    if (!alive)
                    {
                        var ret = _externalMirrorReturnUrl;
                        StopExternalWindowMirror("target_window_closed");
                        EnsureCompositionBrowserStarted(string.IsNullOrWhiteSpace(ret) ? GetResolvedHomeUrl() : ret);
                    }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

    }
}


