using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenCycle
{
    internal enum RotatorState { Stopped, Preparing, Running }

    /// <summary>The rotation engine. Everything runs on the UI thread; async methods only await delays.</summary>
    internal sealed class Rotator : IDisposable
    {
        private AppConfig cfg;
        private readonly Dictionary<string, ItemRuntime> byId = new Dictionary<string, ItemRuntime>();
        private readonly Dictionary<string, string> signatures = new Dictionary<string, string>();
        private List<ItemRuntime> order = new List<ItemRuntime>();

        private readonly ScreenOverlay overlay = new ScreenOverlay();
        private readonly OsdForm osd = new OsdForm();
        private readonly ProgressStrip strip = new ProgressStrip();
        private readonly Timer timer = new Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private long lastTickMs;
        private long lastWatchdogMs;
        private int current = -1;
        private double elapsedMs;
        private bool busy;
        private int generation;
        private bool awake;
        private bool overlayUp;
        private IntPtr topmostHwnd;     // fallback: item kept above the taskbar because it could not take the focus
        private long lastFocusRetryMs;
        private IntPtr loggedLostTopmost;
        private IntPtr lastIntruder;
        private static readonly bool testNoFocus = Environment.GetEnvironmentVariable("SCREENCYCLE_TEST_NOFOCUS") == "1";

        public RotatorState State { get; private set; }
        public bool UserPaused { get; private set; }
        public bool ActivityPaused { get; private set; }
        public bool IsPaused { get { return UserPaused || ActivityPaused; } }
        public event EventHandler StateChanged;

        public Rotator(AppConfig config)
        {
            cfg = config;
            State = RotatorState.Stopped;
            strip.Alpha = 220;
            timer.Interval = 40;
            timer.Tick += OnTick;
            timer.Start();
            Rebuild();
        }

        // ------------------------------------------------------------------ public info

        public List<RotationItem> ActiveItems
        {
            get
            {
                List<RotationItem> l = new List<RotationItem>();
                foreach (ItemRuntime r in order) l.Add(r.Item);
                return l;
            }
        }

        public RotationItem CurrentItem
        {
            get { return current >= 0 && current < order.Count ? order[current].Item : null; }
        }

        public double SecondsLeft
        {
            get
            {
                RotationItem i = CurrentItem;
                if (i == null) return 0;
                return Math.Max(0, i.DurationSeconds - elapsedMs / 1000.0);
            }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case RotatorState.Preparing: return "Preparing…";
                    case RotatorState.Running:
                        RotationItem c = CurrentItem;
                        string name = c != null ? c.DisplayName : "";
                        if (UserPaused) return "Paused — " + name;
                        if (ActivityPaused) return "Paused (screen in use) — " + name;
                        return "Showing " + name;
                    default: return "Stopped";
                }
            }
        }

        private Rectangle Bounds
        {
            get { return WindowTools.ResolveScreen(cfg.MonitorDevice).Bounds; }
        }

        private void Raise()
        {
            EventHandler h = StateChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Notify(string glyph, string text)
        {
            if (!cfg.ShowNotifications) return;
            try { osd.Flash(Bounds, glyph, text); } catch (Exception ex) { Log.Warn("OSD: " + ex.Message); }
        }

        // ------------------------------------------------------------------ configuration

        public void ApplyConfig(AppConfig config)
        {
            cfg = config;
            Rebuild();
            Raise();
        }

        private void Rebuild()
        {
            HashSet<string> keep = new HashSet<string>();
            List<ItemRuntime> newOrder = new List<ItemRuntime>();
            foreach (RotationItem item in cfg.Items)
            {
                keep.Add(item.Id);
                string sig = ItemRuntime.Signature(item);
                ItemRuntime rt;
                string oldSig;
                if (byId.TryGetValue(item.Id, out rt) && signatures.TryGetValue(item.Id, out oldSig) && oldSig == sig)
                {
                    rt.Item = item; // name/duration/etc. may change without relaunching
                }
                else
                {
                    if (rt != null) rt.Release(true);
                    rt = new ItemRuntime(item);
                    byId[item.Id] = rt;
                    signatures[item.Id] = sig;
                }
                if (item.Enabled) newOrder.Add(rt);
                else if (rt.IsAlive) rt.RestoreIfAdopted();
            }
            foreach (string id in new List<string>(byId.Keys))
            {
                if (keep.Contains(id)) continue;
                byId[id].Release(true);
                byId.Remove(id);
                signatures.Remove(id);
            }
            // keep pointing at the same item if it still exists
            RotationItem cur = CurrentItem;
            order = newOrder;
            current = -1;
            if (cur != null)
                for (int i = 0; i < order.Count; i++) if (order[i].Item.Id == cur.Id) current = i;
        }

        private HashSet<IntPtr> ClaimedExcept(ItemRuntime except)
        {
            HashSet<IntPtr> set = new HashSet<IntPtr>();
            foreach (ItemRuntime r in byId.Values)
                if (r != except && r.IsAlive) set.Add(r.Hwnd);
            return set;
        }

        // ------------------------------------------------------------------ control

        public void Start(string startItemId)
        {
            if (State != RotatorState.Stopped) return;
            if (order.Count == 0)
            {
                Notify("", "Nothing to rotate yet — add screens in Settings");
                return;
            }
            int start = 0;
            if (startItemId != null)
            {
                for (int i = 0; i < order.Count; i++) if (order[i].Item.Id == startItemId) start = i;
            }
            else if (current >= 0) start = current;

            generation++;
            UserPaused = false;
            ActivityPaused = false;
            State = RotatorState.Preparing;
            SetAwake(cfg.KeepScreenAwake);
            if (cfg.AutoHideTaskbar) Taskbar.BeginAutoHide();
            Log.Info("Rotation starting (" + order.Count + " items)");
            Raise();
            PrepareAndRun(generation, start);
        }

        public void Stop()
        {
            if (State == RotatorState.Stopped) return;
            generation++;
            State = RotatorState.Stopped;
            UserPaused = false;
            ActivityPaused = false;
            HideOverlay();
            strip.Hide();
            SetAwake(false);
            ClearTopmost();
            Taskbar.EndAutoHide();
            foreach (ItemRuntime r in byId.Values)
            {
                if (r.IsAlive) Taskbar.MarkFullscreen(r.Hwnd, false);
                r.RestoreIfAdopted();
            }
            Log.Info("Rotation stopped");
            Raise();
        }

        public void TogglePause()
        {
            if (State == RotatorState.Stopped) { Start(null); return; }
            if (State != RotatorState.Running) return;
            if (IsPaused)
            {
                UserPaused = false;
                ActivityPaused = false;
                Notify("", "Rotation resumed");
                ReassertCurrent();
            }
            else
            {
                UserPaused = true;
                Notify("", "Rotation paused");
            }
            Raise();
        }

        public void Next() { Step(+1); }
        public void Previous() { Step(-1); }

        private void Step(int dir)
        {
            if (State == RotatorState.Stopped) { Start(null); return; }
            if (State != RotatorState.Running || busy || order.Count == 0) return;
            if (ActivityPaused) ActivityPaused = false;
            int idx = FindReady(current, dir, true);
            ShowAsync(idx < 0 ? Wrap(current + dir) : idx, false);
        }

        public void JumpTo(string itemId)
        {
            int idx = -1;
            for (int i = 0; i < order.Count; i++) if (order[i].Item.Id == itemId) idx = i;
            if (idx < 0) return;
            if (State == RotatorState.Stopped) { Start(itemId); return; }
            if (State != RotatorState.Running || busy) return;
            ActivityPaused = false;
            ShowAsync(idx, false);
        }

        /// <summary>Stops rotation and hands windows back (or closes the ones we opened).</summary>
        public void Shutdown()
        {
            Stop();
            timer.Stop();
            foreach (ItemRuntime r in byId.Values) r.Release(cfg.CloseLaunchedAppsOnExit);
        }

        // ------------------------------------------------------------------ engine

        private int Wrap(int i)
        {
            int n = order.Count;
            if (n == 0) return -1;
            return ((i % n) + n) % n;
        }

        /// <summary>Next index (in direction) whose window is alive or can be re-found; -1 if none.</summary>
        private int FindReady(int from, int dir, bool allowRelaunch)
        {
            int n = order.Count;
            for (int k = 1; k <= n; k++)
            {
                int i = Wrap(from + dir * k);
                ItemRuntime r = order[i];
                if (r.IsAlive || r.TryRefind(ClaimedExcept(r))) return i;
                if (allowRelaunch && cfg.RelaunchClosedItems && r.Item.Kind != ItemKind.Window && r.Failures < 3) return i;
            }
            return -1;
        }

        private async void PrepareAndRun(int gen, int start)
        {
            try
            {
                Rectangle b = Bounds;
                bool allAlive = true;
                foreach (ItemRuntime r in order)
                    if (!r.IsAlive && !r.TryRefind(ClaimedExcept(r))) allAlive = false;

                if (!allAlive)
                {
                    overlay.SetSplash("Preparing your screens", "");
                    overlay.Place(b);
                    overlay.Alpha = 0;
                    overlay.ShowNoActivate();
                    overlayUp = true;
                    await overlay.AnimateAlpha(0, 255, 350);
                }

                for (int i = 0; i < order.Count; i++)
                {
                    if (gen != generation) return;
                    ItemRuntime r = order[i];
                    if (!r.IsAlive)
                    {
                        r.Failures = 0;
                        overlay.SetSplash("Preparing your screens", "Opening " + r.Item.DisplayName + "  (" + (i + 1) + " of " + order.Count + ")");
                        int g = gen;
                        await r.EnsureAsync(ClaimedExcept(r), b, 30000, delegate { return g != generation; });
                        if (gen != generation) return;
                        if (overlayUp) overlay.ShowNoActivate(); // stay above freshly opened windows
                    }
                    if (r.IsAlive)
                    {
                        r.ApplyMode(b);
                        if (overlayUp) Native.SetWindowPos(r.Hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }
                }
                if (gen != generation) return;

                int first = order[Wrap(start)].IsAlive ? Wrap(start) : FindReady(start, +1, false);
                if (first < 0)
                {
                    string why = order.Count > 0 && order[0].LastError.Length > 0 ? order[0].LastError : "check the items in Settings";
                    if (overlayUp)
                    {
                        overlay.SetSplash("Nothing could be opened", why);
                        await Task.Delay(4000);
                    }
                    if (gen != generation) return;
                    Log.Warn("No item could be shown, stopping");
                    Stop();
                    Notify("", "No screen could be opened — " + why);
                    return;
                }
                State = RotatorState.Running;
                Raise();
                await ShowCore(first, gen);
            }
            catch (Exception ex)
            {
                Log.Error("Prepare failed: " + ex);
                if (gen == generation) Stop();
            }
        }

        private async void ShowAsync(int idx, bool unused)
        {
            if (busy || idx < 0) return;
            try { await ShowCore(idx, generation); }
            catch (Exception ex) { Log.Error("Show failed: " + ex); busy = false; HideOverlay(); }
        }

        private async Task BeginTransition(Rectangle b, bool forceFreeze)
        {
            if (overlayUp) return;
            int ms = cfg.TransitionMs;
            TransitionKind kind = cfg.Transition;
            if (kind == TransitionKind.None && !forceFreeze) return;
            overlay.Place(b);
            if (kind == TransitionKind.FadeThroughBlack && !forceFreeze)
            {
                overlay.SetBlack();
                overlay.Alpha = 0;
                overlay.ShowNoActivate();
                overlayUp = true;
                await overlay.AnimateAlpha(0, 255, ms / 2);
            }
            else
            {
                overlay.CaptureScreen(b);
                overlay.Alpha = 255;
                overlay.ShowNoActivate();
                overlayUp = true;
            }
        }

        private async Task EndTransition()
        {
            if (!overlayUp) return;
            int ms = cfg.TransitionMs;
            switch (cfg.Transition)
            {
                case TransitionKind.FadeThroughBlack: ms = ms / 2; break;
                case TransitionKind.None: ms = 150; break;
            }
            await overlay.AnimateAlpha(overlay.Alpha, 0, Math.Max(ms, 120));
            HideOverlay();
        }

        private void HideOverlay()
        {
            overlayUp = false;
            overlay.Hide();
            overlay.SetBlack(); // free the screenshot
        }

        private async Task ShowCore(int idx, int gen)
        {
            if (busy) return;
            busy = true;
            try
            {
                Rectangle b = Bounds;
                ItemRuntime r = order[idx];
                bool needsLaunch = !r.IsAlive && !r.TryRefind(ClaimedExcept(r));

                await BeginTransition(b, needsLaunch);
                if (gen != generation) return;

                if (needsLaunch)
                {
                    bool ok = false;
                    if (cfg.RelaunchClosedItems || r.Item.Kind == ItemKind.Window)
                    {
                        overlay.SetLoadingBadge("Opening " + r.Item.DisplayName + "…");
                        int g = gen;
                        ok = await r.EnsureAsync(ClaimedExcept(r), b, 20000, delegate { return g != generation; });
                        if (gen != generation) return;
                        overlay.SetLoadingBadge("");
                        overlay.ShowNoActivate();
                    }
                    if (!ok)
                    {
                        Log.Warn("Skipping '" + r.Item.DisplayName + "': " + r.LastError);
                        int alt = -1;
                        for (int k = 1; k < order.Count; k++)
                        {
                            int j = Wrap(idx + k);
                            if (order[j].IsAlive || order[j].TryRefind(ClaimedExcept(order[j]))) { alt = j; break; }
                        }
                        if (alt < 0)
                        {
                            elapsedMs = 0;
                            await EndTransition();
                            return;
                        }
                        idx = alt;
                        r = order[idx];
                    }
                }

                if (topmostHwnd != r.Hwnd) ClearTopmost(); // before the new window goes on top
                r.ApplyMode(b);
                bool fg = Focus(r);
                Log.Info("Showing '" + r.Item.DisplayName + "'" + (fg ? "" : " (could not take focus, kept above the taskbar)"));
                if (fg && r.WantsFullscreenKey(b))
                {
                    // website in the person's own browser profile: use the browser's own fullscreen (F11)
                    WindowTools.SendKey(Keys.F11);
                    await Task.Delay(900);
                    if (gen != generation) return;
                    r.CheckFullscreenKey(b);
                    if (r.F11Failed) r.ApplyMode(b);
                    if (!r.CoversScreen(b)) Focus(r); // maximized for now: keep it in front
                }
                if (overlayUp) overlay.ShowNoActivate();
                current = idx;
                elapsedMs = 0;
                Raise();

                int settle = overlayUp ? 160 : 0;
                if (r.Item.RefreshOnShow)
                {
                    await Task.Delay(80);
                    if (Native.GetForegroundWindow() == r.Hwnd) WindowTools.SendKey(Keys.F5);
                    settle = overlayUp ? 700 : 0;
                }
                if (cfg.ParkMouseCursor)
                {
                    SyntheticInput.Mark();
                    Point park = MouseParkingSpot(b);
                    Native.SetCursorPos(park.X, park.Y);
                }
                if (settle > 0) await Task.Delay(settle);
                if (gen != generation) return;
                await EndTransition();
                if (gen != generation) return;
                if (r.IsAlive && Native.GetForegroundWindow() != r.Hwnd) Focus(r); // the overlay is gone: try once more
                KeepTopmost();
                elapsedMs = 0;
                lastWatchdogMs = clock.ElapsedMilliseconds;
            }
            finally
            {
                busy = false;
                if (gen != generation && State == RotatorState.Stopped) HideOverlay();
            }
        }

        /// <summary>
        /// Gives the item the keyboard focus. The taskbar only steps back behind a fullscreen window that has the focus,
        /// and Windows sometimes refuses to hand the focus over (someone is typing in another app...). In that case a
        /// fullscreen item is kept "always on top" for its turn, so it still covers the taskbar.
        /// </summary>
        private bool Focus(ItemRuntime r)
        {
            if (!r.IsAlive) return false;
            bool fullscreen = r.Item.Mode == WindowMode.Fullscreen;
            if (fullscreen) Taskbar.MarkFullscreen(r.Hwnd, true);
            bool fg = !testNoFocus && WindowTools.Activate(r.Hwnd);
            if (fg)
            {
                if (topmostHwnd == r.Hwnd) ClearTopmost();
            }
            else if (fullscreen && topmostHwnd != r.Hwnd)
            {
                ClearTopmost();
                MakeTopmost(r.Hwnd);
                topmostHwnd = r.Hwnd;
                if (strip.Visible) strip.ShowNoActivate(); // keep the progress line above it
            }
            lastFocusRetryMs = clock.ElapsedMilliseconds;
            return fg;
        }

        /// <summary>Some apps (Firefox...) drop the "always on top" state again on their own: put it back.</summary>
        private void KeepTopmost()
        {
            if (topmostHwnd == IntPtr.Zero || !Native.IsWindow(topmostHwnd)) return;
            if (IsTopmost(topmostHwnd)) return;
            MakeTopmost(topmostHwnd);
            if (strip.Visible) strip.ShowNoActivate();
            if (loggedLostTopmost != topmostHwnd) { loggedLostTopmost = topmostHwnd; Log.Info("Window lost its always-on-top state, put back"); }
        }

        private static bool IsTopmost(IntPtr h)
        {
            return (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
        }

        private static void MakeTopmost(IntPtr h)
        {
            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (IsTopmost(h)) return;
            // Firefox (and maybe others) ignore the first request; going through "not topmost" first makes it stick.
            Native.SetWindowPos(h, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }

        private void ClearTopmost()
        {
            if (topmostHwnd == IntPtr.Zero) return;
            IntPtr h = topmostHwnd;
            topmostHwnd = IntPtr.Zero;
            if (Native.IsWindow(h))
                Native.SetWindowPos(h, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>
        /// Where to leave the mouse pointer: the middle of the right edge, where only one pixel of the arrow is visible
        /// and nothing pops up. Never at the bottom, which would bring up the taskbar (unless the taskbar is on the right).
        /// </summary>
        private static Point MouseParkingSpot(Rectangle b)
        {
            if (Taskbar.Edge() == Taskbar.EdgeRight) return new Point(b.Left + b.Width / 2, b.Bottom - 1);
            return new Point(b.Right - 1, b.Top + b.Height / 2);
        }

        /// <summary>Puts the current item back on top (after a pause, or when something stole the screen).</summary>
        private void ReassertCurrent()
        {
            if (State != RotatorState.Running || busy) return;
            ItemRuntime r = current >= 0 && current < order.Count ? order[current] : null;
            if (r == null || !r.IsAlive) { Step(+1); return; }
            Rectangle b = Bounds;
            r.ApplyMode(b);
            if (Native.GetForegroundWindow() != r.Hwnd) Focus(r);
            elapsedMs = 0;
        }

        private void OnTick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            long dt = now - lastTickMs;
            lastTickMs = now;
            if (dt < 0 || dt > 5000) dt = 0;  // sleep / resume safety

            try
            {
                if (State != RotatorState.Running || busy)
                {
                    UpdateStrip();
                    return;
                }

                // --- someone is using the screen?
                if (cfg.PauseOnUserActivity && !UserPaused)
                {
                    uint idle = Native.IdleMilliseconds();
                    bool fresh = idle < 500 && SyntheticInput.MsSinceLast > 1000;
                    if (!ActivityPaused && fresh)
                    {
                        ActivityPaused = true;
                        ClearTopmost(); // let the person use other windows
                        Log.Info("User activity detected, pausing");
                        Notify("", "Paused while you use the screen");
                        Raise();
                    }
                    else if (ActivityPaused && idle >= (uint)cfg.ResumeAfterIdleSeconds * 1000u)
                    {
                        ActivityPaused = false;
                        Log.Info("Idle again, resuming");
                        Raise();
                        int nxt = FindReady(current, +1, true);
                        if (nxt >= 0 && nxt != current) ShowAsync(nxt, false); else ReassertCurrent();
                        return;
                    }
                }
                else if (ActivityPaused && !cfg.PauseOnUserActivity)
                {
                    ActivityPaused = false;
                    Raise();
                }

                RotationItem cur = CurrentItem;
                if (!IsPaused && cur != null)
                {
                    elapsedMs += dt;
                    if (elapsedMs >= cur.DurationSeconds * 1000.0)
                    {
                        if (order.Count > 1)
                        {
                            int nxt = FindReady(current, +1, true);
                            if (nxt >= 0 && nxt != current) { ShowAsync(nxt, false); UpdateStrip(); return; }
                        }
                        elapsedMs = 0;
                        ReassertCurrent();
                        if (cur.RefreshOnShow && Native.GetForegroundWindow() == order[current].Hwnd) WindowTools.SendKey(Keys.F5);
                    }
                }

                KeepTopmost();

                // --- watchdog: window closed, or something else took over the screen
                if (now - lastWatchdogMs > 2500)
                {
                    lastWatchdogMs = now;
                    Watchdog();
                }
                UpdateStrip();
            }
            catch (Exception ex)
            {
                Log.Error("Tick: " + ex);
            }
        }

        private void Watchdog()
        {
            if (current < 0 || current >= order.Count) { Step(+1); return; }
            ItemRuntime r = order[current];
            if (!r.IsAlive)
            {
                if (!r.TryRefind(ClaimedExcept(r)))
                {
                    Log.Info("'" + r.Item.DisplayName + "' window disappeared");
                    Step(+1);
                }
                return;
            }
            if (IsPaused) return;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == r.Hwnd) return;
            uint fgPid = fg == IntPtr.Zero ? 0 : WindowTools.GetPid(fg);
            if (fgPid != 0 && fgPid == (uint)Process.GetCurrentProcess().Id) return;     // our settings window etc.
            if (fgPid != 0 && fgPid == WindowTools.GetPid(r.Hwnd)) return;               // the app's own dialog
            if (fg == IntPtr.Zero || !WindowTools.IsAppWindow(fg))
            {
                // Focus is on the desktop, the taskbar or nowhere (a failed hand-over): the taskbar may be showing over
                // the item. Take the focus back quietly once nobody has touched the PC for a few seconds.
                bool someoneActive = Native.IdleMilliseconds() < 8000 && SyntheticInput.MsSinceLast > 1000;
                if (!someoneActive && clock.ElapsedMilliseconds - lastFocusRetryMs > 7000) Focus(r);
                return;                                                    // start menu, popups...: leave them alone otherwise
            }
            if (fg != lastIntruder) { lastIntruder = fg; Log.Info("Another window took the screen (" + WindowTools.GetTitle(fg) + "), bringing '" + r.Item.DisplayName + "' back"); }
            r.ApplyMode(Bounds);
            Focus(r);
        }

        private void UpdateStrip()
        {
            bool show = cfg.ShowProgressBar && State == RotatorState.Running && CurrentItem != null;
            if (!show) { if (strip.Visible) strip.Hide(); return; }
            strip.Place(Bounds);
            if (!strip.Visible) strip.ShowNoActivate();
            RotationItem cur = CurrentItem;
            strip.Progress = elapsedMs / (cur.DurationSeconds * 1000.0);
        }

        private void SetAwake(bool on)
        {
            if (on == awake) return;
            awake = on;
            if (on) Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED);
            else Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }

        public void Dispose()
        {
            timer.Dispose();
            overlay.Dispose();
            osd.Dispose();
            strip.Dispose();
        }
    }
}
