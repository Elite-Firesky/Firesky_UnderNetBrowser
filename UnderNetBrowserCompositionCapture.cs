using System;
using MelonLoader;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Experimental: native WebView2 CompositionController + WGC CreateFromVisual capture rendered into a Unity-owned texture.
    /// No visible ordinary browser window (native host HWND is hidden). Keeps JPEG and visible-host WGC fallbacks available elsewhere.
    /// </summary>
    internal sealed class UnderNetBrowserCompositionCapture : IDisposable
    {
        private int _width = 1280;
        private int _height = 720;
        private IntPtr _renderEventFunc;
        private Texture2D? _unityTexture;
        private bool _createIssued;
        private bool _disposed;
        private float _lastLog;
        private float _lastNoFrameLogTime;
        private int _lastStatus = int.MinValue;
        private int _lastReady = -1;
        private bool _startRequested;
        private string _lastUrl = "about:blank";

        public Texture2D? Texture => _unityTexture;
        // status: 2=starting (async), 1=running, <0 error
        public bool IsRunning => _createIssued && UnderNetBrowserGpuNative.CompGetStatus() != 0;

        private bool _renderingSuspended;
        /// <summary>
        /// When true, skips Unity render-thread composition blits/copies and pauses native capture/frame processing
        /// while the browser is kept alive but not visible (e.g. detached-to-gameplay or warm-closed background state).
        /// </summary>
        public bool RenderingSuspended
        {
            get => _renderingSuspended;
            set
            {
                _renderingSuspended = value;
                try { UnderNetBrowserGpuNative.CompSetCapturePaused(value ? 1 : 0); } catch { }
            }
        }

        public bool TryStart(string initialUrl, int targetWidth = 1920, int targetHeight = 1080, string? userDataFolder = null)
        {
            try
            {
                _width = Math.Max(2, targetWidth);
                _height = Math.Max(2, targetHeight);
                _lastUrl = string.IsNullOrWhiteSpace(initialUrl) ? "about:blank" : initialUrl;

                UnderNetBrowserGpuNative.EnsureLoaded();

                try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFunc(); } catch { _renderEventFunc = IntPtr.Zero; }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFuncPtr(); } catch { _renderEventFunc = IntPtr.Zero; }
                }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Comp] Native render event function not available.");
                    return false;
                }

                if (!CreateOrResizeTargetTexture(_width, _height, issueInitEvent: true, log: true))
                    return false;

                _createIssued = true;
                try
                {
                    var browserArgs = UnderNetBrowserConfig.BuildWebView2AdditionalBrowserArguments();
                    UnderNetBrowserGpuNative.CompSetAdditionalBrowserArguments(string.IsNullOrWhiteSpace(browserArgs) ? null : browserArgs);
                    if (!string.IsNullOrWhiteSpace(browserArgs))
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Comp] Additional browser arguments configured: '{browserArgs}'");
                }
                catch { }
                try { UnderNetBrowserGpuNative.CompSetParams(_lastUrl, _width, _height, userDataFolder); } catch { }
                _startRequested = true;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Comp] Requested composition browser start url='{_lastUrl}' target={_width}x{_height} userData='{userDataFolder ?? ""}'");
                return true;
            }
            catch (DllNotFoundException)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] UnderNetGpuRenderer.dll not found/unloadable. LoadedPath='{UnderNetBrowserGpuNative.LoadedPath}'.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] Start failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _lastUrl = url;
            try { UnderNetBrowserGpuNative.CompNavigate(url); } catch { }
        }

        public void GoBack() { try { UnderNetBrowserGpuNative.CompGoBack(); } catch { } }
        public void GoForward() { try { UnderNetBrowserGpuNative.CompGoForward(); } catch { } }
        public void Reload() { try { UnderNetBrowserGpuNative.CompReload(); } catch { } }

        public void SendMouseMove(int x, int y, bool leftButton, bool rightButton = false) { try { UnderNetBrowserGpuNative.CompSendMouse(0, (leftButton ? 1 : 0) | (rightButton ? 2 : 0), 0, x, y); } catch { } }
        public void SendMouseDown(int x, int y, bool rightButton = false) { try { UnderNetBrowserGpuNative.CompSendMouse(rightButton ? 4 : 1, rightButton ? 2 : 1, 0, x, y); } catch { } }
        public void SendMouseUp(int x, int y, bool rightButton = false) { try { UnderNetBrowserGpuNative.CompSendMouse(rightButton ? 5 : 2, 0, 0, x, y); } catch { } }
        public void SendMouseWheel(int x, int y, int delta) { try { UnderNetBrowserGpuNative.CompSendMouse(3, 0, unchecked((uint)delta), x, y); } catch { } }
        private bool _compSendKeyErrorLogged;
        private int _compSendKeySuccessCount;
        public void SendKey(uint msg, ulong wParam, ulong lParam)
        {
            try
            {
                UnderNetBrowserGpuNative.CompSendKey(msg, wParam, lParam);
                _compSendKeySuccessCount++;
                if (_compSendKeySuccessCount <= 10 || _compSendKeySuccessCount % 50 == 0)
                {
                    try { MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Comp] SendKey->native ok #{_compSendKeySuccessCount} msg=0x{msg:X} wParam=0x{wParam:X}"); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (!_compSendKeyErrorLogged)
                {
                    _compSendKeyErrorLogged = true;
                    try { MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] SendKey P/Invoke failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                }
            }
        }
        public void SendText(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) UnderNetBrowserGpuNative.CompSendText(text); } catch { }
        }
        public void ExecuteScript(string script)
        {
            try { if (!string.IsNullOrEmpty(script)) UnderNetBrowserGpuNative.CompExecuteScript(script); } catch { }
        }
        public void RequestFocus() { try { UnderNetBrowserGpuNative.CompRequestFocus(); } catch { } }

        public void SetZoomFactor(double zoomFactor)
        {
            try { UnderNetBrowserGpuNative.CompSetZoomFactor(zoomFactor); }
            catch (Exception ex) { if (UnderNetBrowserConfig.DebugLogging?.Value == true) MelonLogger.Warning($"[UnderNetBrowser] CompSetZoomFactor failed (native rebuild/restart required?): {ex.Message}"); }
        }
        public double GetZoomFactor()
        {
            try { return UnderNetBrowserGpuNative.CompGetZoomFactor(); }
            catch (Exception ex) { if (UnderNetBrowserConfig.DebugLogging?.Value == true) MelonLogger.Warning($"[UnderNetBrowser] CompGetZoomFactor failed (native rebuild/restart required?): {ex.Message}"); return 1.0; }
        }
        public string CurrentUrl { get { try { return UnderNetBrowserGpuNative.GetCompCurrentUrlString(); } catch { return ""; } } }
        public bool IsFullscreen { get { try { return _createIssued && UnderNetBrowserGpuNative.CompIsFullscreen() == 1; } catch { return false; } } }
        public int ViewWidth => _width;
        public int ViewHeight => _height;

        public void Tick()
        {
            if (_disposed || !_createIssued || _renderEventFunc == IntPtr.Zero) return;
            try
            {
                int status = 0, devReady = 0, targetReady = 0, compStatus = 0;
                try { status = UnderNetBrowserGpuNative.GetStatus(); } catch { }
                try { devReady = UnderNetBrowserGpuNative.IsUnityDeviceReady(); } catch { }
                try { targetReady = UnderNetBrowserGpuNative.IsTargetReady(); } catch { }
                try { compStatus = UnderNetBrowserGpuNative.CompGetStatus(); } catch { }

                if (targetReady != 0 && _startRequested)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventCompStart);
                    _startRequested = false;
                }

                if (targetReady != 0 && !RenderingSuspended)
                {
                    bool adaptiveStandby = false;
                    try { adaptiveStandby = UnderNetBrowserGpuNative.CompIsAdaptiveVisualStandby() == 1; } catch { adaptiveStandby = false; }
                    if (!adaptiveStandby)
                    {
                        GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventCompCopyLatest);
                    }
                }

                bool changed = (compStatus != _lastStatus || targetReady != _lastReady);
                bool periodic = (Time.realtimeSinceStartup - _lastLog > 5f);
                bool important = (compStatus == 2 || compStatus < 0);
                if (changed || periodic || important)
                {
                    _lastStatus = compStatus;
                    _lastReady = targetReady;
                    _lastLog = Time.realtimeSinceStartup;
                    var err = UnderNetBrowserGpuNative.GetLastErrorString();
                    var diag = "";
                    try { diag = UnderNetBrowserGpuNative.GetCompDiagString(); } catch { }
                    string msg = $"[GPU-Comp] status={status} targetReady={targetReady} deviceReady={devReady} comp={compStatus} gfx={SystemInfo.graphicsDeviceType} err='{err}' diag='{diag}'";
                    bool isNoFrameYet = diag.IndexOf("no frame yet", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool canLogNoFrame = !isNoFrameYet || changed || (Time.realtimeSinceStartup - _lastNoFrameLogTime >= 5f);
                    if (isNoFrameYet && canLogNoFrame) _lastNoFrameLogTime = Time.realtimeSinceStartup;

                    if (changed || compStatus < 0 || (compStatus == 2 && canLogNoFrame))
                        MelonLogger.Msg($"[Firesky UnderNet Browser] {msg}");
                    else if (!(compStatus == 2 && isNoFrameYet))
                        UnderNetBrowserConfig.Log(msg);

                    if (compStatus < 0)
                    {
                        string compErr = "";
                        try { compErr = UnderNetBrowserGpuNative.GetLastErrorString(); } catch { }
                        try { if (string.IsNullOrWhiteSpace(compErr)) compErr = UnderNetBrowserGpuNative.GetCompDiagString(); } catch { }
                        MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] startup/runtime error='{compErr}'");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup - _lastLog > 5f)
                {
                    _lastLog = Time.realtimeSinceStartup;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] Tick failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private bool CreateOrResizeTargetTexture(int width, int height, bool issueInitEvent, bool log)
        {
            try
            {
                width = Math.Max(2, width);
                height = Math.Max(2, height);
                try { if (_unityTexture != null) UnityEngine.Object.Destroy(_unityTexture); } catch { }
                _unityTexture = null;

                _unityTexture = new Texture2D(width, height, TextureFormat.BGRA32, false, false);
                _unityTexture.filterMode = FilterMode.Bilinear;
                _unityTexture.wrapMode = TextureWrapMode.Clamp;
                var fill = new Color32[width * height];
                Array.Fill(fill, new Color32(20, 20, 30, 255));
                _unityTexture.SetPixels32(fill);
                _unityTexture.Apply(false, false);

                IntPtr nativePtr = _unityTexture.GetNativeTexturePtr();
                if (nativePtr == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Comp] GetNativeTexturePtr returned null.");
                    return false;
                }

                _width = width; _height = height;
                int setOk = UnderNetBrowserGpuNative.SetTargetTexture(nativePtr, _width, _height);
                if (log)
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Comp] Unity-owned target texture ready ({_width}x{_height}) nativePtr=0x{nativePtr.ToInt64():X} setTarget={setOk} gfx={SystemInfo.graphicsDeviceType}");

                if (issueInitEvent && _renderEventFunc != IntPtr.Zero)
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventTargetInitUpdate);
                _lastReady = -1;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Comp] CreateOrResizeTargetTexture failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Synchronous native shutdown first (reliable during scene unload/menu quit when render events may not run).
            try { UnderNetBrowserGpuNative.CompShutdown(); } catch { }

            try
            {
                if (_renderEventFunc != IntPtr.Zero)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventCompStop);
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventTargetRelease);
                }
            }
            catch { }
            try { UnderNetBrowserGpuNative.ClearTargetTexture(); } catch { }
            try { if (_unityTexture != null) UnityEngine.Object.Destroy(_unityTexture); } catch { }
            _unityTexture = null;
            _renderEventFunc = IntPtr.Zero;
        }
    }
}