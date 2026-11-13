using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Vivid
{
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
