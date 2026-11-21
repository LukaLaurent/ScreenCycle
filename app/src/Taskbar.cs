using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenCycle
{
    /// <summary>Keeps the Windows taskbar out of the way of the rotation.</summary>
    internal static class Taskbar
    {
        // ------------------------------------------------------------------ "this window is fullscreen" hint

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        private static ITaskbarList2 list;
        private static bool listFailed;

        /// <summary>Tells the taskbar that this window is a fullscreen window, so it drops behind it when the window is active.</summary>
        public static void MarkFullscreen(IntPtr hwnd, bool fullscreen)
        {
            if (hwnd == IntPtr.Zero || listFailed) return;
            try
            {
                if (list == null)
                {
                    Type t = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
                    list = (ITaskbarList2)Activator.CreateInstance(t);
                    list.HrInit();
                }
                list.MarkFullscreenWindow(hwnd, fullscreen);
            }
            catch (Exception ex)
            {
                listFailed = true;
                Log.Warn("Taskbar fullscreen hint unavailable: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ auto-hide while rotating

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Native.RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);

        private const uint ABM_GETSTATE = 4;
        private const uint ABM_SETSTATE = 10;
        private const int ABS_AUTOHIDE = 1;

        private static bool weTurnedItOn;

        private const uint ABM_GETTASKBARPOS = 5;
        public const uint EdgeLeft = 0, EdgeTop = 1, EdgeRight = 2, EdgeBottom = 3;

        /// <summary>Which side of the screen the taskbar is on (bottom if unknown).</summary>
        public static uint Edge()
        {
            try
            {
                APPBARDATA d = Data();
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref d) != UIntPtr.Zero) return d.uEdge;
            }
            catch { }
            return EdgeBottom;
        }

        /// <summary>Written while we have auto-hide switched on, so a crash or power cut can be undone at next start.</summary>
        private static string MarkerPath
        {
            get { return Path.Combine(AppConfig.DataDir, "taskbar-autohide.flag"); }
        }

        private static APPBARDATA Data()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = FindWindow("Shell_TrayWnd", null);
            return d;
        }

        private static bool IsAutoHide()
        {
            APPBARDATA d = Data();
            return ((int)SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt32() & ABS_AUTOHIDE) != 0;
        }

        private static void SetAutoHide(bool on)
        {
            APPBARDATA d = Data();
            int state = (int)SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt32();
            d.lParam = new IntPtr(on ? (state | ABS_AUTOHIDE) : (state & ~ABS_AUTOHIDE));
            SHAppBarMessage(ABM_SETSTATE, ref d);
        }

        /// <summary>Switches the taskbar to "automatically hide" (only if it was not already).</summary>
        public static void BeginAutoHide()
        {
            if (weTurnedItOn) return;
            try
            {
                if (IsAutoHide()) return; // the user already has it: leave their setting alone
                SetAutoHide(true);
                weTurnedItOn = true;
                try { Directory.CreateDirectory(AppConfig.DataDir); File.WriteAllText(MarkerPath, "1"); } catch { }
                Log.Info("Taskbar set to auto-hide while rotating");
            }
            catch (Exception ex) { Log.Warn("Taskbar auto-hide: " + ex.Message); }
        }

        /// <summary>Gives the taskbar its normal behaviour back.</summary>
        public static void EndAutoHide()
        {
            if (!weTurnedItOn) return;
            weTurnedItOn = false;
            try
            {
                SetAutoHide(false);
                Log.Info("Taskbar auto-hide restored");
            }
            catch (Exception ex) { Log.Warn("Taskbar restore: " + ex.Message); }
            try { File.Delete(MarkerPath); } catch { }
        }

        /// <summary>At start-up: undo an auto-hide left over from a crash.</summary>
        public static void RepairAfterCrash()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return;
                SetAutoHide(false);
                File.Delete(MarkerPath);
                Log.Info("Taskbar auto-hide left over from last time was undone");
            }
            catch (Exception ex) { Log.Warn("Taskbar repair: " + ex.Message); }
        }
    }
}
