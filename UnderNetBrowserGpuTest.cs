using System;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Experimental GPU test (Phase 3 alternative): Unity-owned Texture2D + GetNativeTexturePtr passed to native renderer.
    /// Native validates texture on render thread (via GL.IssuePluginEvent), obtains D3D11 device/context from it, and
    /// GPU-copies an animated test pattern into the Unity-owned texture. No UnityPluginLoad required for this path.
    /// Keeps JPEG/WebView path intact; enabled only via config.
    /// </summary>
    internal sealed class UnderNetBrowserGpuTest : IDisposable
    {
        // Stable Unity destination resolution for shader blit (WGC frames are scaled/letterboxed on GPU).
        private int _width = 1280;
        private int _height = 720;

        private IntPtr _renderEventFunc;
        private Texture2D? _unityTexture;
        private bool _createIssued;
        private bool _disposed;
        private float _lastLog;
        private int _lastStatus = int.MinValue;
        private int _lastReady = -1;
        private bool _wgcStartRequested;
        private bool _wgcStarted;
        private IntPtr _wgcHwnd = IntPtr.Zero;
        private float _lastResizeCheck;

        public Texture2D? Texture => _unityTexture;
        public bool IsReady => _unityTexture != null;

        public bool TryStart()
        {
            try
            {
                UnderNetBrowserGpuNative.EnsureLoaded();

                // GetRenderEventFunc() returns the actual render-thread callback pointer (preferred).
                try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFunc(); } catch { _renderEventFunc = IntPtr.Zero; }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFuncPtr(); } catch { _renderEventFunc = IntPtr.Zero; }
                }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Test] Native render event function not available.");
                    return false;
                }

                if (!CreateOrResizeTargetTexture(_width, _height, issueInitEvent: true, log: true))
                    return false;

                _createIssued = true;

                // Phase 4A (best-effort): try to capture foreground window as a quick WGC PoC target.
                try
                {
                    var hwnd = GetForegroundWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        _wgcHwnd = hwnd;
                        UnderNetBrowserGpuNative.SetWgcCaptureHwnd(hwnd);
                        _wgcStartRequested = true;
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Test/WGC] Requested capture hwnd=0x{hwnd.ToInt64():X} (foreground window).");
                    }
                }
                catch { }
                return true;
            }
            catch (DllNotFoundException)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] UnderNetGpuRenderer.dll not found/unloadable by P/Invoke. LoadedPath='{UnderNetBrowserGpuNative.LoadedPath}'.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] Start failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void Tick()
        {
            if (_disposed || !_createIssued || _renderEventFunc == IntPtr.Zero) return;
            try
            {
                int status = UnderNetBrowserGpuNative.GetStatus();
                int devReady = 0, targetReady = 0, pluginLoaded = 0, renderer = -1, wgcStatus = 0;
                try { devReady = UnderNetBrowserGpuNative.IsUnityDeviceReady(); } catch { }
                try { targetReady = UnderNetBrowserGpuNative.IsTargetReady(); } catch { }
                try { pluginLoaded = UnderNetBrowserGpuNative.IsUnityPluginLoaded(); } catch { }
                try { renderer = UnderNetBrowserGpuNative.GetRenderer(); } catch { }
                try { wgcStatus = UnderNetBrowserGpuNative.GetWgcStatus(); } catch { }

                // Once target is ready, try starting WGC (render thread init) and copy latest frames.
                if (targetReady != 0 && _wgcStartRequested && !_wgcStarted)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventWgcStart);
                    _wgcStarted = true;
                }

                // Do not recreate Unity Texture2D on WGC frame size changes; native shader blit scales into stable target.
                // (Keep stable destination resolution as requested.) If needed in future, handle via explicit config/resolution changes only.

                if (targetReady != 0 && _wgcStarted)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventWgcCopyLatest);
                }
                else
                {
                    // Fallback: animated native pattern until WGC active/ready
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventTargetUpdate);
                }

                if (status != _lastStatus || targetReady != _lastReady || Time.realtimeSinceStartup - _lastLog > 5f)
                {
                    _lastStatus = status;
                    _lastReady = targetReady;
                    _lastLog = Time.realtimeSinceStartup;
                    var err = UnderNetBrowserGpuNative.GetLastErrorString();
                    var wgcDiag = "";
                    try { wgcDiag = UnderNetBrowserGpuNative.GetWgcDiagString(); } catch { }
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Test] status={status} targetReady={targetReady} deviceReady={devReady} wgc={wgcStatus} pluginLoaded={pluginLoaded} gfx={SystemInfo.graphicsDeviceType} err='{err}' wgcDiag='{wgcDiag}'");
                }
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup - _lastLog > 5f)
                {
                    _lastLog = Time.realtimeSinceStartup;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] Tick failed: {ex.GetType().Name}: {ex.Message}");
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
                for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(20, 20, 30, 255);
                _unityTexture.SetPixels32(fill);
                _unityTexture.Apply(false, false);

                IntPtr nativePtr = _unityTexture.GetNativeTexturePtr();
                if (nativePtr == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Test] GetNativeTexturePtr returned null during resize/create.");
                    return false;
                }

                _width = width; _height = height;
                int setOk = UnderNetBrowserGpuNative.SetTargetTexture(nativePtr, _width, _height);
                if (log)
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Test] Unity-owned target texture ready ({_width}x{_height}) nativePtr=0x{nativePtr.ToInt64():X} setTarget={setOk} gfx={SystemInfo.graphicsDeviceType}");

                if (issueInitEvent && _renderEventFunc != IntPtr.Zero)
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventTargetInitUpdate);
                _lastReady = -1;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] CreateOrResizeTargetTexture failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_renderEventFunc != IntPtr.Zero)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventWgcStop);
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