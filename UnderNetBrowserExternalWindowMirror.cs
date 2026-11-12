using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Firesky.UnderNetBrowser
{
    internal static class UnderNetBrowserExternalWindowMirror
    {
        internal readonly struct WindowTarget
        {
            public readonly IntPtr Hwnd;
            public readonly string Title;
            public readonly uint ProcessId;
            public WindowTarget(IntPtr hwnd, string title, uint pid) { Hwnd = hwnd; Title = title; ProcessId = pid; }
        }

        public static bool TryFindDiscordWindow(out WindowTarget target)
        {
            target = default;
            try
            {
                var pids = new HashSet<uint>();
                foreach (var name in new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment" })
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try { pids.Add((uint)p.Id); } catch { }
                            try { p.Dispose(); } catch { }
                        }
                    }
                    catch { }
                }
                if (pids.Count == 0) return false;

                WindowTarget found = default;
                bool has = false;
                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd)) return true;
                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (!pids.Contains(pid)) return true;
                        var sbClass = new StringBuilder(256);
                        GetClassName(hwnd, sbClass, sbClass.Capacity);
                        var cls = sbClass.ToString();
                        var sbTitle = new StringBuilder(512);
                        GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
                        var title = sbTitle.ToString();
                        if (string.IsNullOrWhiteSpace(title)) return true;
                        if (title.IndexOf("Discord Updater", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        bool likely = cls.IndexOf("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) >= 0
                                      || title.IndexOf("Discord", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!likely) return true;
                        if (!GetClientRectScreen(hwnd, out var rc)) return true;
                        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                        if (w < 200 || h < 200) return true;
                        found = new WindowTarget(hwnd, title, pid);
                        has = true;
                        return false; // take first good match
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                if (has) { target = found; return true; }
            }
            catch { }
            target = default;
            return false;
        }

        public static bool GetClientRectScreen(IntPtr hwnd, out RECT rect)
        {
            rect = default;
            try
            {
                if (!GetClientRect(hwnd, out rect)) return false;
                var pt = new POINT { X = rect.Left, Y = rect.Top };
                var pt2 = new POINT { X = rect.Right, Y = rect.Bottom };
                if (!ClientToScreen(hwnd, ref pt)) return false;
                if (!ClientToScreen(hwnd, ref pt2)) return false;
                rect.Left = pt.X; rect.Top = pt.Y; rect.Right = pt2.X; rect.Bottom = pt2.Y;
                return rect.Right > rect.Left && rect.Bottom > rect.Top;
            }
            catch { rect = default; return false; }
        }

        public static void PrepareWindowForInput(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                try { BringWindowToTop(hwnd); } catch { }
                // Best-effort focus/activation; may be ignored by Windows focus-stealing protections but helps in some cases.
                try { SetForegroundWindow(hwnd); } catch { }
                try { SetFocus(hwnd); } catch { }
            }
            catch { }
        }

        public static void SendMouseMove(IntPtr hwnd, int x, int y) { try { PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(x, y)); } catch { } }
        public static void SendMouseDown(IntPtr hwnd, int x, int y, bool right)
        {
            try
            {
                PrepareWindowForInput(hwnd);
                uint msg = right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
                IntPtr wParam = right ? (IntPtr)MK_RBUTTON : (IntPtr)MK_LBUTTON;
                PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(x, y));
                PostMessage(hwnd, msg, wParam, MakeLParam(x, y));
            }
            catch { }
        }
        public static void SendMouseUp(IntPtr hwnd, int x, int y, bool right)
        {
            try
            {
                uint msg = right ? WM_RBUTTONUP : WM_LBUTTONUP;
                PostMessage(hwnd, msg, IntPtr.Zero, MakeLParam(x, y));
            }
            catch { }
        }
        public static void SendMouseWheel(IntPtr hwnd, int x, int y, int delta)
        {
            try
            {
                var pt = new POINT { X = x, Y = y };
                ClientToScreen(hwnd, ref pt);
                long wp = ((long)delta << 16);
                PostMessage(hwnd, WM_MOUSEWHEEL, (IntPtr)wp, MakeLParam(pt.X, pt.Y));
            }
            catch { }
        }
        public static void SendKey(IntPtr hwnd, uint msg, ulong wParam, ulong lParam)
        {
            try { PostMessage(hwnd, msg, (IntPtr)(long)wParam, (IntPtr)(long)lParam); } catch { }
        }

        private static IntPtr MakeLParam(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const int MK_LBUTTON = 0x0001;
        private const int MK_RBUTTON = 0x0002;
        private const int SW_RESTORE = 9;
    }
}
