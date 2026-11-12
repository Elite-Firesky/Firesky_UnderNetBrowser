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
        protected override Sprite? Icon => _icon ??= CreateDefaultIcon();

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
        private float _lastRepairCheck;
        private float _lastRepairDonorSearch;
        private bool _repairStateLoggedBad;
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
        // Stage 3/4 PoC: spatial audio playback from captured WebView2 process-loopback PCM (dev-only, behind config)
        private GameObject? _spatialAudioGo;
        private AudioSource? _spatialAudioSource;
        private AudioClip? _spatialAudioClip;
        private uint _spatialAudioRate;
        private uint _spatialAudioChannels;
        private bool _spatialAudioStarted;
        private float _lastSpatialAudioSetupAttempt;
        private float[]? _spatialAudioReadBuffer;
        private readonly UnderNetBrowserSync _sync = new UnderNetBrowserSync();
        private readonly List<(GameObject go, bool wasActive)> _hiddenHudObjects = new List<(GameObject, bool)>();
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
                try { ReleaseAllGameKeys("shutdown:" + reason); } catch { }
                _gameInputMode = false;
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { TryRestoreGameInput(); } catch { }
                try { TryRestoreGameHud(); } catch { }
                try { RestoreDetachedHomeScreenObjects(); } catch { }
                try { StopSpatialAudioPlayback("shutdown:" + reason); } catch { }
            }
            catch { }

            try
            {
                try { StopExternalWindowMirror("shutdown:" + reason); } catch { }
                bool hadCapture = _gpuCompositionCapture != null || _externalMirrorMode;
                if (hadCapture)
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Shutting down browser/composition ({reason}).");

                // Best-effort: stop media/playback before teardown in case native stop event is delayed/missed during unload.
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var ms=document.querySelectorAll('video,audio'); for(var i=0;i<ms.length;i++){try{ms[i].pause(); ms[i].src=''; ms[i].load();}catch(_){}}}catch(_){}})();"); } catch { }
                try { ReleaseAllGameKeys("shutdown_navigate_blank"); } catch { }
                try { _gpuCompositionCapture?.Navigate("about:blank"); } catch { }
                try { _gpuCompositionCapture?.Dispose(); } catch { }
                _gpuCompositionCapture = null;

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
            try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
            TryHideGameHud();
            try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
            UpdateInputOwnershipState();
            // Hybrid suppression: hooks for supported callbacks + verified/explicit game API toggles + reflection fallback for unhooked paths.
            // Capture once per open so states can be restored on close.
            try { TryDisableGameInput(); } catch { }
            // Learn baseline hotbar slot to pin while open (fallback for unhooked equip paths).
            try { _lastKnownHotbarSlot = -1; TryRestoreEquippedSlotIndex(forceRestore: false); } catch { }
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
                    int compW = Mathf.Clamp(UnderNetBrowserConfig.GpuCompositionRenderWidth?.Value ?? 1920, 854, 2560);
                    int compH = Mathf.Clamp(UnderNetBrowserConfig.GpuCompositionRenderHeight?.Value ?? 1080, 480, 1440);
                    string compUserData = "";
                    try { compUserData = System.IO.Path.Combine(GetUserDataRoot(), "WebView2Composition"); } catch { compUserData = ""; }
                    try { EnsureDefaultBrowserExtensionDeployed(compUserData); } catch { }
                    try { UnderNetBrowserDiagnostics.OnBrowserLoadingBegin(); UnderNetBrowserDiagnostics.OnWebViewInitStart(); } catch { }
                    if (_gpuCompositionCapture.TryStart(home, compW, compH, compUserData))
                    {
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
            if (explicitXClose)
            {
                // Explicit app close (X): fully shut down browser/WebView2 to avoid lingering processes.
                try { StopExternalWindowMirror("closed_x"); } catch { }
                try { StopSpatialAudioPlayback("closed_x"); } catch { }
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var ms=document.querySelectorAll('video,audio'); for(var i=0;i<ms.length;i++){try{ms[i].pause(); ms[i].src=''; ms[i].load();}catch(_){}}}catch(_){}})();"); } catch { }
                try { _gpuCompositionCapture?.Navigate("about:blank"); } catch { }
                try { _gpuCompositionCapture?.Dispose(); } catch { }
                _gpuCompositionCapture = null;
                try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }
                _gpuCompositionMode = false;
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Browser runtime fully shut down (X close).");
            }
            else
            {
                // Normal/framework close path: stop media and park browser for fast warm reopen (avoid ~12s controller recreation).
                // Full native shutdown remains in ShutdownBrowserRuntime/world unload.
                try { StopSpatialAudioPlayback("closed"); } catch { }
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
            _detachedToGameplay = false;
            _detachRequested = false;
            AnyDetachActive = false;
            try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
            try { RestoreDetachedHomeScreenObjects(); } catch { }
            try { SetAppInteractionEnabled(true); } catch { }
            try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
            UpdateInputOwnershipState();
            try { TryHideGameHud(); } catch { }
            try { TryDisableGameInput(); } catch { }
            try { if (_lastKnownHotbarSlot < 0) TryRestoreEquippedSlotIndex(forceRestore: false); } catch { }
        }

        protected override void OnUpdate()
        {
            try
            {
                if (!IsOpen) return;
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
                        try { UnderNetBrowserInputHooks.LogDiagnostics(); } catch { }
                        return;
                    }
                }
                UpdateInputOwnershipState();
                // Keep suppression/hotbar-slot pinning active (covers unhooked paths like OnMove and alternate equip-slot changes). Throttled internally.
                try { TryEnsureGameInputSuppressed(); } catch { }
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

                if (Time.realtimeSinceStartup - _lastRepairCheck >= 1.0f)
                {
                    _lastRepairCheck = Time.realtimeSinceStartup;
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
                    // If user navigates (via in-page shortcut/link) to Discord Web and Discord is running, switch to window mirror.
                    try
                    {
                        var cur = _gpuCompositionCapture.CurrentUrl;
                        if (IsDiscordMirrorRequestUrl(cur) && Time.realtimeSinceStartup - _lastExternalMirrorAttemptTime > 1.5f)
                        {
                            // Try mirror; if it fails, stay on web Discord (avoid tight retry loop by updating attempt time inside TryStart)
                            if (TryStartDiscordMirror(cur))
                                return;
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
                    try { UpdateSpatialAudioPlayback(); } catch { }
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
                            UnderNetBrowserConfig.Log($"[UnderNetBrowser][VisualDiag] compStatus={compSt} unityTex={tw}x{th} compFrame={cw}x{ch} videoRect={vrect.width:F1}x{vrect.height:F1} rectAspect={rectAspect:F3} texAspect={texAspect:F3} videoLossy=({vlossy.x:F3},{vlossy.y:F3},{vlossy.z:F3}) canvasScale={canvasScale:F3} texFilter={(_gpuCompositionCapture.Texture!=null?_gpuCompositionCapture.Texture.filterMode.ToString():"n/a")} diag='{diag}'");
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
            if (text.Equals("undernet:home", StringComparison.OrdinalIgnoreCase) || text.Equals("about:home", StringComparison.OrdinalIgnoreCase))
                return "undernet:home";
            if (!text.Contains("://") && !text.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;
            return text;
        }

        private string ResolveNavigationTarget(string? input)
        {
            var normalized = NormalizeUrl(input);
            if (normalized.Equals("undernet:home", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepageUrl();
            // Migrate previous default to new built-in homepage experience
            if (normalized.Equals("https://www.youtube.com", StringComparison.OrdinalIgnoreCase) && string.Equals(UnderNetBrowserConfig.HomeUrl?.Value?.Trim(), "undernet:home", StringComparison.OrdinalIgnoreCase))
                return GetOrCreateHomepageUrl();
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

        private string GetOrCreateHomepageUrl()
        {
            try
            {
                var dir = Path.Combine(GetUserDataRoot(), "Home");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "home.html");
                File.WriteAllText(path, BuildHomepageHtml());
                return new Uri(path).AbsoluteUri;
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Homepage creation failed, falling back to Google: {ex.Message}");
                return "https://www.google.com";
            }
        }

        private static string GetUserDataRoot()
        {
            try
            {
                var ud = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
                if (!string.IsNullOrWhiteSpace(ud))
                    return Path.Combine(ud, "FireskyUnderNetBrowser");
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "FireskyUnderNetBrowser");
        }

        private static void EnsureDefaultBrowserExtensionDeployed(string compositionUserDataRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(compositionUserDataRoot)) return;
                // Deploy default browser extension (uBlock Origin Lite) automatically if missing.
                // Source locations (best-effort): alongside mod DLL and common mod subfolders.
                const string extensionFolderName = "uBlockOriginLite";
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
                Add(asmDir, "BrowserExtensions", extensionFolderName);
                Add(asmDir, "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName);
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "BrowserExtensions", extensionFolderName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "BrowserExtensions", extensionFolderName); } catch { }
                try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "FireskyUnderNetBrowser", "BrowserExtensions", extensionFolderName); } catch { }

                string? sourceDir = null;
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
                if (string.IsNullOrWhiteSpace(sourceDir)) return;

                var targets = new List<string>();
                try { targets.Add(Path.Combine(compositionUserDataRoot, "Extensions", extensionFolderName)); } catch { }
                try { targets.Add(Path.Combine(GetUserDataRoot(), "Extensions", extensionFolderName)); } catch { }

                for (int t = 0; t < targets.Count; t++)
                {
                    try
                    {
                        var dst = targets[t];
                        if (string.IsNullOrWhiteSpace(dst)) continue;
                        if (File.Exists(Path.Combine(dst, "manifest.json"))) continue; // already present
                        CopyDirectoryRecursive(sourceDir, dst);
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Auto-deployed default browser extension to '{dst}'.");
                    }
                    catch { }
                }
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

        private static string BuildHomepageHtml()
        {
            // Self-contained, polished Schedule I / UnderNet-themed dark homepage with preset shortcuts.
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
<title>UnderNet</title>
<style>
  :root{
    --bg0:#040605; --bg1:#070b09; --bg2:#0a120e;
    --panel:#0c1310; --panel2:#0f1914; --edge:#1b2a22; --edge2:#24372d;
    --text:#e9f7ee; --muted:#8ea89a; --muted2:#6f887c;
    --accent:#7cff6b; --accent2:#35d97b; --accent3:#baff6a;
    --glow:rgba(124,255,107,.20); --glow2:rgba(53,217,123,.14);
  }
  *{box-sizing:border-box}
  html,body{height:100%;margin:0}
  body{
    font-family:'Segoe UI Variable','Segoe UI',Tahoma,Arial,sans-serif;
    color:var(--text);
    background:
      radial-gradient(900px 420px at 15% -5%, rgba(53,217,123,.16), transparent 60%),
      radial-gradient(700px 360px at 100% 0%, rgba(124,255,107,.10), transparent 55%),
      radial-gradient(800px 500px at 50% 120%, rgba(124,255,107,.08), transparent 60%),
      linear-gradient(180deg, var(--bg0), var(--bg1) 35%, var(--bg2));
    overflow:hidden;
  }
  body:before{
    content:''; position:fixed; inset:0; pointer-events:none; opacity:.22;
    background:
      repeating-linear-gradient(to bottom, rgba(255,255,255,.025) 0px, rgba(255,255,255,.025) 1px, transparent 2px, transparent 4px),
      linear-gradient(90deg, rgba(124,255,107,.03), transparent 30%, transparent 70%, rgba(124,255,107,.02));
    mix-blend-mode:soft-light;
  }
  .wrap{position:relative;height:100%;padding:26px 30px;display:flex;flex-direction:column;gap:18px}
  .hero{
    position:relative;
    display:flex;align-items:center;justify-content:space-between;gap:18px;
    padding:18px 20px;border:1px solid var(--edge);border-radius:16px;
    background:
      linear-gradient(180deg, rgba(16,26,21,.92), rgba(9,14,11,.94)),
      radial-gradient(500px 120px at 0% 0%, rgba(124,255,107,.08), transparent 60%);
    box-shadow: 0 0 0 1px rgba(124,255,107,.04) inset, 0 22px 50px rgba(0,0,0,.42);
    overflow:hidden;
  }
  .hero:after{
    content:''; position:absolute; right:-80px; top:-80px; width:260px; height:260px; border-radius:50%;
    background:radial-gradient(circle, rgba(124,255,107,.14), transparent 65%);
    filter:blur(2px);
  }
  .brand{display:flex;align-items:center;gap:14px;min-width:0}
  .mark{
    width:52px;height:52px;border-radius:14px;position:relative;flex:0 0 auto;
    background:radial-gradient(circle at 30% 28%, #d8ffbe 0%, var(--accent) 34%, #1b8f52 72%, #0f4a2f 100%);
    box-shadow:0 0 28px var(--glow), 0 0 0 1px rgba(255,255,255,.09) inset;
  }
  .mark:before{content:'';position:absolute;inset:2px;border-radius:12px;border:1px solid rgba(255,255,255,.18);opacity:.35}
  .mark:after{content:'';position:absolute;left:19px;top:14px;width:0;height:0;border-top:12px solid transparent;border-bottom:12px solid transparent;border-left:18px solid rgba(5,14,9,.92);filter:drop-shadow(0 0 6px rgba(0,0,0,.25))}
  .titlewrap{min-width:0}
  h1{margin:0;font-size:34px;line-height:1;letter-spacing:2.4px;font-weight:800;text-transform:uppercase}
  h1 .u{color:var(--accent);text-shadow:0 0 22px rgba(124,255,107,.22)}
  .tag{margin:5px 0 0;color:var(--muted);font-size:12px;letter-spacing:1.1px;text-transform:uppercase}
  .chips{display:flex;gap:8px;align-items:center;flex-wrap:wrap;justify-content:flex-end}
  .chip{
    padding:7px 11px;border-radius:999px;border:1px solid rgba(124,255,107,.22);
    background:rgba(124,255,107,.06);color:#dfffe3;font-size:11px;letter-spacing:.7px;text-transform:uppercase;
    box-shadow:0 0 0 1px rgba(124,255,107,.03) inset;
    white-space:nowrap;
  }
  .chip.dim{border-color:rgba(255,255,255,.10);background:rgba(255,255,255,.03);color:#cfe0d6}
  .content{display:grid;grid-template-columns:1.2fr .8fr;gap:18px;min-height:0;flex:1}
  .panel{
    border:1px solid var(--edge);border-radius:16px;
    background:linear-gradient(180deg, rgba(14,22,18,.95), rgba(9,14,11,.96));
    box-shadow:0 14px 34px rgba(0,0,0,.30), 0 0 0 1px rgba(124,255,107,.03) inset;
    padding:16px;min-height:0;
  }
  .section-title{display:flex;align-items:center;justify-content:space-between;margin:2px 4px 12px}
  .section-title h2{margin:0;font-size:13px;letter-spacing:1.4px;text-transform:uppercase;color:#d7eadf}
  .section-title span{font-size:11px;color:var(--muted2);letter-spacing:.6px;text-transform:uppercase}
  .grid{display:grid;grid-template-columns:repeat(2,minmax(180px,1fr));gap:12px}
  .card{
    position:relative;display:flex;align-items:center;gap:14px;text-decoration:none;color:var(--text);
    padding:14px 14px;border-radius:14px;border:1px solid var(--edge);
    background:linear-gradient(180deg, rgba(18,29,23,.96), rgba(11,17,14,.96));
    transition:transform .14s ease,border-color .14s ease,box-shadow .14s ease,background .14s ease;
    overflow:hidden;
  }
  .card:before{content:'';position:absolute;inset:0;background:linear-gradient(120deg, rgba(124,255,107,.10), transparent 35%);opacity:0;transition:opacity .14s ease}
  .card:hover{transform:translateY(-2px);border-color:rgba(124,255,107,.42);box-shadow:0 0 0 1px rgba(124,255,107,.07) inset, 0 18px 30px rgba(0,0,0,.34)}
  .card:hover:before{opacity:1}
  .icon{
    width:50px;height:50px;border-radius:12px;display:grid;place-items:center;flex:0 0 auto;
    color:#08110b;font-weight:800;font-size:20px;
    box-shadow:0 0 18px rgba(124,255,107,.16), 0 0 0 1px rgba(255,255,255,.08) inset;
  }
  .icon.yt{background:linear-gradient(180deg,#ff9696,#ff3b3b);color:#fff}
  .icon.gg{background:linear-gradient(180deg,#ecff9a,#7cff6b);color:#0a1710}
  .icon.nfs{background:linear-gradient(180deg,#7fdcff,#1f7fff);color:#061018}
  .icon.dc{background:linear-gradient(180deg,#8ea1ff,#5865f2);color:#fff}
  .meta{min-width:0}
  .meta .name{margin:0 0 3px;font-size:18px;font-weight:700;letter-spacing:.2px}
  .meta .desc{margin:0;font-size:12px;color:var(--muted);line-height:1.35}
  .meta .url{margin:6px 0 0;font-size:11px;color:#7fa893;letter-spacing:.4px}
  .side{display:flex;flex-direction:column;gap:12px}
  .info{
    padding:13px 14px;border-radius:12px;border:1px solid rgba(255,255,255,.06);
    background:linear-gradient(180deg, rgba(255,255,255,.03), rgba(255,255,255,.015));
  }
  .info h3{margin:0 0 6px;font-size:12px;letter-spacing:1.1px;text-transform:uppercase;color:#dcecdf}
  .info p{margin:0;color:var(--muted);font-size:12.5px;line-height:1.5}
  .kbd{display:inline-block;padding:1px 6px;border-radius:6px;border:1px solid rgba(255,255,255,.14);background:rgba(255,255,255,.06);color:#eefaf2;font-size:11px}
  .accentline{height:1px;background:linear-gradient(90deg, rgba(124,255,107,.45), rgba(124,255,107,0));margin:10px 0 2px;opacity:.7}
  .footer{display:flex;align-items:center;justify-content:space-between;color:#789083;font-size:11px;letter-spacing:.5px;padding:0 4px}
  .dot{width:7px;height:7px;border-radius:50%;background:var(--accent);box-shadow:0 0 10px rgba(124,255,107,.7);display:inline-block;margin-right:7px;vertical-align:middle}
  @media (max-width: 980px){.content{grid-template-columns:1fr}.grid{grid-template-columns:1fr 1fr}}
  @media (max-width: 760px){.wrap{padding:16px}.grid{grid-template-columns:1fr}h1{font-size:28px}.chips{justify-content:flex-start}}
</style>
</head>
<body tabindex=""0"">
  <div class=""wrap"">
    <header class=""hero"">
      <div class=""brand"">
        <div class=""mark""></div>
        <div class=""titlewrap"">
          <h1><span class=""u"">Under</span>Net</h1>
          <div class=""tag"">In-game browser node • Hyland Point network</div>
        </div>
      </div>
      <div class=""chips"">
        <div class=""chip"">WebView2 Live</div>
        <div class=""chip dim"">Secure Session</div>
        <div class=""chip dim"">TV Display Link</div>
      </div>
    </header>

    <main class=""content"">
      <section class=""panel"">
        <div class=""section-title"">
          <h2>Quick Access</h2>
          <span>Saved Shortcuts</span>
        </div>
        <div class=""grid"">
          <a class=""card"" href=""https://www.youtube.com"">
            <div class=""icon yt"">▶</div>
            <div class=""meta"">
              <p class=""name"">YouTube</p>
              <p class=""desc"">Videos, music, streams and live content.</p>
              <p class=""url"">youtube.com</p>
            </div>
          </a>
          <a class=""card"" href=""https://www.google.com"">
            <div class=""icon gg"">G</div>
            <div class=""meta"">
              <p class=""name"">Google</p>
              <p class=""desc"">Search the web from inside Schedule I.</p>
              <p class=""url"">google.com</p>
            </div>
          </a>
          <a class=""card"" href=""https://nfsu2.xyz/"">
            <div class=""icon nfs"">N</div>
            <div class=""meta"">
              <p class=""name"">NFSU2</p>
              <p class=""desc"">Quick launch Need for Speed Underground 2 web port.</p>
              <p class=""url"">nfsu2.xyz</p>
            </div>
          </a>
          <a class=""card"" href=""https://discord.com/app"">
            <div class=""icon dc"">D</div>
            <div class=""meta"">
              <p class=""name"">Discord</p>
              <p class=""desc"">Mirror Discord if running, or open Discord Web.</p>
              <p class=""url"">discord.com/app</p>
            </div>
          </a>
        </div>
      </section>

      <aside class=""side"">
        <div class=""info"">
          <h3>Navigation</h3>
          <p>Use the top address bar to browse. Paste with <span class=""kbd"">Ctrl</span>+<span class=""kbd"">V</span>, then press <span class=""kbd"">Enter</span> to load.</p>
          <div class=""accentline""></div>
          <p>Back, forward, reload and close are available in the browser chrome.</p>
        </div>
        <div class=""info"">
          <h3>Status</h3>
          <p><span class=""dot""></span>UnderNet ready. Video pages and web apps render directly on the in-game TV.</p>
        </div>
      </aside>
    </main>

    <div class=""footer"">
      <div>UNDERNET // PRIVATE TERMINAL</div>
      <div>Schedule I TV Browser</div>
    </div>
  </div>
  <script>
    try {
      window.focus();
      document.body && document.body.focus && document.body.focus();
      document.addEventListener('DOMContentLoaded', function(){ try{ window.focus(); document.body.focus(); }catch(e){} }, { once: true });
    } catch(e) {}
  </script>
</body>
</html>";
        }

        private void SetUrl(string? input)
        {
            var raw = (input ?? string.Empty).Trim();
            var normalizedRaw = NormalizeUrl(raw);
            var url = ResolveNavigationTarget(raw);

            // External window mirror (Discord): if requested and Discord is running, mirror its window; else fall back to Discord Web.
            if (IsDiscordMirrorRequestUrl(raw) || IsDiscordMirrorRequestUrl(normalizedRaw) || IsDiscordMirrorRequestUrl(url))
            {
                if (_urlInput != null) _urlInput.text = "https://discord.com/app";
                if (TryStartDiscordMirror(url))
                    return;
                // Fallback to web Discord in browser
                url = "https://discord.com/app";
                try { SetUrlDisplayText(url); } catch { }
            }
            else if (_externalMirrorMode)
            {
                // Leaving mirror mode to browse normally
                var ret = _externalMirrorReturnUrl;
                StopExternalWindowMirror("navigate_away");
                EnsureCompositionBrowserStarted();
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
                MelonLogger.Msg($"[Firesky UnderNet Browser] [UnderNetBrowser] Navigate (composition): {url}");
            }
            else
            {
                if (_externalMirrorMode) return;
                SetHint("Composition browser not available.");
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
                // Ensure web focus for game controls; URL bar remains separate (and will exit mode if focused).
                if (_urlInput != null && _urlInput.isFocused)
                {
                    _gameInputMode = false;
                    ReleaseAllGameKeys("mode_blocked_url_focus:" + reason);
                    SetHint("Game Input: click the web view first (URL bar is focused)");
                    try { if (_gameModeIndicatorRoot != null) _gameModeIndicatorRoot.SetActive(false); } catch { }
                    return;
                }
                _compositionWebFocused = true;
                try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                try { _gpuCompositionCapture?.RequestFocus(); } catch { }
                // Best-effort: focus canvas/document for browser games when enabling game input (retry briefly for async pages).
                try { _gpuCompositionCapture?.ExecuteScript("(function(){try{var d=document; function f(){try{var c=d.querySelector('canvas'); if(c){try{c.tabIndex=c.tabIndex||-1; if(c.tabIndex<0)c.tabIndex=0;}catch(_){} try{c.focus&&c.focus();}catch(_){} try{c.click&&c.click();}catch(_){}} try{window&&window.focus&&window.focus();}catch(_){} try{d.body&&d.body.focus&&d.body.focus();}catch(_){}}catch(_){}} f(); setTimeout(f,50); setTimeout(f,200); setTimeout(f,600);}catch(_){}})();"); } catch { }
                try { _lastGameModeFocusNudge = -999f; } catch { }
                UpdateInputOwnershipState();
            }
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
                    try { _gpuCompositionCapture.SendKey(WM_KEYUP, (ulong)vk, 0); } catch { }
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
                string script =
                    "(function(){try{var d=document;var tgt=d.activeElement||d.querySelector('canvas')||d.body||d.documentElement; var evInit={key:'" + EscapeJsString(jsKey) + "',code:'" + EscapeJsString(jsCode) + "',keyCode:" + keyCode + ",which:" + keyCode + ",location:" + location + ",bubbles:true,cancelable:true,composed:true,repeat:false}; var targets=[]; if(tgt) targets.push(tgt); if(d) targets.push(d); if(window) targets.push(window); for(var i=0;i<targets.length;i++){try{targets[i].dispatchEvent(new KeyboardEvent('keyup',evInit));}catch(_){}} }catch(_){}})();";
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

                // Periodic focus nudge for browser games (some pages/canvas lose focus after load or internal transitions).
                try
                {
                    if (Time.unscaledTime - _lastGameModeFocusNudge > 0.75f)
                    {
                        _lastGameModeFocusNudge = Time.unscaledTime;
                        try { _gpuCompositionCapture.RequestFocus(); } catch { }
                        try { _gpuCompositionCapture.ExecuteScript("(function(){try{var d=document,c=d.querySelector('canvas'); if(c){try{if(c.tabIndex==null||c.tabIndex<0)c.tabIndex=0;}catch(_){} try{c.focus&&c.focus();}catch(_){}} }catch(_){}})();"); } catch { }
                    }
                }
                catch { }

                const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;
                foreach (var kc in s_gameInputTrackedKeys)
                {
                    if (!TryGetGameInputVk(kc, out var vk)) continue;
                    bool down = Input.GetKey(kc);
                    bool wasHeld = _gameKeysHeld.Contains(vk);
                    if (down && !wasHeld)
                    {
                        _gameKeysHeld.Add(vk);
                        try { _gpuCompositionCapture.SendKey(WM_KEYDOWN, (ulong)vk, 0); } catch { }
                        // For basic text-producing keys, also send WM_CHAR on initial press for games/pages that expect it.
                        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || vk == 0x20)
                        {
                            char ch = vk == 0x20 ? ' ' : (vk >= 0x41 && vk <= 0x5A ? (char)('a' + (vk - 0x41)) : (char)vk);
                            try { _gpuCompositionCapture.SendKey(WM_CHAR, ch, 0); } catch { }
                        }
                        // Browser games (WebGL/Emscripten) often rely on DOM KeyboardEvents/focus; inject events as a compatibility path.
                        TrySendGameInputDomKeyEvent(kc, vk, true);
                    }
                    else if (!down && wasHeld)
                    {
                        _gameKeysHeld.Remove(vk);
                        try { _gpuCompositionCapture.SendKey(WM_KEYUP, (ulong)vk, 0); } catch { }
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
                // Dispatch to focused/canvas targets and window to maximize compatibility with browser games.
                // Do not prevent default globally except we let page handle; some games need default prevented for arrows/space - emulate common behavior via event options is limited,
                // but dispatching proper KeyboardEvents is often sufficient.
                string script =
                    "(function(){try{var d=document;var tgt=d.activeElement;if(!tgt||tgt===d.body||tgt===d.documentElement){var c=d.querySelector('canvas'); if(c){try{if(typeof c.focus==='function') c.focus();}catch(_){} tgt=c;}} if(!tgt)tgt=d.body||d.documentElement; var evInit={key:'" + EscapeJsString(jsKey) + "',code:'" + EscapeJsString(jsCode) + "',keyCode:" + keyCode + ",which:" + keyCode + ",location:" + location + ",bubbles:true,cancelable:true,composed:true,repeat:false}; var targets=[]; if(tgt) targets.push(tgt); if(d) targets.push(d); if(window) targets.push(window); for(var i=0;i<targets.length;i++){try{targets[i].dispatchEvent(new KeyboardEvent('" + type + "',evInit));}catch(_){}} }catch(_){}})();";
                _gpuCompositionCapture.ExecuteScript(script);
            }
            catch { }
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
                        bool arrowDown = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow);
                        bool spaceDown = Input.GetKeyDown(KeyCode.Space);
                        if (arrowDown || spaceDown)
                        {
                            try
                            {
                                _compositionWebFocused = true;
                                try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                                UpdateInputOwnershipState();
                                try { _gpuCompositionCapture.RequestFocus(); } catch { }
                                const uint wmKd = 0x0100, wmKu = 0x0101, wmCh = 0x0102;
                                if (spaceDown)
                                {
                                    _gpuCompositionCapture.SendKey(wmKd, 0x20, 0);
                                    _gpuCompositionCapture.SendKey(wmCh, ' ', 0);
                                    _gpuCompositionCapture.SendKey(wmKu, 0x20, 0);
                                    try { SendSpacePlayPauseScriptFallback(); } catch { }
                                    LogCompositionTypingDiag($"space(play/pause fallback) urlFocused={urlFocused} ptr={ptrOver}");
                                }
                                if (Input.GetKeyDown(KeyCode.LeftArrow))  { _gpuCompositionCapture.SendKey(wmKd, 0x25, 0); _gpuCompositionCapture.SendKey(wmKu, 0x25, 0); try { SendArrowMediaKeyFallback("left"); } catch { } }
                                if (Input.GetKeyDown(KeyCode.UpArrow))    { _gpuCompositionCapture.SendKey(wmKd, 0x26, 0); _gpuCompositionCapture.SendKey(wmKu, 0x26, 0); try { SendArrowMediaKeyFallback("up"); } catch { } }
                                if (Input.GetKeyDown(KeyCode.RightArrow)) { _gpuCompositionCapture.SendKey(wmKd, 0x27, 0); _gpuCompositionCapture.SendKey(wmKu, 0x27, 0); try { SendArrowMediaKeyFallback("right"); } catch { } }
                                if (Input.GetKeyDown(KeyCode.DownArrow))  { _gpuCompositionCapture.SendKey(wmKd, 0x28, 0); _gpuCompositionCapture.SendKey(wmKu, 0x28, 0); try { SendArrowMediaKeyFallback("down"); } catch { } }
                                if (arrowDown)
                                    LogCompositionTypingDiag($"arrow(media fallback) urlFocused={urlFocused} ptr={ptrOver}");
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
                                try { _gpuCompositionCapture.SendText(textBatch.ToString()); } catch { }
                                textBatch.Clear();
                            }
                            if (c == '\b') { _gpuCompositionCapture.SendKey(WM_KD, 0x08, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x08, 0); sentCtrl++; }
                            else if (c == '\r' || c == '\n') { _gpuCompositionCapture.SendKey(WM_KD, 0x0D, 0); _gpuCompositionCapture.SendKey(WM_CH, 0x0D, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x0D, 0); sentCtrl++; }
                            else if (c == '\t') { _gpuCompositionCapture.SendKey(WM_KD, 0x09, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x09, 0); sentCtrl++; }
                            else if (c == 27) { _gpuCompositionCapture.SendKey(WM_KD, 0x1B, 0); _gpuCompositionCapture.SendKey(WM_KU, 0x1B, 0); sentCtrl++; }
                            continue;
                        }
                        // Best-effort VK for WM_KEYDOWN/WM_KEYUP; actual character comes from WM_CHAR / script injection.
                        ulong vk = 0;
                        if (c >= 'a' && c <= 'z') vk = (ulong)(char.ToUpperInvariant(c));
                        else if (c >= 'A' && c <= 'Z') vk = (ulong)c;
                        else if (c >= '0' && c <= '9') vk = (ulong)c;
                        else if (c == ' ') vk = 0x20;
                        // digits handled above to mark number-key intent; still send as chars if they appear
                        _gpuCompositionCapture.SendKey(WM_KD, vk, 0);
                        _gpuCompositionCapture.SendKey(WM_CH, c, 0);
                        _gpuCompositionCapture.SendKey(WM_KU, vk, 0);
                        if (textBatch == null) textBatch = new System.Text.StringBuilder();
                        textBatch.Append(c);
                        sentChars++;
                    }
                    if (textBatch != null && textBatch.Length > 0)
                    {
                        try { _gpuCompositionCapture.SendText(textBatch.ToString()); } catch { }
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
                            // ESC while in app (non-fullscreen, normal mode): mimic right-click detach (restore gameplay, keep app running/background).
                            try { TryDetachToGameplayKeepAppOpen(); } catch { }
                        }
                    }
                }
                if (Input.GetKeyDown(KeyCode.LeftArrow)) { SendVk(0x25); try { SendArrowMediaKeyFallback("left"); } catch { } }
                if (Input.GetKeyDown(KeyCode.UpArrow)) { SendVk(0x26); try { SendArrowMediaKeyFallback("up"); } catch { } }
                if (Input.GetKeyDown(KeyCode.RightArrow)) { SendVk(0x27); try { SendArrowMediaKeyFallback("right"); } catch { } }
                if (Input.GetKeyDown(KeyCode.DownArrow)) { SendVk(0x28); try { SendArrowMediaKeyFallback("down"); } catch { } }
                if (Input.GetKeyDown(KeyCode.Delete)) SendVk(0x2E);
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    SendVk(0x20, true, ' ');
                    try { SendSpacePlayPauseScriptFallback(); } catch { }
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

        private void SendSpacePlayPauseScriptFallback()
        {
            try
            {
                if (!_gpuCompositionMode || _gpuCompositionCapture == null) return;
                _gpuCompositionCapture.ExecuteScript(
                    "(function(){try{var d=document; var ae=d.activeElement; var tag=ae&&ae.tagName?ae.tagName.toUpperCase():'';" +
                    "var typing=(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'));" +
                    "if(typing) return;" +
                    "var vids=d.querySelectorAll('video'); var v=null;" +
                    "for(var i=0;i<vids.length;i++){var vv=vids[i]; if(!vv) continue; if(vv.closest && vv.closest('ytd-player, .html5-video-player, video')){v=vv; break;} if(!v) v=vv;}" +
                    "var k={key:' ',code:'Space',keyCode:32,which:32,bubbles:true,cancelable:true,composed:true};" +
                    "try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} " +
                    "try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} " +
                    "if(v){try{if(v.paused){var p=v.play(); if(p&&p.catch)p.catch(function(){});} else {v.pause();}}catch(_){}} " +
                    "else {try{var btn=d.querySelector('.ytp-play-button, button[aria-label*=\"Play\"], button[aria-label*=\"Pause\"], button[title*=\"Play\"], button[title*=\"Pause\"]'); if(btn) btn.click();}catch(_){}}" +
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
                    "left" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var v=d.querySelector('video'); var k={key:'ArrowLeft',code:'ArrowLeft',keyCode:37,which:37,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} if(v){try{v.currentTime=Math.max(0,(v.currentTime||0)-5);}catch(_){}} }catch(_){}})();",
                    "right" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var v=d.querySelector('video'); var k={key:'ArrowRight',code:'ArrowRight',keyCode:39,which:39,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} if(v){try{var dur=isFinite(v.duration)?v.duration:(v.currentTime||0)+5; v.currentTime=Math.min(dur,(v.currentTime||0)+5);}catch(_){}} }catch(_){}})();",
                    "up" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var v=d.querySelector('video'); var k={key:'ArrowUp',code:'ArrowUp',keyCode:38,which:38,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} if(v){try{v.volume=Math.min(1,Math.max(0,(v.volume||0)+0.05));}catch(_){}} }catch(_){}})();",
                    "down" => "(function(){try{var d=document,ae=d.activeElement,tag=ae&&ae.tagName?ae.tagName.toUpperCase():''; if(ae&&(ae.isContentEditable||tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT'))return; var v=d.querySelector('video'); var k={key:'ArrowDown',code:'ArrowDown',keyCode:40,which:40,bubbles:true,cancelable:true,composed:true}; try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keydown',k));}catch(_){} try{(ae||d.body||d).dispatchEvent(new KeyboardEvent('keyup',k));}catch(_){} if(v){try{v.volume=Math.min(1,Math.max(0,(v.volume||0)-0.05));}catch(_){}} }catch(_){}})();",
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
                // Best-effort: forward arrow keys for video seek/volume control while browser/video continues off-TV.
                // (Game reportedly doesn't use these keys.)
                if (Input.GetKeyDown(KeyCode.LeftArrow))  SendDetachedWebVkTap(0x25);
                if (Input.GetKeyDown(KeyCode.RightArrow)) SendDetachedWebVkTap(0x27);
                if (Input.GetKeyDown(KeyCode.UpArrow))    SendDetachedWebVkTap(0x26);
                if (Input.GetKeyDown(KeyCode.DownArrow))  SendDetachedWebVkTap(0x28);
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

        private void UpdateInputOwnershipState()
        {
            try
            {
                bool urlFocused = _urlInput != null && _urlInput.isFocused;
                bool webFocused = _compositionWebFocused && !urlFocused;
                bool pointerOver = IsPointerOverVideoDisplay(out _, out _);
                // Clicking browser/URL bar area should not cause hotbar switching per spec; focus ownership tracks url/web focus.
                // Pointer alone is not treated as ownership unless focused, so clicking outside restores immediately on focus loss.
                UnderNetBrowserInputHooks.UpdateOwnership(IsOpen, urlFocused, webFocused, pointerOver);
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
                if (Input.GetKeyDown(KeyCode.V))
                {
                    try
                    {
                        // Best-effort: inject clipboard text into focused editable element in page (works even if browser clipboard APIs/permissions are limited),
                        // and also forward Ctrl+V keys as fallback.
                        bool injected = TryPasteClipboardTextIntoWeb();
                        SendCtrlComboToWeb(0x56); // 'V'
                        LogCompositionTypingDiag($"Ctrl+V to web injected={injected}");
                    }
                    catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Web Ctrl+V failed: {ex.Message}"); }
                    return;
                }
            }

            if (ctrl && Input.GetKeyDown(KeyCode.V))
            {
                bool urlFocused = _urlInput.isFocused;
                bool webFocused = _compositionWebFocused && !urlFocused;
                if (webFocused)
                {
                    // Non-composition fallback path (should be rare): avoid hijacking into address bar.
                    LogCompositionTypingDiag($"Ctrl+V ignored for address bar while webFocused (non-composition path)");
                    return;
                }

                try
                {
                    var clip = GUIUtility.systemCopyBuffer;
                    if (string.IsNullOrWhiteSpace(clip)) return;
                    var text = clip.Trim();
                    _urlInput.text = text;
                    _compositionWebFocused = false;
                    try { _urlInput.ActivateInputField(); _urlInput.caretPosition = text.Length; } catch { }
                    // Paste only into address bar when address bar (or non-web UI context) is the intended target.
                }
                catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Paste failed: {ex.Message}"); }
                return;
            }

            if (_urlInput.isFocused && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            {
                var text = _urlInput.text?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SetUrl(text);
                    _compositionWebFocused = true; // navigating to web content
                    try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
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
                // Also use SendText path (native may have additional handling).
                try { _gpuCompositionCapture.SendText(clip); } catch { }
                return true;
            }
            catch { return false; }
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

        private void HandleBrowserPointerInput()
        {
            if (_videoDisplay == null || !IsOpen || _gpuCompositionCapture == null)
            {
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                if (_gameMouseCaptured) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } _gameMouseCaptured = false; }
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

                int vw = Math.Max(1, _gpuCompositionCapture.ViewWidth);
                int vh = Math.Max(1, _gpuCompositionCapture.ViewHeight);

                bool urlFocusedNowChk = _urlInput != null && _urlInput.isFocused;
                bool gameModeActive = _gameInputMode && _compositionWebFocused && !urlFocusedNowChk;

                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out var local))
                {
                    if (_gameMouseCaptured) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } _gameMouseCaptured = false; }
                    if (mouseDown)
                    {
                        if (_compositionWebFocused) LogCompositionTypingDiag("webFocus cleared by click outside video rect");
                        _compositionWebFocused = false;
                        UpdateInputOwnershipState();
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
                    if (_browserPointerDown && mouseUp) { _browserPointerDown = false; _gpuCompositionCapture.SendMouseUp(0, 0, false); }
                    else if (!mouseHeld) _browserPointerDown = false;
                    if (_browserPointerRightDown && rUp) { _browserPointerRightDown = false; _gpuCompositionCapture.SendMouseUp(0, 0, true); }
                    else if (!rHeld) _browserPointerRightDown = false;
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
                if (!inside)
                {
                    // Click outside web content (e.g. chrome/address bar): allow UI interaction and stop routing keyboard to web.
                    if (mouseDown)
                    {
                        if (_compositionWebFocused) LogCompositionTypingDiag("webFocus cleared by click outside web content (chrome/address bar area)");
                        _compositionWebFocused = false;
                        UpdateInputOwnershipState();
                    }
                    if (_browserPointerDown && mouseUp) { _browserPointerDown = false; _gpuCompositionCapture.SendMouseUp(px, py, false); }
                    else if (!mouseHeld) _browserPointerDown = false;
                    if (_browserPointerRightDown && rUp) { _browserPointerRightDown = false; _gpuCompositionCapture.SendMouseUp(px, py, true); }
                    else if (!rHeld) _browserPointerRightDown = false;
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
                    try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
                    UpdateInputOwnershipState();
                    LogCompositionTypingDiag($"webClick inside px={px} py={py} wasWebFocused={wasWebFocused} urlFocused={(_urlInput != null && _urlInput.isFocused)}");
                    // Avoid forcing host/controller focus on every click: repeated MoveFocus/SetFocus can dismiss web popups/menus.
                    // Request focus only when transitioning into web focus.
                    if (!wasWebFocused)
                    {
                        try { _gpuCompositionCapture.RequestFocus(); } catch { }
                    }
                    // In game mode, help focus the actual game canvas/element so keyboard events reach it.
                    if (_gameInputMode)
                    {
                        try
                        {
                            _gpuCompositionCapture.ExecuteScript("(function(){try{var d=document,c=d.querySelector('canvas'); if(c){try{c.tabIndex=c.tabIndex||0;}catch(_){} try{if(typeof c.focus==='function') c.focus();}catch(_){}} try{d.body&&d.body.focus&&d.body.focus();}catch(_){} }catch(_){}})();");
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
                    try { if (_urlInput != null && _urlInput.isFocused) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
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

                if (mouseUp)
                {
                    if (_browserPointerDown) _gpuCompositionCapture.SendMouseUp(px, py, false);
                    _browserPointerDown = false;
                }
                else if (!mouseHeld && _browserPointerDown)
                {
                    _browserPointerDown = false;
                }
                if (rUp)
                {
                    if (_browserPointerRightDown) _gpuCompositionCapture.SendMouseUp(px, py, true);
                    _browserPointerRightDown = false;
                }
                else if (!rHeld && _browserPointerRightDown)
                {
                    _browserPointerRightDown = false;
                }

                var scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.001f)
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
            _chromeRT.anchorMin = new Vector2(0f, 0.915f);
            _chromeRT.anchorMax = new Vector2(1f, 1f);
            _chromeRT.offsetMin = Vector2.zero; _chromeRT.offsetMax = Vector2.zero;
            var chromeBg = _chromeRoot.AddComponent<Image>();
            chromeBg.color = new Color(0.11f, 0.12f, 0.15f, 0.98f);
            chromeBg.raycastTarget = true;

            var chromeLayout = _chromeRoot.AddComponent<HorizontalLayoutGroup>();
            chromeLayout.spacing = 5f;
            chromeLayout.childAlignment = TextAnchor.MiddleCenter;
            chromeLayout.childForceExpandWidth = false;
            chromeLayout.childForceExpandHeight = false;
            chromeLayout.childControlWidth = true;
            chromeLayout.childControlHeight = true;
            chromeLayout.padding = new RectOffset(6, 6, 4, 4);

            _backButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_BackBtn", "<", new Vector2(30, 26), new Color(0.22f, 0.24f, 0.28f, 1f), () => {
                _gpuCompositionCapture?.GoBack();
            }, 15);
            _forwardButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_FwdBtn", ">", new Vector2(30, 26), new Color(0.22f, 0.24f, 0.28f, 1f), () => {
                _gpuCompositionCapture?.GoForward();
            }, 15);
            _reloadButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_ReloadBtn", "R", new Vector2(30, 26), new Color(0.22f, 0.28f, 0.34f, 1f), () => {
                _gpuCompositionCapture?.Reload();
            }, 14);
            _homeButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_HomeBtn", "H", new Vector2(30, 26), new Color(0.18f, 0.30f, 0.22f, 1f), () => {
                SetUrl("undernet:home");
            }, 14);

            _urlInput = CreateInput(_chromeRoot.transform, "UnderNetBrowser_UrlInput", new Vector2(300, 28), "Search or enter URL");
            var urlLayout = _urlInput.GetComponent<LayoutElement>() ?? _urlInput.gameObject.AddComponent<LayoutElement>();
            urlLayout.flexibleWidth = 1f;
            urlLayout.minWidth = 160f;
            urlLayout.preferredWidth = 400f;
            urlLayout.minHeight = 28f;
            urlLayout.preferredHeight = 28f;
            urlLayout.flexibleHeight = 0f;
            // Reliable visible URL label overlay (InputField text rendering has been unreliable in this TV UI context).
            try
            {
                var overlayGo = new GameObject("UnderNetBrowser_UrlDisplayOverlay");
                overlayGo.transform.SetParent(_urlInput.transform, false);
                var ort = overlayGo.AddComponent<RectTransform>();
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one;
                ort.offsetMin = new Vector2(10, 2); ort.offsetMax = new Vector2(-10, -2);
                _urlDisplayText = overlayGo.AddComponent<Text>();
                _urlDisplayText.font = GetUIFont();
                _urlDisplayText.fontSize = 14;
                _urlDisplayText.color = new Color(0.95f, 0.96f, 0.98f, 1f);
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
                if (_urlInput.textComponent != null) { _urlInput.textComponent.text = homeDisplay; _urlInput.textComponent.color = new Color(0.95f, 0.96f, 0.98f, 1f); }
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
                irt.sizeDelta = new Vector2(120, 24);
                var ile = _gameModeIndicatorRoot.AddComponent<LayoutElement>();
                ile.preferredWidth = 120; ile.minWidth = 110; ile.preferredHeight = 24; ile.minHeight = 24; ile.flexibleWidth = 0; ile.flexibleHeight = 0;
                var ibg = _gameModeIndicatorRoot.AddComponent<Image>();
                ibg.color = new Color(0.18f, 0.55f, 0.24f, 0.95f);
                ibg.raycastTarget = false;
                var igo = new GameObject("Text");
                igo.transform.SetParent(_gameModeIndicatorRoot.transform, false);
                var itrt = igo.AddComponent<RectTransform>();
                itrt.anchorMin = Vector2.zero; itrt.anchorMax = Vector2.one; itrt.offsetMin = new Vector2(6, 1); itrt.offsetMax = new Vector2(-6, -1);
                _gameModeIndicatorText = igo.AddComponent<Text>();
                _gameModeIndicatorText.font = GetUIFont();
                _gameModeIndicatorText.fontSize = 12;
                _gameModeIndicatorText.color = Color.white;
                _gameModeIndicatorText.alignment = TextAnchor.MiddleCenter;
                _gameModeIndicatorText.supportRichText = false;
                _gameModeIndicatorText.raycastTarget = false;
                _gameModeIndicatorText.text = "GAME INPUT (F6)";
                _gameModeIndicatorRoot.SetActive(false);
            }
            catch { }

            _closeButton = CreateButton(_chromeRoot.transform, "UnderNetBrowser_CloseBtn", "X", new Vector2(30, 26), new Color(0.45f, 0.16f, 0.18f, 1f), CloseAppToTvHome, 15);

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
                if (_videoRT != null)
                {
                    // Minimal edge gaps to better fit TV screen; chrome occupies top strip when visible.
                    if (showChrome)
                    {
                        _videoRT.anchorMin = new Vector2(0.002f, 0.002f);
                        _videoRT.anchorMax = new Vector2(0.998f, 0.913f);
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
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] UpdateChromeVisibility failed: {ex.Message}"); }
        }

        // Composition mode polls page fullscreen state during OnUpdate to hide/show chrome.

        private void CloseAppToTvHome()
        {
            try
            {
                MelonLogger.Msg("[Firesky UnderNet Browser] [UnderNetBrowser] Close requested (X).");
                _explicitCloseRequested = true;
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
                            m.Invoke(this, null);
                            return;
                        }
                    }
                    catch { }
                }
                // Fallback: treat as close callback (will at least stop our browser/HUD restore) if framework doesn't expose explicit close.
                try { OnClosed(); } catch { }
                UnderNetBrowserConfig.Log("[UnderNetBrowser] No TVApp close method found via reflection; performed local close cleanup only.");
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Close failed: {ex.Message}"); }
            finally { _explicitCloseRequested = false; }
        }

        private bool _detachRequested;
        private float _detachRequestedTime;
        private bool _explicitCloseRequested;
        private float _lastDetachedReattachCheckRealtime;
        private bool _detachedTvInterfaceWasOpen;
        private float _lastDetachedVisualMaintainRealtime;
        private float _lastDetachedExitEnsureRealtime;
        private float _lastTvScreenMirrorAttemptRealtime;
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

        // When detached to gameplay, do not consume S1API TVApp exit handling (it would mark ExitAction.Used and block pause menu).
        internal static bool TVAppHandleExitPrefix(object __instance)
        {
            try
            {
                if (__instance is UnderNetBrowserApp app)
                {
                    if (app._detachedToGameplay)
                        return false; // skip HandleExit so ESC can reach pause menu/game while app stays running
                }
            }
            catch { }
            return true;
        }

        internal static bool TVAppClosePrefix(object __instance)
        {
            try
            {
                if (__instance is UnderNetBrowserApp app)
                {
                    // Explicit X-button close should proceed.
                    if (app._explicitCloseRequested) return true;

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
                _detachRequested = true;
                _detachRequestedTime = Time.realtimeSinceStartup;
                AnyDetachActive = true;
                _detachedToGameplay = true;
                // Keep rendering active while detached so video playback remains visible on TV (audio already continues).
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
                _compositionWebFocused = false;
                _browserPointerDown = false;
                _browserPointerRightDown = false;
                try { ReleaseAllGameKeys("detach_to_gameplay"); } catch { }
                try { UnderNetBrowserDiagnostics.OnDetachRequested(); } catch { }
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Right-click detach: keeping app open and attempting to exit TV mode (gameplay restore).");

                // Release gameplay/UI ownership immediately (similar to paused state) without pausing app rendering.
                // Pausing the S1API TV app can freeze visual updates while audio continues.
                try { UnderNetBrowserInputHooks.UpdateOwnership(false, false, false, false); } catch { }
                try { TryRestoreGameInput(); } catch { }
                try { TryRestoreGameHud(); } catch { }
                try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch { }

                // Best-effort: exit TV interaction/interface so player returns to gameplay/camera/controls.
                TryExitTvInterfaceBestEffort();

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

                // Prefer instance's actual type for invocation to avoid IL2CPP proxy type mismatches.
                Type actualType = ifaceObj.GetType();
                var candidateTypes = new List<Type>();
                if (actualType != null) candidateTypes.Add(actualType);
                if (ifaceType != null && ifaceType != actualType) candidateTypes.Add(ifaceType);

                foreach (var t in candidateTypes)
                {
                    try
                    {
                        var isOpenProp = t.GetProperty("IsOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (isOpenProp != null)
                        {
                            var v = isOpenProp.GetValue(ifaceObj);
                            if (v is bool b && !b)
                                UnderNetBrowserConfig.Log("[UnderNetBrowser] Detach: TVInterface.IsOpen is false (continuing best-effort RemovePlayer).");
                        }
                    }
                    catch { }

                    try
                    {
                        var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        foreach (var mi in methods)
                        {
                            if (mi.Name != "RemovePlayer") continue;
                            var ps = mi.GetParameters();
                            if (ps.Length != 1) continue;
                            bool argOk = false;
                            try { argOk = ps[0].ParameterType.IsInstanceOfType(localPlayer); } catch { argOk = false; }
                            try
                            {
                                mi.Invoke(ifaceObj, new[] { localPlayer });
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {t.FullName ?? t.Name}.RemovePlayer(localPlayer) for detach (argMatch={argOk}).");
                                return true;
                            }
                            catch (Exception ex)
                            {
                                UnderNetBrowserConfig.Log($"[UnderNetBrowser] {t.FullName ?? t.Name}.RemovePlayer invoke failed (argMatch={argOk}): {ex.Message}");
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
                        var mi = t.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (mi == null) continue;
                        mi.Invoke(ifaceObj, null);
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Invoked {t.FullName ?? t.Name}.Close() for detach (game interaction exit).");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] {t.FullName ?? t.Name}.Close() invoke failed: {ex.Message}");
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

                    // Try on actual component instance and also any IL2CPP-cast interface/interactable view.
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
                                    if (mi.Name != "RemovePlayer") continue;
                                    var ps = mi.GetParameters();
                                    if (ps.Length != 1) continue;
                                    bool argOk = false;
                                    try { argOk = ps[0].ParameterType.IsInstanceOfType(localPlayer); } catch { argOk = false; }
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

                        // Also attempt Close() to reliably restore gameplay/camera/controls (may return TV to home/lock screen visually).
                        try
                        {
                            var miClose = t.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (miClose != null)
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

        private bool TryReattachToTvIfResumedWhileDetached()
        {
            try
            {
                if (!_detachedToGameplay || !IsOpen) return false;
                if (Time.realtimeSinceStartup - _lastDetachedReattachCheckRealtime < 0.25f) return false;
                _lastDetachedReattachCheckRealtime = Time.realtimeSinceStartup;

                // Detect game TV interaction resumed.
                // After RemovePlayer-only detach the interface may remain open/visible; prefer player-presence over IsOpen alone.
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
                try { if (_gpuCompositionCapture != null) _gpuCompositionCapture.RenderingSuspended = false; } catch { }
                UnderNetBrowserConfig.LogAlways("[UnderNetBrowser] Re-attaching to TV interaction after detached gameplay resume (restoring input ownership).");
                try { RestoreDetachedHomeScreenObjects(); } catch { }
                try { SetAppInteractionEnabled(true); } catch { }
                try { UnderNetBrowserInputHooks.EnsureInstalled(); } catch { }
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                try { TryHideGameHud(); } catch { }
                try { TryDisableGameInput(); } catch { }
                try { UpdateInputOwnershipState(); } catch { }
                try { _gpuCompositionCapture?.RequestFocus(); } catch { }
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

        private void RestoreDetachedHomeScreenObjects()
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
            finally { _detachedHiddenHomeScreenObjects.Clear(); }

            try
            {
                for (int i = _detachedHiddenClockRenderers.Count - 1; i >= 0; i--)
                {
                    var item = _detachedHiddenClockRenderers[i];
                    try { if (item.renderer != null) item.renderer.enabled = item.wasEnabled; } catch { }
                }
            }
            catch { }
            finally { _detachedHiddenClockRenderers.Clear(); }

            _detachedVisualTargetsScanned = false;
            try { RestoreTvScreenMirror(); } catch { }
        }

        private bool _tvScreenMirrorNoCandidateLogged;
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
                    UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] TV screen mirror (detached): applied composition texture to {applied.Count} renderer(s): {string.Join(", ", applied)}");
                else if (!_tvScreenMirrorNoCandidateLogged)
                {
                    _tvScreenMirrorNoCandidateLogged = true;
                    // Keep a single short debug note; avoid large per-detach renderer dumps.
                    UnderNetBrowserConfig.Log("[UnderNetBrowser] TV screen mirror: no suitable screen renderer candidates found.");
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
            _detachedVisualTargetsScanned = false;
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
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

                var host = uri.Host ?? "";
                bool isYouTube = host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                    || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
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

                string? url = null;
                try { url = _gpuCompositionCapture?.CurrentUrl; } catch { url = null; }

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

                // Keep a friendly label for our built-in homepage file.
                var display = url;
                try
                {
                    var homeFile = Path.Combine(GetUserDataRoot(), "Home", "home.html");
                    if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        var local = new Uri(url).LocalPath;
                        if (string.Equals(Path.GetFullPath(local), Path.GetFullPath(homeFile), StringComparison.OrdinalIgnoreCase))
                            display = "undernet:home";
                    }
                }
                catch { }

                display = CleanDisplayUrl(display);
                if (string.IsNullOrWhiteSpace(display)) display = GetHomeDisplay();
                if (string.Equals(_lastSyncedUrl, display, StringComparison.Ordinal) && !string.IsNullOrEmpty(_urlInput.text))
                {
                    SetUrlDisplayText(display);
                    return;
                }
                _lastSyncedUrl = display;
                if (!string.Equals(_urlInput.text, display, StringComparison.Ordinal))
                {
                    _urlInput.text = display;
                    if (_urlInput.textComponent != null) _urlInput.textComponent.text = display;
                    try { _urlInput.ForceLabelUpdate(); } catch { }
                }
                SetUrlDisplayText(display);
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
            try
            {
                string[] memberNames = { "CanChangeEquippedItem", "canChangeEquippedItem", "EquippingEnabled", "equippingEnabled", "HotbarEnabled", "hotbarEnabled", "ScrollEnabled", "scrollEnabled" };
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = "";
                    try { an = asm.GetName().Name ?? ""; } catch { an = ""; }
                    // Limit expensive GetTypes() scan to relevant assemblies only.
                    if (an.IndexOf("ScheduleOne", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("S1API", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Il2Cpp", StringComparison.OrdinalIgnoreCase) < 0 &&
                        an.IndexOf("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
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

                            // Try static members
                            foreach (var m in memberNames)
                            {
                                if (captureState) TrySetStaticBoolByTypeName(tn, m, false);
                                else
                                {
                                    var p = t.GetProperty(m, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) { try { p.SetValue(null, false); } catch { } }
                                    var f = t.GetField(m, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (f != null && f.FieldType == typeof(bool)) { try { f.SetValue(null, false); } catch { } }
                                }
                            }

                            // Try singleton-like instances (Instance/instance/Local)
                            object? inst = null;
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
                                    if (inst != null) break;
                                }
                                catch { }
                            }
                            if (inst == null) continue;
                            foreach (var m in memberNames)
                            {
                                if (captureState) TrySetBoolMember(inst, m, false);
                                else
                                {
                                    var p = inst.GetType().GetProperty(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) { try { p.SetValue(inst, false); } catch { } }
                                    var f = inst.GetType().GetField(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (f != null && f.FieldType == typeof(bool)) { try { f.SetValue(inst, false); } catch { } }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
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
                Type? invType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        invType = asm.GetType("Il2CppScheduleOne.PlayerScripts.PlayerInventory", false)
                               ?? asm.GetType("ScheduleOne.PlayerScripts.PlayerInventory", false);
                        if (invType != null) break;
                    }
                    catch { }
                }
                if (invType == null)
                {
                    // fallback scan
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
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(typeName, false); if (t != null) return t; } catch { }
            }
            return null;
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
                Type? t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType(typeName, false); if (t != null) break; } catch { }
                }
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
                Type? t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType(typeName, false); if (t != null) break; } catch { }
                }
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
            img.color = bg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = Color.Lerp(bg, Color.white, 0.12f);
            colors.pressedColor = Color.Lerp(bg, Color.black, 0.15f);
            colors.selectedColor = colors.highlightedColor;
            btn.colors = colors;
            EventHelper.AddListener(onClick, btn.onClick);
            var txt = CreateText(go, label, fontSize, Color.white);
            txt.alignment = TextAnchor.MiddleCenter;
            return btn;
        }

        private InputField CreateInput(Transform parent, string name, Vector2 size, string placeholder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.17f, 0.20f, 1f);
            img.raycastTarget = true;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.08f);
            outline.effectDistance = new Vector2(1, -1);

            var input = go.AddComponent<InputField>();
            input.targetGraphic = img;
            input.transition = Selectable.Transition.ColorTint;
            input.interactable = true;
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = InputField.ContentType.Standard;
            input.keyboardType = TouchScreenKeyboardType.Default;
            input.characterLimit = 2048;
            input.caretColor = new Color(0.95f, 0.96f, 0.98f, 1f);
            input.customCaretColor = true;
            input.selectionColor = new Color(0.30f, 0.55f, 0.95f, 0.45f);
            input.caretBlinkRate = 0.85f;
            input.caretWidth = 2;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(10, 2); trt.offsetMax = new Vector2(-10, -2);
            var txt = textGo.AddComponent<Text>();
            txt.font = GetUIFont(); txt.fontSize = 14; txt.color = new Color(0.95f, 0.96f, 0.98f, 1f); txt.alignment = TextAnchor.MiddleLeft; txt.supportRichText = false; txt.raycastTarget = false; txt.horizontalOverflow = HorizontalWrapMode.Overflow; txt.verticalOverflow = VerticalWrapMode.Truncate;
            input.textComponent = txt;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var prt = phGo.AddComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.offsetMin = new Vector2(10, 2); prt.offsetMax = new Vector2(-10, -2);
            var ptxt = phGo.AddComponent<Text>();
            ptxt.font = GetUIFont(); ptxt.fontSize = 13; ptxt.fontStyle = FontStyle.Italic; ptxt.color = new Color(0.62f, 0.65f, 0.70f, 1f); ptxt.alignment = TextAnchor.MiddleLeft; ptxt.supportRichText = false; ptxt.raycastTarget = false; ptxt.text = placeholder ?? "Search or enter URL";
            input.placeholder = ptxt;

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
                var seen = new HashSet<int>();
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
                            if (!seen.Add(id)) continue;
                            _hiddenHudObjects.Add((go, go.activeSelf));
                            if (go.activeSelf) go.SetActive(false);
                            UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] Hid HUD object candidate: {cn} (canvas={c.gameObject.name})");
                        }
                    }
                    catch { }
                }
                // Skip expensive full-scene GameObject scan on normal paths (canvas-child scan above is sufficient and cheaper).
                // Full scan caused noticeable hitches on reattach/open in some scenes. Keep only if explicitly needed in future.
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] HUD hide: {_hiddenHudObjects.Count} object(s) tracked.");
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
                    canvas.worldCamera = Camera.main;
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: set worldCamera=Camera.main");
                }

                if (!badScale && !badSize)
                {
                    _repairStateLoggedBad = false;
                    return;
                }

                // Keep repair behavior, but avoid spamming logs when the same bad state persists.
                if (!_repairStateLoggedBad || (Time.realtimeSinceStartup - _lastRepairDonorSearch >= 60f))
                {
                    _lastRepairDonorSearch = Time.realtimeSinceStartup;
                    _repairStateLoggedBad = true;
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: detected badScale={badScale}(mag={scaleMag:F6}) badSize={badSize} rootRT={(rootRt != null ? rootRt.rect.size.ToString() : "null")} - applying conservative fallback only (no donor size/scale copy).");
                }

                if (badSize && rootRt != null)
                {
                    // Keep existing anchors/pivot if sane; only ensure non-zero size.
                    if (rootRt.rect.width < 1f || rootRt.rect.height < 1f)
                        rootRt.sizeDelta = new Vector2(685f, 400f);
                }
                if (badScale)
                {
                    // Avoid forcing arbitrary world scale; prefer minimal local correction if parent not degenerate.
                    var p = appRoot.transform.parent;
                    var pLossy = p != null ? p.lossyScale : Vector3.one;
                    bool parentZeroish = Math.Abs(pLossy.x) < 0.0001f || Math.Abs(pLossy.y) < 0.0001f;
                    if (!parentZeroish)
                        appRoot.transform.localScale = Vector3.one;
                    else
                        UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label}: parent lossy scale near-zero; cannot safely correct lossy scale here.");
                }
            }
            catch (Exception ex) { UnderNetBrowserConfig.Log($"[UnderNetBrowser] Repair {label} failed: {ex.Message}"); }
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
                var asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var asmDir = string.IsNullOrEmpty(asmPath) ? "" : System.IO.Path.GetDirectoryName(asmPath);
                string[] candidates = new[]
                {
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "..", "UnderNetIcon.png"),
                    string.IsNullOrEmpty(asmDir) ? null : System.IO.Path.Combine(asmDir, "..", "..", "UnderNetIcon.png"),
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "Mods", "UnderNetIcon.png"),
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "UnderNetIcon.png")
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
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                var pixels = new Color32[64 * 64];
                var bg = new Color32(30, 30, 40, 255);
                var fg = new Color32(220, 60, 60, 255);
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    bool border = x < 4 || x > 59 || y < 4 || y > 59;
                    bool play = (x > 22 && x < 44 && y > 18 && y < 46 && (x - 22) < (y - 18) && (x - 22) < (46 - y));
                    pixels[y * 64 + x] = play ? fg : (border ? new Color32(80, 80, 90, 255) : bg);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                return Sprite.Create(tex, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f));
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
        private void UpdateSpatialAudioPlayback()
        {
            try
            {
                bool poc = false;
                try { poc = UnderNetBrowserConfig.SpatialAudioPocEnabled?.Value == true; } catch { poc = false; }
                if (!_gpuCompositionMode || _gpuCompositionCapture == null || !IsOpen || !poc)
                {
                    if (_spatialAudioStarted || _spatialAudioSource != null || _spatialAudioClip != null)
                        StopSpatialAudioPlayback(poc ? "inactive" : "disabled");
                    return;
                }

                uint sr = 0, ch = 0;
                int active = 0;
                try { UnderNetBrowserGpuNative.CompGetSpatialAudioFormat(out sr, out ch); } catch { sr = 0; ch = 0; }
                try { active = UnderNetBrowserGpuNative.CompIsSpatialCaptureActive(); } catch { active = 0; }

                // Update emitter position to follow TV while detached/open.
                TryUpdateSpatialAudioEmitterTransform();

                if (active == 0 || sr == 0 || ch == 0)
                {
                    // Not capturing yet (deferred start/pending/failed). Keep current playback stopped until active+format ready.
                    if (_spatialAudioStarted) StopSpatialAudioPlayback("capture-not-active");
                    return;
                }

                if (ch > 8) ch = 2;
                if (_spatialAudioStarted && _spatialAudioSource != null && _spatialAudioClip != null && _spatialAudioRate == sr && _spatialAudioChannels == ch)
                {
                    if (!_spatialAudioSource.isPlaying) { try { _spatialAudioSource.Play(); } catch { } }
                    return;
                }

                // (Re)create streaming clip/source when format becomes available or changes.
                if (Time.realtimeSinceStartup - _lastSpatialAudioSetupAttempt < 0.5f) return;
                _lastSpatialAudioSetupAttempt = Time.realtimeSinceStartup;
                StopSpatialAudioPlayback("reinit");
                StartSpatialAudioPlayback(sr, ch);
            }
            catch (Exception ex)
            {
                // Non-fatal; preserve browser behavior.
                try { UnderNetBrowserConfig.Log($"[UnderNetBrowser] [SpatialPoC] UpdateSpatialAudioPlayback error: {ex.Message}"); } catch { }
            }
        }

        private void StartSpatialAudioPlayback(uint sampleRate, uint channels)
        {
            try
            {
                if (sampleRate == 0 || channels == 0) return;
                var emitter = TryGetOrCreateSpatialAudioEmitter();
                if (emitter == null) return;

                int clipSamples = (int)(sampleRate / 2); // ~0.5s ring for streaming callback
                if (clipSamples < 1024) clipSamples = 1024;
                _spatialAudioReadBuffer = new float[4096 * (int)channels];
                _spatialAudioRate = sampleRate;
                _spatialAudioChannels = channels;
                // IL2CPP Unity bindings may expect Il2Cpp array delegate types
                _spatialAudioClip = AudioClip.Create("UnderNetSpatialPoc", clipSamples, (int)channels, (int)sampleRate, true, (Action<Il2CppStructArray<float>>)OnSpatialAudioRead, (Action<int>)OnSpatialAudioSetPosition);
                _spatialAudioSource = emitter.GetComponent<AudioSource>();
                if (_spatialAudioSource == null) _spatialAudioSource = emitter.AddComponent<AudioSource>();
                ConfigureSpatialAudioSource(_spatialAudioSource);
                _spatialAudioSource.clip = _spatialAudioClip;
                _spatialAudioSource.loop = true;
                _spatialAudioSource.Play();
                _spatialAudioStarted = true;
                UnderNetBrowserConfig.LogAlways($"[UnderNetBrowser] [SpatialPoC] Spatial playback started ({sampleRate}Hz, {channels}ch) emitter='{emitter.name}'.");
            }
            catch (Exception ex)
            {
                UnderNetBrowserConfig.Log($"[UnderNetBrowser] [SpatialPoC] StartSpatialAudioPlayback failed: {ex.Message}");
                StopSpatialAudioPlayback("start-failed");
            }
        }

        private void StopSpatialAudioPlayback(string reason)
        {
            try
            {
                if (_spatialAudioSource != null)
                {
                    try { _spatialAudioSource.Stop(); } catch { }
                    try { _spatialAudioSource.clip = null; } catch { }
                }
                if (_spatialAudioClip != null)
                {
                    try { UnityEngine.Object.Destroy(_spatialAudioClip); } catch { }
                }
                _spatialAudioClip = null;
                _spatialAudioStarted = false;
                _spatialAudioRate = 0;
                _spatialAudioChannels = 0;
                _spatialAudioReadBuffer = null;
                // Keep emitter/source GameObject for reuse; disable source.
                try { if (_spatialAudioSource != null) _spatialAudioSource.enabled = false; } catch { }
                // Destroy emitter when fully stopping browser/disable context
                if (reason.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("shutdown", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("destroy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { if (_spatialAudioGo != null) UnityEngine.Object.Destroy(_spatialAudioGo); } catch { }
                    _spatialAudioGo = null;
                    _spatialAudioSource = null;
                }
                if (!string.IsNullOrEmpty(reason))
                    UnderNetBrowserConfig.Log($"[UnderNetBrowser] [SpatialPoC] Spatial playback stopped ({reason}).");
            }
            catch { }
        }

        private void ConfigureSpatialAudioSource(AudioSource src)
        {
            try
            {
                src.enabled = true;
                src.playOnAwake = false;
                src.loop = true;
                src.spatialBlend = 1.0f; // 3D
                src.dopplerLevel = 0f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 0.75f;
                src.maxDistance = 18f;
                src.spread = 0f;
                src.volume = 1.0f;
                src.priority = 64;
                src.mute = false;
                try { src.bypassEffects = false; } catch { }
                try { src.bypassListenerEffects = false; } catch { }
                try { src.bypassReverbZones = false; } catch { }
            }
            catch { }
        }

        private GameObject? TryGetOrCreateSpatialAudioEmitter()
        {
            try
            {
                var tvTransform = TryResolveTvAudioTransform();
                if (_spatialAudioGo == null)
                {
                    _spatialAudioGo = new GameObject("FireskyUnderNet_SpatialAudioEmitter");
                    try { UnityEngine.Object.DontDestroyOnLoad(_spatialAudioGo); } catch { }
                }
                if (tvTransform != null)
                {
                    try { _spatialAudioGo.transform.SetParent(tvTransform, false); _spatialAudioGo.transform.localPosition = Vector3.zero; } catch { try { _spatialAudioGo.transform.position = tvTransform.position; } catch { } }
                }
                else
                {
                    // Fallback: place near camera (not true TV spatial, but better than silence) if TV not found.
                    try
                    {
                        var cam = Camera.main;
                        if (cam != null) _spatialAudioGo.transform.position = cam.transform.position + cam.transform.forward * 1.5f;
                    }
                    catch { }
                }
                if (_spatialAudioSource == null)
                {
                    _spatialAudioSource = _spatialAudioGo.GetComponent<AudioSource>() ?? _spatialAudioGo.AddComponent<AudioSource>();
                    ConfigureSpatialAudioSource(_spatialAudioSource);
                }
                return _spatialAudioGo;
            }
            catch { return null; }
        }

        private void TryUpdateSpatialAudioEmitterTransform()
        {
            try
            {
                if (_spatialAudioGo == null) return;
                var tvTransform = TryResolveTvAudioTransform();
                if (tvTransform == null) return;
                // Re-parent/follow TV if it moved/recreated (best-effort)
                if (_spatialAudioGo.transform.parent != tvTransform)
                {
                    try { _spatialAudioGo.transform.SetParent(tvTransform, false); _spatialAudioGo.transform.localPosition = Vector3.zero; }
                    catch { try { _spatialAudioGo.transform.position = tvTransform.position; } catch { } }
                }
            }
            catch { }
        }

        private Transform? TryResolveTvAudioTransform()
        {
            try
            {
                // Prefer a likely physical TV/screen transform for spatial origin.
                string[] rootNames = { "TV_Built(Clone)/TVInterface", "TV_Built/TVInterface", "TVInterface" };
                foreach (var path in rootNames)
                {
                    try
                    {
                        var go = GameObject.Find(path);
                        if (go != null) return go.transform;
                    }
                    catch { }
                }
                // Fallback: search for TVInterface object name
                try
                {
                    var go = GameObject.Find("TVInterface");
                    if (go != null) return go.transform;
                }
                catch { }
                // Fallback: common TV built root
                foreach (var n in new[] { "TV_Built(Clone)", "TV_Built", "TV" })
                {
                    try { var go = GameObject.Find(n); if (go != null) return go.transform; } catch { }
                }
            }
            catch { }
            return null;
        }

        private void OnSpatialAudioRead(Il2CppStructArray<float> data)
        {
            try
            {
                if (data == null || data.Length == 0) return;
                int channels = (int)(_spatialAudioChannels == 0 ? 2 : _spatialAudioChannels);
                if (channels <= 0) channels = 2;
                int framesWanted = data.Length / channels;
                if (framesWanted <= 0) { for (int i = 0; i < data.Length; i++) data[i] = 0f; return; }

                int need = framesWanted * channels;
                if (_spatialAudioReadBuffer == null || _spatialAudioReadBuffer.Length < need)
                    _spatialAudioReadBuffer = new float[Math.Max(need, 4096 * channels)];

                uint got = 0;
                try
                {
                    got = UnderNetBrowserGpuNative.CompReadSpatialAudioSamples(_spatialAudioReadBuffer, (uint)framesWanted);
                }
                catch { got = 0; }

                int gotSamples = (int)got * channels;
                if (gotSamples > data.Length) gotSamples = data.Length;
                for (int i = 0; i < gotSamples; i++) data[i] = _spatialAudioReadBuffer[i];
                for (int i = gotSamples; i < data.Length; i++) data[i] = 0f;
            }
            catch
            {
                try { if (data != null) for (int i = 0; i < data.Length; i++) data[i] = 0f; } catch { }
            }
        }

        private void OnSpatialAudioSetPosition(int newPosition)
        {
            // Streaming ring-buffer source: position resets are not meaningful; ignore.
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
            return url.IndexOf("discord.com/app", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("discord.com/channels", StringComparison.OrdinalIgnoreCase) >= 0
                || url.Equals("undernet:discord", StringComparison.OrdinalIgnoreCase);
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
                    // Try to restore browser home
                    try { EnsureCompositionBrowserStarted(); } catch { }
                    return false;
                }

                _externalMirrorHwnd = target.Hwnd;
                _externalMirrorMode = true;
                _externalMirrorLabel = string.IsNullOrWhiteSpace(target.Title) ? "Discord" : target.Title;
                try { if (_videoDisplay != null && _externalWindowCapture.Texture != null) _videoDisplay.texture = _externalWindowCapture.Texture; } catch { }
                try { SetUrlDisplayText($"discord:mirror ({_externalMirrorLabel})"); } catch { }
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

        private void EnsureCompositionBrowserStarted()
        {
            try
            {
                if (_gpuCompositionCapture != null) return;
                _gpuCompositionMode = false;
                // Reuse same composition profile/path as normal startup to avoid creating a duplicate WebView2 user data folder.
                var home = GetResolvedHomeUrl();
                string compUserData = "";
                try { compUserData = Path.Combine(GetUserDataRoot(), "WebView2Composition"); } catch { compUserData = ""; }
                try { EnsureDefaultBrowserExtensionDeployed(compUserData); } catch { }
                _gpuCompositionCapture = new UnderNetBrowserCompositionCapture();
                int compW = 1920, compH = 1080;
                try { compW = Mathf.Clamp(UnderNetBrowserConfig.GpuCompositionRenderWidth?.Value ?? 1920, 854, 2560); } catch { }
                try { compH = Mathf.Clamp(UnderNetBrowserConfig.GpuCompositionRenderHeight?.Value ?? 1080, 480, 1440); } catch { }
                bool ok = false;
                try { ok = _gpuCompositionCapture.TryStart(home, compW, compH, compUserData); } catch { ok = false; }
                if (ok)
                {
                    _gpuCompositionMode = true;
                    try { if (_videoDisplay != null && _gpuCompositionCapture.Texture != null) _videoDisplay.texture = _gpuCompositionCapture.Texture; } catch { }
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
                        EnsureCompositionBrowserStarted();
                        try { SetUrl(string.IsNullOrWhiteSpace(ret) ? "undernet:home" : ret); } catch { }
                    }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

    }
}


