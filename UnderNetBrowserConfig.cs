using System;
using MelonLoader;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Configuration for Firesky UnderNet Browser (WebView2-only playback). Entries are exposed in Mod Manager settings.
    /// Legacy yt-dlp/FFmpeg/download/cache/VideoPlayer settings removed in Stage E.
    /// </summary>
    public static class UnderNetBrowserConfig
    {
        private static MelonPreferences_Category? _category;

        public static MelonPreferences_Entry<bool>? Enabled { get; private set; }
        public static MelonPreferences_Entry<bool>? DebugLogging { get; private set; }
        public static MelonPreferences_Entry<string>? HomeUrl { get; private set; }

        public static MelonPreferences_Entry<bool>? BasicAdBlockEnabled { get; private set; }

        public static MelonPreferences_Entry<bool>? AutoConsentEnabled { get; private set; }
        public static MelonPreferences_Entry<bool>? AlwaysLogDiagnostics { get; private set; }
        public static MelonPreferences_Entry<bool>? SpatialAudioPocEnabled { get; private set; }

        public static MelonPreferences_Entry<int>? GpuCompositionRenderWidth { get; private set; }
        public static MelonPreferences_Entry<int>? GpuCompositionRenderHeight { get; private set; }

        // Keybinds (configurable). Stored as KeyCode names (e.g. F3, F5, F6).
        public static MelonPreferences_Entry<string>? KeyToggleCursor { get; private set; }
        public static MelonPreferences_Entry<string>? KeyReload { get; private set; }
        public static MelonPreferences_Entry<string>? KeyGameInputMode { get; private set; }

        // Stage 4 diagnostics (lightweight, opt-in). No rebuild required to enable/disable.
        public static MelonPreferences_Entry<bool>? PerformanceDiagnosticsEnabled { get; private set; }
        public static MelonPreferences_Entry<float>? DiagnosticsSampleIntervalSeconds { get; private set; }
        public static MelonPreferences_Entry<string>? DiagnosticsOutputDirectory { get; private set; }

        public static void Initialize()
        {
            _category = MelonPreferences.CreateCategory("FireskyUnderNetBrowser");

            Enabled = _category.CreateEntry("Enabled", true, "Enable UnderNet app");
            DebugLogging = _category.CreateEntry("DebugLogging", false, "Enable verbose debug logging");
            AlwaysLogDiagnostics = _category.CreateEntry("AlwaysLogDiagnostics", true, "Always log key diagnostics (input/HUD/navigation)");
            SpatialAudioPocEnabled = _category.CreateEntry("SpatialAudioPocEnabled", false, "[DEV/PoC] Stage 3 spatial audio proof-of-concept (off by default). No effect unless a validated PoC implementation is enabled; preserves current browser audio path.");
            HomeUrl = _category.CreateEntry("HomeUrl", "undernet:home", "Default URL loaded when the TV app opens (undernet:home for built-in homepage)");
            BasicAdBlockEnabled = _category.CreateEntry("BasicAdBlockEnabled", true, "Enable basic built-in ad/tracker request blocking (WebView2)");
            AutoConsentEnabled = _category.CreateEntry("AutoConsentEnabled", false, "Auto-accept/dismiss some consent dialogs (can cause unwanted clicks)");
            // UnderNet now runs in WebView2 Composition mode only (GPU composition browser).
            GpuCompositionRenderWidth = _category.CreateEntry("GpuCompositionRenderWidth", 1920, "Composition browser internal render width (e.g. 1280/1920). Restart/reopen may be required.");
            GpuCompositionRenderHeight = _category.CreateEntry("GpuCompositionRenderHeight", 1080, "Composition browser internal render height (e.g. 720/1080). Restart/reopen may be required.");

            // Keybinds
            KeyToggleCursor = _category.CreateEntry("KeyToggleCursor", "F3", "Toggle cursor visibility/lock (works globally, including outside TV). Unity KeyCode name.");
            KeyReload = _category.CreateEntry("KeyReload", "F5", "Reload current page while browser/TV app is active. Unity KeyCode name.");
            KeyGameInputMode = _category.CreateEntry("KeyGameInputMode", "F6", "Toggle Game Input Mode while browser/TV app is active. Unity KeyCode name.");

            // Stage 4 diagnostics (opt-in, low overhead). Samples periodically and records phase markers (A-F).
            PerformanceDiagnosticsEnabled = _category.CreateEntry("PerformanceDiagnosticsEnabled", false, "Enable Stage 4 performance/memory diagnostics logging (opt-in, low overhead).");
            DiagnosticsSampleIntervalSeconds = _category.CreateEntry("DiagnosticsSampleIntervalSeconds", 1.0f, "Diagnostics sample interval in seconds (periodic; not per-frame).");
            DiagnosticsOutputDirectory = _category.CreateEntry("DiagnosticsOutputDirectory", "", "Diagnostics output folder (empty = UserData/FireskyUnderNetBrowser/Diagnostics).");

            // Boundary diagnostics (Stage 3 activation): confirm registered/read values at startup.
            try
            {
                MelonLogger.Msg($"[Firesky UnderNet Browser] [SpatialPoC] Config.Initialize: SpatialAudioPocEnabled registered value={SpatialAudioPocEnabled?.Value} default={SpatialAudioPocEnabled?.DefaultValue} (category={_category?.Identifier}) beforeLoad");
            }
            catch { }

            // Ensure saved preferences are applied to created entries (deterministic activation across restarts/load order).
            try { _category?.LoadFromFile(false); } catch { try { MelonPreferences.Load(); } catch { } }

            try
            {
                MelonLogger.Msg($"[Firesky UnderNet Browser] [SpatialPoC] Config.Initialize: SpatialAudioPocEnabled value={SpatialAudioPocEnabled?.Value} default={SpatialAudioPocEnabled?.DefaultValue} (category={_category?.Identifier}) afterLoad");
            }
            catch { }
        }

        public static void LogAlways(string message)
        {
            MelonLogger.Msg($"[Firesky UnderNet Browser] {message}");
        }

        public static void Log(string message)
        {
            if (DebugLogging?.Value == true)
                MelonLogger.Msg($"[Firesky UnderNet Browser] {message}");
        }

        public static void LogError(string message)
        {
            MelonLogger.Error($"[Firesky UnderNet Browser] {message}");
        }

        public static UnityEngine.KeyCode GetKey(UnityEngine.KeyCode fallback, MelonPreferences_Entry<string>? entry)
        {
            try
            {
                string? name = entry?.Value;
                if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse<UnityEngine.KeyCode>(name.Trim(), true, out var kc))
                    return kc;
            }
            catch { }
            return fallback;
        }
    }
}
