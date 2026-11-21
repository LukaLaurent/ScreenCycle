using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ScreenCycle
{
    internal enum IconState { Running, Paused, Stopped }

    /// <summary>Draws the app's icon at any size, so it is crisp at every DPI (no image files needed).</summary>
    internal static class IconFactory
    {
        public static readonly Color Accent = Color.FromArgb(56, 132, 255);
        private static readonly Color Accent2 = Color.FromArgb(124, 92, 255);

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap RenderLogo(int size, IconState state)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                float pad = s * 0.04f;
                RectangleF box = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);

                Color c1 = Accent, c2 = Accent2;
                if (state == IconState.Stopped) { c1 = Color.FromArgb(120, 128, 140); c2 = Color.FromArgb(90, 96, 108); }
                using (GraphicsPath bg = Rounded(box, s * 0.22f))
                using (LinearGradientBrush br = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), c1, c2))
                    g.FillPath(br, bg);

                // two chasing arrows = "cycle"
                float cx = s / 2f, cy = s / 2f;
                float r = s * 0.25f;
                float w = Math.Max(1.6f, s * 0.095f);
                using (Pen pen = new Pen(Color.White, w))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Flat;
                    RectangleF arc = new RectangleF(cx - r, cy - r, r * 2, r * 2);
                    g.DrawArc(pen, arc, 200, 125);
                    g.DrawArc(pen, arc, 20, 125);
                }
                DrawHead(g, cx, cy, r, 325, s, w);
                DrawHead(g, cx, cy, r, 145, s, w);

                if (state == IconState.Paused)
                {
                    float bd = s * 0.46f;
                    RectangleF b = new RectangleF(s - bd - pad * 0.2f, s - bd - pad * 0.2f, bd, bd);
                    using (SolidBrush ob = new SolidBrush(Color.FromArgb(255, 170, 30)))
                        g.FillEllipse(ob, b);
                    using (Pen edge = new Pen(Color.White, Math.Max(1f, s * 0.035f)))
                        g.DrawEllipse(edge, b);
                    float bw = bd * 0.14f, bh = bd * 0.44f;
                    float bx = b.X + bd / 2f, by = b.Y + (bd - bh) / 2f;
                    g.FillRectangle(Brushes.White, bx - bw * 1.5f, by, bw, bh);
                    g.FillRectangle(Brushes.White, bx + bw * 0.5f, by, bw, bh);
                }
            }
            return bmp;
        }

        private static void DrawHead(Graphics g, float cx, float cy, float r, float angleDeg, float s, float w)
        {
            double a = angleDeg * Math.PI / 180.0;
            PointF tip = new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
            // tangent direction (clockwise)
            double tx = -Math.Sin(a), ty = Math.Cos(a);
            double nx = Math.Cos(a), ny = Math.Sin(a);
            float len = Math.Max(2.5f, s * 0.16f);
            float half = Math.Max(2.2f, s * 0.13f);
            PointF front = new PointF(tip.X + (float)(tx * len * 0.75), tip.Y + (float)(ty * len * 0.75));
            PointF left = new PointF(tip.X + (float)(nx * half - tx * len * 0.25), tip.Y + (float)(ny * half - ty * len * 0.25));
            PointF right = new PointF(tip.X - (float)(nx * half + tx * len * 0.25), tip.Y - (float)(ny * half + ty * len * 0.25));
            g.FillPolygon(Brushes.White, new PointF[] { front, left, right });
        }

        public static Icon MakeIcon(int size, IconState state)
        {
            using (Bitmap bmp = RenderLogo(size, state))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        /// <summary>Writes a multi-resolution .ico (PNG entries) — used by the build to give the exe its icon.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using (Bitmap b = RenderLogo(sz, IconState.Running))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }
}
