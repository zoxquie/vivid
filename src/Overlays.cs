using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>Draws a crosshair into a bitmap with pixel-exact arms.</summary>
    public static class CrosshairRenderer
    {
        static bool HasArms(CrossStyle s) => s == CrossStyle.Cross || s == CrossStyle.CrossDot || s == CrossStyle.TShape;
        static bool HasDot(CrossStyle s) => s == CrossStyle.Dot || s == CrossStyle.CrossDot || s == CrossStyle.CircleDot;
        static bool HasCircle(CrossStyle s) => s == CrossStyle.Circle || s == CrossStyle.CircleDot;

        /// <summary>Half-size of the square needed to hold the crosshair.</summary>
        public static int Extent(CrosshairSettings c)
        {
            int e = 2;
            if (HasArms(c.Style)) e = Math.Max(e, c.Gap + c.Length + c.Thickness);
            if (HasDot(c.Style)) e = Math.Max(e, c.DotSize);
            if (HasCircle(c.Style)) e = Math.Max(e, c.CircleRadius + c.Thickness);
            return e + c.Outline + 3;
        }

        /// <summary>Renders at 1:1 pixels. Bitmap is (2E+1)², and the centre pixel is (E,E).</summary>
        public static Bitmap Render(CrosshairSettings c)
        {
            int e = Extent(c);
            int n = 2 * e + 1;
            var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, e, c);
            }
            return bmp;
        }

        static void Draw(Graphics g, int e, CrosshairSettings c)
        {
            int alpha = (int)Math.Round(255 * c.Opacity);
            var col = Color.FromArgb(alpha, Color.FromArgb(c.Color));
            var outline = Color.FromArgb((int)Math.Round(alpha * 0.85), 0, 0, 0);
            int t = c.Thickness;
            // The centre "block" is t x t pixels: [lo, hi). Arms sit Gap pixels away from it on each side,
            // so the shape is perfectly symmetric for both odd and even thickness.
            int lo = e - (t - 1) / 2;
            int hi = lo + t;
            var rects = new System.Collections.Generic.List<Rectangle>();

            if (HasArms(c.Style))
            {
                int gap = c.Gap, L = c.Length;
                rects.Add(new Rectangle(hi + gap, lo, L, t));            // right
                rects.Add(new Rectangle(lo - gap - L, lo, L, t));        // left
                rects.Add(new Rectangle(lo, hi + gap, t, L));            // bottom
                if (c.Style != CrossStyle.TShape)
                    rects.Add(new Rectangle(lo, lo - gap - L, t, L));    // top
            }

            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (c.Outline > 0)
                using (var ob = new SolidBrush(outline))
                    foreach (var r in rects) { var o = r; o.Inflate(c.Outline, c.Outline); g.FillRectangle(ob, o); }

            if (HasCircle(c.Style))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = e + 0.5f, R = c.CircleRadius;
                if (c.Outline > 0)
                    using (var op = new Pen(outline, t + 2 * c.Outline))
                        g.DrawEllipse(op, cx - R, cx - R, 2 * R, 2 * R);
                using (var p = new Pen(col, t))
                    g.DrawEllipse(p, cx - R, cx - R, 2 * R, 2 * R);
                g.SmoothingMode = SmoothingMode.None;
            }

            if (HasDot(c.Style))
            {
                int d = c.DotSize;
                int dlo = e - (d - 1) / 2;
                if (HasArms(c.Style))
                {
                    // A dot can only sit exactly on the arms' centre line if both have the same parity.
                    if (d % 2 != t % 2) d += 1;
                    dlo = lo + (t - d) / 2;
                }
                var dr = new Rectangle(dlo, dlo, d, d);
                if (c.Outline > 0)
                {
                    var o = dr; o.Inflate(c.Outline, c.Outline);
                    using (var ob = new SolidBrush(outline)) FillDot(g, ob, o, d >= 5);
                }
                using (var b = new SolidBrush(col)) FillDot(g, b, dr, d >= 5);
            }

            using (var b = new SolidBrush(col))
                foreach (var r in rects) g.FillRectangle(b, r);
        }

        static void FillDot(Graphics g, Brush b, Rectangle r, bool round)
        {
            if (!round) { g.FillRectangle(b, r); return; }
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(b, r);
            g.SmoothingMode = sm;
        }
    }

    /// <summary>A click-through, always-on-top window that shows a per-pixel-alpha bitmap.</summary>
    public class LayeredWindow : Form
    {
        public LayeredWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "";
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        public void SetBitmap(Bitmap bmp, int x, int y, byte opacity = 255)
        {
            if (!Native.IsWindows || !IsHandleCreated) return;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(x, y);
                var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = opacity, AlphaFormat = Native.AC_SRC_ALPHA };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero) { Native.SelectObject(memDc, old); Native.DeleteObject(hBmp); }
                Native.DeleteDC(memDc);
            }
        }

        public void KeepOnTop()
        {
            if (!Native.IsWindows || !IsHandleCreated || !Visible) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>The crosshair overlay window.</summary>
    public sealed class CrosshairOverlay : IDisposable
    {
        LayeredWindow win;
        readonly Timer topTimer, aimTimer;
        CrosshairSettings shown;
        bool aimShown;

        public CrosshairOverlay()
        {
            topTimer = new Timer { Interval = 1500 };
            topTimer.Tick += (s, e) => { if (shown == null || !shown.AimOnly || aimShown) win?.KeepOnTop(); };
            // "only while aiming": watch the right mouse button and show/hide without taking focus
            aimTimer = new Timer { Interval = 8 };
            aimTimer.Tick += (s, e) => SetAimVisible(Aiming);
        }

        static bool Aiming => Native.IsWindows && (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;

        void SetAimVisible(bool on)
        {
            if (win == null || win.IsDisposed || !win.IsHandleCreated || on == aimShown) return;
            aimShown = on;
            Native.ShowWindow(win.Handle, on ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            if (on) win.KeepOnTop();
        }

        public static Rectangle TargetBounds(CrosshairSettings c)
        {
            var screens = Screen.AllScreens;
            Screen s = Screen.PrimaryScreen;
            if (c.Display > 0 && c.Display < screens.Length)
            {
                // Display index counts the primary as 0, the rest in system order.
                int k = 0;
                foreach (var scr in screens)
                {
                    if (scr.Primary) continue;
                    k++;
                    if (k == c.Display) { s = scr; break; }
                }
            }
            return s.Bounds;
        }

        public void Update(CrosshairSettings c)
        {
            if (!Native.IsWindows) return;
            if (!c.Enabled) { Hide(); return; }
            if (win == null || win.IsDisposed)
            {
                win = new LayeredWindow();
                win.Show();
            }
            else if (!win.Visible) win.Show();

            using (var bmp = CrosshairRenderer.Render(c))
            {
                int e = (bmp.Width - 1) / 2;
                var b = TargetBounds(c);
                int x = b.X + b.Width / 2 - e + c.OffsetX;
                int y = b.Y + b.Height / 2 - e + c.OffsetY;
                win.Bounds = new Rectangle(x, y, bmp.Width, bmp.Height);
                win.SetBitmap(bmp, x, y);
            }
            win.KeepOnTop();
            topTimer.Start();
            shown = c.Clone();
            if (c.AimOnly)
            {
                aimShown = true;          // force the first check to apply
                SetAimVisible(Aiming);
                aimTimer.Start();
            }
            else { aimTimer.Stop(); aimShown = true; }
        }

        public void Hide()
        {
            aimTimer.Stop();
            topTimer.Stop();
            if (win != null && !win.IsDisposed) win.Hide();
        }

        public void Dispose()
        {
            aimTimer.Stop();
            aimTimer.Dispose();
            topTimer.Stop();
            topTimer.Dispose();
            if (win != null && !win.IsDisposed) win.Close();
        }
    }

    /// <summary>Small on-screen message shown when a hotkey changes something while you are in game.</summary>
    public sealed class OsdToast : IDisposable
    {
        LayeredWindow win;
        readonly Timer timer;
        DateTime shownAt;
        Bitmap current;
        int x, y;
        const int HoldMs = 1100, FadeMs = 350;

        public OsdToast()
        {
            timer = new Timer { Interval = 30 };
            timer.Tick += Tick;
        }

        public void Show(string title, string detail, Color dot)
        {
            if (!Native.IsWindows) return;
            current?.Dispose();
            current = RenderPill(title, detail, dot);
            var b = Screen.PrimaryScreen.Bounds;
            x = b.X + (b.Width - current.Width) / 2;
            y = b.Y + (int)(b.Height * 0.06);
            if (win == null || win.IsDisposed) { win = new LayeredWindow(); win.Show(); }
            else if (!win.Visible) win.Show();
            win.Bounds = new Rectangle(x, y, current.Width, current.Height);
            win.SetBitmap(current, x, y, 255);
            win.KeepOnTop();
            shownAt = DateTime.UtcNow;
            timer.Start();
        }

        void Tick(object s, EventArgs e)
        {
            double ms = (DateTime.UtcNow - shownAt).TotalMilliseconds;
            if (ms < HoldMs) return;
            double f = 1 - (ms - HoldMs) / FadeMs;
            if (f <= 0 || current == null) { timer.Stop(); win?.Hide(); return; }
            win.SetBitmap(current, x, y, (byte)(255 * f));
        }

        static Bitmap RenderPill(string title, string detail, Color dot)
        {
            float S = Theme.Scale;
            using (var ft = new Font(Theme.SemiboldFamily, 15 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fd = new Font(Theme.RegularFamily, 13 * S, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF st, sd;
                using (var tmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(tmp))
                {
                    st = g.MeasureString(title, ft);
                    sd = string.IsNullOrEmpty(detail) ? SizeF.Empty : g.MeasureString(detail, fd);
                }
                float pad = 18 * S, dotD = 10 * S, gap = 10 * S;
                float w = pad + dotD + gap + st.Width + (sd.Width > 0 ? gap + sd.Width : 0) + pad;
                float h = 46 * S;
                var bmp = new Bitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    var r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
                    using (var path = Theme.Round(r, h / 2))
                    {
                        using (var bg = new SolidBrush(Color.FromArgb(240, 13, 27, 39))) g.FillPath(bg, path);
                        using (var pen = new Pen(Color.FromArgb(255, 101, 228, 196), 1)) g.DrawPath(pen, path);
                    }
                    float cx = pad;
                    using (var db = new SolidBrush(dot)) g.FillEllipse(db, cx, (h - dotD) / 2, dotD, dotD);
                    cx += dotD + gap;
                    using (var tb = new SolidBrush(Theme.Text)) g.DrawString(title, ft, tb, cx, (h - st.Height) / 2);
                    cx += st.Width + gap;
                    if (sd.Width > 0)
                        using (var mb = new SolidBrush(Theme.TextMuted)) g.DrawString(detail, fd, mb, cx, (h - sd.Height) / 2 + 1 * S);
                }
                return bmp;
            }
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            current?.Dispose();
            if (win != null && !win.IsDisposed) win.Close();
        }
    }
}
