using System;
using System.Reflection;
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
        public override void OnInitializeMelon()
        {
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

            // Stage A (architecture migration): WebView2 compatibility proof probe.
            // Does not change playback behavior; logs detection/loader probe results for in-game validation.
            UnderNetBrowserWebView2Probe.RunStageA();
        }

        private static void TryPatchTvAppCloseMethods()
        {
            try
            {
                if (_tvHarmony == null) return;
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
