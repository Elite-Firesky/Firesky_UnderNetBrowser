using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using MelonLoader;
using Microsoft.Web.WebView2.Core;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Stage B proof-of-concept: host a WebView2 controller attached to a hidden HWND,
    /// navigate, capture frames via CapturePreview and upload to a Unity Texture2D.
    /// CPU-based capture (not GPU-shared) - simplest reliable path for initial in-game proof.
    /// </summary>
    internal sealed class UnderNetBrowserWebView : IDisposable
    {
        private IntPtr _hwnd;
        private CoreWebView2Environment? _env;
        private CoreWebView2Controller? _controller;
        private CoreWebView2CompositionController? _compController;
        private CoreWebView2? _core;
        private Texture2D? _texture;
        private bool _initStarted;
        private bool _ready;
        private bool _disposed;
        private float _lastCapture;
        private bool _capturePending;
        private string? _pendingNavigate;
        private int _width;
        private int _height;
        private bool _gpuCaptureHostMode; // 4B: standard controller on capturable HWND (no CDP screencast/JPEG pipeline)
        private IntPtr _inputTargetHwnd; // Child HWND for standard controller input routing (WebView2 renders in child windows)
        private bool _visibilityStrategyApplied;
        private float _lastHostWindowLog;

        private readonly object _frameLock = new object();
        private byte[]? _pendingFrameBytes;
        private int _captureSuccessCount;
        private int _uploadedFrameCount;
        private float _lastUploadLogTime;
        private float _lastFrameUploadRealtime;

        // GPU-spike path: CompositionController + CDP screencast (higher-FPS frame delivery than CapturePreviewAsync).
        // Frames are still decoded/uploaded on CPU in Unity (no zero-copy D3D11 shared texture yet).
        private bool _useScreencast;
        private CoreWebView2DevToolsProtocolEventReceiver? _screencastReceiver;

        public Texture2D? Texture => _texture;
        public bool IsReady => _ready;
        public string? LastError { get; private set; }
        public string? CurrentUrl { get; private set; }
        public int ViewWidth => _width;
        public int ViewHeight => _height;

        public IntPtr HostWindowHandle => _hwnd;

        public void Start(int width, int height, string initialUrl) => Start(width, height, initialUrl, gpuCaptureHostMode: false);

        public void Start(int width, int height, string initialUrl, bool gpuCaptureHostMode)
        {
            if (_initStarted) return;
            _initStarted = true;
            _gpuCaptureHostMode = gpuCaptureHostMode;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _pendingNavigate = initialUrl;

            // In GPU/WGC capture mode, frames are rendered by native capture into a Unity-owned texture; skip JPEG screencast texture.
            if (!_gpuCaptureHostMode)
            {
                try
                {
                    _texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                    try { _texture.filterMode = FilterMode.Bilinear; _texture.wrapMode = TextureWrapMode.Clamp; } catch { }
                    // Fill with dark color so it's visible when assigned
                    var fill = new Color32(20, 20, 20, 255);
                    var pixels = new Color32[_width * _height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
                    _texture.SetPixels32(pixels);
                    _texture.Apply(false, false);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Failed to create Texture2D: {ex.Message}");
                }
            }

            _ = InitializeAsync(initialUrl);
        }

        private async Task InitializeAsync(string initialUrl)
        {
            try
            {
                MelonLogger.Msg(_gpuCaptureHostMode
                    ? "[Firesky UnderNet Browser] [StageB/WebView2] Creating WebView2 host window (GPU/WGC capture mode)..."
                    : "[Firesky UnderNet Browser] [StageB/WebView2] Creating hidden host window...");
                _hwnd = CreateHiddenWindow(_width, _height, showWindow: _gpuCaptureHostMode);
                if (_hwnd == IntPtr.Zero)
                {
                    LastError = "Failed to create host HWND (err=" + Marshal.GetLastWin32Error() + ")";
                    MelonLogger.Warning("[Firesky UnderNet Browser] [StageB/WebView2] " + LastError);
                    return;
                }
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Host HWND=0x{_hwnd.ToInt64():X} gpuCapture={_gpuCaptureHostMode}");

                string userDataFolder;
                try
                {
                    var ud = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
                    userDataFolder = Path.Combine(string.IsNullOrWhiteSpace(ud) ? AppContext.BaseDirectory : ud, "UnderNetBrowser", "WebView2", "UserData");
                }
                catch
                {
                    userDataFolder = Path.Combine(AppContext.BaseDirectory, "UnderNetBrowser_WebView2_UserData");
                }
                Directory.CreateDirectory(userDataFolder);

                MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Creating CoreWebView2Environment...");
                _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                if (_disposed) return;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Environment ready. BrowserVersion='{_env.BrowserVersionString}'");

                // 4B GPU capture mode: standard controller rendered into HWND so WGC can capture actual browser content.
                // (CompositionController without DComp RootVisualTarget is not reliably capturable as window content.)
                // Existing screencast/JPEG path remains default fallback.
                if (_gpuCaptureHostMode)
                {
                    MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Creating CoreWebView2Controller (GPU/WGC capture host)...");
                    _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd);
                    _compController = null;
                }
                else
                {
                    MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Creating CoreWebView2CompositionController (JPEG/screencast path)...");
                    _compController = await _env.CreateCoreWebView2CompositionControllerAsync(_hwnd);
                    _controller = _compController; // also a CoreWebView2Controller
                }
                if (_disposed) return;
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, _width, _height);
                try { _controller.IsVisible = true; } catch { }
                _core = _controller.CoreWebView2;
                _core.Settings.IsStatusBarEnabled = false;

                TryEnableBasicAdBlock();

                _core.NavigationStarting += (_, e) =>
                {
                    try { MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] NavigationStarting: {e.Uri}"); } catch { }
                };
                _core.NavigationCompleted += (_, e) =>
                {
                    try
                    {
                        CurrentUrl = _core.Source;
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] NavigationCompleted: success={e.IsSuccess} status={e.HttpStatusCode} url='{_core.Source}'");
                    }
                    catch { }

                    if (e.IsSuccess)
                    {
                        try { InjectCosmeticCleanup(); } catch { }
                        if (UnderNetBrowserConfig.AutoConsentEnabled?.Value == true)
                            TryAutoConsentAndPlay();
                    }
                };
                // Additional UI-thread callback opportunities to run delayed consent attempts (only if enabled).
                try { _core.DOMContentLoaded += (_, __) => { if (UnderNetBrowserConfig.AutoConsentEnabled?.Value == true) PumpConsentAttempts("domcontentloaded"); }; } catch { }
                try { _core.FrameNavigationCompleted += (_, __) => { if (UnderNetBrowserConfig.AutoConsentEnabled?.Value == true) PumpConsentAttempts("frame-nav"); }; } catch { }

                _ready = true;
                LastError = null;
                if (!_gpuCaptureHostMode)
                    await TryStartScreencastAsync();
                else
                    MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] GPU/WGC capture host mode: CDP screencast/JPEG path disabled (awaiting external WGC capture).");

                var target = _pendingNavigate ?? initialUrl;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Controller ready (comp={_compController != null}, screencast={_useScreencast}, gpuCaptureHost={_gpuCaptureHostMode}). Navigating to '{target}'...");
                Navigate(target);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] InitializeAsync failed: {ex}");
                try { if (ex is COMException comEx) MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] HRESULT=0x{comEx.HResult:X8}"); } catch { }
            }
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _pendingNavigate = url;
            if (!_ready || _core == null)
            {
                return;
            }
            try
            {
                CurrentUrl = url;
                _core.Navigate(url);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Navigate failed: {ex.Message}");
            }
        }

        public void Reload()
        {
            if (!_ready || _core == null) { if (!string.IsNullOrWhiteSpace(_pendingNavigate)) Navigate(_pendingNavigate); return; }
            try { MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Reload"); _core.Reload(); }
            catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Reload failed: {ex.Message}"); }
        }

        public void GoBack()
        {
            if (!_ready || _core == null) return;
            try { if (_core.CanGoBack) { MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] GoBack"); _core.GoBack(); } } 
            catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] GoBack failed: {ex.Message}"); }
        }

        public void GoForward()
        {
            if (!_ready || _core == null) return;
            try { if (_core.CanGoForward) { MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] GoForward"); _core.GoForward(); } } 
            catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] GoForward failed: {ex.Message}"); }
        }

        public void StopLoading()
        {
            if (!_ready || _core == null) return;
            try { MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Stop loading"); _core.Stop(); } 
            catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Stop failed: {ex.Message}"); }
        }

        public Task<string?> ExecuteScriptAsync(string script)
        {
            if (string.IsNullOrEmpty(script)) return Task.FromResult<string?>(null);
            if (!_ready || _core == null || _disposed) return Task.FromResult<string?>(null);
            try
            {
                return _core.ExecuteScriptAsync(script).ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsFaulted || t.IsCanceled) return null;
                        return t.Result;
                    }
                    catch { return null; }
                });
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }

        public void Tick(float unscaledTime)
        {
            if (_disposed || !_ready || _core == null) return;

            // Best-effort: try running due consent attempts only when explicitly enabled.
            try { if (UnderNetBrowserConfig.AutoConsentEnabled?.Value == true) PumpConsentAttempts("tick"); } catch { }

            // GPU/WGC capture mode: no JPEG frame decode/upload pipeline (frames handled externally by native WGC).
            if (_gpuCaptureHostMode || _texture == null)
            {
                TryLogAndApplyHostWindowStrategy();
                return;
            }

            // Apply latest captured frame on Unity main thread.
            byte[]? frame = null;
            lock (_frameLock)
            {
                if (_pendingFrameBytes != null)
                {
                    frame = _pendingFrameBytes;
                    _pendingFrameBytes = null;
                }
            }
            if (frame != null)
            {
                try
                {
                    var ok = ImageConversion.LoadImage(_texture, frame, false);
                    if (ok)
                    {
                        _captureSuccessCount++;
                        _uploadedFrameCount++;
                        _lastFrameUploadRealtime = Time.realtimeSinceStartup;
                        if (_captureSuccessCount == 1)
                            MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] First frame uploaded to Unity texture ({frame.Length} bytes).");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Texture upload failed: {ex.Message}");
                }
            }

            // Periodic capture/render diagnostics (helps diagnose delayed/stale preview)
            if (unscaledTime - _lastUploadLogTime >= 5f)
            {
                _lastUploadLogTime = unscaledTime;
                var age = _lastFrameUploadRealtime > 0f ? (Time.realtimeSinceStartup - _lastFrameUploadRealtime) : -1f;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Capture stats: uploaded={_uploadedFrameCount} pendingCapture={_capturePending} lastFrameAge={(age >= 0f ? age.ToString("0.0") + "s" : "n/a")} url='{CurrentUrl}'");
            }

            // CDP screencast path (GPU spike / higher FPS) delivers frames via events.
            // Fallback: legacy CapturePreviewAsync at ~2 FPS if screencast is unavailable.
            if (_useScreencast || _capturePending || _disposed || _core == null) return;
            if (unscaledTime - _lastCapture < 0.5f) return;
            _lastCapture = unscaledTime;
            _capturePending = true;
            _ = CaptureOnceAsync();
        }

        private async Task TryStartScreencastAsync()
        {
            try
            {
                if (_disposed || _core == null) return;
                _screencastReceiver = _core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                _screencastReceiver.DevToolsProtocolEventReceived += (_, e) =>
                {
                    try { OnScreencastFrame(e.ParameterObjectAsJson, e.SessionId); } catch { }
                };

                // Higher quality/resolution to reduce blurriness while keeping CPU decode/upload manageable.
                var p = "{\"format\":\"jpeg\",\"quality\":92,\"maxWidth\":" + _width + ",\"maxHeight\":" + _height + ",\"everyNthFrame\":1}";
                await _core.CallDevToolsProtocolMethodAsync("Page.startScreencast", p).ConfigureAwait(false);
                _useScreencast = true;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] CDP screencast started (jpeg q92, {_width}x{_height}, everyNthFrame=1).");
            }
            catch (Exception ex)
            {
                _useScreencast = false;
                _screencastReceiver = null;
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Screencast start failed, falling back to CapturePreview: {ex.Message}");
            }
        }

        private void OnScreencastFrame(string json, string? sessionId)
        {
            if (_disposed || _core == null) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var session = 0;
                if (root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.Number) session = s.GetInt32();
                else if (root.TryGetProperty("sessionId", out var s2) && s2.ValueKind == JsonValueKind.String && int.TryParse(s2.GetString(), out var si)) session = si;

                if (root.TryGetProperty("data", out var dataEl))
                {
                    var b64 = dataEl.GetString();
                    if (!string.IsNullOrEmpty(b64))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(b64);
                            lock (_frameLock) { _pendingFrameBytes = bytes; }
                        }
                        catch { }
                    }
                }

                // Ack immediately to keep frames flowing.
                _ = AckScreencastFrameAsync(session, sessionId);
            }
            catch (Exception ex)
            {
                var now = Time.realtimeSinceStartup;
                if (now - _lastCaptureErrorLog >= 5f)
                {
                    _lastCaptureErrorLog = now;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Screencast frame handling failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task AckScreencastFrameAsync(int session, string? devToolsSessionId)
        {
            try
            {
                if (_disposed || _core == null) return;
                var p = "{\"sessionId\":" + session + "}";
                if (!string.IsNullOrEmpty(devToolsSessionId))
                    await _core.CallDevToolsProtocolMethodForSessionAsync(devToolsSessionId, "Page.screencastFrameAck", p).ConfigureAwait(false);
                else
                    await _core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", p).ConfigureAwait(false);
            }
            catch { }
        }

        private float _lastCaptureErrorLog;
        private async Task CaptureOnceAsync()
        {
            try
            {
                if (_disposed || _core == null || _texture == null) return;
                using var ms = new MemoryStream();
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms).ConfigureAwait(false);
                if (_disposed || _core == null || _texture == null) return;
                var bytes = ms.ToArray();
                if (bytes == null || bytes.Length == 0) return;

                // Marshal decoded upload to Unity main thread (Tick runs on main thread).
                lock (_frameLock)
                {
                    _pendingFrameBytes = bytes;
                }
            }
            catch (Exception ex)
            {
                if (_disposed) return; // expected during teardown
                LastError = ex.Message;
                var now = Time.realtimeSinceStartup;
                if (now - _lastCaptureErrorLog >= 5f)
                {
                    _lastCaptureErrorLog = now;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Capture failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                _capturePending = false;
            }
        }

        private static readonly string[] AdBlockHostHints = new[]
        {
            "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google",
            "pagead2.googlesyndication.com", "tpc.googlesyndication.com", "adnxs.com", "adsrvr.org",
            "taboola.com", "outbrain.com", "criteo.com", "pubmatic.com", "rubiconproject.com",
            "scorecardresearch.com", "quantserve.com", "bluekai.com", "moatads.com", "serving-sys.com",
            "adform.net", "smartadserver.com", "openx.net", "demdex.net", "everesttech.net"
        };

        private void TryEnableBasicAdBlock()
        {
            try
            {
                if (_core == null || _env == null) return;
                if (UnderNetBrowserConfig.BasicAdBlockEnabled?.Value != true) return;

                // Basic built-in request blocking ("uBlock-like" lightweight filter, not a full extension).
                // Uses WebView2 WebResourceRequested filtering (API availability depends on runtime/SDK; guarded).
                try { _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All); } catch { }
                _core.WebResourceRequested += (_, e) =>
                {
                    try
                    {
                        var uri = e.Request?.Uri ?? string.Empty;
                        if (string.IsNullOrEmpty(uri)) return;
                        var lower = uri.ToLowerInvariant();
                        for (int i = 0; i < AdBlockHostHints.Length; i++)
                        {
                            if (lower.Contains(AdBlockHostHints[i]))
                            {
                                try
                                {
                                    var resp = _env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                                    e.Response = resp;
                                }
                                catch { }
                                return;
                            }
                        }
                    }
                    catch { }
                };
                MelonLogger.Msg("[Firesky UnderNet Browser] [StageB/WebView2] Basic ad/tracker blocking enabled (lightweight filter).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Ad block setup failed (non-fatal): {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { lock (_consentLock) _pendingConsentAttempts.Clear(); } catch { }
            try { if (_useScreencast && _core != null) _ = _core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}"); } catch { }
            _useScreencast = false;
            _screencastReceiver = null;
            try { _controller?.Close(); } catch { }
            try { (_compController as IDisposable)?.Dispose(); } catch { }
            _core = null;
            _controller = null;
            _compController = null;
            _env = null;
            if (_hwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_hwnd); } catch { }
                _hwnd = IntPtr.Zero;
            }
            try { if (_texture != null) UnityEngine.Object.Destroy(_texture); } catch { }
            _texture = null;
        }

        private readonly object _consentLock = new object();
        private readonly List<(int attempt, int total, float dueAt)> _pendingConsentAttempts = new List<(int, int, float)>();

        private void TryAutoConsentAndPlay()
        {
            // Opt-in only. Kept conservative to avoid unintended page clicks.
            try
            {
                if (UnderNetBrowserConfig.AutoConsentEnabled?.Value != true) return;
                int[] delaysMs = new[] { 0, 1200, 3000 };
                var now = Time.realtimeSinceStartup;
                lock (_consentLock)
                {
                    _pendingConsentAttempts.Clear();
                    for (int i = 0; i < delaysMs.Length; i++)
                        _pendingConsentAttempts.Add((i + 1, delaysMs.Length, now + (delaysMs[i] / 1000f)));
                }
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent scheduled {delaysMs.Length} attempts (opt-in).");
                PumpConsentAttempts("schedule");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent scheduling failed: {ex.Message}");
            }
        }

        private void PumpConsentAttempts(string reason)
        {
            try
            {
                if (_disposed || _core == null) return;
                var now = Time.realtimeSinceStartup;
                // Avoid tight re-entry spam from multiple callbacks in same moment.
                // (NavigationCompleted and other callbacks may fire; allow attempts when due.)
                List<(int attempt, int total, float dueAt)>? due = null;
                lock (_consentLock)
                {
                    if (_pendingConsentAttempts.Count == 0) return;
                    for (int i = _pendingConsentAttempts.Count - 1; i >= 0; i--)
                    {
                        var item = _pendingConsentAttempts[i];
                        if (item.dueAt <= now)
                        {
                            due ??= new List<(int, int, float)>();
                            due.Add(item);
                            _pendingConsentAttempts.RemoveAt(i);
                        }
                    }
                }
                if (due == null) return;
                // Execute in attempt order
                due.Sort((a, b) => a.attempt.CompareTo(b.attempt));
                foreach (var item in due)
                {
                    if (_disposed || _core == null) return;
                    ExecuteAutoConsentAttempt(item.attempt, item.total);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent pump failed ({reason}): {ex.Message}");
            }
        }

        private void InjectCosmeticCleanup()
        {
            // Best-effort cosmetic hiding for common sign-in/consent nags (not ad-blocking in-video ads).
            try
            {
                if (_disposed || _core == null) return;
                const string script = @"(function(){
  try{
    var id='undernet-cosmetic-cleanup';
    if(!document.getElementById(id)){
      var st=document.createElement('style'); st.id=id;
      st.textContent = [
        'ytd-consent-bump-v2-lightbox, tp-yt-iron-overlay-backdrop, ytd-popup-container tp-yt-paper-dialog, yt-mealbar-promo-renderer, ytd-enforcement-message-view-model, #masthead-ad, ytd-ad-slot-renderer, ytd-display-ad-renderer, ytd-promoted-sparkles-web-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer { display: none !important; }',
        'tp-yt-paper-dialog:has(ytd-consent-bump-v2-lightbox), ytd-popup-container:has(a[href*=""accounts.google.com""]) { display:none !important; }'
      ].join('\n');
      (document.head||document.documentElement).appendChild(st);
    }
    return 'ok';
  }catch(e){ return 'err'; }
})();";
                _ = _core.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private void ExecuteAutoConsentAttempt(int attempt, int total)
        {
            try
            {
                if (_disposed || _core == null) return;
                if (UnderNetBrowserConfig.AutoConsentEnabled?.Value != true) return;

                // Conservative: only target explicit consent/cookie actions (no generic play/continue/ok clicks).
                const string script = @"(function(){
  try{
    const norm=s=>(s||'').replace(/\s+/g,' ').trim().toLowerCase();
    const isVisible=el=>{ if(!el) return false; const r=el.getBoundingClientRect(); const st=window.getComputedStyle(el); return r.width>0 && r.height>0 && st && st.visibility!=='hidden' && st.display!=='none'; };
    const clickByText=(texts)=>{
      const els=[...document.querySelectorAll('button, [role=""button""], tp-yt-paper-button, ytd-button-renderer button, input[type=""button""], input[type=""submit""]')];
      for(const el of els){
        if(!isVisible(el)) continue;
        const t=norm(el.innerText||el.textContent||el.value||el.getAttribute('aria-label')||el.getAttribute('title'));
        if(!t) continue;
        for(const needle of texts){ if(t === needle || t.includes(needle)){ try{ el.click(); return needle; }catch(e){} } }
      }
      return null;
    };
    const host = (location.hostname||'').toLowerCase();
    if (host.indexOf('consent.') === -1 && host.indexOf('youtube.') === -1 && host.indexOf('google.') === -1) return 'skip-host';
    const clicked = clickByText(['accept all','reject all','i agree','agree and continue','allow all']);
    return clicked ? ('clicked:'+clicked) : 'no-click';
  }catch(e){ return 'err:'+e; }
})();";

                _ = _core.ExecuteScriptAsync(script).ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsFaulted) MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent attempt {attempt}/{total} fault: {t.Exception?.GetBaseException().Message}");
                        else MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent attempt {attempt}/{total} result: {t.Result}");
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Auto-consent attempt {attempt}/{total} failed: {ex.Message}");
            }
        }

        #region Input forwarding

        private float _lastInputLog;

        public void NotifyPointerMove(int x, int y, bool leftButton)
        {
            if (!_ready) return;
            x = Clamp(x, 0, Math.Max(0, _width - 1));
            y = Clamp(y, 0, Math.Max(0, _height - 1));
            try
            {
                if (_compController != null)
                {
                    var keys = leftButton ? CoreWebView2MouseEventVirtualKeys.LeftButton : CoreWebView2MouseEventVirtualKeys.None;
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.Move, keys, 0, new System.Drawing.Point(x, y));
                    return;
                }
                if (TryGetInputTarget(out var target, out var tx, out var ty, x, y))
                {
                    var wParam = leftButton ? (IntPtr)MK_LBUTTON : IntPtr.Zero;
                    PostMessage(target, WM_MOUSEMOVE, wParam, MakeLParam(tx, ty));
                }
            }
            catch { }
        }

        public void NotifyPointerDown(int x, int y)
        {
            if (!_ready) return;
            x = Clamp(x, 0, Math.Max(0, _width - 1));
            y = Clamp(y, 0, Math.Max(0, _height - 1));
            try
            {
                if (_compController != null)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Input: PointerDown ({x},{y}) hwnd=0x{(_hwnd == IntPtr.Zero ? 0 : _hwnd.ToInt64()):X} ready={_ready} comp=True");
                    var pt = new System.Drawing.Point(x, y);
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, pt);
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, pt);
                    return;
                }
                if (TryGetInputTarget(out var target, out var tx, out var ty, x, y))
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Input: PointerDown ({x},{y}) -> ({tx},{ty}) hwnd=0x{_hwnd.ToInt64():X} target=0x{target.ToInt64():X} ready={_ready} comp=False");
                    try { SetFocus(target); } catch { }
                    try { SetFocus(_hwnd); } catch { }
                    PostMessage(target, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(tx, ty));
                    PostMessage(target, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(tx, ty));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Input PointerDown failed: {ex.Message}");
            }
        }

        public void NotifyPointerUp(int x, int y)
        {
            if (!_ready) return;
            x = Clamp(x, 0, Math.Max(0, _width - 1));
            y = Clamp(y, 0, Math.Max(0, _height - 1));
            try
            {
                if (_compController != null)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Input: PointerUp ({x},{y}) comp=True");
                    var pt = new System.Drawing.Point(x, y);
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, pt);
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, pt);
                    return;
                }
                if (TryGetInputTarget(out var target, out var tx, out var ty, x, y))
                {
                    PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(tx, ty));
                    PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(tx, ty));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Input PointerUp failed: {ex.Message}");
            }
        }

        public void NotifyMouseWheel(int x, int y, int delta)
        {
            if (!_ready) return;
            x = Clamp(x, 0, Math.Max(0, _width - 1));
            y = Clamp(y, 0, Math.Max(0, _height - 1));
            try
            {
                var now = Time.realtimeSinceStartup;
                if (now - _lastInputLog > 0.5f)
                {
                    _lastInputLog = now;
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] Input: Wheel ({x},{y}) delta={delta} comp={_compController != null}");
                }
                if (_compController != null)
                {
                    _compController.SendMouseInput(CoreWebView2MouseEventKind.Wheel, CoreWebView2MouseEventVirtualKeys.None, (uint)delta, new System.Drawing.Point(x, y));
                    return;
                }
                // WM_MOUSEWHEEL lParam is in screen coordinates
                if (TryGetInputTarget(out var target, out var tx, out var ty, x, y))
                {
                    var pt = new POINT { X = tx, Y = ty };
                    try { ClientToScreen(target, ref pt); } catch { }
                    var wParam = (IntPtr)((delta << 16));
                    PostMessage(target, WM_MOUSEWHEEL, wParam, MakeLParam(pt.X, pt.Y));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/WebView2] Input Wheel failed: {ex.Message}");
            }
        }

        private bool TryGetInputTarget(out IntPtr target, out int tx, out int ty, int viewX, int viewY)
        {
            tx = viewX; ty = viewY; target = IntPtr.Zero;
            if (_hwnd == IntPtr.Zero) return false;
            try { if (_inputTargetHwnd == IntPtr.Zero || !IsWindow(_inputTargetHwnd)) _inputTargetHwnd = FindBestWebViewChild(_hwnd); } catch { _inputTargetHwnd = IntPtr.Zero; }
            target = _inputTargetHwnd != IntPtr.Zero ? _inputTargetHwnd : _hwnd;
            // map view/controller coords -> target client coords
            try
            {
                if (GetClientRect(target, out var rc))
                {
                    int cw = Math.Max(1, rc.Right - rc.Left);
                    int ch = Math.Max(1, rc.Bottom - rc.Top);
                    int vw = Math.Max(1, _width);
                    int vh = Math.Max(1, _height);
                    tx = Clamp((int)Math.Round(viewX * (cw - 1) / (double)(vw - 1)), 0, cw - 1);
                    ty = Clamp((int)Math.Round(viewY * (ch - 1) / (double)(vh - 1)), 0, ch - 1);
                }
            }
            catch { }
            return true;
        }

        private void TryLogAndApplyHostWindowStrategy()
        {
            try
            {
                if (!_gpuCaptureHostMode || _hwnd == IntPtr.Zero) return;

                // Log host window state periodically for visibility experiments
                if (Time.realtimeSinceStartup - _lastHostWindowLog >= 5f)
                {
                    _lastHostWindowLog = Time.realtimeSinceStartup;
                    try
                    {
                        bool visible = IsWindowVisible(_hwnd);
                        bool iconic = IsIconic(_hwnd);
                        GetWindowRect(_hwnd, out var wr);
                        GetClientRect(_hwnd, out var cr);
                        long style = (long)GetWindowLongPtr(_hwnd, GWL_STYLE);
                        long exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] HostWindow state: hwnd=0x{_hwnd.ToInt64():X} visible={visible} minimized={iconic} rect=({wr.Left},{wr.Top},{wr.Right},{wr.Bottom}) client={cr.Right - cr.Left}x{cr.Bottom - cr.Top} style=0x{style:X} exStyle=0x{exStyle:X}");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/WebView2] HostWindow state log failed: {ex.Message}");
                    }
                }

                // Host visibility strategies removed: composition mode is the active render path and
                // legacy host-window visibility tweaks are no longer configurable. Keep host window as-is.
                _visibilityStrategyApplied = true;
                return;
            }
            catch { }
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_SETFOCUS = 0x0007;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const int MK_LBUTTON = 0x0001;
        private const int HTCLIENT = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private static IntPtr FindBestWebViewChild(IntPtr parent)
        {
            IntPtr best = IntPtr.Zero;
            try
            {
                EnumChildWindows(parent, (child, _) =>
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(256);
                        GetClassName(child, sb, sb.Capacity);
                        var cls = sb.ToString();
                        // Prefer the widget/input host window typically used by WebView2/Chromium
                        if (cls.IndexOf("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) >= 0) best = child;
                        else if (best == IntPtr.Zero && cls.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0) best = child;
                        else if (best == IntPtr.Zero && cls.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0) best = child;
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best;
        }

        #endregion

        #region Win32
        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private static WndProc? _wndProcDelegate;
        private static ushort _classAtom;
        private static readonly object _classLock = new object();
        private const string ClassName = "FireskyUnderNetBrowser_WebViewHost";

        private static IntPtr CreateHiddenWindow(int width, int height, bool showWindow = false)
        {
            lock (_classLock)
            {
                if (_classAtom == 0)
                {
                    _wndProcDelegate = DefWindowProc; // delegate kept alive
                    var wnd = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                        hInstance = GetModuleHandle(null),
                        lpszClassName = ClassName
                    };
                    _classAtom = RegisterClassEx(ref wnd);
                    // if already registered by previous attempt, ignore error
                }
            }

            const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
            const uint WS_VISIBLE = 0x10000000;
            const uint WS_EX_TOOLWINDOW = 0x00000080;
            // GPU/WGC diagnostic mode: show a real, top-level window so WGC can capture rendered WebView2 content.
            // IMPORTANT: visible on-screen with exStyle 0 is the currently verified capturable configuration.
            // WS_EX_TOOLWINDOW / hidden / off-screen variations previously broke CreateForWindow (0x80070057) in tests.
            // JPEG/screencast path keeps hidden off-screen window (legacy behavior).
            var style = WS_OVERLAPPEDWINDOW | (showWindow ? WS_VISIBLE : 0u);
            uint exStyle = showWindow ? 0u : WS_EX_TOOLWINDOW;
            int x = showWindow ? 40 : -32000;
            int y = showWindow ? 40 : -32000;
            var title = showWindow ? "UnderNet Browser (WGC Capture Host)" : "FireskyUnderNetBrowser WebView";
            var hwnd = CreateWindowEx(exStyle, ClassName, title, style,
                x, y, Math.Max(1, width), Math.Max(1, height), IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                hwnd = CreateWindowEx(exStyle, "Static", title, style,
                    x, y, Math.Max(1, width), Math.Max(1, height), IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            }
            if (hwnd != IntPtr.Zero && showWindow)
            {
                try { ShowWindow(hwnd, 1 /*SW_SHOWNORMAL*/); } catch { }
                try { UpdateWindow(hwnd); } catch { }
            }
            return hwnd;
        }
        #endregion
    }
}
