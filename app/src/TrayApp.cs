using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenCycle
{
    internal sealed class TrayApp : ApplicationContext
    {
        private AppConfig cfg;
        private readonly Rotator rotator;
        private readonly HotkeyManager hotkeys;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly Control invoker;
        private SettingsForm settings;
        private List<string> hotkeyFailures = new List<string>();
        private IconState iconState = (IconState)(-1);
        private readonly Dictionary<IconState, Icon> icons = new Dictionary<IconState, Icon>();
        private bool exiting;

        private ToolStripMenuItem miStatus, miPause, miStartStop, miNext, miPrev, miShow, miSettings, miExit;

        public TrayApp(AppConfig config, bool autostart, bool openSettings, bool noStart)
        {
            cfg = config;
            invoker = new Control();
            invoker.CreateControl();

            rotator = new Rotator(cfg);
            rotator.StateChanged += delegate { UpdateUi(); };

            hotkeys = new HotkeyManager();
            hotkeys.Pressed += OnHotkey;
            hotkeyFailures = hotkeys.Apply(cfg.Hotkeys);
            HotkeyBox.Recording += delegate(bool on)
            {
                if (on) hotkeys.Suspend(); else hotkeyFailures = hotkeys.Apply(cfg.Hotkeys);
            };

            menu = BuildMenu();
            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = "ScreenCycle";
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowSettings();
            };
            UpdateUi();
            tray.Visible = true;

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionEnding += delegate { ExitApp(); };

            if (cfg.StartWithWindows != Autostart.IsSet()) Autostart.Set(cfg.StartWithWindows);

            if (cfg.Items.Count == 0 || openSettings)
            {
                invoker.BeginInvoke((Action)delegate
                {
                    if (cfg.Items.Count == 0)
                        tray.ShowBalloonTip(5000, "ScreenCycle", "ScreenCycle lives here in the tray. Add the apps and websites you want to rotate.", ToolTipIcon.Info);
                    ShowSettings();
                });
            }
            else if (cfg.StartRotationOnLaunch && !noStart)
            {
                int delay = autostart ? cfg.StartupDelaySeconds * 1000 : 300;
                DelayedStart(delay);
            }
            if (hotkeyFailures.Count > 0)
                tray.ShowBalloonTip(6000, "Some shortcuts are unavailable",
                    "Already used by another program: " + string.Join(", ", hotkeyFailures.ToArray()) + ". You can change them in Settings.", ToolTipIcon.Warning);
        }

        private async void DelayedStart(int ms)
        {
            if (ms > 0)
            {
                tray.Text = "ScreenCycle — starting in " + (ms / 1000) + " s";
                await Task.Delay(ms);
            }
            if (exiting || settings != null) return;
            rotator.Start(null);
        }

        // ------------------------------------------------------------------ menu

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            m.Font = new Font("Segoe UI", 9.5f);
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors());

            miStatus = new ToolStripMenuItem("ScreenCycle");
            miStatus.Enabled = false;
            miStatus.Font = new Font("Segoe UI Semibold", 9.5f);
            miPause = new ToolStripMenuItem("Pause", null, delegate { rotator.TogglePause(); });
            miStartStop = new ToolStripMenuItem("Start rotation", null, delegate
            {
                if (rotator.State == RotatorState.Stopped) rotator.Start(null); else rotator.Stop();
            });
            miNext = new ToolStripMenuItem("Next screen", null, delegate { rotator.Next(); });
            miPrev = new ToolStripMenuItem("Previous screen", null, delegate { rotator.Previous(); });
            miShow = new ToolStripMenuItem("Show now");
            miSettings = new ToolStripMenuItem("Settings…", null, delegate { ShowSettings(); });
            miSettings.Font = new Font("Segoe UI Semibold", 9.5f);
            miExit = new ToolStripMenuItem("Exit ScreenCycle", null, delegate { ExitApp(); });

            m.Items.AddRange(new ToolStripItem[] {
                miStatus, new ToolStripSeparator(),
                miPause, miStartStop, miNext, miPrev, miShow, new ToolStripSeparator(),
                miSettings, miExit });
            m.Opening += delegate { RebuildShowMenu(); UpdateUi(); };
            return m;
        }

        private void RebuildShowMenu()
        {
            miShow.DropDownItems.Clear();
            RotationItem cur = rotator.CurrentItem;
            foreach (RotationItem item in rotator.ActiveItems)
            {
                string id = item.Id;
                ToolStripMenuItem mi = new ToolStripMenuItem(item.DisplayName, null, delegate { rotator.JumpTo(id); });
                mi.Checked = cur != null && cur.Id == id && rotator.State == RotatorState.Running;
                miShow.DropDownItems.Add(mi);
            }

        }

        private static string Hint(HotkeySpec h)
        {
            return h == null || h.IsEmpty ? null : h.ToString();
        }

        private void UpdateUi()
        {
            if (exiting) return;
            RotatorState st = rotator.State;
            bool running = st != RotatorState.Stopped;

            IconState want = !running ? IconState.Stopped : (rotator.IsPaused ? IconState.Paused : IconState.Running);
            if (want != iconState)
            {
                iconState = want;
                Icon ic;
                if (!icons.TryGetValue(want, out ic))
                {
                    int size = SystemInformation.SmallIconSize.Width;
                    ic = IconFactory.MakeIcon(Math.Max(16, size), want);
                    icons[want] = ic;
                }
                tray.Icon = ic;
            }
            string tip = "ScreenCycle — " + rotator.StatusText;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;

            if (miStatus != null)
            {
                miStatus.Text = rotator.StatusText;
                miPause.Text = rotator.IsPaused ? "Resume" : "Pause";
                bool free = settings == null;
                miPause.Enabled = free && st == RotatorState.Running;
                miStartStop.Enabled = free;
                miShow.Enabled = free && miShow.DropDownItems.Count > 0;
                miPause.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.PauseResume);
                miStartStop.Text = running ? "Stop rotation" : "Start rotation";
                miStartStop.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.StartStop);
                miNext.Enabled = miPrev.Enabled = free && st == RotatorState.Running;
                miNext.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.Next);
                miPrev.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.Previous);
                miSettings.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.Settings);
                miExit.ShortcutKeyDisplayString = Hint(cfg.Hotkeys.Exit);
            }
        }

        private void OnHotkey(HotkeyAction a)
        {
            if (settings != null && a != HotkeyAction.Settings && a != HotkeyAction.Exit)
            {
                ShowSettings(); // rotation is stopped while settings are open: just bring the window forward
                return;
            }
            switch (a)
            {
                case HotkeyAction.PauseResume: rotator.TogglePause(); break;
                case HotkeyAction.Next: rotator.Next(); break;
                case HotkeyAction.Previous: rotator.Previous(); break;
                case HotkeyAction.StartStop:
                    if (rotator.State == RotatorState.Stopped) rotator.Start(null);
                    else { rotator.Stop(); rotator.Notify("", "Rotation stopped"); }
                    break;
                case HotkeyAction.Settings: ShowSettings(); break;
                case HotkeyAction.Exit: ExitApp(); break;
            }
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            // monitors changed: the next switch re-applies fullscreen with new bounds
            Log.Info("Display settings changed");
        }

        // ------------------------------------------------------------------ settings

        public void ShowSettings()
        {
            if (exiting) return;
            if (settings != null)
            {
                if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
                settings.Activate();
                WindowTools.Activate(settings.Handle);
                return;
            }
            bool wasRunning = rotator.State != RotatorState.Stopped;
            string resumeAt = rotator.CurrentItem != null ? rotator.CurrentItem.Id : null;
            if (wasRunning) rotator.Stop();

            settings = new SettingsForm(cfg, hotkeyFailures, rotator.State);
            settings.Saved += delegate(AppConfig newCfg) { ApplyNewConfig(newCfg); };
            settings.FormClosed += delegate
            {
                bool startNow = settings.StartRequested || (wasRunning && !settings.StopRequested);
                string startAt = settings.StartAtItemId ?? resumeAt;
                bool quit = settings.ExitRequested;
                settings = null;
                if (exiting) return;
                if (quit) { InvokeOnUi(ExitApp); return; }
                if (startNow && cfg.Items.Exists(delegate(RotationItem i) { return i.Enabled; }))
                    rotator.Start(startAt);
                UpdateUi();
            };
            settings.Show();
            WindowTools.Activate(settings.Handle);
        }

        private void ApplyNewConfig(AppConfig newCfg)
        {
            cfg = newCfg;
            try { cfg.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save settings:\n" + ex.Message, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Autostart.Set(cfg.StartWithWindows);
            hotkeyFailures = hotkeys.Apply(cfg.Hotkeys);
            rotator.ApplyConfig(cfg);
            if (settings != null) settings.SetHotkeyFailures(hotkeyFailures);
            UpdateUi();
        }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            Log.Info("Exiting");
            try { if (settings != null) settings.Close(); } catch { }
            try { rotator.Shutdown(); } catch (Exception ex) { Log.Error("Shutdown: " + ex); }
            try { hotkeys.Dispose(); } catch { }
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            tray.Visible = false;
            tray.Dispose();
            rotator.Dispose();
            ExitThread();
        }

        public void InvokeOnUi(Action a)
        {
            if (invoker.IsHandleCreated) invoker.BeginInvoke(a);
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(229, 239, 255); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(229, 239, 255); } }
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color MenuBorder { get { return Color.FromArgb(210, 210, 215); } }
            public override Color SeparatorDark { get { return Color.FromArgb(225, 225, 230); } }
            public override Color SeparatorLight { get { return Color.White; } }
            public override Color ImageMarginGradientBegin { get { return Color.White; } }
            public override Color ImageMarginGradientMiddle { get { return Color.White; } }
            public override Color ImageMarginGradientEnd { get { return Color.White; } }
        }
    }
}
