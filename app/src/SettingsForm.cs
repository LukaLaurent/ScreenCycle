using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace ScreenCycle
{
    internal sealed class SettingsForm : Form
    {
        public event Action<AppConfig> Saved;
        public bool StartRequested { get; private set; }
        public bool StopRequested { get; private set; }
        public bool ExitRequested { get; private set; }
        public string StartAtItemId { get; private set; }

        private AppConfig work;
        private bool filling = true; // ignore ItemChecked while the list is (re)built, including its first handle creation
        private string savedSnapshot;

        private ListView list;
        private Label lbSummary, lbHotkeyWarn;
        private Button btEdit, btDup, btRemove, btUp, btDown, btStartHere;

        private ComboBox cbMonitor, cbTransition;
        private NumericUpDown nuTransition, nuDefault, nuDelay, nuIdle;
        private CheckBox ckStartRot, ckWindows, ckAwake, ckActivity, ckRelaunch, ckProgress, ckMouse, ckTaskbar, ckOsd, ckClose;
        private readonly List<string> monitorIds = new List<string>();

        private HotkeyBox hkPause, hkNext, hkPrev, hkStartStop, hkSettings, hkExit;

        public SettingsForm(AppConfig cfg, List<string> hotkeyFailures, RotatorState state)
        {
            work = cfg.Clone();
            savedSnapshot = Snapshot(work);
            UiKit.PrepareForm(this);
            Text = "ScreenCycle Settings";
            Icon = IconFactory.MakeIcon(32, IconState.Running);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(760, 520);
            KeyPreview = true;

            Controls.Add(BuildTabs());
            Controls.Add(BuildHeader());
            Controls.Add(BuildFooter());

            LoadValues();
            SetHotkeyFailures(hotkeyFailures);
        }

        // ------------------------------------------------------------------ layout

        private Control BuildHeader()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = 72;
            p.BackColor = Color.White;
            p.Padding = new Padding(20, 14, 20, 10);

            PictureBox logo = new PictureBox();
            logo.Size = new Size(40, 40);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(20, 16);
            logo.Image = IconFactory.RenderLogo(80, IconState.Running);

            Label title = new Label { Text = "ScreenCycle", Font = UiKit.Title, AutoSize = true, Location = new Point(70, 12) };
            Label sub = new Label
            {
                Text = "Rotates apps and websites on this screen. Rotation is paused while this window is open.",
                ForeColor = UiKit.Muted, AutoSize = true, Location = new Point(72, 42)
            };
            p.Controls.Add(logo);
            p.Controls.Add(title);
            p.Controls.Add(sub);
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(UiKit.Border)) e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
            };
            return p;
        }

        private Control BuildFooter()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Bottom;
            p.Height = 60;
            p.BackColor = UiKit.Surface;
            Panel topLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiKit.Border };

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.FlowDirection = FlowDirection.RightToLeft;
            right.Dock = DockStyle.Right;
            right.AutoSize = true;
            right.WrapContents = false;
            right.Padding = new Padding(0, 14, 16, 0);
            Button start = UiKit.Primary("▶  Save && start rotation", delegate { if (DoSave()) { StartRequested = true; Close(); } });
            Button save = UiKit.Button("Save", delegate { DoSave(); });
            Button close = UiKit.Button("Close", delegate { Close(); });
            start.Margin = save.Margin = close.Margin = new Padding(8, 0, 0, 0);
            start.MinimumSize = new Size(190, 32);
            save.MinimumSize = close.MinimumSize = new Size(90, 32);
            right.Controls.Add(start);
            right.Controls.Add(save);
            right.Controls.Add(close);

            FlowLayoutPanel left = new FlowLayoutPanel();
            left.Dock = DockStyle.Left;
            left.AutoSize = true;
            left.WrapContents = false;
            left.Padding = new Padding(16, 14, 0, 0);
            Button stop = UiKit.Button("Keep stopped", delegate { StopRequested = true; Close(); });
            stop.MinimumSize = new Size(110, 32);
            ToolTip tt = new ToolTip();
            tt.SetToolTip(stop, "Close this window without restarting the rotation.");
            left.Controls.Add(stop);
            Button quit = UiKit.Button("Exit ScreenCycle", delegate { ExitRequested = true; Close(); });
            quit.MinimumSize = new Size(120, 32);
            quit.Margin = new Padding(8, 0, 0, 0);
            tt.SetToolTip(quit, "Stop the rotation and close ScreenCycle completely (tray icon included).");
            left.Controls.Add(quit);

            p.Controls.Add(right);
            p.Controls.Add(left);
            p.Controls.Add(topLine); // added last = docked first, spans the full width
            return p;
        }

        private Control BuildTabs()
        {
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            tabs.TabPages.Add(BuildScreensTab());
            tabs.TabPages.Add(BuildBehaviorTab());
            tabs.TabPages.Add(BuildShortcutsTab());
            tabs.TabPages.Add(BuildAboutTab());
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 10, 12, 8);
            host.Controls.Add(tabs);
            return host;
        }

        private TabPage Page(string title)
        {
            TabPage tp = new TabPage(title);
            tp.BackColor = Color.White;
            tp.UseVisualStyleBackColor = false;
            return tp;
        }

        private TabPage BuildScreensTab()
        {
            TabPage tp = Page("Screens");
            tp.Padding = new Padding(16, 14, 16, 12);

            list = new ListView();
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.AllowDrop = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("Name", 210);
            list.Columns.Add("Type", 100);
            list.Columns.Add("Shows", 220);
            list.Columns.Add("Time", 70, HorizontalAlignment.Right);
            list.Columns.Add("Display", 90);
            UiKit.ModernList(list);
            list.DoubleClick += delegate { EditSelected(); };
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (filling || e.Item == null) return;
                RotationItem it = e.Item.Tag as RotationItem;
                if (it != null && it.Enabled != e.Item.Checked)
                {
                    it.Enabled = e.Item.Checked;
                    e.Item.ForeColor = it.Enabled ? SystemColors.WindowText : UiKit.Muted;
                    UpdateSummary();
                }
            };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) RemoveSelected();
                else if (e.KeyCode == Keys.Enter) EditSelected();
                else if (e.Alt && e.KeyCode == Keys.Up) { MoveItem(-1); e.Handled = true; }
                else if (e.Alt && e.KeyCode == Keys.Down) { MoveItem(+1); e.Handled = true; }
            };
            list.Resize += delegate { FitColumns(); };
            SetupDragDrop();

            FlowLayoutPanel side = new FlowLayoutPanel();
            side.FlowDirection = FlowDirection.TopDown;
            side.Dock = DockStyle.Right;
            side.Width = 190;
            side.Padding = new Padding(14, 0, 0, 0);
            side.WrapContents = false;

            Func<string, EventHandler, Button> sb = delegate(string text, EventHandler h)
            {
                Button b = UiKit.Button(text, h);
                b.AutoSize = false;
                b.Width = 170;
                b.Height = 30;
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.Margin = new Padding(0, 0, 0, 5);
                side.Controls.Add(b);
                return b;
            };
            Label add = UiKit.Label("Add");
            add.Font = UiKit.Bold;
            add.Margin = new Padding(0, 0, 0, 6);
            side.Controls.Add(add);
            sb("＋  Website…", delegate { AddNew(ItemKind.Website); });
            sb("＋  Application or file…", delegate { AddNew(ItemKind.Application); });
            sb("＋  Open window…", delegate { AddNew(ItemKind.Window); });
            Label sel = UiKit.Label("Selected");
            sel.Font = UiKit.Bold;
            sel.Margin = new Padding(0, 14, 0, 6);
            side.Controls.Add(sel);
            btEdit = sb("Edit…", delegate { EditSelected(); });
            btDup = sb("Duplicate", delegate { DuplicateSelected(); });
            btUp = sb("↑  Move up", delegate { MoveItem(-1); });
            btDown = sb("↓  Move down", delegate { MoveItem(+1); });
            btRemove = sb("Remove", delegate { RemoveSelected(); });
            btStartHere = sb("▶  Start from here", delegate
            {
                if (list.SelectedItems.Count == 0) return;
                RotationItem it = (RotationItem)list.SelectedItems[0].Tag;
                if (!it.Enabled) { it.Enabled = true; list.SelectedItems[0].Checked = true; }
                if (DoSave()) { StartRequested = true; StartAtItemId = it.Id; Close(); }
            });

            lbSummary = UiKit.Hint("");
            lbSummary.Dock = DockStyle.Bottom;
            lbSummary.AutoSize = false;
            lbSummary.Height = 30;
            lbSummary.TextAlign = ContentAlignment.BottomLeft;

            tp.Controls.Add(list);
            tp.Controls.Add(side);
            tp.Controls.Add(lbSummary);
            return tp;
        }

        private void FitColumns()
        {
            if (list.Columns.Count < 5) return;
            int fixedW = list.Columns[0].Width + list.Columns[1].Width + list.Columns[3].Width + list.Columns[4].Width;
            int w = list.ClientSize.Width - fixedW - 4;
            if (w > 80) list.Columns[2].Width = w;
        }

        private TabPage BuildBehaviorTab()
        {
            TabPage tp = Page("Behavior");
            FlowLayoutPanel st = UiKit.Stack();

            st.Controls.Add(UiKit.SectionTitle("Display"));
            TableLayoutPanel g = UiKit.Grid(2);
            cbMonitor = UiKit.Combo(320);
            monitorIds.Add("");
            cbMonitor.Items.Add("Main display");
            int n = 1;
            foreach (Screen s in Screen.AllScreens)
            {
                monitorIds.Add(s.DeviceName);
                cbMonitor.Items.Add("Display " + n++ + "  —  " + s.Bounds.Width + " × " + s.Bounds.Height + (s.Primary ? "  (main)" : ""));
            }
            AddGridRow(g, "Show on", cbMonitor);
            cbTransition = UiKit.Combo(200, "Crossfade", "Fade through black", "Instant (no animation)");
            nuTransition = UiKit.Number(0, 5000, 600, 70);
            nuTransition.Increment = 100;
            cbTransition.SelectedIndexChanged += delegate { nuTransition.Enabled = cbTransition.SelectedIndex != 2; };
            AddGridRow(g, "Transition", UiKit.Row(cbTransition, nuTransition, UiKit.Label("ms")));
            nuDefault = UiKit.Number(1, 86400, 30, 70);
            AddGridRow(g, "Default time per screen", UiKit.Row(nuDefault, UiKit.Label("seconds (for new screens)")));
            st.Controls.Add(g);

            st.Controls.Add(UiKit.SectionTitle("Startup"));
            ckStartRot = UiKit.Check("Start the rotation automatically when ScreenCycle starts", true);
            st.Controls.Add(ckStartRot);
            ckWindows = UiKit.Check("Launch ScreenCycle when I sign in to Windows,", false);
            nuDelay = UiKit.Number(0, 600, 10, 60);
            Label delayL = UiKit.Label("then wait");
            Label delayS = UiKit.Label("seconds before starting");
            FlowLayoutPanel r1 = UiKit.Row(ckWindows, delayL, nuDelay, delayS);
            ckWindows.Margin = new Padding(0, 5, 4, 5);
            ckWindows.CheckedChanged += delegate { nuDelay.Enabled = ckWindows.Checked; };
            st.Controls.Add(r1);

            st.Controls.Add(UiKit.SectionTitle("While rotating"));
            ckAwake = UiKit.Check("Keep the screen on (no sleep, no screen saver)", true);
            st.Controls.Add(ckAwake);
            ckActivity = UiKit.Check("Pause when someone uses the mouse or keyboard, resume after", false);
            nuIdle = UiKit.Number(5, 3600, 60, 64);
            ckActivity.Margin = new Padding(0, 5, 4, 5);
            ckActivity.CheckedChanged += delegate { nuIdle.Enabled = ckActivity.Checked; };
            st.Controls.Add(UiKit.Row(ckActivity, nuIdle, UiKit.Label("seconds without activity")));
            ckRelaunch = UiKit.Check("Reopen a screen automatically if its window was closed or crashed", true);
            st.Controls.Add(ckRelaunch);
            ckProgress = UiKit.Check("Show a thin progress line at the bottom of the screen", false);
            st.Controls.Add(ckProgress);
            ckMouse = UiKit.Check("Move the mouse pointer out of the way (bottom-right corner)", false);
            st.Controls.Add(ckMouse);
            ckTaskbar = UiKit.Check("Hide the Windows taskbar automatically (it comes back when you point at the bottom of the screen)", false);
            st.Controls.Add(ckTaskbar);
            ckOsd = UiKit.Check("Show short on-screen messages (paused, resumed, …)", false);
            st.Controls.Add(ckOsd);

            st.Controls.Add(UiKit.SectionTitle("When exiting"));
            ckClose = UiKit.Check("Close the apps and websites that ScreenCycle opened", false);
            st.Controls.Add(ckClose);
            st.Controls.Add(UiKit.Hint("Windows that were already open before (“Open window” screens) are never closed — they just get their frame back."));

            tp.Controls.Add(st);
            return tp;
        }

        private static void AddGridRow(TableLayoutPanel g, string label, Control c)
        {
            int r = g.RowCount++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = UiKit.Label(label);
            l.MinimumSize = new Size(170, 0);
            g.Controls.Add(l, 0, r);
            g.Controls.Add(c, 1, r);
        }

        private TabPage BuildShortcutsTab()
        {
            TabPage tp = Page("Shortcuts");
            FlowLayoutPanel st = UiKit.Stack();
            st.Controls.Add(UiKit.SectionTitle("Keyboard shortcuts"));
            st.Controls.Add(UiKit.Hint("They work everywhere in Windows, even when a screen is fullscreen. Click a box and press the new combination;\nBackspace clears it."));

            TableLayoutPanel g = UiKit.Grid(3);
            hkPause = AddHotkeyRow(g, "Pause / resume");
            hkNext = AddHotkeyRow(g, "Next screen");
            hkPrev = AddHotkeyRow(g, "Previous screen");
            hkStartStop = AddHotkeyRow(g, "Start / stop rotation");
            hkSettings = AddHotkeyRow(g, "Open settings");
            hkExit = AddHotkeyRow(g, "Exit ScreenCycle");
            st.Controls.Add(g);

            lbHotkeyWarn = UiKit.Label("");
            lbHotkeyWarn.ForeColor = UiKit.Warn;
            lbHotkeyWarn.MaximumSize = new Size(640, 0);
            lbHotkeyWarn.Margin = new Padding(0, 12, 0, 0);
            st.Controls.Add(lbHotkeyWarn);

            Button reset = UiKit.Button("Restore defaults", delegate
            {
                HotkeySet d = new HotkeySet();
                hkPause.Value = d.PauseResume; hkNext.Value = d.Next; hkPrev.Value = d.Previous;
                hkStartStop.Value = d.StartStop; hkSettings.Value = d.Settings; hkExit.Value = d.Exit;
            });
            reset.Margin = new Padding(0, 14, 0, 0);
            st.Controls.Add(reset);
            tp.Controls.Add(st);
            return tp;
        }

        private HotkeyBox AddHotkeyRow(TableLayoutPanel g, string label)
        {
            int r = g.RowCount++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = UiKit.Label(label);
            l.MinimumSize = new Size(170, 0);
            HotkeyBox hb = new HotkeyBox();
            hb.Width = 220;
            Button clear = UiKit.Button("Clear", delegate { hb.Value = new HotkeySpec(); });
            clear.MinimumSize = new Size(70, 26);
            g.Controls.Add(l, 0, r);
            g.Controls.Add(hb, 1, r);
            g.Controls.Add(clear, 2, r);
            return hb;
        }

        private TabPage BuildAboutTab()
        {
            TabPage tp = Page("About");
            FlowLayoutPanel st = UiKit.Stack();
            Label name = UiKit.Label("ScreenCycle " + Program.Version);
            name.Font = UiKit.Title;
            name.Margin = new Padding(0, 12, 0, 4);
            st.Controls.Add(name);
            st.Controls.Add(UiKit.Hint("Turns any Windows PC into a display screen: apps, documents and websites take turns on screen."));
            Label by = UiKit.Label("Created by Luka LAURENT");
            by.Font = UiKit.Bold;
            by.ForeColor = IconFactory.Accent;
            by.Margin = new Padding(0, 8, 0, 0);
            st.Controls.Add(by);

            st.Controls.Add(UiKit.SectionTitle("Files"));
            st.Controls.Add(PathRow("Settings file", AppConfig.ConfigPath));
            st.Controls.Add(PathRow("Log file", Log.LogPath));
            st.Controls.Add(UiKit.Hint("Portable use: put a ScreenCycle.config.xml next to ScreenCycle.ps1 (in the app folder) and it will be used instead\n(handy to copy a ready setup to another PC)."));

            FlowLayoutPanel b = UiKit.Row(
                UiKit.Button("Export settings…", delegate { Export(); }),
                UiKit.Button("Import settings…", delegate { Import(); }),
                UiKit.Button("Open log", delegate { OpenPath(Log.LogPath); }),
                UiKit.Button("Create Start menu && desktop shortcuts", delegate
                {
                    try { Shortcuts.Install(true); MessageBox.Show(this, "Shortcuts created in the Start menu and on the desktop.", "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch (Exception ex) { MessageBox.Show(this, "Could not create the shortcuts: " + ex.Message, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }));
            b.Margin = new Padding(0, 10, 0, 0);
            st.Controls.Add(b);

            tp.Controls.Add(st);
            return tp;
        }

        private Control PathRow(string label, string path)
        {
            Label l = UiKit.Label(label);
            l.MinimumSize = new Size(110, 0);
            LinkLabel link = new LinkLabel();
            link.Text = path.Length > 80 ? path.Substring(0, 30) + "\u2026" + path.Substring(path.Length - 45) : path;
            new ToolTip().SetToolTip(link, path);
            link.AutoSize = true;
            link.Margin = new Padding(0, 6, 0, 6);
            link.LinkColor = IconFactory.Accent;
            link.LinkClicked += delegate { RevealPath(path); };
            return UiKit.Row(l, link);
        }

        // ------------------------------------------------------------------ values

        private void LoadValues()
        {
            int mi = monitorIds.IndexOf(work.MonitorDevice ?? "");
            cbMonitor.SelectedIndex = mi < 0 ? 0 : mi;
            cbTransition.SelectedIndex = (int)work.Transition;
            nuTransition.Value = Math.Min(5000, Math.Max(0, work.TransitionMs));
            nuTransition.Enabled = work.Transition != TransitionKind.None;
            nuDefault.Value = Math.Max(1, Math.Min(86400, work.DefaultDurationSeconds));
            ckStartRot.Checked = work.StartRotationOnLaunch;
            ckWindows.Checked = work.StartWithWindows;
            nuDelay.Value = Math.Min(600, Math.Max(0, work.StartupDelaySeconds));
            nuDelay.Enabled = work.StartWithWindows;
            ckAwake.Checked = work.KeepScreenAwake;
            ckActivity.Checked = work.PauseOnUserActivity;
            nuIdle.Value = Math.Min(3600, Math.Max(5, work.ResumeAfterIdleSeconds));
            nuIdle.Enabled = work.PauseOnUserActivity;
            ckRelaunch.Checked = work.RelaunchClosedItems;
            ckProgress.Checked = work.ShowProgressBar;
            ckMouse.Checked = work.ParkMouseCursor;
            ckTaskbar.Checked = work.AutoHideTaskbar;
            ckOsd.Checked = work.ShowNotifications;
            ckClose.Checked = work.CloseLaunchedAppsOnExit;
            hkPause.Value = work.Hotkeys.PauseResume;
            hkNext.Value = work.Hotkeys.Next;
            hkPrev.Value = work.Hotkeys.Previous;
            hkStartStop.Value = work.Hotkeys.StartStop;
            hkSettings.Value = work.Hotkeys.Settings;
            hkExit.Value = work.Hotkeys.Exit;
        }

        private void Collect()
        {
            // the ticks in the list are the truth for "include in the rotation"
            if (!filling && list.IsHandleCreated)
                foreach (ListViewItem li in list.Items)
                {
                    RotationItem it = li.Tag as RotationItem;
                    if (it != null) it.Enabled = li.Checked;
                }
            work.MonitorDevice = monitorIds[Math.Max(0, cbMonitor.SelectedIndex)];
            work.Transition = (TransitionKind)Math.Max(0, cbTransition.SelectedIndex);
            work.TransitionMs = (int)nuTransition.Value;
            work.DefaultDurationSeconds = (int)nuDefault.Value;
            work.StartRotationOnLaunch = ckStartRot.Checked;
            work.StartWithWindows = ckWindows.Checked;
            work.StartupDelaySeconds = (int)nuDelay.Value;
            work.KeepScreenAwake = ckAwake.Checked;
            work.PauseOnUserActivity = ckActivity.Checked;
            work.ResumeAfterIdleSeconds = (int)nuIdle.Value;
            work.RelaunchClosedItems = ckRelaunch.Checked;
            work.ShowProgressBar = ckProgress.Checked;
            work.ParkMouseCursor = ckMouse.Checked;
            work.AutoHideTaskbar = ckTaskbar.Checked;
            work.ShowNotifications = ckOsd.Checked;
            work.CloseLaunchedAppsOnExit = ckClose.Checked;
            work.Hotkeys.PauseResume = hkPause.Value;
            work.Hotkeys.Next = hkNext.Value;
            work.Hotkeys.Previous = hkPrev.Value;
            work.Hotkeys.StartStop = hkStartStop.Value;
            work.Hotkeys.Settings = hkSettings.Value;
            work.Hotkeys.Exit = hkExit.Value;
        }

        private static string Snapshot(AppConfig c)
        {
            XmlSerializer xs = new XmlSerializer(typeof(AppConfig));
            StringBuilder sb = new StringBuilder();
            using (StringWriter w = new StringWriter(sb)) xs.Serialize(w, c);
            return sb.ToString();
        }

        private bool IsDirty
        {
            get { Collect(); return Snapshot(work) != savedSnapshot; }
        }

        private bool DoSave()
        {
            Collect();
            // duplicate shortcuts would silently fail
            List<HotkeySpec> all = new List<HotkeySpec> { work.Hotkeys.PauseResume, work.Hotkeys.Next, work.Hotkeys.Previous, work.Hotkeys.StartStop, work.Hotkeys.Settings, work.Hotkeys.Exit };
            HashSet<string> seen = new HashSet<string>();
            foreach (HotkeySpec h in all)
            {
                if (h.IsEmpty) continue;
                if (!seen.Add(h.ToString()))
                {
                    MessageBox.Show(this, "The shortcut " + h + " is used twice. Please pick different shortcuts.", "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }
            work.Normalize();
            AppConfig toSave = work.Clone();
            Action<AppConfig> h2 = Saved;
            if (h2 != null) h2(toSave);
            savedSnapshot = Snapshot(work);
            return true;
        }

        public void SetHotkeyFailures(List<string> failures)
        {
            if (lbHotkeyWarn == null) return;
            lbHotkeyWarn.Text = failures == null || failures.Count == 0 ? "" :
                "⚠ Windows refused these shortcuts because another program already uses them: " + string.Join(", ", failures.ToArray()) + ".";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // fill the list only now: its native window exists and won't replay check events at us
            RefreshList();
            filling = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && IsDirty)
            {
                DialogResult r = MessageBox.Show(this, "Save your changes?", "ScreenCycle", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; StartRequested = false; StopRequested = false; ExitRequested = false; return; }
                if (r == DialogResult.Yes && !DoSave()) { e.Cancel = true; ExitRequested = false; return; }
            }
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ list

        private void RefreshList()
        {
            string sel = list.SelectedItems.Count > 0 ? ((RotationItem)list.SelectedItems[0].Tag).Id : null;
            bool wasFilling = filling;
            filling = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (RotationItem it in work.Items)
            {
                ListViewItem li = new ListViewItem(it.DisplayName);
                li.SubItems.Add(UiKit.KindLabel(it.Kind));
                li.SubItems.Add(it.TargetDescription);
                li.SubItems.Add(UiKit.FormatDuration(it.DurationSeconds));
                li.SubItems.Add(UiKit.ModeLabel(it.Mode));
                li.Tag = it;
                li.Checked = it.Enabled;
                if (!it.Enabled) li.ForeColor = UiKit.Muted;
                list.Items.Add(li);
                if (it.Id == sel) li.Selected = true;
            }
            list.EndUpdate();
            filling = wasFilling && !Visible;
            FitColumns();
            UpdateButtons();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int total = 0, count = 0;
            foreach (RotationItem it in work.Items) if (it.Enabled) { total += it.DurationSeconds; count++; }
            if (work.Items.Count == 0)
                lbSummary.Text = "No screens yet — add a website, an application or an open window with the buttons on the right.";
            else
                lbSummary.Text = count + " of " + work.Items.Count + " screens in the rotation · one full cycle takes " + UiKit.FormatDuration(total) +
                    "   ·   Tick a box to include a screen; drag rows to reorder; double-click to edit.";
        }

        private void UpdateButtons()
        {
            bool has = list.SelectedItems.Count > 0;
            int idx = has ? list.SelectedIndices[0] : -1;
            btEdit.Enabled = btDup.Enabled = btRemove.Enabled = btStartHere.Enabled = has;
            btUp.Enabled = has && idx > 0;
            btDown.Enabled = has && idx < list.Items.Count - 1;
        }

        private void SelectItem(string id)
        {
            foreach (ListViewItem li in list.Items)
                if (((RotationItem)li.Tag).Id == id) { li.Selected = true; li.Focused = true; li.EnsureVisible(); }
            list.Focus();
        }

        private void AddNew(ItemKind kind)
        {
            Collect();
            RotationItem it = new RotationItem();
            it.Kind = kind;
            it.DurationSeconds = work.DefaultDurationSeconds;
            it.Mode = kind == ItemKind.Window ? WindowMode.Fullscreen : WindowMode.Fullscreen;
            using (ItemEditorForm f = new ItemEditorForm(it, true))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                work.Items.Add(f.Result);
                RefreshList();
                SelectItem(f.Result.Id);
            }
        }

        private void EditSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            RotationItem it = (RotationItem)list.SelectedItems[0].Tag;
            using (ItemEditorForm f = new ItemEditorForm(it, false))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int i = work.Items.IndexOf(it);
                work.Items[i] = f.Result;
                RefreshList();
                SelectItem(f.Result.Id);
            }
        }

        private void DuplicateSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            RotationItem it = (RotationItem)list.SelectedItems[0].Tag;
            RotationItem c = it.Clone();
            c.Id = Guid.NewGuid().ToString("N");
            if (c.Name.Length > 0) c.Name += " (copy)";
            work.Items.Insert(work.Items.IndexOf(it) + 1, c);
            RefreshList();
            SelectItem(c.Id);
        }

        private void RemoveSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            RotationItem it = (RotationItem)list.SelectedItems[0].Tag;
            if (MessageBox.Show(this, "Remove “" + it.DisplayName + "” from the rotation?", "ScreenCycle",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int idx = work.Items.IndexOf(it);
            work.Items.Remove(it);
            RefreshList();
            if (list.Items.Count > 0) SelectItem(((RotationItem)list.Items[Math.Min(idx, list.Items.Count - 1)].Tag).Id);
        }

        private void MoveItem(int dir)
        {
            if (list.SelectedItems.Count == 0) return;
            RotationItem it = (RotationItem)list.SelectedItems[0].Tag;
            int i = work.Items.IndexOf(it), j = i + dir;
            if (j < 0 || j >= work.Items.Count) return;
            work.Items.RemoveAt(i);
            work.Items.Insert(j, it);
            RefreshList();
            SelectItem(it.Id);
        }

        private void SetupDragDrop()
        {
            list.ItemDrag += delegate(object s, ItemDragEventArgs e) { list.DoDragDrop(e.Item, DragDropEffects.Move); };
            list.DragEnter += delegate(object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(typeof(ListViewItem)) ? DragDropEffects.Move : DragDropEffects.None;
            };
            list.DragOver += delegate(object s, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(typeof(ListViewItem))) return;
                e.Effect = DragDropEffects.Move;
                Point p = list.PointToClient(new Point(e.X, e.Y));
                int idx = list.InsertionMark.NearestIndex(p);
                if (idx > -1)
                {
                    Rectangle r = list.GetItemRect(idx);
                    list.InsertionMark.AppearsAfterItem = p.Y > r.Top + r.Height / 2;
                }
                list.InsertionMark.Index = idx;
            };
            list.DragLeave += delegate { list.InsertionMark.Index = -1; };
            list.DragDrop += delegate(object s, DragEventArgs e)
            {
                int target = list.InsertionMark.Index;
                bool after = list.InsertionMark.AppearsAfterItem;
                list.InsertionMark.Index = -1;
                ListViewItem dragged = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
                if (dragged == null || target < 0) return;
                RotationItem it = (RotationItem)dragged.Tag;
                RotationItem targetItem = (RotationItem)list.Items[target].Tag;
                if (it == targetItem) return;
                work.Items.Remove(it);
                int ti = work.Items.IndexOf(targetItem) + (after ? 1 : 0);
                work.Items.Insert(ti, it);
                RefreshList();
                SelectItem(it.Id);
            };
        }

        // ------------------------------------------------------------------ files

        private void Export()
        {
            Collect();
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "ScreenCycle settings|*.xml";
                d.FileName = "ScreenCycle.config.xml";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { work.SaveTo(d.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void Import()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "ScreenCycle settings|*.xml|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    AppConfig c = AppConfig.LoadFrom(d.FileName);
                    if (MessageBox.Show(this, "Replace the current settings with the " + c.Items.Count + " screens from this file?\n(Nothing is saved until you click Save.)",
                        "ScreenCycle", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    work = c;
                    LoadValues();
                    RefreshList();
                }
                catch (Exception ex) { MessageBox.Show(this, "This file could not be read: " + ex.Message, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else RevealPath(path);
            }
            catch { }
        }

        private static void RevealPath(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { }
        }
    }
}
