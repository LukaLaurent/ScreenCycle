using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Threading.Tasks;

namespace ScreenCycle
{
    /// <summary>Live state for one rotation item: which window belongs to it, whether we launched it, etc.</summary>
    internal class ItemRuntime
    {
        public RotationItem Item;
        public IntPtr Hwnd = IntPtr.Zero;
        public bool LaunchedByUs;
        public uint RootPid;              // process we started (or that owns the window)
        public string MatchProcess = "";  // process name used to re-find windows
        public bool SelfFullscreen;       // browser kiosk handles fullscreen itself
        public bool UserProfile;          // website in the person's own browser profile: its browser is shared with their other windows
        public bool F11Failed;            // the browser's own fullscreen (F11) never worked: fall back to ScreenCycle's
        private int f11Misses;
        public string LastError = "";
        public int Failures;

        private DateTime foundAt = DateTime.MinValue;
        private int cachedCaption;
        private bool stylesSaved;
        private long origStyle, origEx;
        private Rectangle origRect;

        public ItemRuntime(RotationItem item) { Item = item; }

        public static string Signature(RotationItem i)
        {
            return string.Join("|", new string[] {
                i.Kind.ToString(), i.Url.Trim(), i.Browser.ToString(), i.BrowserProfile ?? "", i.Path.Trim(), i.Arguments.Trim(),
                i.TitleContains.Trim(), i.ProcessName.Trim(),
                (i.Kind == ItemKind.Website ? i.Mode.ToString() : "") });
        }

        public bool IsAlive { get { return WindowTools.IsAlive(Hwnd); } }

        // ------------------------------------------------------------------ matching

        private static bool TitleOk(RotationItem item, WindowInfo w)
        {
            if (string.IsNullOrWhiteSpace(item.TitleContains)) return true;
            return w.Title.IndexOf(item.TitleContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormProc(string p)
        {
            p = (p ?? "").Trim();
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            return p;
        }

        public static bool MatchesWindowItem(RotationItem item, WindowInfo w)
        {
            bool hasTitle = !string.IsNullOrWhiteSpace(item.TitleContains);
            string proc = NormProc(item.ProcessName);
            if (!hasTitle && proc.Length == 0) return false;
            if (!TitleOk(item, w)) return false;
            if (proc.Length > 0 && !string.Equals(proc, w.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Looks for an existing window that belongs to this item (no launching). Cheap enough for the UI thread.</summary>
        public bool TryRefind(ICollection<IntPtr> claimed)
        {
            if (IsAlive) return true;
            List<WindowInfo> wins = WindowTools.ListAppWindows(true);
            WindowInfo best = null;
            if (Item.Kind == ItemKind.Window)
            {
                foreach (WindowInfo w in wins)
                {
                    if (claimed.Contains(w.Handle)) continue;
                    if (MatchesWindowItem(Item, w)) { best = Pick(best, w); }
                }
            }
            else if (RootPid != 0 && !UserProfile)
            {
                Dictionary<uint, uint> parents = WindowTools.ParentMap();
                foreach (WindowInfo w in wins)
                {
                    if (claimed.Contains(w.Handle)) continue;
                    if (!TitleOk(Item, w)) continue;
                    bool inTree = WindowTools.IsInTree(w.Pid, RootPid, parents);
                    bool sameProc = Item.Kind == ItemKind.Application && MatchProcess.Length > 0
                        && string.Equals(w.ProcessName, MatchProcess, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(Item.TitleContains);
                    if (inTree || sameProc) best = Pick(best, w);
                }
            }
            if (best != null)
            {
                Adopt(best.Handle);
                return true;
            }
            return false;
        }

        private static WindowInfo Pick(WindowInfo a, WindowInfo b)
        {
            if (a == null) return b;
            Rectangle ra = WindowTools.GetRect(a.Handle), rb = WindowTools.GetRect(b.Handle);
            return (long)rb.Width * rb.Height > (long)ra.Width * ra.Height ? b : a;
        }

        private void Adopt(IntPtr h)
        {
            if (h != Hwnd) { stylesSaved = false; cachedCaption = 0; foundAt = DateTime.UtcNow; }
            Hwnd = h;
            uint pid = WindowTools.GetPid(h);
            if (RootPid == 0) RootPid = pid;
            if (MatchProcess.Length == 0) MatchProcess = WindowTools.GetProcessName(pid);
            Failures = 0;
            LastError = "";
            if (UserProfile) WindowMemory.Remember(Item.Id, h);
        }

        // ------------------------------------------------------------------ launching

        /// <summary>Makes sure the item has a window, launching it if needed. Runs on the UI thread, awaits between polls.</summary>
        public async Task<bool> EnsureAsync(ICollection<IntPtr> claimed, Rectangle bounds, int timeoutMs, Func<bool> cancelled)
        {
            if (IsAlive) return true;
            if (TryRefind(claimed)) return true;
            try
            {
                switch (Item.Kind)
                {
                    case ItemKind.Window:
                        LastError = "No open window matches " + Item.TargetDescription;
                        // the window might appear later (someone opens it) - wait a little
                        return await WaitForExisting(claimed, Math.Min(timeoutMs, 3000), cancelled);
                    case ItemKind.Website:
                        return await LaunchWebsite(claimed, bounds, timeoutMs, cancelled);
                    default:
                        return await LaunchApplication(claimed, timeoutMs, cancelled);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("Launching '" + Item.DisplayName + "' failed: " + ex);
                Failures++;
                return false;
            }
        }

        private async Task<bool> WaitForExisting(ICollection<IntPtr> claimed, int timeoutMs, Func<bool> cancelled)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) return false;
                await Task.Delay(300);
                if (TryRefind(claimed)) return true;
            }
            Failures++;
            return false;
        }

        private static HashSet<IntPtr> Snapshot()
        {
            HashSet<IntPtr> set = new HashSet<IntPtr>();
            foreach (WindowInfo w in WindowTools.ListAppWindows(false)) set.Add(w.Handle);
            return set;
        }

        private async Task<bool> LaunchWebsite(ICollection<IntPtr> claimed, Rectangle bounds, int timeoutMs, Func<bool> cancelled)
        {
            if (string.IsNullOrWhiteSpace(Item.Url)) { LastError = "No URL set"; Failures++; return false; }
            BrowserLaunch bl = Browsers.Build(Item, bounds);

            if (bl != null && bl.UserProfile)
            {
                UserProfile = true;
                F11Failed = false;
                f11Misses = 0;
                MatchProcess = bl.ProcessName;
                SelfFullscreen = false;
                // The window opened last time (ScreenCycle restarted, browser kept running)? Use it again.
                IntPtr known = WindowMemory.Recall(Item.Id, bl.ProcessName);
                if (known != IntPtr.Zero && !claimed.Contains(known))
                {
                    RootPid = WindowTools.GetPid(known);
                    Adopt(known);
                    LaunchedByUs = true;
                    Log.Info("Reusing the window opened earlier for '" + Item.DisplayName + "'");
                    return true;
                }
                HashSet<IntPtr> before = Snapshot();
                ProcessStartInfo psi = new ProcessStartInfo(bl.Exe, bl.Args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(bl.Exe);
                Log.Info("Launching '" + Item.DisplayName + "' with the browser's own profile: " + bl.Exe + " " + bl.Args);
                Process p = Process.Start(psi);
                RootPid = p != null ? (uint)p.Id : 0;
                LaunchedByUs = true;
                return await WaitForNewWindow(before, claimed, timeoutMs, bl.ProcessName, false, cancelled);
            }
            UserProfile = false;

            if (bl != null)
            {
                // A browser left running from a previous session with this item's private profile? Reuse it.
                string marker = Item.Id;
                string procName = bl.ProcessName;
                uint existing = await Task.Run(delegate { return FindProcessWithMarker(procName + ".exe", marker); });
                if (existing != 0)
                {
                    RootPid = existing;
                    MatchProcess = procName;
                    SelfFullscreen = bl.SelfFullscreen;
                    if (TryRefind(claimed))
                    {
                        LaunchedByUs = true;
                        Log.Info("Reusing running browser for '" + Item.DisplayName + "'");
                        return true;
                    }
                }

                HashSet<IntPtr> before = Snapshot();
                ProcessStartInfo psi = new ProcessStartInfo(bl.Exe, bl.Args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(bl.Exe);
                Log.Info("Launching '" + Item.DisplayName + "': " + bl.Exe + " " + bl.Args);
                Process p = Process.Start(psi);
                RootPid = p != null ? (uint)p.Id : 0;
                MatchProcess = bl.ProcessName;
                SelfFullscreen = bl.SelfFullscreen;
                LaunchedByUs = true;
                return await WaitForNewWindow(before, claimed, timeoutMs, bl.ProcessName, false, cancelled);
            }
            else
            {
                if (Item.Browser != BrowserKind.Default)
                    Log.Warn("No supported browser found, opening '" + Item.Url + "' with the default browser");
                HashSet<IntPtr> before = Snapshot();
                ProcessStartInfo psi = new ProcessStartInfo(Browsers.NormalizeUrl(Item.Url));
                psi.UseShellExecute = true;
                Process.Start(psi);
                RootPid = 0;
                MatchProcess = "";
                SelfFullscreen = false;
                LaunchedByUs = false; // the default browser may be the user's own session: never close it
                return await WaitForNewWindow(before, claimed, timeoutMs, "", true, cancelled);
            }
        }

        private static uint FindProcessWithMarker(string exeName, string marker)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exeName.Replace("'", "") + "'"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        string cl = mo["CommandLine"] as string;
                        if (cl != null && cl.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0
                            && cl.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0
                            && cl.IndexOf("-contentproc", StringComparison.OrdinalIgnoreCase) < 0)
                            return Convert.ToUInt32(mo["ProcessId"]);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Process lookup failed: " + ex.Message); }
            return 0;
        }

        private async Task<bool> LaunchApplication(ICollection<IntPtr> claimed, int timeoutMs, Func<bool> cancelled)
        {
            string path = Environment.ExpandEnvironmentVariables((Item.Path ?? "").Trim().Trim('"'));
            if (path.Length == 0) { LastError = "No program set"; Failures++; return false; }

            bool isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            MatchProcess = NormProc(Item.ProcessName);
            if (MatchProcess.Length == 0 && isExe) MatchProcess = Path.GetFileNameWithoutExtension(path);

            // With a title filter, an already-open matching window is adopted instead of launching a duplicate.
            if (!string.IsNullOrWhiteSpace(Item.TitleContains))
            {
                foreach (WindowInfo w in WindowTools.ListAppWindows(true))
                {
                    if (claimed.Contains(w.Handle) || !TitleOk(Item, w)) continue;
                    if (MatchProcess.Length > 0 && !string.Equals(MatchProcess, w.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                    Adopt(w.Handle);
                    LaunchedByUs = false;
                    Log.Info("Adopted existing window for '" + Item.DisplayName + "': " + w.Title);
                    return true;
                }
            }

            HashSet<IntPtr> before = Snapshot();
            ProcessStartInfo psi = new ProcessStartInfo(path, Environment.ExpandEnvironmentVariables(Item.Arguments ?? ""));
            psi.UseShellExecute = true;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            }
            catch { }
            Log.Info("Launching '" + Item.DisplayName + "': " + path + " " + Item.Arguments);
            Process p = Process.Start(psi);
            RootPid = 0;
            try { if (p != null) RootPid = (uint)p.Id; } catch { }
            LaunchedByUs = true;
            return await WaitForNewWindow(before, claimed, timeoutMs, MatchProcess, true, cancelled);
        }

        private async Task<bool> WaitForNewWindow(HashSet<IntPtr> before, ICollection<IntPtr> claimed, int timeoutMs,
            string procName, bool allowAnyNew, Func<bool> cancelled)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) return false;
                await Task.Delay(250);
                List<WindowInfo> wins = WindowTools.ListAppWindows(true);
                Dictionary<uint, uint> parents = WindowTools.ParentMap();
                WindowInfo best = null;
                int bestScore = 0;
                foreach (WindowInfo w in wins)
                {
                    if (before.Contains(w.Handle) || claimed.Contains(w.Handle)) continue;
                    if (!TitleOk(Item, w)) continue;
                    int score = 0;
                    if (RootPid != 0 && WindowTools.IsInTree(w.Pid, RootPid, parents)) score = 3;
                    else if (procName.Length > 0 && string.Equals(w.ProcessName, procName, StringComparison.OrdinalIgnoreCase)) score = 2;
                    else if (allowAnyNew && sw.ElapsedMilliseconds > 1500) score = 1; // e.g. UWP apps, documents
                    if (score > bestScore || (score == bestScore && score > 0 && Pick(best, w) == w)) { best = w; bestScore = score; }
                }
                if (best != null)
                {
                    // give splash screens a moment to be replaced by the real window
                    if (bestScore < 3 || sw.ElapsedMilliseconds < 800) await Task.Delay(400);
                    if (!WindowTools.IsAlive(best.Handle)) continue;
                    Adopt(best.Handle);
                    if (RootPid == 0 || bestScore < 3) RootPid = best.Pid;
                    Log.Info("Window found for '" + Item.DisplayName + "' after " + sw.ElapsedMilliseconds + " ms: " + best.Title + " [" + best.ProcessName + "]");
                    return true;
                }

                // Single-instance apps hand the request to an already running copy and exit.
                if (sw.ElapsedMilliseconds > 5000 && Item.Kind == ItemKind.Application && procName.Length > 0)
                {
                    foreach (WindowInfo w in wins)
                    {
                        if (claimed.Contains(w.Handle) || !TitleOk(Item, w)) continue;
                        if (string.Equals(w.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                        {
                            Adopt(w.Handle);
                            RootPid = w.Pid;
                            LaunchedByUs = false;
                            Log.Info("Using existing window of " + procName + " for '" + Item.DisplayName + "'");
                            return true;
                        }
                    }
                }
            }
            LastError = "No window appeared within " + (timeoutMs / 1000) + " s";
            Failures++;
            Log.Warn("'" + Item.DisplayName + "': " + LastError);
            return false;
        }

        // ------------------------------------------------------------------ presentation

        public void ApplyMode(Rectangle bounds)
        {
            if (!IsAlive) return;
            switch (Item.Mode)
            {
                case WindowMode.Fullscreen:
                    if (UserProfile && !F11Failed)
                    {
                        // fullscreen comes from the browser's own F11 (see WantsFullscreenKey); just keep it on the right display
                        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
                        if (!CoversScreen(bounds)) WindowTools.MoveOnto(Hwnd, bounds);
                        return;
                    }
                    if (SelfFullscreen)
                    {
                        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
                        Rectangle r = WindowTools.GetRect(Hwnd);
                        if (r.Contains(bounds)) return;   // kiosk window already covers the screen
                        // the browser switches to kiosk a moment after its window appears: don't interfere meanwhile
                        if ((DateTime.UtcNow - foundAt).TotalSeconds < 8 && Item.Kind == ItemKind.Website) return;
                    }
                    SaveStyles();
                    WindowTools.FitFullscreen(Hwnd, bounds, Item.TitleBarCrop, ref cachedCaption);
                    break;
                case WindowMode.Maximized:
                    WindowTools.MaximizeOn(Hwnd, bounds);
                    break;
            }
        }

        public bool CoversScreen(Rectangle bounds)
        {
            return IsAlive && WindowTools.GetRect(Hwnd).Contains(bounds);
        }

        /// <summary>True when the browser's own fullscreen (F11) should be switched on now (window has the focus).</summary>
        public bool WantsFullscreenKey(Rectangle bounds)
        {
            return UserProfile && !F11Failed && Item.Mode == WindowMode.Fullscreen && IsAlive && !CoversScreen(bounds)
                && Native.GetForegroundWindow() == Hwnd;
        }

        /// <summary>After F11 was sent: if the window still doesn't cover the screen, use ScreenCycle's own fullscreen instead.</summary>
        public void CheckFullscreenKey(Rectangle bounds)
        {
            if (!IsAlive) return;
            if (CoversScreen(bounds)) { f11Misses = 0; return; }
            // A browser dialog (restore session, first-run...) can swallow the key: maximize for now, retry next turn.
            if (++f11Misses < 3)
            {
                Log.Info("'" + Item.DisplayName + "': F11 had no effect (dialog open?), will try again next time");
                WindowTools.MaximizeOn(Hwnd, bounds);
                return;
            }
            F11Failed = true;
            Log.Warn("'" + Item.DisplayName + "': the browser did not go fullscreen with F11, ScreenCycle fits the window instead");
        }
        private void SaveStyles()
        {
            if (stylesSaved) return;
            origStyle = Native.GetWindowLong(Hwnd, Native.GWL_STYLE);
            origEx = Native.GetWindowLong(Hwnd, Native.GWL_EXSTYLE);
            Native.WINDOWPLACEMENT wp = new Native.WINDOWPLACEMENT();
            wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
            if (Native.IsZoomed(Hwnd) || Native.IsIconic(Hwnd))
            {
                Native.GetWindowPlacement(Hwnd, ref wp);
                origRect = Rectangle.FromLTRB(wp.rcNormalPosition.Left, wp.rcNormalPosition.Top, wp.rcNormalPosition.Right, wp.rcNormalPosition.Bottom);
            }
            else origRect = WindowTools.GetRect(Hwnd);
            stylesSaved = true;
        }

        /// <summary>Gives a window we did not launch its frame back.</summary>
        public void RestoreIfAdopted()
        {
            if (LaunchedByUs || !stylesSaved || !IsAlive) { stylesSaved = false; return; }
            WindowTools.RestoreStyles(Hwnd, origStyle, origEx, origRect);
            stylesSaved = false;
        }

        public void Release(bool closeLaunched)
        {
            try
            {
                if (LaunchedByUs && closeLaunched)
                {
                    if (IsAlive) Native.PostMessage(Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                else RestoreIfAdopted();
            }
            catch (Exception ex) { Log.Warn("Release failed: " + ex.Message); }
            Hwnd = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Remembers which window was opened for a screen that uses the person's own browser profile. That browser keeps
    /// running when ScreenCycle exits, so the next start can take the same window again instead of opening a duplicate.
    /// </summary>
    internal static class WindowMemory
    {
        private static string FilePath { get { return Path.Combine(AppConfig.DataDir, "windows.txt"); } }

        private static Dictionary<string, long> Load()
        {
            Dictionary<string, long> d = new Dictionary<string, long>();
            try
            {
                if (!File.Exists(FilePath)) return d;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split('|');
                    long h;
                    if (p.Length == 2 && long.TryParse(p[1], out h)) d[p[0]] = h;
                }
            }
            catch { }
            return d;
        }

        public static void Remember(string itemId, IntPtr hwnd)
        {
            try
            {
                Dictionary<string, long> d = Load();
                d[itemId] = hwnd.ToInt64();
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, long> kv in d)
                    if (WindowTools.IsAlive(new IntPtr(kv.Value))) lines.Add(kv.Key + "|" + kv.Value);
                Directory.CreateDirectory(AppConfig.DataDir);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception ex) { Log.Warn("Window memory: " + ex.Message); }
        }

        /// <summary>The remembered window, if it still exists and still belongs to that browser.</summary>
        public static IntPtr Recall(string itemId, string processName)
        {
            long v;
            if (!Load().TryGetValue(itemId, out v)) return IntPtr.Zero;
            IntPtr h = new IntPtr(v);
            if (!WindowTools.IsAlive(h) || !WindowTools.IsAppWindow(h)) return IntPtr.Zero;
            if (!string.Equals(WindowTools.GetProcessName(WindowTools.GetPid(h)), processName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            return h;
        }
    }
}