using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MelonLoader;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Stage A compatibility probe for WebView2 (Evergreen Runtime).
    /// Performs detection, loader/version probe, and a best-effort environment creation attempt.
    /// Actual in-process initialization inside Schedule I must be validated in-game.
    /// </summary>
    internal static class UnderNetBrowserWebView2Probe
    {
        private const string ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string MachineKey = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientGuid;
        private const string UserKey = @"Software\Microsoft\EdgeUpdate\Clients\" + ClientGuid;

        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetAvailableCoreWebView2BrowserVersionString(
            string? browserExecutableFolder,
            out IntPtr versionInfo);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public static void RunStageA()
        {
            try
            {
                MelonLogger.Msg("[Firesky UnderNet Browser] [StageA/WebView2] Starting WebView2 runtime detection probe...");

                var machineVer = ReadPv(RegistryHive.LocalMachine, MachineKey);
                var userVer = ReadPv(RegistryHive.CurrentUser, UserKey);
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] Registry HKLM pv={(machineVer ?? "<none>")}, HKCU pv={(userVer ?? "<none>")}");

                var installed = IsUsableVersion(machineVer) || IsUsableVersion(userVer);
                if (!installed)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [StageA/WebView2] RESULT: WebView2 Evergreen Runtime not detected. User must install it (Evergreen Runtime). Do not auto-download/install.");
                    return;
                }

                MelonLogger.Msg("[Firesky UnderNet Browser] [StageA/WebView2] Runtime appears installed. Attempting native loader/version probe (GetAvailableCoreWebView2BrowserVersionString)...");

                // Explicitly load bundled WebView2Loader.dll (redistributable, x64) from mod locations first.
                TryLoadBundledLoader();

                // Best-effort: try default loader resolution first (after explicit load attempt).
                if (TryGetVersion(null, out var ver, out var err))
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] Loader probe OK. Available version='{ver}'. (Skipping Stage A environment creation proof at startup; composition browser manages its own profile on demand.)");
                    return;
                }

                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] WebView2Loader probe failed: {err}");

                // Best-effort: try loading WebView2Loader.dll from installed runtime folder candidates.
                var installLoc = GetInstallLocation(RegistryHive.LocalMachine, MachineKey) ?? GetInstallLocation(RegistryHive.CurrentUser, UserKey);
                var version = IsUsableVersion(machineVer) ? machineVer : userVer;
                foreach (var candidate in CandidateLoaderPaths(installLoc, version))
                {
                    try
                    {
                        if (System.IO.File.Exists(candidate))
                        {
                            var h = LoadLibrary(candidate);
                            MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] Tried LoadLibrary('{candidate}') => {(h == IntPtr.Zero ? "NULL" : "OK")}");
                            if (TryGetVersion(null, out ver, out err))
                            {
                                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] Loader probe OK after explicit load. Available version='{ver}'. (Skipping Stage A environment creation proof at startup.)");
                                return;
                            }
                            MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Probe still failed after loading candidate: {err}");
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Candidate '{candidate}' failed: {ex.Message}");
                    }
                }

                MelonLogger.Warning("[Firesky UnderNet Browser] [StageA/WebView2] RESULT: Runtime detected by registry but native loader/version probe did not succeed in this process. Ensure WebView2Loader.dll (x64) is shipped next to Firesky_UnderNetBrowser.dll (or in Mods/FireskyUnderNetBrowser/). Full CoreWebView2Environment creation must be tested in Schedule I (Stage A in-game).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Probe exception: {ex}");
            }
        }

        private static void TryLoadBundledLoader()
        {
            try
            {
                var asmLoc = typeof(UnderNetBrowserWebView2Probe).Assembly.Location;
                var asmDir = string.IsNullOrWhiteSpace(asmLoc) ? null : System.IO.Path.GetDirectoryName(asmLoc);
                var baseDir = AppContext.BaseDirectory;
                var candidates = new System.Collections.Generic.List<string>();

                void Add(string? dir)
                {
                    if (string.IsNullOrWhiteSpace(dir)) return;
                    candidates.Add(System.IO.Path.Combine(dir, "WebView2Loader.dll"));
                    candidates.Add(System.IO.Path.Combine(dir, "FireskyUnderNetBrowser", "WebView2Loader.dll"));
                    candidates.Add(System.IO.Path.Combine(dir, "Redist", "WebView2", "WebView2Loader.dll"));
                    candidates.Add(System.IO.Path.Combine(dir, "runtimes", "win-x64", "native", "WebView2Loader.dll"));
                }

                Add(asmDir);
                try { Add(MelonLoader.Utils.MelonEnvironment.ModsDirectory); } catch { }
                Add(baseDir);
                try { Add(MelonLoader.Utils.MelonEnvironment.GameRootDirectory); } catch { }
                try
                {
                    var ud = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
                    if (!string.IsNullOrWhiteSpace(ud)) Add(System.IO.Path.Combine(ud, "FireskyUnderNetBrowser"));
                }
                catch { }

                foreach (var path in candidates)
                {
                    try
                    {
                        if (!System.IO.File.Exists(path)) continue;
                        var h = LoadLibrary(path);
                        MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] LoadLibrary bundled loader '{path}' => {(h == IntPtr.Zero ? "NULL (err=" + Marshal.GetLastWin32Error() + ")" : "OK")}" );
                        if (h != IntPtr.Zero)
                            return; // loaded; P/Invoke should bind
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Failed loading '{path}': {ex.Message}");
                    }
                }

                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Bundled WebView2Loader.dll not found in expected mod locations. asmDir='{asmDir}' baseDir='{baseDir}'");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] TryLoadBundledLoader error: {ex.Message}");
            }
        }

        private static void TryCreateEnvironmentProof()
        {
            try
            {
                string? userDataFolder = null;
                try
                {
                    var ud = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
                    userDataFolder = Path.Combine(string.IsNullOrWhiteSpace(ud) ? AppContext.BaseDirectory : ud, "FireskyUnderNetBrowser", "WebView2", "UserData");
                }
                catch
                {
                    userDataFolder = Path.Combine(AppContext.BaseDirectory, "FireskyUnderNetBrowser_WebView2_UserData");
                }

                Directory.CreateDirectory(userDataFolder);
                MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] Attempting CoreWebView2Environment.CreateAsync (userData='{userDataFolder}')...");

                Task<CoreWebView2Environment> task;
                try
                {
                    // browserExecutableFolder=null -> use installed Evergreen runtime; options null.
                    task = CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] CreateAsync threw synchronously: {ex.GetType().Name}: {ex.Message}");
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] RESULT: environment creation NOT proven (sync exception). Controller/init still unproven.");
                    return;
                }

                // Best-effort wait: WebView2 creation typically requires a message pump/STA; this may timeout or deadlock in some contexts.
                // We use a short timeout and do not block indefinitely.
                bool completed;
                try
                {
                    completed = task.Wait(7000);
                }
                catch (AggregateException aex)
                {
                    var inner = aex.InnerException ?? aex;
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] CreateAsync faulted: {inner.GetType().Name}: {inner.Message}");
                    if (inner is COMException comEx)
                        MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] HRESULT=0x{comEx.HResult:X8}");
                    MelonLogger.Warning("[Firesky UnderNet Browser] [StageA/WebView2] RESULT: environment creation FAILED in this context (see error). This may be due to COM/threading/message-pump requirements in the game process.");
                    return;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] Wait/observe failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (!completed)
                {
                    MelonLogger.Warning("[Firesky UnderNet Browser] [StageA/WebView2] CreateAsync did not complete within timeout (7s). This is common if a COM/STA message pump is required. RESULT: environment creation NOT proven yet in this context (timeout).");
                    return;
                }

                if (task.IsFaulted)
                {
                    var ex = task.Exception?.GetBaseException();
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] CreateAsync faulted: {ex?.GetType().Name}: {ex?.Message}");
                    return;
                }

                var env = task.IsCompletedSuccessfully ? task.Result : null;
                if (env != null)
                {
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [StageA/WebView2] SUCCESS: CoreWebView2Environment created. BrowserVersionString='{env.BrowserVersionString}'. (Controller/composition + in-game rendering still unproven.)");
                }
                else
                {
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] CreateAsync completed but environment was null (status={task.Status}).");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [StageA/WebView2] TryCreateEnvironmentProof exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryGetVersion(string? folder, out string? version, out string? error)
        {
            version = null;
            error = null;
            IntPtr p = IntPtr.Zero;
            try
            {
                int hr = GetAvailableCoreWebView2BrowserVersionString(folder, out p);
                if (hr != 0)
                {
                    error = $"HRESULT 0x{hr:X8}";
                    return false;
                }
                if (p == IntPtr.Zero)
                {
                    error = "returned NULL (runtime not found by loader)";
                    return false;
                }
                version = Marshal.PtrToStringUni(p);
                return !string.IsNullOrWhiteSpace(version);
            }
            catch (DllNotFoundException)
            {
                error = "WebView2Loader.dll not found (DllNotFoundException)";
                return false;
            }
            catch (BadImageFormatException ex)
            {
                error = "BadImageFormatException: " + ex.Message;
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                error = "EntryPointNotFoundException: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (p != IntPtr.Zero)
                {
                    try { CoTaskMemFree(p); } catch { }
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<string> CandidateLoaderPaths(string? installLocation, string? version)
        {
            // installLocation is usually the Application root (e.g. ...\EdgeWebView\Application)
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                if (!string.IsNullOrWhiteSpace(version))
                {
                    yield return System.IO.Path.Combine(installLocation, version, "WebView2Loader.dll");
                    yield return System.IO.Path.Combine(installLocation, version, "EBWebView", "x64", "WebView2Loader.dll");
                    yield return System.IO.Path.Combine(installLocation, version, "EBWebView", "x86", "WebView2Loader.dll");
                }
                yield return System.IO.Path.Combine(installLocation, "WebView2Loader.dll");
            }
        }

        private static string? ReadPv(RegistryHive hive, string subKey)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
                using var key = baseKey.OpenSubKey(subKey);
                var pv = key?.GetValue("pv") as string;
                return string.IsNullOrWhiteSpace(pv) ? null : pv;
            }
            catch { return null; }
        }

        private static string? GetInstallLocation(RegistryHive hive, string subKey)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
                using var key = baseKey.OpenSubKey(subKey);
                var loc = key?.GetValue("location") as string;
                return string.IsNullOrWhiteSpace(loc) ? null : loc;
            }
            catch { return null; }
        }

        private static bool IsUsableVersion(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            return v != "0.0.0.0";
        }
    }
}
