using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenCycle
{
    /// <summary>Base for click-through, never-activated, topmost layered windows.</summary>
    internal class GhostForm : Form
    {
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        private const uint LWA_ALPHA = 0x2;
        private byte alpha = 255;

        public GhostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
        }

        public byte Alpha
        {
            get { return alpha; }
            set
            {
                alpha = value;
                if (IsHandleCreated) SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
            }
        }

        public void ShowNoActivate()
        {
            if (!Visible)
            {
                Show();
            }
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        protected const int WM_MOUSEACTIVATE = 0x21;
        protected const int MA_NOACTIVATE = 3;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public static double Ease(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        /// <summary>Animates alpha from one value to another over ms with ease-in-out.</summary>
        public async Task AnimateAlpha(byte from, byte to, int ms)
        {
            if (ms <= 0) { Alpha = to; return; }
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                double t = sw.ElapsedMilliseconds / (double)ms;
                double e = Ease(t);
                Alpha = (byte)Math.Round(from + (to - from) * e);
                if (t >= 1) break;
                await Task.Delay(10);
            }
            Alpha = to;
        }
    }

    /// <summary>Full-monitor overlay used for transitions and the "preparing" splash.</summary>
    internal sealed class ScreenOverlay : GhostForm
    {
        private Bitmap image;
        private string title = "", subtitle = "";
        private bool splash;
        private float spinnerAngle;
        private readonly Timer spinTimer;

        public ScreenOverlay()
        {
            spinTimer = new Timer();
            spinTimer.Interval = 30;
            spinTimer.Tick += delegate { spinnerAngle = (spinnerAngle + 9) % 360; Invalidate(); };
        }

        public void Place(Rectangle bounds)
        {
            Bounds = bounds;
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
        }

        /// <summary>Freezes the current picture of the monitor.</summary>
        public bool CaptureScreen(Rectangle bounds)
        {
            try
            {
                Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                SetImage(bmp);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Screen capture failed: " + ex.Message);
                SetImage(null);
                return false;
            }
        }

        public void SetImage(Bitmap bmp)
        {
            if (image != null && image != bmp) image.Dispose();
            image = bmp;
            splash = false;
            spinTimer.Stop();
            Invalidate();
        }

        public void SetBlack()
        {
            SetImage(null);
        }

        public void SetSplash(string t, string sub)
        {
            if (image != null) { image.Dispose(); image = null; }
            title = t ?? "";
            subtitle = sub ?? "";
            splash = true;
            if (!spinTimer.Enabled) spinTimer.Start();
            Invalidate();
        }

        /// <summary>Shows a small "loading" badge on top of the frozen picture.</summary>
        public void SetLoadingBadge(string text)
        {
            subtitle = text ?? "";
            if (!spinTimer.Enabled) spinTimer.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (image != null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImageUnscaled(image, 0, 0);
                if (spinTimer.Enabled && subtitle.Length > 0) DrawBadge(g);
                return;
            }
            g.Clear(Color.Black);
            if (!splash) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float scale = Height / 1080f;
            if (scale < 0.5f) scale = 0.5f;
            int cx = Width / 2, cy = Height / 2;

            using (Bitmap logo = IconFactory.RenderLogo((int)(120 * scale), IconState.Running))
                g.DrawImage(logo, cx - logo.Width / 2, cy - logo.Height - (int)(40 * scale));

            DrawSpinner(g, new PointF(cx, cy + 10 * scale), 18 * scale, 3.5f * scale);

            using (Font f = new Font("Segoe UI Semibold", 26 * scale, GraphicsUnit.Pixel))
            using (Font f2 = new Font("Segoe UI", 18 * scale, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
            {
                g.DrawString(title, f, Brushes.White, new RectangleF(0, cy + 50 * scale, Width, 40 * scale), sf);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g.DrawString(subtitle, f2, b, new RectangleF(0, cy + 92 * scale, Width, 30 * scale), sf);
            }
        }

        private void DrawSpinner(Graphics g, PointF c, float r, float w)
        {
            using (Pen bg = new Pen(Color.FromArgb(40, 255, 255, 255), w))
                g.DrawEllipse(bg, c.X - r, c.Y - r, r * 2, r * 2);
            using (Pen p = new Pen(IconFactory.Accent, w))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, c.X - r, c.Y - r, r * 2, r * 2, spinnerAngle, 90);
            }
        }

        private void DrawBadge(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float scale = Math.Max(0.6f, Height / 1080f);
            using (Font f = new Font("Segoe UI", 16 * scale, GraphicsUnit.Pixel))
            {
                SizeF ts = g.MeasureString(subtitle, f);
                float h = 44 * scale, w = ts.Width + 70 * scale;
                RectangleF box = new RectangleF((Width - w) / 2, Height - h - 40 * scale, w, h);
                using (GraphicsPath gp = IconFactory.Rounded(box, h / 2))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 24, 24, 28)))
                    g.FillPath(b, gp);
                DrawSpinner(g, new PointF(box.X + 24 * scale, box.Y + h / 2), 8 * scale, 2.5f * scale);
                g.DrawString(subtitle, f, Brushes.White, box.X + 44 * scale, box.Y + (h - ts.Height) / 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                spinTimer.Dispose();
                if (image != null) { image.Dispose(); image = null; }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Small toast shown near the bottom of the screen ("Paused", "Next: ...").</summary>
    internal sealed class OsdForm : GhostForm
    {
        private string text = "";
        private string icon = "";
        private int hideGeneration;

        public OsdForm()
        {
            BackColor = Color.FromArgb(28, 28, 32);
        }

        public async void Flash(Rectangle monitor, string glyph, string message)
        {
            text = message ?? "";
            icon = glyph ?? "";
            float scale = Math.Max(0.75f, monitor.Height / 1080f);
            using (Graphics g = CreateGraphics())
            using (Font f = MainFont(scale))
            {
                SizeF s = g.MeasureString(text, f);
                int w = (int)(s.Width + (icon.Length > 0 ? 90 : 56) * scale);
                int h = (int)(64 * scale);
                Bounds = new Rectangle(monitor.X + (monitor.Width - w) / 2, monitor.Bottom - h - (int)(80 * scale), w, h);
            }
            Region = new Region(IconFactory.Rounded(new RectangleF(0, 0, Width, Height), Height / 2f));
            Invalidate();
            int gen = ++hideGeneration;
            Alpha = 0;
            ShowNoActivate();
            await AnimateAlpha(0, 235, 120);
            await Task.Delay(1300);
            if (gen != hideGeneration) return;
            await AnimateAlpha(235, 0, 350);
            if (gen != hideGeneration) return;
            Hide();
        }

        private static Font MainFont(float scale)
        {
            return new Font("Segoe UI Semibold", 20 * scale, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float scale = Height / 64f;
            using (Font f = MainFont(scale))
            {
                SizeF s = g.MeasureString(text, f);
                float x = 28 * scale;
                if (icon.Length > 0)
                {
                    using (Font fi = new Font("Segoe MDL2 Assets", 18 * scale, GraphicsUnit.Pixel))
                    using (SolidBrush b = new SolidBrush(IconFactory.Accent))
                    {
                        SizeF si = g.MeasureString(icon, fi);
                        g.DrawString(icon, fi, b, x, (Height - si.Height) / 2 + 1 * scale);
                    }
                    x += 34 * scale;
                }
                g.DrawString(text, f, Brushes.White, x, (Height - s.Height) / 2);
            }
        }
    }

    /// <summary>Thin progress line along the bottom edge that shows how long the current item stays.</summary>
    internal sealed class ProgressStrip : GhostForm
    {
        private double progress;

        public ProgressStrip()
        {
            BackColor = Color.Black;
        }

        public void Place(Rectangle monitor)
        {
            int h = Math.Max(3, monitor.Height / 270);
            Rectangle b = new Rectangle(monitor.X, monitor.Bottom - h, monitor.Width, h);
            if (Bounds != b) Bounds = b;
        }

        public double Progress
        {
            get { return progress; }
            set
            {
                value = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(value - progress) * Width < 0.5 && value != 0) return;
                progress = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(20, 20, 20));
            int w = (int)Math.Round(Width * progress);
            if (w > 0)
                using (SolidBrush b = new SolidBrush(IconFactory.Accent))
                    e.Graphics.FillRectangle(b, 0, 0, w, Height);
        }
    }
}
