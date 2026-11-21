using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("ScreenCycle")]
[assembly: AssemblyDescription("Rotates apps and websites on a display screen")]
[assembly: AssemblyProduct("ScreenCycle")]
[assembly: AssemblyCopyright("Copyright © 2026 Luka LAURENT")]
[assembly: AssemblyCompany("Luka LAURENT")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ScreenCycle
{
    public static class Program
    {
        private const string MutexName = "Local\\ScreenCycle-6f1c1c2e-instance";
        private const string ShowEventName = "Local\\ScreenCycle-6f1c1c2e-show";
        private const string ExitEventName = "Local\\ScreenCycle-6f1c1c2e-exit";

        /// <summary>Set when ScreenCycle runs from ScreenCycle.ps1 (in-memory, via Windows PowerShell) instead of the exe.</summary>
        internal static string HostScript;

        public static string Version
        {
            get { Version v = typeof(Program).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        /// <summary>Folder holding ScreenCycle.exe or ScreenCycle.ps1.</summary>
        internal static string AppDir
        {
            get
            {
                if (HostScript != null) return Path.GetDirectoryName(HostScript);
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>Command line that starts ScreenCycle again (used for "start with Windows" and shortcuts).</summary>
        internal static string LaunchCommand
        {
            get
            {
                if (HostScript == null) return "\"" + Application.ExecutablePath + "\"";
                return "\"" + LauncherExe + "\" " + LauncherArgs;
            }
        }

        internal static string LauncherExe
        {
            get
            {
                if (HostScript == null) return Application.ExecutablePath;
                return Path.Combine(Environment.SystemDirectory, "conhost.exe");
            }
        }

        internal static string LauncherArgs
        {
            get
            {
                if (HostScript == null) return "";
                string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                return "--headless \"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + HostScript + "\"";
            }
        }

        internal static string LaunchIdentity
        {
            get { return HostScript ?? Application.ExecutablePath; }
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

        /// <summary>Entry point used by ScreenCycle.ps1.</summary>
        public static int RunFromScript(string scriptPath, string[] args)
        {
            HostScript = Path.GetFullPath(scriptPath);
            args = args ?? new string[0];
            bool install = args.Length > 0 && args[0] == "--install";
            if (!install)
            {
                // started with "Run with PowerShell": hide the console window
                IntPtr con = GetConsoleWindow();
                if (con != IntPtr.Zero) Native.ShowWindow(con, Native.SW_HIDE);
            }
            return Main(args);
        }

        private static bool IsRunning()
        {
            try { using (Mutex m = Mutex.OpenExisting(MutexName)) return true; }
            catch { return false; }
        }

        private static void Signal(string eventName)
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(eventName)) ev.Set();
            }
            catch { }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2 (no-op if the manifest already did it)

            bool autostart = false, openSettings = false, noStart = false, exitOther = false;
            foreach (string a in args)
            {
                string s = a.ToLowerInvariant();
                if (s == "--autostart") autostart = true;
                else if (s == "--settings") openSettings = true;
                else if (s == "--no-start") noStart = true;
                else if (s == "--exit") exitOther = true;
            }
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                IconFactory.WriteIco(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--render-ui")
            {
                Application.EnableVisualStyles();
                DevTools.RenderUi(args[1]);
                return 0;
            }
            if (args.Length >= 1 && args[0] == "--install")
            {
                Shortcuts.Install(true);
                return 0;
            }
            if (args.Length >= 1 && args[0] == "--welcome")
            {
                // ScreenCycle.cmd: the small launch / set-up window first
                Application.EnableVisualStyles();
                try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                WelcomeChoice choice = WelcomeForm.Run(IsRunning());
                if (choice == WelcomeChoice.None) return 0;
                if (choice != WelcomeChoice.Launch)
                {
                    Signal(choice == WelcomeChoice.Stop ? ExitEventName : ShowEventName);
                    return 0;
                }
                // Launch: carry on below in this same process
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew || exitOther)
                {
                    // Already running: ask the running copy to open its settings window (or to exit).
                    Signal(exitOther ? ExitEventName : ShowEventName);
                    return 0;
                }

                Application.EnableVisualStyles();
                try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { } // already set if the welcome window ran
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Error("Unhandled UI exception: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Error("Fatal: " + e.ExceptionObject); };

                Log.Info("ScreenCycle " + Version + " starting" + (autostart ? " (autostart)" : "") +
                    (HostScript != null ? " [script]" : " [exe]") + ", config: " + AppConfig.ConfigPath);
                AppConfig cfg = AppConfig.Load();
                Taskbar.RepairAfterCrash();

                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (EventWaitHandle exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName))
                {
                    TrayApp app = new TrayApp(cfg, autostart, openSettings, noStart);
                    RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
                    {
                        app.InvokeOnUi(delegate { app.ShowSettings(); });
                    }, null, -1, false);
                    RegisteredWaitHandle waitExit = ThreadPool.RegisterWaitForSingleObject(exitEvent, delegate
                    {
                        app.InvokeOnUi(delegate { app.ExitApp(); });
                    }, null, -1, true);
                    Application.Run(app);
                    wait.Unregister(null);
                    waitExit.Unregister(null);
                }
                Log.Info("ScreenCycle stopped");
            }
            return 0;
        }
    }

    /// <summary>Start menu / desktop shortcuts (so the script version starts without any console window).</summary>
    internal static class Shortcuts
    {
        public static string IconPath
        {
            get { return Path.Combine(AppConfig.DataDir, "ScreenCycle.ico"); }
        }

        public static string StartMenuPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ScreenCycle.lnk"); }
        }

        public static string DesktopPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ScreenCycle.lnk"); }
        }

        public static void Install(bool desktopToo)
        {
            Apply(true, desktopToo);
        }

        /// <summary>Creates (or refreshes) the wanted shortcuts and removes the unwanted ones.</summary>
        public static void Apply(bool startMenu, bool desktop)
        {
            if (startMenu || desktop)
            {
                Directory.CreateDirectory(AppConfig.DataDir);
                try { IconFactory.WriteIco(IconPath); } catch (Exception ex) { Log.Warn("Icon: " + ex.Message); }
            }
            if (startMenu) Create(StartMenuPath); else Remove(StartMenuPath);
            if (desktop) Create(DesktopPath); else Remove(DesktopPath);
        }

        private static void Remove(string lnk)
        {
            if (!File.Exists(lnk)) return;
            File.Delete(lnk);
            Log.Info("Shortcut removed: " + lnk);
        }

        private static void Create(string lnk)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Program.LauncherExe });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { Program.LauncherArgs });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Program.AppDir });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Rotate apps and websites on this screen" });
                st.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, sc, new object[] { 7 });
                string icon = Program.HostScript == null ? Application.ExecutablePath + ",0" : IconPath + ",0";
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            Log.Info("Shortcut created: " + lnk);
        }
    }
}
