using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenCycle
{
    /// <summary>Small helpers so all windows share one clean look.</summary>
    internal static class UiKit
    {
        public static readonly Font BaseFont = new Font("Segoe UI", 9.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.75f);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 11.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public static readonly Color Muted = Color.FromArgb(105, 110, 120);
        public static readonly Color Border = Color.FromArgb(222, 224, 230);
        public static readonly Color Surface = Color.FromArgb(247, 248, 250);
        public static readonly Color Warn = Color.FromArgb(185, 95, 0);
        public static readonly Color Good = Color.FromArgb(20, 135, 70);

        public static void PrepareForm(Form f)
        {
            f.Font = BaseFont;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.BackColor = Color.White;
        }

        public static Label Label(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 12, 6);
            return l;
        }

        public static Label Hint(string text)
        {
            Label l = Label(text);
            l.ForeColor = Muted;
            l.Font = Small;
            l.Margin = new Padding(0, 0, 0, 8);
            return l;
        }

        public static Label SectionTitle(string text)
        {
            Label l = Label(text);
            l.Font = Heading;
            l.Margin = new Padding(0, 14, 0, 6);
            return l;
        }

        public static Button Button(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(88, 30);
            b.Padding = new Padding(8, 0, 8, 0);
            b.UseVisualStyleBackColor = true;
            b.Margin = new Padding(0, 0, 8, 0);
            if (click != null) b.Click += click;
            return b;
        }

        public static Button Primary(string text, EventHandler click)
        {
            Button b = Button(text, click);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = IconFactory.Accent;
            b.ForeColor = Color.White;
            b.Font = Bold;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 110, 230);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 90, 200);
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static CheckBox Check(string text, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = value;
            c.AutoSize = true;
            c.Margin = new Padding(0, 5, 0, 5);
            return c;
        }

        public static NumericUpDown Number(int min, int max, int value, int width)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = width;
            n.TextAlign = HorizontalAlignment.Right;
            n.Margin = new Padding(0, 3, 6, 3);
            return n;
        }

        public static ComboBox Combo(int width, params string[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = width;
            c.Margin = new Padding(0, 3, 6, 3);
            c.Items.AddRange(items);
            return c;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.WrapContents = false;
            p.FlowDirection = FlowDirection.LeftToRight;
            p.Margin = new Padding(0);
            p.Padding = new Padding(0);
            foreach (Control c in controls)
            {
                if (c is Label) c.Margin = new Padding(0, 6, 6, 6);
                p.Controls.Add(c);
            }
            return p;
        }

        public static TableLayoutPanel Grid(int columns)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = columns;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);
            for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        public static FlowLayoutPanel Stack()
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.FlowDirection = FlowDirection.TopDown;
            p.WrapContents = false;
            p.AutoScroll = true;
            p.Dock = DockStyle.Fill;
            p.Padding = new Padding(20, 8, 20, 16);
            return p;
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 60) return seconds + " s";
            int m = seconds / 60, s = seconds % 60;
            if (m < 60) return s == 0 ? m + " min" : m + " min " + s + " s";
            int h = m / 60; m = m % 60;
            return h + " h " + m + " min";
        }

        public static string KindLabel(ItemKind k)
        {
            switch (k)
            {
                case ItemKind.Website: return "Website";
                case ItemKind.Application: return "Application";
                default: return "Open window";
            }
        }

        public static string ModeLabel(WindowMode m)
        {
            switch (m)
            {
                case WindowMode.Fullscreen: return "Fullscreen";
                case WindowMode.Maximized: return "Maximized";
                default: return "As is";
            }
        }

        public static void ModernList(ListView lv)
        {
            lv.HandleCreated += delegate { Native.SetWindowTheme(lv.Handle, "Explorer", null); };
        }
    }

    /// <summary>Text box that records a key combination when pressed.</summary>
    internal sealed class HotkeyBox : TextBox
    {
        private HotkeySpec value = new HotkeySpec();
        public event EventHandler ValueChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            Width = 200;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            Margin = new Padding(0, 3, 6, 3);
        }

        public HotkeySpec Value
        {
            get { return value.Clone(); }
            set { this.value = value == null ? new HotkeySpec() : value.Clone(); Text = this.value.ToString(); }
        }

        /// <summary>Raised with true while any shortcut box is recording, so global shortcuts can be suspended.</summary>
        public static event Action<bool> Recording;

        private static void RaiseRecording(bool on)
        {
            Action<bool> h = Recording;
            if (h != null) h(on);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            BackColor = Color.FromArgb(235, 243, 255);
            RaiseRecording(true);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            RaiseRecording(false);
            BackColor = Color.White;
            Text = value.ToString();
        }

        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (key == Keys.Tab && mods == Keys.None) return base.ProcessCmdKey(ref msg, keyData);
            Record(key, mods);
            return true;
        }

        private void Record(Keys key, Keys mods)
        {
            bool modifierOnly = key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin || key == Keys.None;
            bool win = (Control.ModifierKeys & Keys.LWin) != 0;
            if (modifierOnly)
            {
                string t = "";
                if ((mods & Keys.Control) != 0) t += "Ctrl + ";
                if ((mods & Keys.Alt) != 0) t += "Alt + ";
                if ((mods & Keys.Shift) != 0) t += "Shift + ";
                Text = t.Length > 0 ? t + "…" : "Press a combination";
                return;
            }
            if (mods == Keys.None && (key == Keys.Back || key == Keys.Delete || key == Keys.Escape))
            {
                if (key != Keys.Escape) SetValue(new HotkeySpec());
                else Text = value.ToString();
                return;
            }
            bool isFKey = key >= Keys.F1 && key <= Keys.F24;
            if (mods == Keys.None && !isFKey && key != Keys.Pause && key != Keys.Scroll)
            {
                Text = "Add Ctrl, Alt or Shift";
                return;
            }
            HotkeySpec h = new HotkeySpec();
            h.Key = key;
            h.Ctrl = (mods & Keys.Control) != 0;
            h.Alt = (mods & Keys.Alt) != 0;
            h.Shift = (mods & Keys.Shift) != 0;
            h.Win = win;
            SetValue(h);
        }

        private void SetValue(HotkeySpec h)
        {
            value = h;
            Text = h.ToString();
            EventHandler ev = ValueChanged;
            if (ev != null) ev(this, EventArgs.Empty);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (Text.EndsWith("…") || Text == "Add Ctrl, Alt or Shift") Text = value.ToString();
        }
    }
}
