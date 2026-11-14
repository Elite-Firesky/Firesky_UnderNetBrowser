using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// P/Invoke surface for the experimental native GPU renderer DLL (UnderNetGpuRenderer.dll).
    /// Used for Phase 3 (Unity external texture animated test) and future Target A WGC path.
    /// </summary>
    internal static class UnderNetBrowserGpuNative
    {
        public const string DllName = "UnderNetGpuRenderer";
        private static bool _loadAttempted;
        private static bool _loaded;
        private static string _loadedPath = "";
        private static readonly object _loadLock = new object();

        public static bool EnsureLoaded()
        {
            lock (_loadLock)
            {
                if (_loadAttempted) return _loaded;
                _loadAttempted = true;

                var candidates = GetCandidatePaths();
                foreach (var path in candidates)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
#if NET6_0_OR_GREATER
                        System.Runtime.InteropServices.NativeLibrary.Load(path);
#else
                        if (LoadLibrary(path) == IntPtr.Zero) continue;
#endif
                        _loaded = true;
                        _loadedPath = path;
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [GPU-Test] Loaded native DLL: {path}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] Failed loading '{path}': {ex.GetType().Name}: {ex.Message}");
                    }
                }

                MelonLogger.Warning($"[Firesky UnderNet Browser] [GPU-Test] Native DLL not found. Probed: {string.Join(" | ", candidates)}");
                return false;
            }
        }

        public static string LoadedPath => _loadedPath;

        private static IEnumerable<string> GetCandidatePaths()
        {
            var list = new List<string>();
            try
            {
                var dataPath = Application.dataPath; // e.g. ...\Schedule I_Data
                if (!string.IsNullOrEmpty(dataPath))
                {
                    list.Add(Path.Combine(dataPath, "Plugins", "x86_64", "UnderNetGpuRenderer.dll"));
                    list.Add(Path.Combine(dataPath, "Plugins", "UnderNetGpuRenderer.dll"));
                }
            }
            catch { }
            try { list.Add(Path.Combine(AppContext.BaseDirectory ?? "", "Schedule I_Data", "Plugins", "x86_64", "UnderNetGpuRenderer.dll")); } catch { }
            try { list.Add(Path.Combine(AppContext.BaseDirectory ?? "", "Mods", "UnderNetGpuRenderer.dll")); } catch { }
            try { list.Add(Path.Combine(Directory.GetCurrentDirectory(), "Schedule I_Data", "Plugins", "x86_64", "UnderNetGpuRenderer.dll")); } catch { }
            try { list.Add(Path.GetFullPath("UnderNetGpuRenderer.dll")); } catch { }
            return list;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public enum Status : int
        {
            Ok = 0,
            NotInitialized = -1,
            NoD3D11Device = -2,
            AlreadyExists = -3,
            CreateTextureFailed = -4,
            Invalid = -5,
        }

        public const int EventCreateTestTexture = 1;
        public const int EventUpdateTestTexture = 2;
        public const int EventDestroyTestTexture = 3;
        public const int EventTargetInitUpdate = 20;
        public const int EventTargetUpdate = 21;
        public const int EventTargetRelease = 22;
        public const int EventWgcStart = 30;
        public const int EventWgcCopyLatest = 31;
        public const int EventWgcStop = 32;
        public const int EventCompStart = 40;
        public const int EventCompCopyLatest = 41;
        public const int EventCompStop = 42;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void LogCallback(int level, [MarshalAs(UnmanagedType.LPStr)] string message);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_SetLogCallback")]
        public static extern void SetLogCallback(LogCallback cb);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetStatus")]
        public static extern int GetStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetLastError")]
        public static extern void GetLastError([Out] byte[] buffer, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_IsUnityDeviceReady")]
        public static extern int IsUnityDeviceReady();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_IsUnityPluginLoaded")]
        public static extern int IsUnityPluginLoaded();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetRenderer")]
        public static extern int GetRenderer();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetTestTexturePtr")]
        public static extern IntPtr GetTestTexturePtr();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetTestTextureWidth")]
        public static extern int GetTestTextureWidth();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetTestTextureHeight")]
        public static extern int GetTestTextureHeight();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_SetTargetTexture")]
        public static extern int SetTargetTexture(IntPtr nativeTexturePtr, int width, int height);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_ClearTargetTexture")]
        public static extern void ClearTargetTexture();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_IsTargetReady")]
        public static extern int IsTargetReady();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_SetWgcCaptureHwnd")]
        public static extern int SetWgcCaptureHwnd(IntPtr hwnd);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetWgcStatus")]
        public static extern int GetWgcStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetWgcDiag")]
        public static extern void GetWgcDiag([Out] byte[] buffer, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetWgcFrameSize")]
        public static extern void GetWgcFrameSize(out int width, out int height);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, EntryPoint = "UnGpu_CompSetParams")]
        public static extern int CompSetParams(string initialUrl, int width, int height, string? userDataFolder);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, EntryPoint = "UnGpu_CompSetAdditionalBrowserArguments")]
        public static extern int CompSetAdditionalBrowserArguments(string? additionalBrowserArguments);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, EntryPoint = "UnGpu_CompNavigate")]
        public static extern int CompNavigate(string url);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGoBack")]
        public static extern void CompGoBack();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGoForward")]
        public static extern void CompGoForward();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompReload")]
        public static extern void CompReload();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGetStatus")]
        public static extern int CompGetStatus();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGetDiag")]
        public static extern void CompGetDiag([Out] byte[] buffer, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGetFrameSize")]
        public static extern void CompGetFrameSize(out int width, out int height);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSendMouse")]
        public static extern void CompSendMouse(int kind, int vk, uint data, int x, int y); // kind: 0 move,1 left down,2 left up,3 wheel,4 right down,5 right up (vk bitmask move: 1=left,2=right)

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, EntryPoint = "UnGpu_CompGetCurrentUrl")]
        public static extern void CompGetCurrentUrl([Out] char[] buffer, int bufferChars);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompIsFullscreen")]
        public static extern int CompIsFullscreen();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompShutdown")]
        public static extern void CompShutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSetCapturePaused")]
        public static extern void CompSetCapturePaused(int paused);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSetAdaptiveVisualState")]
        public static extern void CompSetAdaptiveVisualState(int captureIntervalMs, int visualStandby);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGetAdaptiveCaptureIntervalMs")]
        public static extern int CompGetAdaptiveCaptureIntervalMs();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompIsAdaptiveVisualStandby")]
        public static extern int CompIsAdaptiveVisualStandby();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSendKey")]
        public static extern void CompSendKey(uint msg, ulong wParam, ulong lParam);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSendText", CharSet = CharSet.Unicode)]
        public static extern void CompSendText(string text);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompExecuteScript", CharSet = CharSet.Unicode)]
        public static extern void CompExecuteScript(string script);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompRequestFocus")]
        public static extern void CompRequestFocus();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSetZoomFactor")]
        public static extern void CompSetZoomFactor(double zoomFactor);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompGetZoomFactor")]
        public static extern double CompGetZoomFactor();

        // Best-effort process-level audio attenuation for WebView2 browser process tree (distance-based volume testing).
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_CompSetDistanceAudioVolume")]
        public static extern void CompSetDistanceAudioVolume(int enabled, float volume01);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "UnGpu_GetRenderEventFuncPtr")]
        public static extern IntPtr GetRenderEventFuncPtr();

        // Unity native plugin export (if loaded as Unity plugin) - also callable directly
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GetRenderEventFunc")]
        public static extern IntPtr GetRenderEventFunc();

        public static string GetLastErrorString()
        {
            try
            {
                var buf = new byte[512];
                GetLastError(buf, buf.Length);
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;
                return System.Text.Encoding.UTF8.GetString(buf, 0, len);
            }
            catch { return ""; }
        }

        public static string GetWgcDiagString()
        {
            try
            {
                var buf = new byte[512];
                GetWgcDiag(buf, buf.Length);
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;
                return System.Text.Encoding.UTF8.GetString(buf, 0, len);
            }
            catch { return ""; }
        }

        public static string GetCompDiagString()
        {
            try
            {
                var buf = new byte[512];
                CompGetDiag(buf, buf.Length);
                int len = Array.IndexOf(buf, (byte)0);
                if (len < 0) len = buf.Length;
                return System.Text.Encoding.UTF8.GetString(buf, 0, len);
            }
            catch { return ""; }
        }

        public static string GetCompCurrentUrlString()
        {
            try
            {
                var buf = new char[2048];
                CompGetCurrentUrl(buf, buf.Length);
                int len = Array.IndexOf(buf, '\0');
                if (len < 0) len = buf.Length;
                return new string(buf, 0, len);
            }
            catch { return ""; }
        }
    }
}