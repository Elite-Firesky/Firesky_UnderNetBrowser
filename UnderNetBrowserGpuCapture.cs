using System;
using MelonLoader;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Phase 4B (PoC): capture an existing HWND (WebView2 host) via native WGC and present into a Unity-owned texture.
    /// Uses the proven render-thread shader blit path (no CPU readback, no JPEG/managed per-frame uploads in this path).
    /// JPEG/screencast fallback remains in the browser/WebView for non-GPU mode or failure.
    /// </summary>
    internal sealed class UnderNetBrowserGpuCapture : IDisposable
    {
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

        public Texture2D? Texture => _unityTexture;
        public bool IsReady => _unityTexture != null && _wgcStarted;

        public bool TryStart(IntPtr hwnd, int targetWidth = 1280, int targetHeight = 720)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Browser] Cannot start WGC capture: HWND is zero.");
                    return false;
                }
                _width = Math.Max(2, targetWidth);
                _height = Math.Max(2, targetHeight);
                _wgcHwnd = hwnd;

                UnderNetBrowserGpuNative.EnsureLoaded();

                try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFunc(); } catch { _renderEventFunc = IntPtr.Zero; }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    try { _renderEventFunc = UnderNetBrowserGpuNative.GetRenderEventFuncPtr(); } catch { _renderEventFunc = IntPtr.Zero; }
                }
                if (_renderEventFunc == IntPtr.Zero)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Browser] Native render event function not available.");
                    return false;
                }

                if (!CreateOrResizeTargetTexture(_width, _height, issueInitEvent: true, log: true))
                    return false;

                _createIssued = true;
                UnderNetBrowserGpuNative.SetWgcCaptureHwnd(_wgcHwnd);
                _wgcStartRequested = true;
                MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Browser] Requested WGC capture for WebView host hwnd=0x{_wgcHwnd.ToInt64():X} target={_width}x{_height}");
                return true;
            }
            catch (DllNotFoundException)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Browser] UnderNetGpuRenderer.dll not found/unloadable. LoadedPath='{UnderNetBrowserGpuNative.LoadedPath}'.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Browser] Start failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public void Tick()
        {
            if (_disposed || !_createIssued || _renderEventFunc == IntPtr.Zero) return;
            try
            {
                int status = UnderNetBrowserGpuNative.GetStatus();
                int devReady = 0, targetReady = 0, wgcStatus = 0;
                try { devReady = UnderNetBrowserGpuNative.IsUnityDeviceReady(); } catch { }
                try { targetReady = UnderNetBrowserGpuNative.IsTargetReady(); } catch { }
                try { wgcStatus = UnderNetBrowserGpuNative.GetWgcStatus(); } catch { }

                if (targetReady != 0 && _wgcStartRequested && !_wgcStarted)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventWgcStart);
                    _wgcStarted = true;
                }

                if (targetReady != 0 && _wgcStarted)
                {
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventWgcCopyLatest);
                }
                else
                {
                    // Keep target initialized/ready path warm until capture starts
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
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Browser] status={status} targetReady={targetReady} deviceReady={devReady} wgc={wgcStatus} gfx={SystemInfo.graphicsDeviceType} err='{err}' wgcDiag='{wgcDiag}'");
                }
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup - _lastLog > 5f)
                {
                    _lastLog = Time.realtimeSinceStartup;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Browser] Tick failed: {ex.GetType().Name}: {ex.Message}");
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
                    MelonLogger.Warning("[Firesky UnderNet Browser] [GPU-Browser] GetNativeTexturePtr returned null.");
                    return false;
                }

                _width = width; _height = height;
                int setOk = UnderNetBrowserGpuNative.SetTargetTexture(nativePtr, _width, _height);
                if (log)
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Browser] Unity-owned target texture ready ({_width}x{_height}) nativePtr=0x{nativePtr.ToInt64():X} setTarget={setOk} gfx={SystemInfo.graphicsDeviceType}");

                if (issueInitEvent && _renderEventFunc != IntPtr.Zero)
                    GL.IssuePluginEvent(_renderEventFunc, UnderNetBrowserGpuNative.EventTargetInitUpdate);
                _lastReady = -1;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Browser] CreateOrResizeTargetTexture failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

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
