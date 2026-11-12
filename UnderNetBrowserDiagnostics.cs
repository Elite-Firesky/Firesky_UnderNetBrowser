using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Stage 4 lightweight diagnostics: periodic sampling, phase markers (A-F), CSV log + session summary.
    /// Opt-in via config; avoids per-frame logging and expensive forced profiling.
    /// </summary>
    internal static class UnderNetBrowserDiagnostics
    {
        internal enum Phase
        {
            A_GameplayBaseline = 0,
            B_TvInteraction = 1,
            C_BrowserLoading = 2,
            D_BrowserActive = 3,
            E_GameplayResumed = 4,
            F_Recovery = 5
        }

        private static readonly object _lock = new();
        private static bool _enabled;
        private static bool _sessionStarted;
        private static string? _logPath;
        private static string? _summaryPath;
        private static StreamWriter? _writer;
        private static float _sampleInterval = 1f;
        private static float _nextSampleRealtime;
        private static float _sessionStartRealtime;
        private static float _sessionStartTime;
        private static Phase _currentPhase = Phase.A_GameplayBaseline;

        // Frame timing accumulator (sampled each frame when enabled; aggregated periodically)
        private static readonly List<float> _frameTimesMs = new(512);
        private static float _phaseStartRealtime;

        // Phase transition timestamps (realtime seconds since session start)
        private static readonly List<(float t, string evt, string detail)> _events = new();
        private static readonly Dictionary<Phase, List<SampleSnapshot>> _phaseSamples = new();

        // Recovery tracking
        private static bool _recoveryTracking;
        private static float _recoveryTargetBaselineFps;
        private static float _recoveryStableSince = -1f;
        private static float _recoveryDetectedAt = -1f;

        // Warnings (dedupe)
        private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

        // Event timing metrics (measurable lifecycle timings)
        private static float _tvInteractionAt = -1f;
        private static float _browserVisibleAt = -1f;
        private static float _webViewInitStartAt = -1f;
        private static float _webViewInitDoneAt = -1f;
        private static float _firstRenderedFrameAt = -1f;
        private static float _detachRequestedAt = -1f;
        private static float _gameplayResumedAt = -1f;

        private struct SampleSnapshot
        {
            public float SessionSec;
            public string Phase;
            public float FpsAvg;
            public float Fps1Low;
            public float FrameMsAvg;
            public float FrameMsP95;
            public float FrameMsP99;
            public float FrameMsMax;
            public long WorkingSet;
            public long PrivateBytes;
            public long GcTotalMemory;
            public int Gc0, Gc1, Gc2;
            public int Threads;
            public int Handles;
            public int WebView2Count;
            public long WebView2WorkingSet;
            public long WebView2PrivateBytes;
            public string Note;
        }

        public static bool IsEnabled => _enabled;

        public static void InitializeIfEnabled()
        {
            try
            {
                bool want = UnderNetBrowserConfig.PerformanceDiagnosticsEnabled?.Value == true;
                if (want && !_enabled) StartSession();
                else if (!want && _enabled) StopSession("disabled");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [Diagnostics] InitializeIfEnabled failed: {ex.Message}");
            }
        }

        public static void StartSession(string reason = "startup")
        {
            lock (_lock)
            {
                try
                {
                    if (_enabled) return;
                    _sampleInterval = Mathf.Clamp(UnderNetBrowserConfig.DiagnosticsSampleIntervalSeconds?.Value ?? 1f, 0.25f, 10f);
                    string dir = ResolveOutputDirectory();
                    Directory.CreateDirectory(dir);
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    _logPath = Path.Combine(dir, $"undernet_perf_{stamp}.csv");
                    _summaryPath = Path.Combine(dir, $"undernet_perf_{stamp}_summary.txt");
                    _writer = new StreamWriter(_logPath, false, new UTF8Encoding(false)) { AutoFlush = false };
                    _writer.WriteLine("session_sec,utc_iso,phase,event,detail,fps_avg,fps_1low,frame_ms_avg,frame_ms_p95,frame_ms_p99,frame_ms_max,proc_working_set,proc_private_bytes,gc_total_memory,gc0,gc1,gc2,threads,handles,webview2_count,webview2_working_set,webview2_private_bytes,note");

                    _enabled = true;
                    _sessionStarted = true;
                    _sessionStartRealtime = Time.realtimeSinceStartup;
                    _sessionStartTime = Time.time;
                    _nextSampleRealtime = Time.realtimeSinceStartup + _sampleInterval;
                    _phaseStartRealtime = Time.realtimeSinceStartup;
                    _currentPhase = Phase.A_GameplayBaseline;
                    _frameTimesMs.Clear();
                    _events.Clear();
                    _phaseSamples.Clear();
                    foreach (Phase p in Enum.GetValues(typeof(Phase))) _phaseSamples[p] = new List<SampleSnapshot>();
                    _warned.Clear();
                    _recoveryTracking = false; _recoveryStableSince = -1f; _recoveryDetectedAt = -1f; _recoveryTargetBaselineFps = 0f;
                    _tvInteractionAt = _browserVisibleAt = _webViewInitStartAt = _webViewInitDoneAt = _firstRenderedFrameAt = _detachRequestedAt = _gameplayResumedAt = -1f;

                    MarkEventInternal("session_start", reason);
                    MarkPhaseInternal(Phase.A_GameplayBaseline, "session_start");
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [Diagnostics] Started. Log='{_logPath}' interval={_sampleInterval:0.###}s");
                }
                catch (Exception ex)
                {
                    _enabled = false;
                    SafeCloseWriter();
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [Diagnostics] StartSession failed: {ex.Message}");
                }
            }
        }

        public static void StopSession(string reason = "stop")
        {
            lock (_lock)
            {
                if (!_enabled) return;
                try { MarkEventInternal("session_stop", reason); } catch { }
                try { WriteSummary(); } catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [Diagnostics] WriteSummary failed: {ex.Message}"); }
                _enabled = false;
                SafeCloseWriter();
                MelonLogger.Msg($"[Firesky UnderNet Browser] [Diagnostics] Stopped ({reason}). Summary='{_summaryPath}'");
            }
        }

        public static void OnFrame()
        {
            if (!_enabled) return;
            try
            {
                float dt = Time.unscaledDeltaTime;
                if (dt > 0f && dt < 10f)
                {
                    lock (_lock)
                    {
                        if (!_enabled) return;
                        _frameTimesMs.Add(dt * 1000f);
                        if (_frameTimesMs.Count > 6000) _frameTimesMs.RemoveRange(0, _frameTimesMs.Count - 4000);
                    }
                }

                if (Time.realtimeSinceStartup >= _nextSampleRealtime)
                {
                    lock (_lock)
                    {
                        if (!_enabled) return;
                        EmitSampleLocked("");
                        _nextSampleRealtime = Time.realtimeSinceStartup + _sampleInterval;
                    }
                }

                UpdateRecoveryTracking();
            }
            catch { }
        }

        public static void MarkPhase(Phase phase, string detail = "")
        {
            if (!_enabled) return;
            lock (_lock) { if (_enabled) MarkPhaseInternal(phase, detail); }
        }

        public static void MarkEvent(string evt, string detail = "")
        {
            if (!_enabled) return;
            lock (_lock) { if (_enabled) MarkEventInternal(evt, detail); }
        }

        public static void OnTvInteractionBegin()
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _tvInteractionAt = NowSec();
                MarkPhaseInternal(Phase.B_TvInteraction, "tv_interaction");
                MarkEventInternal("tv_interaction_begin", "player interacted with TV / browser opening");
            }
        }
        public static void OnBrowserLoadingBegin()
        {
            if (!_enabled) return;
            lock (_lock)
            {
                MarkPhaseInternal(Phase.C_BrowserLoading, "browser_loading");
                MarkEventInternal("browser_loading_begin", "composition/webview startup requested");
            }
        }
        public static void OnWebViewInitStart() { if (_enabled) lock (_lock) { _webViewInitStartAt = NowSec(); MarkEventInternal("webview_init_start", ""); } }
        public static void OnWebViewInitDone() { if (_enabled) lock (_lock) { _webViewInitDoneAt = NowSec(); MarkEventInternal("webview_init_done", ""); } }
        public static void OnBrowserVisible() { if (_enabled) lock (_lock) { _browserVisibleAt = NowSec(); MarkEventInternal("browser_visible", "UI/browser visible"); } }
        public static void OnFirstRenderedFrame()
        {
            if (!_enabled) return;
            lock (_lock)
            {
                if (_firstRenderedFrameAt >= 0f) return;
                _firstRenderedFrameAt = NowSec();
                MarkEventInternal("first_rendered_frame", "comp blit ok observed");
                MarkPhaseInternal(Phase.D_BrowserActive, "first_rendered_frame");
            }
        }
        public static void OnDetachRequested()
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _detachRequestedAt = NowSec();
                MarkEventInternal("detach_requested", "right-click/ESC detach -> gameplay resume requested");
            }
        }
        public static void OnGameplayResumed()
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _gameplayResumedAt = NowSec();
                MarkPhaseInternal(Phase.E_GameplayResumed, "gameplay_resumed");
                MarkEventInternal("gameplay_resumed", "TV input ownership released / gameplay restored");
                BeginRecoveryTrackingLocked();
            }
        }

        private static void BeginRecoveryTrackingLocked()
        {
            _recoveryTracking = true;
            _recoveryStableSince = -1f;
            _recoveryDetectedAt = -1f;
            var a = _phaseSamples.TryGetValue(Phase.A_GameplayBaseline, out var list) ? list : null;
            _recoveryTargetBaselineFps = (a != null && a.Count > 0) ? a.Average(s => s.FpsAvg) : 0f;
            MarkEventInternal("recovery_tracking_begin", $"targetBaselineFps={_recoveryTargetBaselineFps:0.##}");
        }

        private static void UpdateRecoveryTracking()
        {
            if (!_enabled || !_recoveryTracking) return;
            lock (_lock)
            {
                if (!_enabled || !_recoveryTracking) return;
                var samples = new List<SampleSnapshot>();
                if (_phaseSamples.TryGetValue(Phase.E_GameplayResumed, out var e)) samples.AddRange(e);
                if (_phaseSamples.TryGetValue(Phase.F_Recovery, out var f)) samples.AddRange(f);
                if (samples.Count < 2) return;
                float target = _recoveryTargetBaselineFps > 0 ? _recoveryTargetBaselineFps * 0.90f : 55f;
                var recent = samples.TakeLast(Math.Min(5, samples.Count)).ToList();
                float avg = recent.Average(s => s.FpsAvg);
                bool ok = avg >= target;
                float now = NowSec();
                if (ok)
                {
                    if (_recoveryStableSince < 0f) _recoveryStableSince = now;
                    if (_recoveryDetectedAt < 0f && (now - _recoveryStableSince) >= 3f)
                    {
                        _recoveryDetectedAt = now;
                        MarkPhaseInternal(Phase.F_Recovery, "baseline_recovered");
                        MarkEventInternal("recovery_detected", $"avgFps={avg:0.##} target>={target:0.##} at={_recoveryDetectedAt:0.###}s");
                        _recoveryTracking = false;
                    }
                }
                else { _recoveryStableSince = -1f; }
            }
        }

        private static void MarkPhaseInternal(Phase phase, string detail)
        {
            _currentPhase = phase;
            _phaseStartRealtime = Time.realtimeSinceStartup;
            MarkEventInternal("phase", $"{phase}|{detail}");
        }

        private static void MarkEventInternal(string evt, string detail)
        {
            float now = NowSec();
            _events.Add((now, evt ?? "", detail ?? ""));
            WriteRowLocked(now, evt ?? "", detail ?? "", null, "");
            try { _writer?.Flush(); } catch { }
        }

        private static void EmitSampleLocked(string note)
        {
            var snap = CaptureSnapshotLocked(note);
            if (!_phaseSamples.TryGetValue(_currentPhase, out var list)) { list = new List<SampleSnapshot>(); _phaseSamples[_currentPhase] = list; }
            list.Add(snap);
            WriteRowLocked(snap.SessionSec, "sample", "", snap, note);
            try { _writer?.Flush(); } catch { }
            EvaluateWarningsLocked(snap);
        }

        private static SampleSnapshot CaptureSnapshotLocked(string note)
        {
            var frames = _frameTimesMs.ToArray();
            _frameTimesMs.Clear();
            ComputeFrameStats(frames, out float avgMs, out float p95, out float p99, out float maxMs, out float fpsAvg, out float fps1Low);

            long ws = 0, priv = 0; int threads = 0, handles = 0;
            try
            {
                using var p = Process.GetCurrentProcess();
                p.Refresh();
                ws = SafeLong(() => p.WorkingSet64);
                priv = SafeLong(() => p.PrivateMemorySize64);
                threads = SafeInt(() => p.Threads.Count);
                handles = SafeInt(() => p.HandleCount);
            }
            catch { }

            long gcMem = 0; int gc0 = 0, gc1 = 0, gc2 = 0;
            try { gcMem = GC.GetTotalMemory(false); gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2); } catch { }
            GetWebView2Stats(out int wvCount, out long wvWs, out long wvPriv);

            return new SampleSnapshot
            {
                SessionSec = NowSec(), Phase = _currentPhase.ToString(),
                FpsAvg = fpsAvg, Fps1Low = fps1Low,
                FrameMsAvg = avgMs, FrameMsP95 = p95, FrameMsP99 = p99, FrameMsMax = maxMs,
                WorkingSet = ws, PrivateBytes = priv, GcTotalMemory = gcMem,
                Gc0 = gc0, Gc1 = gc1, Gc2 = gc2, Threads = threads, Handles = handles,
                WebView2Count = wvCount, WebView2WorkingSet = wvWs, WebView2PrivateBytes = wvPriv,
                Note = note ?? ""
            };
        }

        private static void ComputeFrameStats(float[] ms, out float avgMs, out float p95, out float p99, out float maxMs, out float fpsAvg, out float fps1Low)
        {
            avgMs = p95 = p99 = maxMs = 0f; fpsAvg = 0f; fps1Low = 0f;
            if (ms == null || ms.Length == 0) return;
            Array.Sort(ms);
            double sum = 0; maxMs = ms[ms.Length - 1];
            for (int i = 0; i < ms.Length; i++) sum += ms[i];
            avgMs = (float)(sum / ms.Length);
            p95 = PercentileSorted(ms, 0.95);
            p99 = PercentileSorted(ms, 0.99);
            fpsAvg = avgMs > 0.0001f ? 1000f / avgMs : 0f;
            int count1 = Math.Max(1, (int)Math.Ceiling(ms.Length * 0.01));
            double worstSum = 0;
            for (int i = ms.Length - count1; i < ms.Length; i++) worstSum += ms[i];
            float worstAvg = (float)(worstSum / count1);
            fps1Low = worstAvg > 0.0001f ? 1000f / worstAvg : 0f;
        }

        private static float PercentileSorted(float[] sortedMs, double p)
        {
            if (sortedMs.Length == 1) return sortedMs[0];
            double idx = (sortedMs.Length - 1) * p;
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            if (lo == hi) return sortedMs[lo];
            double t = idx - lo;
            return (float)(sortedMs[lo] + (sortedMs[hi] - sortedMs[lo]) * t);
        }

        private static void GetWebView2Stats(out int count, out long ws, out long priv)
        {
            count = 0; ws = 0; priv = 0;
            try
            {
                var procs = Process.GetProcessesByName("msedgewebview2");
                count = procs.Length;
                foreach (var p in procs)
                {
                    try { ws += SafeLong(() => p.WorkingSet64); priv += SafeLong(() => p.PrivateMemorySize64); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }

        private static void WriteRowLocked(float sessionSec, string evt, string detail, SampleSnapshot? snap, string note)
        {
            if (_writer == null) return;
            try
            {
                string utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                string phase = _currentPhase.ToString();
                var s = snap ?? default;
                bool has = snap.HasValue;
                _writer.WriteLine(string.Join(",",
                    F(sessionSec), Csv(utc), Csv(phase), Csv(evt), Csv(detail),
                    has ? F(s.FpsAvg) : "", has ? F(s.Fps1Low) : "",
                    has ? F(s.FrameMsAvg) : "", has ? F(s.FrameMsP95) : "", has ? F(s.FrameMsP99) : "", has ? F(s.FrameMsMax) : "",
                    has ? s.WorkingSet.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.PrivateBytes.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.GcTotalMemory.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.Gc0.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.Gc1.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.Gc2.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.Threads.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.Handles.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.WebView2Count.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.WebView2WorkingSet.ToString(CultureInfo.InvariantCulture) : "",
                    has ? s.WebView2PrivateBytes.ToString(CultureInfo.InvariantCulture) : "",
                    Csv(note ?? "")
                ));
            }
            catch { }
        }

        private static void EvaluateWarningsLocked(SampleSnapshot s)
        {
            try
            {
                if (s.FpsAvg > 0 && s.FpsAvg < 30f) WarnOnce($"low_fps_{_currentPhase}", $"[Diagnostics][WARN] Sustained low FPS in {_currentPhase}: avg={s.FpsAvg:0.##} 1%low={s.Fps1Low:0.##} p99={s.FrameMsP99:0.##}ms");
                if (s.FrameMsP99 > 50f) WarnOnce($"spike_p99_{_currentPhase}_{(int)(s.SessionSec/10)}", $"[Diagnostics][WARN] Frame-time spike pressure in {_currentPhase}: p99={s.FrameMsP99:0.##}ms max={s.FrameMsMax:0.##}ms");
                if (_phaseSamples.TryGetValue(Phase.A_GameplayBaseline, out var a) && a.Count >= 2)
                {
                    double basePriv = a.Average(x => (double)x.PrivateBytes);
                    if (basePriv > 0 && s.PrivateBytes > basePriv * 1.35 + 100 * 1024 * 1024)
                        WarnOnce($"mem_growth_{_currentPhase}", $"[Diagnostics][WARN] Process private bytes elevated vs baseline in {_currentPhase}: now={s.PrivateBytes / (1024 * 1024)}MB baseline~={basePriv / (1024 * 1024):0}MB");
                }
                if ((_currentPhase == Phase.E_GameplayResumed || _currentPhase == Phase.F_Recovery) && s.WebView2Count > 0)
                {
                    if (_phaseSamples[_currentPhase].Count >= 5)
                        WarnOnce($"webview2_linger_{_currentPhase}", $"[Diagnostics][WARN] WebView2 processes still present in {_currentPhase}: count={s.WebView2Count} ws={s.WebView2WorkingSet / (1024 * 1024)}MB (verify expected lifecycle/caching)");
                }
            }
            catch { }
        }

        private static void WarnOnce(string key, string msg) { if (_warned.Add(key)) MelonLogger.Warning($"[Firesky UnderNet Browser] {msg}"); }

        private static void WriteSummary()
        {
            if (string.IsNullOrEmpty(_summaryPath)) return;
            try
            {
                using var sw = new StreamWriter(_summaryPath, false, new UTF8Encoding(false));
                sw.WriteLine("Firesky UnderNet Browser - Stage 4 Performance Diagnostics Summary");
                sw.WriteLine($"Log: {_logPath}");
                sw.WriteLine($"GeneratedUtc: {DateTime.UtcNow:o}");
                sw.WriteLine($"SampleIntervalSec: {_sampleInterval:0.###}");
                sw.WriteLine($"FinalPhase: {_currentPhase}");
                sw.WriteLine();
                sw.WriteLine("Lifecycle timings (seconds since session start, -1 = not observed):");
                sw.WriteLine($"  tv_interaction_begin: {_tvInteractionAt:0.###}");
                sw.WriteLine($"  browser_visible:      {_browserVisibleAt:0.###}");
                sw.WriteLine($"  webview_init_start:   {_webViewInitStartAt:0.###}");
                sw.WriteLine($"  webview_init_done:    {_webViewInitDoneAt:0.###}");
                sw.WriteLine($"  first_rendered_frame: {_firstRenderedFrameAt:0.###}");
                sw.WriteLine($"  detach_requested:     {_detachRequestedAt:0.###}");
                sw.WriteLine($"  gameplay_resumed:     {_gameplayResumedAt:0.###}");
                sw.WriteLine($"  recovery_detected:    {_recoveryDetectedAt:0.###}");
                if (_tvInteractionAt >= 0 && _firstRenderedFrameAt >= 0) sw.WriteLine($"  tv_to_first_frame:    {(_firstRenderedFrameAt - _tvInteractionAt):0.###}");
                if (_detachRequestedAt >= 0 && _gameplayResumedAt >= 0) sw.WriteLine($"  detach_to_resumed:    {(_gameplayResumedAt - _detachRequestedAt):0.###}");
                if (_gameplayResumedAt >= 0 && _recoveryDetectedAt >= 0) sw.WriteLine($"  resumed_to_recovered: {(_recoveryDetectedAt - _gameplayResumedAt):0.###}");
                sw.WriteLine();
                sw.WriteLine("Per-phase aggregates (from periodic samples; may be empty if phase not reached):");
                sw.WriteLine("phase | samples | fps_avg | fps_1low(avg) | frame_p95_ms(avg) | frame_p99_ms(avg) | frame_max_ms(max) | privMB(avg) | wsMB(avg) | gcMB(avg) | wv2_count(avg/max) | wv2_privMB(avg/max)");
                foreach (Phase p in Enum.GetValues(typeof(Phase)))
                {
                    if (!_phaseSamples.TryGetValue(p, out var list) || list.Count == 0) { sw.WriteLine($"{p} | 0 | - | - | - | - | - | - | - | - | - | -"); continue; }
                    sw.WriteLine($"{p} | {list.Count} | {list.Average(x=>x.FpsAvg):0.##} | {list.Average(x=>x.Fps1Low):0.##} | {list.Average(x=>x.FrameMsP95):0.##} | {list.Average(x=>x.FrameMsP99):0.##} | {list.Max(x=>x.FrameMsMax):0.##} | {AvgMB(list,x=>x.PrivateBytes):0.##} | {AvgMB(list,x=>x.WorkingSet):0.##} | {AvgMB(list,x=>x.GcTotalMemory):0.##} | {list.Average(x=>x.WebView2Count):0.##}/{list.Max(x=>x.WebView2Count)} | {AvgMB(list,x=>x.WebView2PrivateBytes):0.##}/{(list.Max(x=>x.WebView2PrivateBytes)/(1024.0*1024.0)):0.##}");
                }
                sw.WriteLine();
                sw.WriteLine("Metric meanings / collection notes:");
                sw.WriteLine("- fps_avg, fps_1low, frame_ms_*: derived from Unity unscaledDeltaTime accumulated between periodic samples (not per-frame logging). 1% low = avg FPS of worst 1% frames in sample window.");
                sw.WriteLine("- proc_working_set / proc_private_bytes: game process (System.Diagnostics.Process).");
                sw.WriteLine("- gc_total_memory / gc0-2: managed GC.GetTotalMemory(false) and collection counts (no forced GC).");
                sw.WriteLine("- threads/handles: process thread/handle counts.");
                sw.WriteLine("- webview2_*: best-effort count and summed memory of all 'msedgewebview2' processes on system (not strictly scoped to this mod/profile). Lingering processes may indicate caching or leak; correlate with lifecycle.");
                sw.WriteLine("- Not reliably available from this managed IL2CPP mod without extra native/profiler hooks: main vs render thread timings, CPU/GPU utilization %, GPU memory, Unity texture/render-target memory, D3D11 resource create/release counts, WebView2 child-process role breakdown. These require in-game/profiler/native instrumentation.");
                sw.WriteLine("- Event timings recorded only when lifecycle hooks fire; navigation completion time is not explicitly instrumented in this milestone.");
                sw.WriteLine();
                sw.WriteLine("Events (last 200):");
                foreach (var e in _events.TakeLast(200)) sw.WriteLine($"  [{e.t:0.000}s] {e.evt} :: {e.detail}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [Diagnostics] Failed to write summary: {ex.Message}"); }
        }

        private static double AvgMB(List<SampleSnapshot> list, Func<SampleSnapshot, long> sel) => list.Average(x => sel(x) / (1024.0 * 1024.0));
        private static float NowSec() => Mathf.Max(0f, Time.realtimeSinceStartup - _sessionStartRealtime);
        private static string ResolveOutputDirectory()
        {
            try
            {
                string? cfg = UnderNetBrowserConfig.DiagnosticsOutputDirectory?.Value;
                if (!string.IsNullOrWhiteSpace(cfg))
                {
                    string p = cfg.Trim();
                    if (!Path.IsPathRooted(p)) p = Path.Combine(MelonEnvironment.UserDataDirectory, p);
                    return p;
                }
            }
            catch { }
            return Path.Combine(MelonEnvironment.UserDataDirectory, "FireskyUnderNetBrowser", "Diagnostics");
        }
        private static string Csv(string s) { if (s == null) return ""; if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\""; return s; }
        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static void SafeCloseWriter() { try { _writer?.Flush(); } catch { } try { _writer?.Dispose(); } catch { } _writer = null; }
    }
}