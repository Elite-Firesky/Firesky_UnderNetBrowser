using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// MelonLoader entry point for Firesky UnderNet Browser.
    /// Initializes configuration and ensures the S1API TVApp is registered.
    /// S1API auto-discovers TVApp subclasses in loaded assemblies, so the
    /// UnderNetBrowserApp class will automatically appear on the TV home screen.
    /// </summary>
    public class UnderNetBrowserMod : MelonMod
    {
        private static HarmonyLib.Harmony? _tvHarmony;
        private static bool _depsResolved;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool AddDllDirectory(IntPtr newDirectory);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static void EnsureDependencyResolution()
        {
            if (_depsResolved) return;
            _depsResolved = true;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
            catch { }
            try
            {
                var asmLoc = typeof(UnderNetBrowserMod).Assembly.Location;
                var asmDir = string.IsNullOrWhiteSpace(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
                var candidates = new System.Collections.Generic.List<string>();
                void Add(string? d)
                {
                    if (string.IsNullOrWhiteSpace(d)) return;
                    candidates.Add(d);
                    candidates.Add(Path.Combine(d, "UnderNetBrowser"));
                    candidates.Add(Path.Combine(d, "FireskyUnderNetBrowser"));
                    candidates.Add(Path.Combine(d, "deps"));
                    candidates.Add(Path.Combine(d, "lib"));
                }
                Add(asmDir);
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory); } catch { }
                try { Add(AppContext.BaseDirectory); } catch { }
                foreach (var d in candidates)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(d)) continue;
                        if (!Directory.Exists(d)) continue;
                        // Try to preload native loader from subfolder
                        var loader = Path.Combine(d, "WebView2Loader.dll");
                        if (File.Exists(loader)) { try { LoadLibrary(loader); } catch { } }
                        // Also add to DLL search path
                        try { SetDllDirectory(d); } catch { }
                        try { AddDllDirectory(Marshal.StringToHGlobalUni(d)); } catch { } /* best-effort; handle not freed, acceptable for lifetime of process */
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static System.Reflection.Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new System.Reflection.AssemblyName(args.Name);
                var simpleName = name.Name;
                if (string.IsNullOrWhiteSpace(simpleName)) return null;
                // Only handle our known dependencies
                if (!string.Equals(simpleName, "Microsoft.Web.WebView2.Core", StringComparison.OrdinalIgnoreCase))
                    return null;
                var asmLoc = typeof(UnderNetBrowserMod).Assembly.Location;
                var asmDir = string.IsNullOrWhiteSpace(asmLoc) ? null : Path.GetDirectoryName(asmLoc);
                string[] baseDirs = new[]
                {
                    asmDir ?? "",
                    tryGet(() => MelonLoader.Utils.MelonEnvironment.ModsDirectory) ?? "",
                    AppContext.BaseDirectory ?? ""
                };
                string[] sub = new[] { "UnderNetBrowser", "FireskyUnderNetBrowser", "deps", "lib", "" };
                foreach (var bd in baseDirs)
                {
                    if (string.IsNullOrWhiteSpace(bd)) continue;
                    foreach (var s in sub)
                    {
                        try
                        {
                            string dir = string.IsNullOrEmpty(s) ? bd : Path.Combine(bd, s);
                            string path = Path.Combine(dir, simpleName + ".dll");
                            if (File.Exists(path))
                                return System.Reflection.Assembly.LoadFrom(path);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? tryGet(Func<string> f) { try { return f(); } catch { return null; } }

        public override void OnInitializeMelon()
        {
            EnsureDependencyResolution();
            UnderNetBrowserConfig.Initialize();
            MelonLogger.Msg("[Firesky UnderNet Browser] Mod initialized. TV app 'UnderNet' registered via S1API.");

            try
            {
                _tvHarmony = new HarmonyLib.Harmony("Firesky.UnderNetBrowser.TVApp");
                TryPatchTvAppCloseMethods();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] TVApp close patch init failed (non-fatal): {ex.Message}");
            }

            // Stage 4 diagnostics (opt-in). Starts baseline session if enabled in config.
            try { UnderNetBrowserDiagnostics.InitializeIfEnabled(); } catch (Exception ex) { MelonLogger.Warning($"[Firesky UnderNet Browser] [Diagnostics] init failed (non-fatal): {ex.Message}"); }
        }

        private static void TryPatchTvAppCloseMethods()
        {
            try
            {
                if (_tvHarmony == null) return;
                TryPatchSimpleChatEnterOpen(); // best-effort: prevent chat mod from consuming Enter while browser owns input
                TryPatchModManagerPreferencesOrder(); // best-effort: preserve our MelonPreferences display order in Mod Manager & Phone App UI
                var tvAppType = typeof(S1API.TVApp.TVApp);
                foreach (var name in new[] { "Close", "ForceClose" })
                {
                    try
                    {
                        var mi = tvAppType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (mi == null) continue;
                        var prefix = typeof(UnderNetBrowserApp).GetMethod(nameof(UnderNetBrowserApp.TVAppClosePrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (prefix != null)
                        {
                            _tvHarmony.Patch(mi, prefix: new HarmonyMethod(prefix));
                            MelonLogger.Msg($"[Firesky UnderNet Browser] Patched S1API.TVApp.TVApp.{name} (detach-close guard).");
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] Failed to patch TVApp.{name}: {ex.Message}");
                    }
                }

                // When detached to gameplay, S1API's HandleExit marks ExitAction.Used=true then calls Close().
                // Blocking Close alone is not enough (Used stays true and can prevent pause menu). Skip HandleExit instead.
                try
                {
                    MethodInfo? handleExit = null;
                    foreach (var mi in tvAppType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (mi.Name != "HandleExit") continue;
                        var ps = mi.GetParameters();
                        if (ps.Length == 1) { handleExit = mi; break; }
                    }
                    if (handleExit != null)
                    {
                        var prefix = typeof(UnderNetBrowserApp).GetMethod(nameof(UnderNetBrowserApp.TVAppHandleExitPrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (prefix != null)
                        {
                            _tvHarmony.Patch(handleExit, prefix: new HarmonyMethod(prefix));
                            MelonLogger.Msg("[Firesky UnderNet Browser] Patched S1API.TVApp.TVApp.HandleExit (detached exit passthrough).");
                        }
                    }
                    else
                    {
                        MelonLogger.Warning("[Firesky UnderNet Browser] Could not find S1API.TVApp.TVApp.HandleExit to patch (non-fatal).");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] Failed to patch TVApp.HandleExit: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] TryPatchTvAppCloseMethods failed: {ex.Message}");
            }
        }

        public override void OnLateInitializeMelon()
        {
            // S1API discovers TVApp subclasses after all mods are initialized.
            // By OnLateInitializeMelon, the TVApp registry should be populated.
            MelonLogger.Msg("[Firesky UnderNet Browser] Late initialization complete.");
        }

        private bool _graphicsApiLogged;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[Firesky UnderNet Browser] Scene loaded: {sceneName} (build index {buildIndex})");

            // Ensure browser/composition/WebView2 processes and audio are stopped when returning to menu/world unload.
            try
            {
                bool isMenuScene = buildIndex == 0 || (!string.IsNullOrEmpty(sceneName) && sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isMenuScene)
                {
                    UnderNetBrowserApp.ShutdownForWorldUnload("scene:" + (sceneName ?? buildIndex.ToString()));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] Scene unload cleanup failed (non-fatal): {ex.Message}");
            }

            if (!_graphicsApiLogged)
            {
                _graphicsApiLogged = true;
                try
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageB/Probe] Graphics API: {SystemInfo.graphicsDeviceType}, device='{SystemInfo.graphicsDeviceName}', version='{SystemInfo.graphicsDeviceVersion}'");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageB/Probe] Failed to query graphics API: {ex.Message}");
                }
            }
        }

        public override void OnUpdate()
        {
            // Stage 4 diagnostics: lightweight periodic sampling + frame-time accumulation (no-op when disabled).
            try { UnderNetBrowserDiagnostics.OnFrame(); } catch { }

            // Push Schedule I in-game time/day and settings state to built-in homepage pages (best-effort, throttled internally).
            try { UnderNetBrowserApp.TryUpdateHomepageGameTime(); } catch { }
            try { UnderNetBrowserApp.TryUpdateSettingsPageState(); } catch { }

            // Global cursor toggle (works outside TV/app as well).
            try
            {
                var key = UnderNetBrowserConfig.GetKey(KeyCode.F3, UnderNetBrowserConfig.KeyToggleCursor);
                if (Input.GetKeyDown(key))
                {
                    ToggleCursorVisibility();
                }
            }
            catch { }
        }

        internal static void ToggleCursorVisibility()
        {
            try
            {
                bool visibleNow = Cursor.visible && Cursor.lockState != CursorLockMode.Locked;
                if (visibleNow)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                else
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            catch { }
        }

        private static void TryPatchSimpleChatEnterOpen()
        {
            try
            {
                if (_tvHarmony == null) return;
                Type? chatUiType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { chatUiType = asm.GetType("SimpleChat.ChatUI", false); } catch { chatUiType = null; }
                    if (chatUiType != null) break;
                }
                if (chatUiType == null)
                {
                    // SimpleChat may load later; harmless if absent.
                    // MelonLogger.Msg("[Firesky UnderNet Browser] SimpleChat.ChatUI not found (Enter-guard patch skipped).");
                    return;
                }

                var prefix = typeof(UnderNetBrowserMod).GetMethod(nameof(SimpleChat_OpenChatPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null) return;

                foreach (var name in new[] { "OpenChat", "Update" })
                {
                    try
                    {
                        var mi = chatUiType.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi == null) continue;
                        _tvHarmony.Patch(mi, prefix: new HarmonyMethod(prefix));
                        MelonLogger.Msg($"[Firesky UnderNet Browser] Patched SimpleChat.ChatUI.{name} (browser Enter/chat guard, best-effort).");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] Failed to patch SimpleChat.ChatUI.{name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] SimpleChat patch failed (non-fatal): {ex.Message}");
            }
        }

        private static bool SimpleChat_OpenChatPrefix(System.Reflection.MethodBase __originalMethod)
        {
            try
            {
                if (UnderNetBrowserInputHooks.ShouldBrowserConsumeEnterNow())
                {
                    // Allow ChatUI.Update to continue doing harmless UI refreshes? No, prefix runs before method.
                    // For OpenChat: block opening chat while browser is using Enter.
                    // For Update: do not block whole update (it manages rendering/state); only block Enter-open inside Update via Input hooks.
                    // So skip only OpenChat here.
                    var name = __originalMethod?.Name ?? "";
                    if (string.Equals(name, "OpenChat", StringComparison.Ordinal))
                        return false;
                }
            }
            catch { }
            return true;
        }

        // --- Mod Manager & Phone App: best-effort UI ordering patch (v2.2.4 sorts categories/entries by Identifier) ---
        private static bool _modManagerOrderPatchAttempted;
        private static readonly string[] _modManagerPreferredEntryOrder = new[]
        {
            "Enabled",
            "HomeUrl",
            "BasicAdBlockEnabled",
            "AutoConsentEnabled",
            "DistanceVolumeEnabled",
            "DistanceVolumeFullDistance",
            "DistanceVolumeSilentDistance",
            "KeyToggleCursor",
            "KeyReload",
            "KeyGameInputMode",
            "AdaptiveRenderingEnabled",
            "AdaptiveDistanceQualityScaling",
            "AdaptiveVisualStandbyEnabled",
            "AdaptiveResolutionScalingEnabled",
            "AdaptiveFullDistance",
            "AdaptiveReducedDistance",
            "AdaptiveLowDistance",
            "AdaptiveStandbyDistance",
            "AdaptiveHysteresisDistance",
            "AdaptiveTransitionDelaySeconds",
            "AdaptiveReducedCaptureFps",
            "AdaptiveLowCaptureFps",
            "GpuCompositionRenderWidth",
            "GpuCompositionRenderHeight",
            "DebugLogging",
            "AlwaysLogDiagnostics",
            "PerformanceDiagnosticsEnabled",
            "DiagnosticsSampleIntervalSeconds",
            "DiagnosticsOutputDirectory",
            "WebView2ArgDisableBackgroundTimerThrottling",
            "WebView2ArgDisableRendererBackgrounding",
            "WebView2ArgAutoplayNoUserGesture",
            "WebView2ArgDisableFrameRateLimit",
            "WebView2CustomBrowserArguments",
            "SpatialAudioPocEnabled"
        };

        private static void TryPatchModManagerPreferencesOrder()
        {
            try
            {
                if (_tvHarmony == null) return;
                if (_modManagerOrderPatchAttempted) return;

                Type? creatorType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { creatorType = asm.GetType("ModManagerPhoneApp.ModSettingsAppCreator", false); } catch { creatorType = null; }
                    if (creatorType != null) break;
                }
                if (creatorType == null)
                {
                    // Not installed / not loaded yet in this context; allow retry on next scene/init.
                    return;
                }

                _modManagerOrderPatchAttempted = true;

                var postfix = typeof(UnderNetBrowserMod).GetMethod(nameof(ModManager_PopulateSettingsPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null) return;

                int patched = 0;
                foreach (var methodName in new[] { "PopulateMenuConfigPanel", "PopulateAllSettings", "PopulateModSettings" })
                {
                    try
                    {
                        var mi = creatorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi == null) continue;
                        _tvHarmony.Patch(mi, postfix: new HarmonyMethod(postfix));
                        patched++;
                        MelonLogger.Msg($"[Firesky UnderNet Browser] Patched ModManagerPhoneApp.ModSettingsAppCreator.{methodName} (preferences order, best-effort).");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] Failed to patch ModManagerPhoneApp.ModSettingsAppCreator.{methodName}: {ex.Message}");
                    }
                }

                if (patched == 0)
                    MelonLogger.Warning("[Firesky UnderNet Browser] Mod Manager detected but no settings population methods were patched.");
            }
            catch (Exception ex)
            {
                _modManagerOrderPatchAttempted = true; // avoid repeated noisy failures
                MelonLogger.Warning($"[Firesky UnderNet Browser] Mod Manager order patch failed (non-fatal): {ex.Message}");
            }
        }

        private static void ModManager_PopulateSettingsPostfix(object __instance)
        {
            try
            {
                if (__instance == null)
                {
                    MelonLogger.Msg("[Firesky UnderNet Browser] [ModManagerOrder] Postfix called with null instance.");
                    return;
                }
                var type = __instance.GetType();

                // Menu config UI and phone app UI use different content roots.
                TryReorderModManagerCategoryEntries(__instance, type, "_menuConfigContent", "FireskyUnderNetBrowser");
                TryReorderModManagerCategoryEntries(__instance, type, "rightPanelContent", "FireskyUnderNetBrowser");
            }
            catch (Exception ex)
            {
                try { MelonLogger.Warning($"[Firesky UnderNet Browser] [ModManagerOrder] Postfix exception: {ex.Message}"); } catch { }
            }
        }

        private static void TryReorderModManagerCategoryEntries(object instance, Type instanceType, string contentFieldName, string categoryIdentifier)
        {
            try
            {
                var f = instanceType.GetField(contentFieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [ModManagerOrder] {contentFieldName}: field not found on {instanceType.FullName}");
                    return;
                }
                var contentObj = f.GetValue(instance);
                if (contentObj == null)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [ModManagerOrder] {contentFieldName}: null");
                    return;
                }

                // UnityEngine.Transform in IL2CPP/MelonLoader context
                UnityEngine.Transform? contentTransform = contentObj as UnityEngine.Transform;
                if (contentTransform == null)
                {
                    try
                    {
                        var tt = contentObj.GetType();
                        // Il2CppInterop/IL2CPP objects sometimes require TryCast; use reflection so this still compiles without hard dependency.
                        var tryCast = tt.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                            .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                        if (tryCast != null)
                        {
                            try
                            {
                                var gm = tryCast.MakeGenericMethod(typeof(UnityEngine.Transform));
                                contentTransform = gm.Invoke(contentObj, null) as UnityEngine.Transform;
                            }
                            catch { }
                        }
                        if (contentTransform == null)
                        {
                            var trProp = tt.GetProperty("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                            if (trProp != null) contentTransform = trProp.GetValue(contentObj) as UnityEngine.Transform;
                        }
                        if (contentTransform == null)
                        {
                            var trField = tt.GetField("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (trField != null) contentTransform = trField.GetValue(contentObj) as UnityEngine.Transform;
                        }
                    }
                    catch { }
                }
                if (contentTransform == null)
                {
                    // Last resort: if type name indicates Transform, proceed with reflection-based access below? No, just log.
                    try { MelonLogger.Msg($"[Firesky UnderNet Browser] [ModManagerOrder] {contentFieldName}: not a Transform ({contentObj.GetType().FullName})"); } catch { }
                    return;
                }

                int childCount = 0;
                try { childCount = contentTransform.childCount; } catch { childCount = 0; }
                if (childCount <= 0)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [ModManagerOrder] {contentFieldName}: childCount=0");
                    return;
                }

                static bool IsOurCategoryHeader(string name, string categoryId)
                {
                    if (string.IsNullOrEmpty(name)) return false;
                    return string.Equals(name, "Menu_Category_" + categoryId, StringComparison.Ordinal) ||
                           string.Equals(name, "Category_" + categoryId, StringComparison.Ordinal) ||
                           name.EndsWith("_Category_" + categoryId, StringComparison.Ordinal) ||
                           name.EndsWith("Category_" + categoryId, StringComparison.Ordinal);
                }

                static bool IsAnyCategoryHeader(string name)
                {
                    if (string.IsNullOrEmpty(name)) return false;
                    return name.StartsWith("Menu_Category_", StringComparison.Ordinal) ||
                           name.StartsWith("Category_", StringComparison.Ordinal) ||
                           name.Contains("_Category_", StringComparison.Ordinal);
                }

                // Reorder every block belonging to our category (some UI paths may temporarily duplicate/rebuild sections).
                int headerCount = 0;
                int totalMoved = 0;
                int totalEntries = 0;
                int totalMatched = 0;
                int blocks = 0;
                string marker = "_" + categoryIdentifier + "_";

                for (int scan = 0; scan < childCount; scan++)
                {
                    UnityEngine.Transform? child = null;
                    string name = "";
                    try { child = contentTransform.GetChild(scan); name = child?.name ?? ""; } catch { child = null; name = ""; }
                    if (child == null || !IsOurCategoryHeader(name, categoryIdentifier))
                        continue;

                    headerCount++;
                    blocks++;
                    int headerIndex = scan;
                    int blockEndExclusive = childCount;
                    for (int i = headerIndex + 1; i < childCount; i++)
                    {
                        try
                        {
                            var c = contentTransform.GetChild(i);
                            var n = c != null ? (c.name ?? "") : "";
                            if (IsAnyCategoryHeader(n))
                            {
                                blockEndExclusive = i;
                                break;
                            }
                        }
                        catch { }
                    }

                    var entries = new System.Collections.Generic.List<UnityEngine.Transform>();
                    var byId = new System.Collections.Generic.Dictionary<string, UnityEngine.Transform>(StringComparer.Ordinal);

                    for (int i = headerIndex + 1; i < blockEndExclusive; i++)
                    {
                        UnityEngine.Transform? entryChild = null;
                        string entryName = "";
                        try { entryChild = contentTransform.GetChild(i); entryName = entryChild?.name ?? ""; } catch { entryChild = null; entryName = ""; }
                        if (entryChild == null || string.IsNullOrEmpty(entryName)) continue;

                        string? entryId = null;
                        int idx = entryName.IndexOf(marker, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            // Menu config UI naming: Menu_<Template>_<CategoryId>_<EntryId>
                            entryId = entryName.Substring(idx + marker.Length);
                        }
                        else
                        {
                            // Phone UI naming (observed): Menu_<Template>_<EntryId> (no category id in entry object name).
                            // Map rows in this category block by matching known entry ID suffixes.
                            for (int p = 0; p < _modManagerPreferredEntryOrder.Length; p++)
                            {
                                var candidate = _modManagerPreferredEntryOrder[p];
                                if (!string.IsNullOrEmpty(candidate) && entryName.EndsWith("_" + candidate, StringComparison.Ordinal))
                                {
                                    entryId = candidate;
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(entryId)) continue;
                        entries.Add(entryChild);
                        if (!byId.ContainsKey(entryId)) byId[entryId] = entryChild;
                    }

                    totalEntries += entries.Count;
                    totalMatched += byId.Count;

                    if (entries.Count == 0)
                    {
                        scan = blockEndExclusive - 1;
                        // Refresh childCount in case hierarchy changed unexpectedly (defensive)
                        try { childCount = contentTransform.childCount; } catch { }
                        continue;
                    }

                    // Reinsert in preferred order immediately after this category header, preserving unknown entries at the end of the block.
                    int insertIndex = headerIndex + 1;
                    var placed = new System.Collections.Generic.HashSet<UnityEngine.Transform>();

                    foreach (var entryId in _modManagerPreferredEntryOrder)
                    {
                        if (!byId.TryGetValue(entryId, out var t) || t == null) continue;
                        try { t.SetSiblingIndex(insertIndex++); totalMoved++; } catch { }
                        placed.Add(t);
                    }

                    foreach (var t in entries)
                    {
                        if (t == null || placed.Contains(t)) continue;
                        try { t.SetSiblingIndex(insertIndex++); totalMoved++; } catch { }
                    }

                    // Continue scanning after the (possibly unchanged-size) block.
                    scan = blockEndExclusive - 1;
                    try { childCount = contentTransform.childCount; } catch { }
                }

                if (headerCount > 0)
                {
                    try
                    {
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [ModManagerOrder] {contentFieldName}: headers={headerCount} blocks={blocks} entries={totalEntries} matched={totalMatched} moved={totalMoved}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try { MelonLogger.Warning($"[Firesky UnderNet Browser] [ModManagerOrder] Failed for {contentFieldName}: {ex.Message}"); } catch { }
            }
        }

        public override void OnApplicationQuit()
        {
            try { UnderNetBrowserDiagnostics.StopSession("quit"); } catch { }
            try { UnderNetBrowserApp.ShutdownForWorldUnload("quit"); } catch { }
        }

        public override void OnDeinitializeMelon()
        {
            try { UnderNetBrowserDiagnostics.StopSession("deinitialize"); } catch { }
            try { UnderNetBrowserApp.ShutdownForWorldUnload("deinitialize"); } catch { }
            MelonLogger.Msg("[Firesky UnderNet Browser] Mod deinitialized.");
        }
    }
}
