using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenCycle
{
    public class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string ClassName;
        public uint Pid;
        public string ProcessName;   // without .exe
        public string ProcessPath;

        public override string ToString()
        {
            return Title + "  —  " + ProcessName;
        }
    }

    internal static class WindowTools
    {
        private static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

        /// <summary>Windows that would show in Alt+Tab (top-level, visible, not tool windows, not cloaked), excluding our own.</summary>
        public static List<WindowInfo> ListAppWindows(bool withProcessInfo)
        {
            List<WindowInfo> list = new List<WindowInfo>();
            Dictionary<uint, string[]> procCache = new Dictionary<uint, string[]>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsAppWindow(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == OwnPid) return true;
                WindowInfo wi = new WindowInfo();
                wi.Handle = h;
                wi.Title = GetTitle(h);
                wi.ClassName = GetClass(h);
                wi.Pid = pid;
                if (withProcessInfo)
                {
                    string[] info;
                    if (!procCache.TryGetValue(pid, out info))
                    {
                        string path = GetProcessPath(pid);
                        info = new string[] { path, string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path) };
                        procCache[pid] = info;
                    }
                    wi.ProcessPath = info[0];
                    wi.ProcessName = info[1];
                }
                list.Add(wi);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static bool IsAppWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            IntPtr owner = Native.GetWindow(h, Native.GW_OWNER);
            if (owner != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            if (IsCloaked(h)) return false;
            if (Native.GetWindowTextLength(h) == 0) return false;
            string cls = GetClass(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            return true;
        }

        public static bool IsAlive(IntPtr h)
        {
            return h != IntPtr.Zero && Native.IsWindow(h) && (Native.IsWindowVisible(h) || Native.IsIconic(h));
        }

        public static bool IsCloaked(IntPtr h)
        {
            int cloaked;
            try
            {
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0) return cloaked != 0;
            }
            catch { }
            return false;
        }

        public static string GetTitle(IntPtr h)
        {
            int len = Native.GetWindowTextLength(h);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 2);
            Native.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static uint GetPid(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        public static string GetProcessPath(uint pid)
        {
            IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (Native.QueryFullProcessImageName(hp, 0, sb, ref size)) return sb.ToString();
                return "";
            }
            finally { Native.CloseHandle(hp); }
        }

        public static string GetProcessName(uint pid)
        {
            string p = GetProcessPath(pid);
            return string.IsNullOrEmpty(p) ? "" : Path.GetFileNameWithoutExtension(p);
        }

        /// <summary>Returns pid -> parent pid for every running process.</summary>
        public static Dictionary<uint, uint> ParentMap()
        {
            Dictionary<uint, uint> map = new Dictionary<uint, uint>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                Native.PROCESSENTRY32 pe = new Native.PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                if (Native.Process32First(snap, ref pe))
                {
                    do { map[pe.th32ProcessID] = pe.th32ParentProcessID; }
                    while (Native.Process32Next(snap, ref pe));
                }
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        /// <summary>True if pid is root or a descendant of root.</summary>
        public static bool IsInTree(uint pid, uint root, Dictionary<uint, uint> parents)
        {
            if (root == 0) return false;
            uint cur = pid;
            for (int i = 0; i < 32 && cur != 0; i++)
            {
                if (cur == root) return true;
                uint parent;
                if (!parents.TryGetValue(cur, out parent) || parent == cur) return false;
                cur = parent;
            }
            return false;
        }

        // ------------------------------------------------------------------ activation

        private static void NudgeInput()
        {
            // Input sent by our process makes Windows treat us as the source of the last input event,
            // which is what lets SetForegroundWindow succeed while someone uses another app.
            // A zero-length mouse move plus a press of an unassigned key (0xE8): no app reacts to either.
            Native.INPUT[] inp = new Native.INPUT[3];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_MOVE;
            inp[1].type = Native.INPUT_KEYBOARD;
            inp[1].u.ki.wVk = 0xE8;
            inp[2].type = Native.INPUT_KEYBOARD;
            inp[2].u.ki.wVk = 0xE8;
            inp[2].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            SyntheticInput.Mark();
            Native.SendInput(3, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public static bool Activate(IntPtr h)
        {
            if (!Native.IsWindow(h)) return false;
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);

            if (Native.GetForegroundWindow() == h)
            {
                Native.BringWindowToTop(h);
                return true;
            }

            IntPtr fg = Native.GetForegroundWindow();
            uint dummy;
            uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out dummy) : 0;
            uint myThread = Native.GetCurrentThreadId();
            uint targetThread = Native.GetWindowThreadProcessId(h, out dummy);

            NudgeInput();
            bool attachedFg = false, attachedTarget = false;
            try
            {
                if (fgThread != 0 && fgThread != myThread) attachedFg = Native.AttachThreadInput(myThread, fgThread, true);
                if (targetThread != 0 && targetThread != myThread && targetThread != fgThread) attachedTarget = Native.AttachThreadInput(myThread, targetThread, true);

                Native.SetWindowPos(h, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);
            }
            finally
            {
                if (attachedFg) Native.AttachThreadInput(myThread, fgThread, false);
                if (attachedTarget) Native.AttachThreadInput(myThread, targetThread, false);
            }

            if (Native.GetForegroundWindow() != h)
            {
                NudgeInput();
                Native.SetForegroundWindow(h);
            }
            if (Native.GetForegroundWindow() != h)
            {
                Native.SwitchToThisWindow(h, true);
            }
            return Native.GetForegroundWindow() == h;
        }

        public static void SendKey(Keys key)
        {
            Native.INPUT[] inp = new Native.INPUT[2];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].u.ki.wVk = (ushort)key;
            inp[1].type = Native.INPUT_KEYBOARD;
            inp[1].u.ki.wVk = (ushort)key;
            inp[1].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            SyntheticInput.Mark();
            Native.SendInput(2, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // ------------------------------------------------------------------ placement

        public static Rectangle GetRect(IntPtr h)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static bool IsBorderlessOn(IntPtr h, Rectangle bounds)
        {
            long style = Native.GetWindowLong(h, Native.GWL_STYLE);
            return (style & (Native.WS_CAPTION | Native.WS_THICKFRAME)) == 0 && GetRect(h) == bounds && !Native.IsZoomed(h);
        }

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Native.RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Native.POINT p);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);
        private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>
        /// True when the app draws its own title bar inside the client area (new Notepad, Chrome-style apps, Office, Terminal...).
        /// Removing the frame of those windows garbles them, so they are positioned instead.
        /// </summary>
        public static bool HasCustomFrame(IntPtr h)
        {
            long style = Native.GetWindowLong(h, Native.GWL_STYLE);
            if ((style & Native.WS_CAPTION) != Native.WS_CAPTION) return false; // no standard caption at all
            Rectangle win = GetRect(h);
            Native.POINT p = new Native.POINT();
            if (!ClientToScreen(h, ref p)) return false;
            return p.Y - win.Top < 8; // a standard caption pushes the client area down by ~30 px or more
        }

        /// <summary>Height of the app-drawn title bar, from the caption-button area DWM knows about (0 when unknown).</summary>
        public static int CustomCaptionHeight(IntPtr h)
        {
            Native.RECT r;
            try
            {
                if (DwmGetWindowAttribute(h, DWMWA_CAPTION_BUTTON_BOUNDS, out r, Marshal.SizeOf(typeof(Native.RECT))) == 0 && r.Bottom > 0 && r.Bottom < 200)
                    return r.Bottom;
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Makes a window fill the monitor. Classic windows lose their frame; windows with an app-drawn title bar are
        /// stretched over the monitor (invisible borders compensated) with their title bar pushed above the top edge.
        /// </summary>
        public static void FitFullscreen(IntPtr h, Rectangle bounds, int cropTop, ref int cachedCaption)
        {
            if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            if (!HasCustomFrame(h))
            {
                MakeBorderless(h, bounds);
                return;
            }
            Rectangle win = GetRect(h);
            Native.RECT fr;
            Rectangle frame = win;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                frame = Rectangle.FromLTRB(fr.Left, fr.Top, fr.Right, fr.Bottom);
            int padL = Math.Max(0, frame.Left - win.Left), padR = Math.Max(0, win.Right - frame.Right);
            int padT = Math.Max(0, frame.Top - win.Top), padB = Math.Max(0, win.Bottom - frame.Bottom);

            int crop = cropTop;
            if (crop < 0) crop = 0;          // "don't hide the title bar"
            else if (crop == 0)
            {
                int cap = CustomCaptionHeight(h);
                if (cap > 0) cachedCaption = cap; // the value is only reliable while the window still has its normal layout
                crop = cachedCaption;
            }
            Rectangle target = Rectangle.FromLTRB(bounds.Left - padL, bounds.Top - padT - crop, bounds.Right + padR, bounds.Bottom + padB);
            if (win != target)
                Native.SetWindowPos(h, Native.HWND_TOP, target.X, target.Y, target.Width, target.Height,
                    Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        }

        /// <summary>Removes the frame and stretches the window over the monitor.</summary>
        public static void MakeBorderless(IntPtr h, Rectangle bounds)
        {
            if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            long style = Native.GetWindowLong(h, Native.GWL_STYLE);
            long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            long newStyle = style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME);
            long newEx = ex & ~(Native.WS_EX_DLGMODALFRAME | Native.WS_EX_WINDOWEDGE | Native.WS_EX_CLIENTEDGE | Native.WS_EX_STATICEDGE);
            if (newStyle != style) Native.SetWindowLong(h, Native.GWL_STYLE, newStyle);
            if (newEx != ex) Native.SetWindowLong(h, Native.GWL_EXSTYLE, newEx);
            if (newStyle != style || newEx != ex || GetRect(h) != bounds)
            {
                Native.SetWindowPos(h, Native.HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    Native.SWP_FRAMECHANGED | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            }
        }

        /// <summary>Moves a (normal, framed) window onto the given monitor if it is somewhere else.</summary>
        public static void MoveOnto(IntPtr h, Rectangle bounds)
        {
            Rectangle r = GetRect(h);
            Point center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (bounds.Contains(center)) return;
            if (Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            int w = Math.Min(r.Width, bounds.Width - 80), hgt = Math.Min(r.Height, bounds.Height - 80);
            Native.SetWindowPos(h, IntPtr.Zero, bounds.X + 40, bounds.Y + 40, w, hgt, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        /// <summary>Maximizes the window on the given monitor.</summary>
        public static void MaximizeOn(IntPtr h, Rectangle bounds)
        {
            Rectangle cur = GetRect(h);
            Point center = new Point(cur.X + cur.Width / 2, cur.Y + cur.Height / 2);
            bool onMonitor = bounds.Contains(center);
            if (Native.IsZoomed(h) && onMonitor) return;
            if (!onMonitor)
            {
                if (Native.IsZoomed(h) || Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                int w = Math.Min(cur.Width, bounds.Width), hh = Math.Min(cur.Height, bounds.Height);
                Native.SetWindowPos(h, Native.HWND_TOP, bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - hh) / 2, w, hh,
                    Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE);
            }
            Native.ShowWindow(h, Native.SW_SHOWMAXIMIZED);
        }

        /// <summary>For windows that are already fullscreen on their own (browser kiosk): just make sure they sit on the right monitor.</summary>
        public static void EnsureCoversMonitor(IntPtr h, Rectangle bounds)
        {
            Rectangle cur = GetRect(h);
            if (cur == bounds) return;
            Point center = new Point(cur.X + cur.Width / 2, cur.Y + cur.Height / 2);
            if (bounds.Contains(center) && cur.Contains(bounds)) return; // already covers it
            Native.SetWindowPos(h, Native.HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE);
        }

        public static void RestoreStyles(IntPtr h, long style, long ex, Rectangle rect)
        {
            if (!Native.IsWindow(h)) return;
            Native.SetWindowLong(h, Native.GWL_STYLE, style);
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
            if (rect.Width > 0 && rect.Height > 0)
                Native.SetWindowPos(h, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            else
                Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public static Screen ResolveScreen(string device)
        {
            if (!string.IsNullOrEmpty(device))
            {
                foreach (Screen s in Screen.AllScreens)
                    if (string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return Screen.PrimaryScreen;
        }
    }

    /// <summary>Remembers when we injected input ourselves so "pause on user activity" ignores it.</summary>
    internal static class SyntheticInput
    {
        private static int lastTick;
        public static void Mark() { lastTick = Environment.TickCount; }
        public static int MsSinceLast { get { return unchecked(Environment.TickCount - lastTick); } }
    }
}
