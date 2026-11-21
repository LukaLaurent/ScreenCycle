using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ScreenCycle
{
    /// <summary>Developer helper: renders the settings windows to PNG files (ScreenCycle --render-ui folder).</summary>
    internal static class DevTools
    {
        public static void RenderUi(string dir)
        {
            Directory.CreateDirectory(dir);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { Log.Error("render EX: " + e.Exception); };
            AppConfig cfg = AppConfig.Load();
            using (SettingsForm f = new SettingsForm(cfg, new List<string>(), RotatorState.Stopped))
            {
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ShowInTaskbar = false;
                f.Show();
                TabControl tabs = Find<TabControl>(f);
                for (int i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i;
                    Application.DoEvents();
                    Save(f, Path.Combine(dir, "settings" + i + ".png"));
                }
                f.Hide();
            }
            foreach (bool running in new bool[] { false, true })
            {
                using (WelcomeForm w = WelcomeForm.Preview(running))
                {
                    w.StartPosition = FormStartPosition.Manual;
                    w.Location = new Point(-5000, -5000);
                    w.Show();
                    Application.DoEvents();
                    Save(w, Path.Combine(dir, "welcome-" + (running ? "running" : "ready") + ".png"));
                    w.Hide();
                }
            }            ItemKind[] kinds = { ItemKind.Website, ItemKind.Application, ItemKind.Window };
            foreach (ItemKind k in kinds)
            {
                RotationItem it = new RotationItem();
                it.Kind = k;
                if (k == ItemKind.Website) { it.Url = "https://youtube.com"; it.Browser = BrowserKind.Firefox; List<BrowserProfileInfo> pr = Browsers.ListProfiles(BrowserKind.Firefox); if (pr.Count > 0) it.BrowserProfile = pr[0].Id; }
                if (k == ItemKind.Window) it.ProcessName = "notepad";
                using (ItemEditorForm e = new ItemEditorForm(it, true))
                {
                    e.StartPosition = FormStartPosition.Manual;
                    e.Location = new Point(-5000, -5000);
                    e.Show();
                    Application.DoEvents();
                    Save(e, Path.Combine(dir, "editor-" + k + ".png"));
                    e.Hide();
                }
            }
        }

        private static T Find<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                T t = c as T;
                if (t != null) return t;
                T inner = Find<T>(c);
                if (inner != null) return inner;
            }
            return null;
        }

        private static void Save(Form f, string path)
        {
            using (Bitmap b = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(path, ImageFormat.Png);
            }
        }
    }
}
