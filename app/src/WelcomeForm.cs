using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScreenCycle
{
    internal enum WelcomeChoice { None, Launch, OpenSettings, Stop }

    /// <summary>The small window shown by ScreenCycle.cmd: launch ScreenCycle and set up shortcuts.</summary>
    internal sealed class WelcomeForm : Form
    {
        public WelcomeChoice Choice { get; private set; }

        private readonly bool running;
        private readonly CheckBox ckDesktop, ckStartMenu, ckAutostart;
        private readonly Timer fade = new Timer();
        private bool fadingOut;

        public static WelcomeChoice Run(bool isRunning)
        {
            using (WelcomeForm f = new WelcomeForm(isRunning))
            {
                Application.Run(f);
                return f.Choice;
            }
        }

        /// <summary>For --render-ui.</summary>
        internal static WelcomeForm Preview(bool isRunning)
        {
            WelcomeForm f = new WelcomeForm(isRunning);
            f.Opacity = 1;
            return f;
        }

        private WelcomeForm(bool isRunning)
        {
            running = isRunning;
            UiKit.PrepareForm(this);
            Text = "ScreenCycle";
            Icon = IconFactory.MakeIcon(32, IconState.Running);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 370);
            Opacity = 0;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) CloseSmooth(); };

            // ---- header
            PictureBox logo = new PictureBox { Size = new Size(52, 52), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(26, 24) };
            logo.Image = IconFactory.RenderLogo(104, IconState.Running);
            Label title = new Label { Text = "ScreenCycle", Font = UiKit.Title, AutoSize = true, Location = new Point(90, 22) };
            Label sub = new Label
            {
                Text = "Rotates apps and websites on this screen.",
                ForeColor = UiKit.Muted, AutoSize = true, Location = new Point(92, 54)
            };
            Label status = new Label { AutoSize = true, Location = new Point(28, 96), Font = UiKit.Bold };
            status.Text = running ? "●  ScreenCycle is running (see the icon near the clock)" : "●  Ready to start";
            status.ForeColor = running ? UiKit.Good : IconFactory.Accent;
            Controls.Add(logo);
            Controls.Add(title);
            Controls.Add(sub);
            Controls.Add(status);

            // ---- options
            Label opt = new Label { Text = "Options", Font = UiKit.Heading, AutoSize = true, Location = new Point(26, 134) };
            Controls.Add(opt);

            bool desktopExists = File.Exists(Shortcuts.DesktopPath);
            bool startExists = File.Exists(Shortcuts.StartMenuPath);
            bool firstTime = !desktopExists && !startExists && !File.Exists(AppConfig.ConfigPath);

            ckDesktop = Option("Add a ScreenCycle shortcut on the desktop", firstTime || desktopExists, 166);
            ckStartMenu = Option("Add ScreenCycle to the Start menu", firstTime || startExists, 196);
            ckAutostart = Option("Start ScreenCycle automatically when I sign in to Windows", Autostart.IsSet(), 226);
            if (running)
            {
                ckAutostart.Enabled = false;
                new ToolTip().SetToolTip(ckAutostart, "ScreenCycle is running: change this in its Settings › Behavior.");
            }
            Label hint = new Label
            {
                Text = "You can come back here at any time by opening ScreenCycle.cmd.",
                ForeColor = UiKit.Muted, Font = UiKit.Small, AutoSize = true, Location = new Point(28, 262)
            };
            Controls.Add(hint);

            // ---- footer
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = UiKit.Surface };
            Panel line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiKit.Border };
            Label credit = new Label
            {
                Text = "Created by Luka LAURENT", ForeColor = UiKit.Muted, Font = UiKit.Small,
                AutoSize = true, Location = new Point(26, 24)
            };
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true,
                WrapContents = false, Padding = new Padding(0, 16, 18, 0)
            };
            Button primary = UiKit.Primary(running ? "Open settings" : "▶  Launch ScreenCycle", delegate
            {
                ApplyOptions();
                Choice = running ? WelcomeChoice.OpenSettings : WelcomeChoice.Launch;
                CloseSmooth();
            });
            primary.MinimumSize = new Size(running ? 130 : 180, 32);
            Button second;
            if (running)
            {
                second = UiKit.Button("Stop ScreenCycle", delegate { ApplyOptions(); Choice = WelcomeChoice.Stop; CloseSmooth(); });
            }
            else
            {
                second = UiKit.Button("Save only", delegate { ApplyOptions(); Choice = WelcomeChoice.None; CloseSmooth(); });
            }
            second.MinimumSize = new Size(100, 32);
            primary.Margin = second.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(primary);
            buttons.Controls.Add(second);
            footer.Controls.Add(buttons);
            footer.Controls.Add(credit);
            footer.Controls.Add(line);
            Controls.Add(footer);
            AcceptButton = primary;

            fade.Interval = 15;
            fade.Tick += OnFade;
        }

        private CheckBox Option(string text, bool value, int y)
        {
            CheckBox c = UiKit.Check(text, value);
            c.Location = new Point(30, y);
            Controls.Add(c);
            return c;
        }

        private void ApplyOptions()
        {
            try { Shortcuts.Apply(ckStartMenu.Checked, ckDesktop.Checked); }
            catch (Exception ex)
            {
                Log.Error("Shortcuts: " + ex.Message);
                MessageBox.Show(this, "The shortcuts could not be updated: " + ex.Message, "ScreenCycle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (!running && ckAutostart.Checked != Autostart.IsSet())
            {
                try
                {
                    AppConfig cfg = AppConfig.Load();
                    cfg.StartWithWindows = ckAutostart.Checked;
                    cfg.Save();
                    Autostart.Set(ckAutostart.Checked);
                }
                catch (Exception ex) { Log.Error("Autostart from welcome window: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ smooth in / out

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            fade.Start();
        }

        private void CloseSmooth()
        {
            fadingOut = true;
            fade.Start();
        }

        private void OnFade(object sender, EventArgs e)
        {
            double step = 0.12;
            if (fadingOut)
            {
                if (Opacity <= step) { fade.Stop(); Close(); return; }
                Opacity -= step;
            }
            else
            {
                if (Opacity >= 1 - step) { Opacity = 1; fade.Stop(); return; }
                Opacity += step;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) fade.Dispose();
            base.Dispose(disposing);
        }
    }
}
