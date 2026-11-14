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
        public static MelonPreferences_Entry<bool>? DistanceVolumeEnabled { get; private set; }
        public static MelonPreferences_Entry<float>? DistanceVolumeFullDistance { get; private set; }
        public static MelonPreferences_Entry<float>? DistanceVolumeSilentDistance { get; private set; }

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

        // Advanced WebView2/Chromium browser arguments (testing). Applied when the WebView2 environment is created (cold start / hard reset).
        public static MelonPreferences_Entry<bool>? WebView2ArgDisableBackgroundTimerThrottling { get; private set; }
        public static MelonPreferences_Entry<bool>? WebView2ArgDisableRendererBackgrounding { get; private set; }
        public static MelonPreferences_Entry<bool>? WebView2ArgAutoplayNoUserGesture { get; private set; }
        public static MelonPreferences_Entry<bool>? WebView2ArgDisableFrameRateLimit { get; private set; }
        public static MelonPreferences_Entry<string>? WebView2CustomBrowserArguments { get; private set; }

        // --- Adaptive Rendering (visual quality/performance; independent of audio distance-volume) ---
        public static MelonPreferences_Entry<bool>? AdaptiveRenderingEnabled { get; private set; }
        public static MelonPreferences_Entry<bool>? AdaptiveDistanceQualityScaling { get; private set; }
        public static MelonPreferences_Entry<bool>? AdaptiveVisualStandbyEnabled { get; private set; }
        public static MelonPreferences_Entry<bool>? AdaptiveResolutionScalingEnabled { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveFullDistance { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveReducedDistance { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveLowDistance { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveStandbyDistance { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveHysteresisDistance { get; private set; }
        public static MelonPreferences_Entry<float>? AdaptiveTransitionDelaySeconds { get; private set; }
        public static MelonPreferences_Entry<int>? AdaptiveReducedCaptureFps { get; private set; }
        public static MelonPreferences_Entry<int>? AdaptiveLowCaptureFps { get; private set; }

        // --- UI ---
        public static MelonPreferences_Entry<float>? ChromeHeightFraction { get; private set; }

        public static void Initialize()
        {
            // Keep stable identifier for Mod Manager & Phone App settings discovery/compatibility.
            // (UserData/runtime paths use UnderNetBrowser; prefs category ID remains FireskyUnderNetBrowser.)
            _category = MelonPreferences.CreateCategory("FireskyUnderNetBrowser", "Firesky UnderNet Browser");

            // --- Basic ---
            Enabled = _category.CreateEntry(
                "Enabled", true,
                "Enable App",
                "Enable or disable the UnderNet TV app.");

            HomeUrl = _category.CreateEntry(
                "HomeUrl", "undernet:home",
                "Home URL",
                "Default page loaded when UnderNet opens. Use undernet:home for the built-in homepage, or home:<app-id> for a local app in UserData/UnderNetBrowser/root/Apps/<app-id>/index.html.");

            BasicAdBlockEnabled = _category.CreateEntry(
                "BasicAdBlockEnabled", true,
                "Basic Ad/Tracker Blocking",
                "Enable built-in basic ad and tracker request blocking in WebView2.");

            AutoConsentEnabled = _category.CreateEntry(
                "AutoConsentEnabled", false,
                "Auto-Accept Consent Dialogs",
                "Automatically accept or dismiss some website consent/cookie dialogs (may cause unintended clicks on some sites).");

            DistanceVolumeEnabled = _category.CreateEntry(
                "DistanceVolumeEnabled", true,
                "Distance-Based Audio Volume",
                "Attenuate browser/WebView2 audio based on distance from the TV (best-effort process audio; not spatial/3D audio).");

            DistanceVolumeFullDistance = _category.CreateEntry(
                "DistanceVolumeFullDistance", 7.5f,
                "Distance Volume: Full Distance (m)",
                "Distance in meters at or below which browser audio is at full volume when distance-based volume is enabled.");

            DistanceVolumeSilentDistance = _category.CreateEntry(
                "DistanceVolumeSilentDistance", 35.0f,
                "Distance Volume: Silent Distance (m)",
                "Distance in meters at or beyond which browser audio is muted when distance-based volume is enabled. Beyond this range, detached remote arrow controls are also disabled.");

            // --- Controls / Keybinds ---
            KeyToggleCursor = _category.CreateEntry(
                "KeyToggleCursor", "F3",
                "Keybind: Toggle Cursor",
                "Unity KeyCode name for toggling cursor visibility/lock (works globally, including outside the TV).");

            KeyReload = _category.CreateEntry(
                "KeyReload", "F5",
                "Keybind: Reload Page",
                "Unity KeyCode name for reloading the current page while the browser/TV app is active.");

            KeyGameInputMode = _category.CreateEntry(
                "KeyGameInputMode", "F6",
                "Keybind: Game Input Mode",
                "Unity KeyCode name for toggling Game Input Mode while the browser/TV app is active.");

            // --- Advanced / Display ---
            GpuCompositionRenderWidth = _category.CreateEntry(
                "GpuCompositionRenderWidth", 1920,
                "Advanced: Render Width",
                "Internal WebView2 composition render width (e.g. 1280, 1920). Restart/reopen may be required.");

            GpuCompositionRenderHeight = _category.CreateEntry(
                "GpuCompositionRenderHeight", 1080,
                "Advanced: Render Height",
                "Internal WebView2 composition render height (e.g. 720, 1080). Restart/reopen may be required.");

            // --- Advanced / Diagnostics & Developer ---
            DebugLogging = _category.CreateEntry(
                "DebugLogging", false,
                "Advanced: Verbose Debug Logging",
                "Enable verbose debug logging for troubleshooting.");

            AlwaysLogDiagnostics = _category.CreateEntry(
                "AlwaysLogDiagnostics", false,
                "Advanced: Key Diagnostics Logging",
                "Always log key diagnostics (input/HUD/navigation) even when verbose debug logging is disabled. Keep disabled for normal use to reduce log noise/performance impact.");

            PerformanceDiagnosticsEnabled = _category.CreateEntry(
                "PerformanceDiagnosticsEnabled", false,
                "Advanced: Performance Diagnostics",
                "Enable periodic performance/memory diagnostics logging (opt-in, low overhead).");

            DiagnosticsSampleIntervalSeconds = _category.CreateEntry(
                "DiagnosticsSampleIntervalSeconds", 1.0f,
                "Advanced: Diagnostics Sample Interval (s)",
                "Sampling interval in seconds for performance diagnostics (periodic, not per-frame).");

            DiagnosticsOutputDirectory = _category.CreateEntry(
                "DiagnosticsOutputDirectory", "",
                "Advanced: Diagnostics Output Folder",
                "Custom output folder for diagnostics files. Leave empty to use UserData/UnderNetBrowser/Diagnostics.");

            // --- Advanced / WebView2 Browser Arguments (testing) ---
            WebView2ArgDisableBackgroundTimerThrottling = _category.CreateEntry(
                "WebView2ArgDisableBackgroundTimerThrottling", false,
                "Testing: Disable Background Timer Throttling",
                "Adds --disable-background-timer-throttling. May help background timers/media in some cases but can increase CPU usage. Applied on WebView2 environment creation (restart/reopen or Home hard-reset).");

            WebView2ArgDisableRendererBackgrounding = _category.CreateEntry(
                "WebView2ArgDisableRendererBackgrounding", false,
                "Testing: Disable Renderer Backgrounding",
                "Adds --disable-renderer-backgrounding. May affect background behavior; can increase resource usage. Applied on WebView2 environment creation (restart/reopen or Home hard-reset).");

            WebView2ArgAutoplayNoUserGesture = _category.CreateEntry(
                "WebView2ArgAutoplayNoUserGesture", false,
                "Testing: Autoplay Without User Gesture",
                "Adds --autoplay-policy=no-user-gesture-required. May allow media to autoplay more readily; site behavior may change. Applied on WebView2 environment creation (restart/reopen or Home hard-reset).");

            WebView2ArgDisableFrameRateLimit = _category.CreateEntry(
                "WebView2ArgDisableFrameRateLimit", false,
                "Testing: Disable Frame Rate Limit",
                "Adds --disable-frame-rate-limit. Test only: may increase GPU/CPU/power usage and usually does not improve captured TV playback. Applied on WebView2 environment creation (restart/reopen or Home hard-reset).");

            WebView2CustomBrowserArguments = _category.CreateEntry(
                "WebView2CustomBrowserArguments", "",
                "Testing: Custom WebView2 Browser Arguments",
                "Optional additional Chromium/WebView2 switches appended to the environment (space-separated), e.g. --enable-features=... Use for testing only; invalid/risky flags can break rendering, input, security, or stability. Applied on WebView2 environment creation (restart/reopen or Home hard-reset).");

            // --- Adaptive Rendering ---
            AdaptiveRenderingEnabled = _category.CreateEntry(
                "AdaptiveRenderingEnabled", true,
                "Adaptive Rendering: Enable",
                "Reduce TV browser visual workload when far away or not meaningfully visible (independent of audio). Keeps WebView2 session/audio/state; does not restart the browser.");

            AdaptiveDistanceQualityScaling = _category.CreateEntry(
                "AdaptiveDistanceQualityScaling", true,
                "Adaptive Rendering: Distance Quality Scaling",
                "Use player-to-TV distance to select visual quality tiers (capture throttling/resolution/standby).");

            AdaptiveVisualStandbyEnabled = _category.CreateEntry(
                "AdaptiveVisualStandbyEnabled", true,
                "Adaptive Rendering: Visual Standby",
                "At far distance tiers, pause expensive visual capture/frame updates and show the last frame while keeping WebView2, page state, and audio running.");

            AdaptiveResolutionScalingEnabled = _category.CreateEntry(
                "AdaptiveResolutionScalingEnabled", false,
                "Adaptive Rendering: Resolution Scaling (Experimental)",
                "Allow lower composition/render resolution in reduced tiers. Disabled by default for safety/compatibility; applied conservatively to avoid session churn.");

            AdaptiveFullDistance = _category.CreateEntry(
                "AdaptiveFullDistance", 7.0f,
                "Adaptive Rendering: Full Quality Distance (m)",
                "Distance at/below which full visual quality is preferred.");

            AdaptiveReducedDistance = _category.CreateEntry(
                "AdaptiveReducedDistance", 14.0f,
                "Adaptive Rendering: Reduced Quality Distance (m)",
                "Distance beyond full-quality range where capture/update rate is reduced.");

            AdaptiveLowDistance = _category.CreateEntry(
                "AdaptiveLowDistance", 22.0f,
                "Adaptive Rendering: Low Quality Distance (m)",
                "Distance where capture/update rate is reduced further (and optional resolution scaling may apply if enabled).");

            AdaptiveStandbyDistance = _category.CreateEntry(
                "AdaptiveStandbyDistance", 30.0f,
                "Adaptive Rendering: Visual Standby Distance (m)",
                "Distance beyond which visual standby may activate (if enabled), pausing visual capture/updates while keeping browser/audio alive.");

            AdaptiveHysteresisDistance = _category.CreateEntry(
                "AdaptiveHysteresisDistance", 1.0f,
                "Adaptive Rendering: Hysteresis Distance (m)",
                "Buffer distance used to prevent rapid tier switching near boundaries.");

            AdaptiveTransitionDelaySeconds = _category.CreateEntry(
                "AdaptiveTransitionDelaySeconds", 0.5f,
                "Adaptive Rendering: Transition Delay (s)",
                "Minimum time a new visual tier condition must hold before switching (reduces oscillation). Upgrades to higher quality restore quickly.");

            AdaptiveReducedCaptureFps = _category.CreateEntry(
                "AdaptiveReducedCaptureFps", 15,
                "Adaptive Rendering: Reduced Tier Capture FPS",
                "Target capture/update rate for the Reduced tier (1-60). Lower values save more CPU/GPU/capture work.");

            AdaptiveLowCaptureFps = _category.CreateEntry(
                "AdaptiveLowCaptureFps", 8,
                "Adaptive Rendering: Low Tier Capture FPS",
                "Target capture/update rate for the Low tier (1-60). Lower values save more CPU/GPU/capture work.");

            // --- UI ---
            ChromeHeightFraction = _category.CreateEntry(
                "ChromeHeightFraction", 0.06f,
                "UI: Chrome Height",
                "Height of the top browser chrome/address bar area as a fraction of the TV screen height (0.04-0.20). Default 0.08.");

            // Ensure saved preferences are applied to created entries (deterministic activation across restarts/load order).
            try { _category?.LoadFromFile(false); } catch { try { MelonPreferences.Load(); } catch { } }

            // Persist newly registered entries/defaults so they appear in the MelonPreferences cfg without requiring Mod Manager to save first.
            try { MelonPreferences.Save(); } catch { try { _category?.SaveToFile(false); } catch { } }
        }

        private static string NormalizeLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? string.Empty;
            // Avoid duplicate prefixes when callers/log sinks already include module/mod tags.
            if (message.StartsWith("[Firesky UnderNet Browser]", StringComparison.Ordinal)) return message;
            if (message.StartsWith("[UnderNetBrowser]", StringComparison.Ordinal)) return message;
            return $"[UnderNetBrowser] {message}";
        }

        public static void LogAlways(string message)
        {
            MelonLogger.Msg(NormalizeLogMessage(message));
        }

        public static void Log(string message)
        {
            if (DebugLogging?.Value == true)
                MelonLogger.Msg(NormalizeLogMessage(message));
        }

        public static void LogError(string message)
        {
            MelonLogger.Error(NormalizeLogMessage(message));
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

        public static float GetChromeHeightFraction()
        {
            try
            {
                float v = ChromeHeightFraction?.Value ?? 0.06f;
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0.08f;
                return UnityEngine.Mathf.Clamp(v, 0.04f, 0.20f);
            }
            catch { return 0.08f; }
        }

        /// <summary>
        /// Builds AdditionalBrowserArguments for WebView2 environment creation from testing toggles and custom args.
        /// Applied only on environment creation (cold start / hard reset / restart).
        /// </summary>
        public static string BuildWebView2AdditionalBrowserArguments()
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>(8);
                if (WebView2ArgDisableBackgroundTimerThrottling?.Value == true) parts.Add("--disable-background-timer-throttling");
                if (WebView2ArgDisableRendererBackgrounding?.Value == true) parts.Add("--disable-renderer-backgrounding");
                if (WebView2ArgAutoplayNoUserGesture?.Value == true) parts.Add("--autoplay-policy=no-user-gesture-required");
                if (WebView2ArgDisableFrameRateLimit?.Value == true) parts.Add("--disable-frame-rate-limit");

                var custom = WebView2CustomBrowserArguments?.Value;
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    // Basic normalization: collapse newlines/tabs to spaces; do not attempt to parse/quote shell-style args here.
                    custom = custom.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
                    if (custom.Length > 0) parts.Add(custom);
                }

                return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
