using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;

namespace Firesky.UnderNetBrowser
{
    /// <summary>
    /// Runtime, game-specific hotbar input interception for UnderNet browser.
    /// Identifies and hooks actual Schedule I methods (IL2CPP/MelonLoader net6) to prevent
    /// hotbar switching while the browser owns input, without blocking browser input delivery.
    /// </summary>
    internal static class UnderNetBrowserInputHooks
    {
        private static HarmonyLib.Harmony? _harmony;
        private static bool _initAttempted;
        private static bool _installed;
        private static readonly object _lock = new object();
        private static readonly List<string> _hookedMethods = new List<string>();
        private static readonly List<string> _hookFailures = new List<string>(); // actual patch/resolve failures
        private static readonly List<string> _hookNotes = new List<string>();    // optional/unavailable candidates, informational
        private static readonly HashSet<IntPtr> _suppressedInventoryInstances = new HashSet<IntPtr>();
        private static readonly object _diagLock = new object();

        // Ownership state (set from Unity main thread)
        private static volatile bool _appOpen;
        private static volatile bool _ownershipActive; // browser/web/url focus: own wheel/number hotbar input and forward to web as appropriate
        private static volatile bool _blockHotbarSelection; // block final hotbar selection changes while TV session owns gameplay input
        private static volatile bool _suppressGameplayMovement; // suppress character movement/actions while TV session owns gameplay input
        private static volatile bool _urlBarFocused;
        private static volatile bool _webFocused;
        private static volatile bool _pointerOverBrowser;

        // Diagnostics (updated from hooks/main thread)
        private static long _wheelBlocked;
        private static long _numberBlocked;
        private static long _selectionBlocked;
        private static long _equipSlotObservedChanges;
        private static int _lastObservedEquipSlot = int.MinValue;
        private static long _lastDiagLogTick;
        private static int _ownershipTransitions;

        public static bool Installed => _installed;
        public static IReadOnlyList<string> HookedMethods => _hookedMethods;
        public static string HookedMethodsSummary => _hookedMethods.Count == 0 ? "(none)" : string.Join("; ", _hookedMethods);

        public static void UpdateOwnership(bool appOpen, bool urlBarFocused, bool webFocused, bool pointerOverBrowser)
        {
            bool prevOwnership = _ownershipActive;
            _appOpen = appOpen;
            _urlBarFocused = urlBarFocused;
            _webFocused = webFocused;
            _pointerOverBrowser = pointerOverBrowser;

            // Input ownership model:
            // - webFocused/urlBarFocused: browser owns keyboard/wheel/number keys for web/URL interaction (forward to WebView2 as appropriate).
            // - appOpen (TV session active): suppress gameplay movement and hotbar switching so character does not move/equip while using TV.
            //   (Browser/web forwarding still occurs when webFocused; URL bar is a Unity field and receives its own input.)
            // - When TV closes / gameplay focus restored, UpdateOwnership(...false...) restores immediately.
            bool owns = appOpen && (urlBarFocused || webFocused);
            _ownershipActive = owns;
            _blockHotbarSelection = appOpen;      // no hotbar switching while TV app session is active
            _suppressGameplayMovement = appOpen;   // no WASD/character movement while TV app session is active
            if (owns != prevOwnership) System.Threading.Interlocked.Increment(ref _ownershipTransitions);
        }

        public static bool IsOwnershipActive => _ownershipActive;
        public static bool IsBlockHotbarSelection => _blockHotbarSelection;
        public static bool IsSuppressGameplayMovement => _suppressGameplayMovement;
        public static bool IsAppOpen => _appOpen;

        public static void EnsureInstalled()
        {
            if (_installed || _initAttempted) return;
            lock (_lock)
            {
                if (_installed || _initAttempted) return;
                _initAttempted = true;
                try
                {
                    _harmony = new HarmonyLib.Harmony("Firesky.UnderNetBrowser.InputHooks");
                    int ok = 0;
                    ok += TryHookPlayerInventory() ? 1 : 0;
                    ok += TryHookScrollWheelHandlers();
                    ok += TryHookGameplayActionHandlers(); // WASD/movement/jump/crouch/sprint/interact suppression while TV active
                    _installed = ok > 0;
                    MelonLogger.Msg($"[Firesky UnderNet Browser] [InputHooks] Init complete. installed={_installed} hooks={_hookedMethods.Count} failures={_hookFailures.Count} optionalUnavailable={_hookNotes.Count}");
                    foreach (var h in _hookedMethods) MelonLogger.Msg($"[Firesky UnderNet Browser] [InputHooks] Hooked: {h}");
                    foreach (var n in _hookNotes) MelonLogger.Msg($"[Firesky UnderNet Browser] [InputHooks] Optional/unavailable: {n}");
                    foreach (var f in _hookFailures) MelonLogger.Warning($"[Firesky UnderNet Browser] [InputHooks] Hook failed: {f}");
                }
                catch (Exception ex)
                {
                    _hookFailures.Add($"EnsureInstalled exception: {ex.Message}");
                    MelonLogger.Warning($"[Firesky UnderNet Browser] [InputHooks] EnsureInstalled exception: {ex}");
                }
            }
        }

        public static void Shutdown()
        {
            try
            {
                lock (_lock)
                {
                    _harmony?.UnpatchSelf();
                    _hookedMethods.Clear();
                    _hookFailures.Clear();
                    _hookNotes.Clear();
                    _suppressedInventoryInstances.Clear();
                    _installed = false;
                    _initAttempted = false;
                    _harmony = null;
                }
                MelonLogger.Msg("[Firesky UnderNet Browser] [InputHooks] Shutdown/unpatched.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Firesky UnderNet Browser] [InputHooks] Shutdown exception: {ex.Message}");
            }
        }

        public static void LogDiagnostics(bool force = false)
        {
            try
            {
                if (UnderNetBrowserConfig.DebugLogging?.Value != true) return;
                long now = DateTime.UtcNow.Ticks;
                if (!force && (now - System.Threading.Interlocked.Read(ref _lastDiagLogTick)) < TimeSpan.FromSeconds(5).Ticks) return;
                System.Threading.Interlocked.Exchange(ref _lastDiagLogTick, now);
                MelonLogger.Msg($"[Firesky UnderNet Browser] [InputHooks] diag hooks={_hookedMethods.Count} installed={_installed} ownership={_ownershipActive} blockSelect={_blockHotbarSelection} suppressMove={_suppressGameplayMovement} open={_appOpen} url={_urlBarFocused} web={_webFocused} ptr={_pointerOverBrowser} wheelBlocked={System.Threading.Interlocked.Read(ref _wheelBlocked)} numBlocked={System.Threading.Interlocked.Read(ref _numberBlocked)} selBlocked={System.Threading.Interlocked.Read(ref _selectionBlocked)} equipSlot={_lastObservedEquipSlot} equipChanges={System.Threading.Interlocked.Read(ref _equipSlotObservedChanges)} transitions={_ownershipTransitions} methods='{HookedMethodsSummary}' notes={_hookNotes.Count}");
            }
            catch { }
        }

        public static void NotifyNumberKeyIntent()
        {
            // Called from app when a number key is being routed to browser while ownership active.
            // Actual blocking is enforced by selection hook/fallback; this tracks intent.
            if (_ownershipActive) System.Threading.Interlocked.Increment(ref _numberBlocked);
        }

        private static bool TryHookPlayerInventory()
        {
            bool any = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = asm.GetType("Il2CppScheduleOne.PlayerScripts.PlayerInventory", false) ?? asm.GetType("ScheduleOne.PlayerScripts.PlayerInventory", false); }
                catch { t = null; }
                if (t == null)
                {
                    // fallback scan by name
                    try
                    {
                        foreach (var cand in asm.GetTypes())
                        {
                            var fn = cand.FullName ?? cand.Name ?? "";
                            if (fn.EndsWith("PlayerInventory", StringComparison.Ordinal) && (fn.Contains("ScheduleOne", StringComparison.Ordinal) || fn.Contains("Il2CppScheduleOne", StringComparison.Ordinal)))
                            { t = cand; break; }
                        }
                    }
                    catch { }
                }
                if (t == null) continue;

                // Hook SetEquippedSlotIndex(int) as final selection guard/fallback (and source interception point when called from input handlers)
                any |= TryPatchMethod(t, "SetEquippedSlotIndex", new[] { typeof(int) }, nameof(SetEquippedSlotIndex_Prefix), prefix: true, optional: true);
                // Also try RpcLogic___SetEquippedSlotIndex_3316948804(int) if present (network/server path) - block only as selection guard when local ownership active; safe to skip if absent.
                any |= TryPatchMethod(t, "RpcLogic___SetEquippedSlotIndex_3316948804", new[] { typeof(int) }, nameof(SetEquippedSlotIndex_Prefix), prefix: true, optional: true);

                // Block/observe property setter path too (verified IL2CPP member name). Blocking here prevents hotbar switches via direct property assignment.
                any |= TryPatchMethod(t, "set_EquippedSlotIndex", new[] { typeof(int) }, nameof(SetEquippedSlotIndex_Prefix), prefix: true, postfixName: nameof(EquippedSlotChanged_Postfix), optional: false);

                // Try to find OnScrollWheel(InputValue) on PlayerInventory itself (in case it's declared here)
                any |= TryHookScrollWheelOnType(t);
                break; // type found in one assembly is enough
            }
            if (!any) _hookFailures.Add("PlayerInventory methods not found/hooked (SetEquippedSlotIndex/scroll). Will rely on runtime availability.");
            return any;
        }

        private static int TryHookScrollWheelHandlers()
        {
            int count = 0;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name ?? "";
                    if (!(an.Contains("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || an.Contains("ScheduleOne", StringComparison.OrdinalIgnoreCase))) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        try
                        {
                            var fn = t.FullName ?? t.Name ?? "";
                            if (!(fn.Contains("ScheduleOne", StringComparison.Ordinal) || fn.Contains("Il2CppScheduleOne", StringComparison.Ordinal))) continue;
                            // Hook OnScrollWheel methods (Unity Input System callback) to intercept source wheel input when browser owns it.
                            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            {
                                if (!string.Equals(m.Name, "OnScrollWheel", StringComparison.Ordinal)) continue;
                                var ps = m.GetParameters();
                                if (ps.Length != 1) continue; // expect InputValue/callback value
                                // Narrow scope: prefer player/input/inventory/hotbar controllers, but allow and rely on ownership guard.
                                bool relevant = fn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0
                                    || fn.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0
                                    || fn.IndexOf("Hotbar", StringComparison.OrdinalIgnoreCase) >= 0
                                    || fn.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0
                                    || fn.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!relevant) continue;
                                if (TryPatchMethodBase(m, nameof(OnScrollWheel_Prefix), prefix: true))
                                    count++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { _hookFailures.Add($"ScrollWheel scan exception: {ex.Message}"); }
            if (count == 0) _hookNotes.Add("No OnScrollWheel(InputValue/1-param) methods hooked in ScheduleOne/Assembly-CSharp types (may be named differently or unavailable); wheel interception may rely on other hooked paths/fallbacks.");
            return count;
        }

        private static bool TryHookScrollWheelOnType(Type t)
        {
            bool any = false;
            try
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(m.Name, "OnScrollWheel", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length != 1) continue;
                    any |= TryPatchMethodBase(m, nameof(OnScrollWheel_Prefix), prefix: true);
                }
            }
            catch { }
            return any;
        }

        private static bool TryPatchMethod(Type t, string name, Type[] argTypes, string? prefixName, bool prefix = true, string? postfixName = null, bool optional = false)
        {
            try
            {
                MethodInfo? mi = null;
                try { mi = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, argTypes, null); } catch { mi = null; }
                if (mi == null)
                {
                    // fallback: by name + param count
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                        if (m.GetParameters().Length == argTypes.Length) { mi = m; break; }
                    }
                }
                if (mi == null)
                {
                    var msg = $"{t.FullName}.{name}({argTypes.Length}) not found";
                    if (optional) _hookNotes.Add(msg + " (optional fallback; another hook/path may cover this)");
                    else _hookFailures.Add(msg);
                    return false;
                }
                return TryPatchMethodBase(mi, prefix ? prefixName : null, prefix, postfixName);
            }
            catch (Exception ex)
            {
                _hookFailures.Add($"{t.FullName}.{name} resolve exception: {ex.Message}");
                return false;
            }
        }

        private static bool TryPatchMethodBase(MethodBase method, string? prefixName, bool prefix = true, string? postfixName = null)
        {
            try
            {
                if (_harmony == null) return false;
                var key = $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(",", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name))})";
                if (_hookedMethods.Contains(key)) return true;
                HarmonyMethod? pre = null, post = null;
                if (!string.IsNullOrEmpty(prefixName))
                {
                    var pmi = typeof(UnderNetBrowserInputHooks).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                    if (pmi != null) pre = new HarmonyMethod(pmi);
                }
                if (!string.IsNullOrEmpty(postfixName))
                {
                    var pmi = typeof(UnderNetBrowserInputHooks).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
                    if (pmi != null) post = new HarmonyMethod(pmi);
                }
                _harmony.Patch(method, prefix: pre, postfix: post);
                _hookedMethods.Add(key);
                return true;
            }
            catch (Exception ex)
            {
                _hookFailures.Add($"{method.DeclaringType?.FullName}.{method.Name} patch exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ===== Harmony patches =====

        // Prefix for PlayerInventory.SetEquippedSlotIndex(int) and compatible signatures.
        // Blocks hotbar selection changes while browser owns input (fallback/final guard).
        private static bool SetEquippedSlotIndex_Prefix(object __instance, int __0)
        {
            try
            {
                // Observe changes opportunistically for diagnostics
                ObserveEquipSlot(__instance, __0, changing: true);

                if (!_appOpen) return true;
                if (!_blockHotbarSelection) return true;

                // While browser owns input (including URL bar focused per spec), prevent player hotbar selection changes.
                // If a number key was intended for browser, it is forwarded separately by the app; this blocks the game reaction.
                System.Threading.Interlocked.Increment(ref _selectionBlocked);
                // Rate-limited diagnostic
                MaybeLogBlockedSelection(__instance, __0);
                return false; // skip original
            }
            catch { return true; }
        }

        private static void EquippedSlotChanged_Postfix(object __instance)
        {
            try { ObserveEquipSlot(__instance, null, changing: false); } catch { }
        }

        // Prefix for OnScrollWheel(InputValue/1-param callbacks) on player/input/inventory-related types.
        private static bool OnScrollWheel_Prefix(object __instance)
        {
            try
            {
                if (!_appOpen) return true;
                // Only consume wheel hotbar source input when browser owns input focus (web/url) per spec.
                // (Pointer over browser without focus should not steal; focus transitions restore immediately.)
                if (!_ownershipActive) return true;

                // Browser receives wheel separately via managed pointer handling; block game hotbar switching.
                System.Threading.Interlocked.Increment(ref _wheelBlocked);
                return false;
            }
            catch { return true; }
        }

        private static void ObserveEquipSlot(object instance, int? targetSlot, bool changing)
        {
            try
            {
                int cur = int.MinValue;
                if (instance != null)
                {
                    var t = instance.GetType();
                    // try common properties/fields
                    cur = TryReadIntMember(instance, t, "EquippedSlotIndex");
                    if (cur == int.MinValue) cur = TryReadIntMember(instance, t, "_equippedSlotIndex");
                    if (cur == int.MinValue) cur = TryReadIntMember(instance, t, "equippedSlotIndex");
                }
                if (changing && targetSlot.HasValue)
                {
                    if (_lastObservedEquipSlot != int.MinValue && targetSlot.Value != _lastObservedEquipSlot)
                        System.Threading.Interlocked.Increment(ref _equipSlotObservedChanges);
                    _lastObservedEquipSlot = targetSlot.Value;
                }
                else if (cur != int.MinValue)
                {
                    if (_lastObservedEquipSlot != int.MinValue && cur != _lastObservedEquipSlot)
                        System.Threading.Interlocked.Increment(ref _equipSlotObservedChanges);
                    _lastObservedEquipSlot = cur;
                }
            }
            catch { }
        }

        private static int TryReadIntMember(object instance, Type t, string name)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.PropertyType == typeof(int))
                {
                    var v = p.GetValue(instance);
                    if (v is int i) return i;
                }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(int))
                {
                    var v = f.GetValue(instance);
                    if (v is int i) return i;
                }
            }
            catch { }
            return int.MinValue;
        }

        private static long _lastBlockedSelLogTick;
        private static void MaybeLogBlockedSelection(object instance, int slot)
        {
            try
            {
                if (UnderNetBrowserConfig.DebugLogging?.Value != true) return;
                long now = DateTime.UtcNow.Ticks;
                long prev = System.Threading.Interlocked.Read(ref _lastBlockedSelLogTick);
                if ((now - prev) < TimeSpan.FromSeconds(2).Ticks) return;
                System.Threading.Interlocked.Exchange(ref _lastBlockedSelLogTick, now);
                MelonLogger.Msg($"[Firesky UnderNet Browser] [InputHooks] blocked hotbar selection change target={slot} owner={instance?.GetType().FullName} ownership={_ownershipActive} appOpen={_appOpen} url={_urlBarFocused} web={_webFocused}");
            }
            catch { }
        }

        // ===== Gameplay movement/action suppression (WASD, jump, crouch, sprint, interact etc.) while TV app session active =====
        // Best-effort: hook common Unity Input System callback method names on ScheduleOne player/input types.
        private static int TryHookGameplayActionHandlers()
        {
            int count = 0;
            try
            {
                // Common input callback names in Unity Input System PlayerInput "Send Messages"/"Broadcast Messages" setups.
                // We intentionally focus on movement/locomotion/action controls; browser keyboard forwarding is handled separately by the mod.
                string[] actionNames = {
                    "OnMove", "OnLook", "OnSprint", "OnJump", "OnCrouch", "OnInteract", "OnUse", "OnPrimary", "OnSecondary",
                    "OnAttack", "OnFire", "OnRun", "OnWalk", "OnDodge", "OnSlide", "OnProne", "OnLeanLeft", "OnLeanRight"
                };
                var actionSet = new HashSet<string>(actionNames, StringComparer.Ordinal);

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name ?? "";
                    if (!(an.Contains("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || an.Contains("ScheduleOne", StringComparison.OrdinalIgnoreCase))) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        try
                        {
                            var fn = t.FullName ?? t.Name ?? "";
                            if (!(fn.Contains("ScheduleOne", StringComparison.Ordinal) || fn.Contains("Il2CppScheduleOne", StringComparison.Ordinal))) continue;

                            // Narrow to likely player/input/controller types to avoid suppressing unrelated UI systems.
                            bool relevantType = fn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Movement", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Motor", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("FirstPerson", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("Locomotion", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!relevantType) continue;

                            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            {
                                if (!actionSet.Contains(m.Name)) continue;
                                var ps = m.GetParameters();
                                if (ps.Length > 1) continue; // typical Input System callbacks have 0 or 1 parameter
                                if (TryPatchMethodBase(m, nameof(SuppressGameplayAction_Prefix), prefix: true))
                                    count++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { _hookFailures.Add($"Gameplay action hook scan exception: {ex.Message}"); }

            if (count == 0) _hookNotes.Add("No gameplay movement/action callbacks (e.g. OnMove) hooked; WASD suppression may rely on runtime-specific handlers not detected.");
            return count;
        }

        private static bool SuppressGameplayAction_Prefix(object __instance)
        {
            try
            {
                // Suppress gameplay movement/actions only while the TV app session is active.
                // This prevents character movement (WASD etc.) without blocking browser keyboard forwarding handled by the mod.
                if (!_appOpen) return true;
                if (!_suppressGameplayMovement) return true;
                return false; // skip original gameplay action handler
            }
            catch { return true; }
        }
    }
}
