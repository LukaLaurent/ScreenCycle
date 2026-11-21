using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScreenCycle
{
    internal sealed class ItemEditorForm : Form
    {
        public RotationItem Result { get; private set; }
        private readonly RotationItem item;

        private RadioButton rbWeb, rbApp, rbWin;
        private TextBox tbName, tbUrl, tbPath, tbArgs, tbAppTitle, tbWinTitle, tbWinProc;
        private ComboBox cbBrowser, cbProfile, cbMode, cbCrop;
        private readonly List<string> profileIds = new List<string>();
        private Control profileLabel;
        private NumericUpDown nuCrop;
        private Control cropRow, cropLabel;
        private NumericUpDown nuDuration;
        private CheckBox ckRefresh, ckEnabled;
        private Label lbMatch, lbBrowserHint;
        private readonly List<Control> webRows = new List<Control>(), appRows = new List<Control>(), winRows = new List<Control>();
        private static readonly BrowserKind[] BrowserOrder = { BrowserKind.Auto, BrowserKind.Edge, BrowserKind.Chrome, BrowserKind.Firefox, BrowserKind.Brave, BrowserKind.Default };

        public ItemEditorForm(RotationItem source, bool isNew)
        {
            item = source.Clone();
            UiKit.PrepareForm(this);
            Text = isNew ? "Add a screen" : "Edit screen";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 16, 20, 16);
            Build();
            LoadItem(item);
        }

        private void AddRow(TableLayoutPanel t, string label, Control c, List<Control> group)
        {
            Label l = UiKit.Label(label);
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, r);
            t.Controls.Add(c, 1, r);
            if (group != null) { group.Add(l); group.Add(c); }
        }

        private void AddFull(TableLayoutPanel t, Control c, List<Control> group)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 1, r);
            if (group != null) group.Add(c);
        }

        private TextBox NewText(int width)
        {
            TextBox tb = new TextBox();
            tb.Width = width;
            tb.Margin = new Padding(0, 3, 6, 3);
            return tb;
        }

        private void Build()
        {
            TableLayoutPanel t = UiKit.Grid(2);
            t.MinimumSize = new Size(630, 0); // same width for every type, so the dialog doesn't jump
            int wide = 440;

            rbWeb = new RadioButton { Text = "Website", AutoSize = true, Margin = new Padding(0, 5, 16, 5) };
            rbApp = new RadioButton { Text = "Application or file", AutoSize = true, Margin = new Padding(0, 5, 16, 5) };
            rbWin = new RadioButton { Text = "Already open window", AutoSize = true, Margin = new Padding(0, 5, 0, 5) };
            EventHandler kindChanged = delegate { UpdateKind(); };
            rbWeb.CheckedChanged += kindChanged;
            rbApp.CheckedChanged += kindChanged;
            rbWin.CheckedChanged += kindChanged;
            AddRow(t, "Type", UiKit.Row(rbWeb, rbApp, rbWin), null);

            tbName = NewText(wide);
            AddRow(t, "Name", tbName, null);
            AddFull(t, UiKit.Hint("Optional — shown in the list and the tray menu."), null);

            // ---- website
            tbUrl = NewText(wide);
            AddRow(t, "Web address", tbUrl, webRows);
            cbBrowser = UiKit.Combo(wide);
            foreach (BrowserKind k in BrowserOrder) cbBrowser.Items.Add(Browsers.Describe(k));
            cbBrowser.SelectedIndexChanged += delegate { RefreshProfiles(SelectedProfileId()); UpdateBrowserHint(); };
            AddRow(t, "Browser", cbBrowser, webRows);
            cbProfile = UiKit.Combo(wide);
            cbProfile.SelectedIndexChanged += delegate { UpdateBrowserHint(); };
            AddRow(t, "Profile", cbProfile, webRows);
            profileLabel = t.GetControlFromPosition(0, t.RowCount - 1);
            lbBrowserHint = UiKit.Hint("");
            lbBrowserHint.MaximumSize = new Size(wide, 0);
            AddFull(t, lbBrowserHint, webRows);

            // ---- application
            tbPath = NewText(wide - 190);
            Button browse = UiKit.Button("Browse…", delegate { BrowseProgram(); });
            Button fromWin = UiKit.Button("From open app…", delegate { PickRunning(true); });
            AddRow(t, "Program or file", UiKit.Row(tbPath, browse, fromWin), appRows);
            tbArgs = NewText(wide);
            AddRow(t, "Arguments", tbArgs, appRows);
            tbAppTitle = NewText(wide);
            AddRow(t, "Window title has", tbAppTitle, appRows);
            Label appHint = UiKit.Hint("Programs, shortcuts (.lnk), documents (PDF, PowerPoint, pictures…) all work. The title filter is\noptional: use it when the app opens several windows, or to reuse a window that is already open.");
            AddFull(t, appHint, appRows);

            // ---- existing window
            tbWinTitle = NewText(wide);
            AddRow(t, "Window title has", tbWinTitle, winRows);
            tbWinProc = NewText(wide - 190);
            Button pick = UiKit.Button("Pick an open window…", delegate { PickRunning(false); });
            AddRow(t, "Process name", UiKit.Row(tbWinProc, pick), winRows);
            lbMatch = UiKit.Hint("");
            lbMatch.MaximumSize = new Size(wide, 0);
            AddFull(t, lbMatch, winRows);
            tbWinTitle.TextChanged += delegate { UpdateMatch(); };
            tbWinProc.TextChanged += delegate { UpdateMatch(); };

            // ---- common
            Panel sep = new Panel { Height = 1, Width = wide + 120, BackColor = UiKit.Border, Margin = new Padding(0, 10, 0, 10) };
            int rs = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(sep, 0, rs);
            t.SetColumnSpan(sep, 2);

            nuDuration = UiKit.Number(1, 86400, 30, 80);
            AddRow(t, "Show for", UiKit.Row(nuDuration, UiKit.Label("seconds")), null);
            cbMode = UiKit.Combo(200, "Fullscreen", "Maximized", "Leave as is");
            AddRow(t, "Display", cbMode, null);
            cbCrop = UiKit.Combo(200, "Hide it automatically", "Keep it visible", "Cut off the top by");
            nuCrop = UiKit.Number(1, 400, 40, 64);
            Label px = UiKit.Label("pixels");
            cbCrop.SelectedIndexChanged += delegate { nuCrop.Visible = px.Visible = cbCrop.SelectedIndex == 2; };
            cbMode.SelectedIndexChanged += delegate { cropRow.Visible = cropLabel.Visible = cbMode.SelectedIndex == 0; };
            cropRow = UiKit.Row(cbCrop, nuCrop, px);
            AddRow(t, "App title bar", cropRow, null);
            cropLabel = t.GetControlFromPosition(0, t.RowCount - 1);
            ckRefresh = UiKit.Check("Refresh it (F5) each time it comes on screen", false);
            AddFull(t, ckRefresh, null);
            ckEnabled = UiKit.Check("Include in the rotation", true);
            AddFull(t, ckEnabled, null);

            // ---- buttons
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(0, 16, 0, 0);
            Button ok = UiKit.Primary("OK", delegate { Accept(); });
            Button cancel = UiKit.Button("Cancel", null);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Margin = new Padding(8, 0, 0, 0);
            cancel.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            TableLayoutPanel root = UiKit.Grid(1);
            root.Location = new Point(Padding.Left, Padding.Top);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(t, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);
        }

        private void LoadItem(RotationItem i)
        {
            rbWeb.Checked = i.Kind == ItemKind.Website;
            rbApp.Checked = i.Kind == ItemKind.Application;
            rbWin.Checked = i.Kind == ItemKind.Window;
            tbName.Text = i.Name;
            tbUrl.Text = i.Url;
            cbBrowser.SelectedIndex = Math.Max(0, Array.IndexOf(BrowserOrder, i.Browser));
            RefreshProfiles(i.BrowserProfile ?? "");
            tbPath.Text = i.Path;
            tbArgs.Text = i.Arguments;
            tbAppTitle.Text = i.Kind == ItemKind.Application ? i.TitleContains : "";
            tbWinTitle.Text = i.Kind == ItemKind.Window ? i.TitleContains : "";
            tbWinProc.Text = i.ProcessName;
            nuDuration.Value = Math.Max(1, Math.Min(86400, i.DurationSeconds));
            cbMode.SelectedIndex = (int)i.Mode;
            cbCrop.SelectedIndex = i.TitleBarCrop < 0 ? 1 : i.TitleBarCrop == 0 ? 0 : 2;
            if (i.TitleBarCrop > 0) nuCrop.Value = Math.Min(400, i.TitleBarCrop);
            nuCrop.Visible = cbCrop.SelectedIndex == 2;
            cropRow.Visible = cropLabel.Visible = i.Mode == WindowMode.Fullscreen;
            ckRefresh.Checked = i.RefreshOnShow;
            ckEnabled.Checked = i.Enabled;
            UpdateKind();
        }

        private ItemKind Kind
        {
            get { return rbWeb.Checked ? ItemKind.Website : rbApp.Checked ? ItemKind.Application : ItemKind.Window; }
        }

        private void UpdateKind()
        {
            ItemKind k = Kind;
            SuspendLayout();
            foreach (Control c in webRows) c.Visible = k == ItemKind.Website;
            foreach (Control c in appRows) c.Visible = k == ItemKind.Application;
            foreach (Control c in winRows) c.Visible = k == ItemKind.Window;
            ResumeLayout(true);
            if (k == ItemKind.Window) UpdateMatch();
            if (k == ItemKind.Website) { ShowProfileRow(); UpdateBrowserHint(); }
        }

        private string SelectedProfileId()
        {
            int i = cbProfile.SelectedIndex;
            return i >= 0 && i < profileIds.Count ? profileIds[i] : "";
        }

        /// <summary>Fills the profile list for the chosen browser and selects <paramref name="want"/> when possible.</summary>
        private void RefreshProfiles(string want)
        {
            BrowserKind real = Browsers.Resolve(BrowserOrder[Math.Max(0, cbBrowser.SelectedIndex)]);
            cbProfile.BeginUpdate();
            cbProfile.Items.Clear();
            profileIds.Clear();
            cbProfile.Items.Add("Separate profile for this screen (private, log in once inside it)");
            profileIds.Add("");
            if (real != BrowserKind.Default)
            {
                foreach (BrowserProfileInfo p in Browsers.ListProfiles(real))
                {
                    cbProfile.Items.Add("My profile: " + p.Label);
                    profileIds.Add(p.Id);
                }
            }
            int sel = Math.Max(0, profileIds.IndexOf(want ?? ""));
            if (!string.IsNullOrEmpty(want) && profileIds.IndexOf(want) < 0)
            {
                cbProfile.Items.Add("Saved profile, not found on this PC: " + want);
                profileIds.Add(want);
                sel = profileIds.Count - 1;
            }
            cbProfile.SelectedIndex = sel;
            cbProfile.EndUpdate();
            ShowProfileRow();
        }

        private void ShowProfileRow()
        {
            bool show = Kind == ItemKind.Website && BrowserOrder[Math.Max(0, cbBrowser.SelectedIndex)] != BrowserKind.Default;
            cbProfile.Visible = profileLabel.Visible = show;
        }

        private void UpdateBrowserHint()
        {
            BrowserKind b = BrowserOrder[Math.Max(0, cbBrowser.SelectedIndex)];
            BrowserKind real = Browsers.Resolve(b);
            if (b != BrowserKind.Default && SelectedProfileId().Length > 0)
                lbBrowserHint.Text = "Opens in a new window of your own " + Browsers.Pretty(real) + " profile, so you are already signed in (Google, YouTube…). " +
                    "Works even while " + Browsers.Pretty(real) + " is open; ScreenCycle makes the window fullscreen with F11.";
            else if (b == BrowserKind.Default)
                lbBrowserHint.Text = "Opens in your normal browser. Fullscreen is applied by ScreenCycle; a dedicated browser below gives a cleaner kiosk view.";
            else if (Browsers.Find(b) == null && b != BrowserKind.Auto)
                lbBrowserHint.Text = "This browser is not installed on this PC — another installed browser will be used.";
            else
                lbBrowserHint.Text = "Opens in its own private browser window (kiosk style, no tabs or address bar). Logins you make in it are remembered for this screen.";
        }

        private void UpdateMatch()
        {
            RotationItem probe = new RotationItem();
            probe.Kind = ItemKind.Window;
            probe.TitleContains = tbWinTitle.Text;
            probe.ProcessName = tbWinProc.Text;
            if (string.IsNullOrWhiteSpace(probe.TitleContains) && string.IsNullOrWhiteSpace(probe.ProcessName))
            {
                lbMatch.ForeColor = UiKit.Muted;
                lbMatch.Text = "Fill in part of the window title and/or the process name (for example “chrome” or “EXCEL”).";
                return;
            }
            int count = 0;
            WindowInfo first = null;
            foreach (WindowInfo w in WindowTools.ListAppWindows(true))
            {
                if (ItemRuntime.MatchesWindowItem(probe, w)) { count++; if (first == null) first = w; }
            }
            if (count == 0)
            {
                lbMatch.ForeColor = UiKit.Warn;
                lbMatch.Text = "No open window matches right now. It will be used as soon as it is open.";
            }
            else
            {
                lbMatch.ForeColor = UiKit.Good;
                lbMatch.Text = "✓ Matches “" + Trim(first.Title, 60) + "”" + (count > 1 ? "  (and " + (count - 1) + " other" + (count > 2 ? "s" : "") + ")" : "");
            }
        }

        private static string Trim(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private void BrowseProgram()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose a program or file";
                d.Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd|All files|*.*";
                d.DereferenceLinks = false;
                try { if (File.Exists(tbPath.Text)) d.InitialDirectory = Path.GetDirectoryName(tbPath.Text); } catch { }
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    tbPath.Text = d.FileName;
                    if (tbName.Text.Trim().Length == 0) tbName.Text = Path.GetFileNameWithoutExtension(d.FileName);
                }
            }
        }

        private void PickRunning(bool forApp)
        {
            using (WindowPickerForm p = new WindowPickerForm())
            {
                if (p.ShowDialog(this) != DialogResult.OK || p.Selected == null) return;
                WindowInfo w = p.Selected;
                if (forApp)
                {
                    if (!string.IsNullOrEmpty(w.ProcessPath)) tbPath.Text = w.ProcessPath;
                    if (tbName.Text.Trim().Length == 0) tbName.Text = w.Title.Length > 0 ? Trim(w.Title, 40) : w.ProcessName;
                }
                else
                {
                    tbWinTitle.Text = w.Title;
                    tbWinProc.Text = w.ProcessName;
                    if (tbName.Text.Trim().Length == 0) tbName.Text = Trim(w.Title, 40);
                }
            }
        }

        private void Accept()
        {
            ItemKind k = Kind;
            if (k == ItemKind.Website && tbUrl.Text.Trim().Length == 0) { Complain(tbUrl, "Please enter a web address."); return; }
            if (k == ItemKind.Application && tbPath.Text.Trim().Length == 0) { Complain(tbPath, "Please choose a program or file."); return; }
            if (k == ItemKind.Window && tbWinTitle.Text.Trim().Length == 0 && tbWinProc.Text.Trim().Length == 0)
            { Complain(tbWinTitle, "Please enter part of the window title or the process name."); return; }
            if (k == ItemKind.Application)
            {
                string p = Environment.ExpandEnvironmentVariables(tbPath.Text.Trim().Trim('"'));
                bool looksLikePath = p.Contains("\\") || p.Contains("/");
                if (looksLikePath && !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && !Directory.Exists(p))
                {
                    if (MessageBox.Show(this, "This file doesn't exist:\n" + p + "\n\nKeep it anyway?", "ScreenCycle",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
            }

            item.Kind = k;
            item.Name = tbName.Text.Trim();
            item.Url = tbUrl.Text.Trim();
            item.Browser = BrowserOrder[Math.Max(0, cbBrowser.SelectedIndex)];
            item.BrowserProfile = item.Browser == BrowserKind.Default ? "" : SelectedProfileId();
            item.Path = tbPath.Text.Trim().Trim('"');
            item.Arguments = tbArgs.Text.Trim();
            item.TitleContains = k == ItemKind.Application ? tbAppTitle.Text.Trim() : k == ItemKind.Window ? tbWinTitle.Text.Trim() : "";
            item.ProcessName = k == ItemKind.Window ? tbWinProc.Text.Trim() : "";
            item.DurationSeconds = (int)nuDuration.Value;
            item.Mode = (WindowMode)Math.Max(0, cbMode.SelectedIndex);
            item.RefreshOnShow = ckRefresh.Checked;
            item.TitleBarCrop = cbCrop.SelectedIndex == 1 ? -1 : cbCrop.SelectedIndex == 0 ? 0 : (int)nuCrop.Value;
            item.Enabled = ckEnabled.Checked;
            if (k == ItemKind.Website) item.Url = Browsers.NormalizeUrl(item.Url);
            Result = item;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Complain(Control c, string msg)
        {
            MessageBox.Show(this, msg, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            c.Focus();
        }
    }

    internal sealed class WindowPickerForm : Form
    {
        private readonly ListView list;
        public WindowInfo Selected { get; private set; }

        public WindowPickerForm()
        {
            UiKit.PrepareForm(this);
            Text = "Pick an open window";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(640, 420);
            MinimumSize = new Size(480, 320);
            Padding = new Padding(16);

            Label hint = UiKit.Hint("Choose the window to put in the rotation. Open the app first if it is not listed.");
            hint.Dock = DockStyle.Top;

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("Window", 420);
            list.Columns.Add("Process", 160);
            UiKit.ModernList(list);
            list.DoubleClick += delegate { Choose(); };
            list.Resize += delegate { if (list.Columns.Count == 2) list.Columns[0].Width = Math.Max(150, list.ClientSize.Width - list.Columns[1].Width - 4); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(0, 12, 0, 0);
            Button ok = UiKit.Primary("Use this window", delegate { Choose(); });
            Button cancel = UiKit.Button("Cancel", null);
            cancel.DialogResult = DialogResult.Cancel;
            Button refresh = UiKit.Button("Refresh", delegate { Fill(); });
            ok.Margin = cancel.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(refresh);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(list);
            Controls.Add(hint);
            Controls.Add(buttons);
            Fill();
        }

        private void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            List<WindowInfo> wins = WindowTools.ListAppWindows(true);
            wins.Sort(delegate(WindowInfo a, WindowInfo b) { return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase); });
            foreach (WindowInfo w in wins)
            {
                ListViewItem li = new ListViewItem(w.Title);
                li.SubItems.Add(w.ProcessName);
                li.Tag = w;
                list.Items.Add(li);
            }
            list.EndUpdate();
            if (list.Items.Count > 0) list.Items[0].Selected = true;
        }

        private void Choose()
        {
            if (list.SelectedItems.Count == 0) return;
            Selected = (WindowInfo)list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
