using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>
    /// Finds the holosight reticle's pixels in a screen capture and builds the overlay that recolors them.
    /// Pure pixel logic (no Windows calls) so it can be tested anywhere.
    /// </summary>
    public sealed class HoloMatcher
    {
        // 32×32×32 colour classes: 0 = no, 1 = reticle, 2 = glow (only counts next to reticle pixels)
        readonly byte[] lut = new byte[32 * 32 * 32];
        byte[] cls = new byte[0];
        int[] hits = new int[0];
        int hitCount;
        readonly List<int> painted = new List<int>();
        string key;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Pixels recolored in the last frame.</summary>
        public int Changed { get; private set; }
        /// <summary>Box around the pixels that changed since the previous frame (overlay coordinates).</summary>
        public Rectangle Dirty { get; private set; }

        public void Configure(HoloSettings s)
        {
            string k = s.Target + "|" + s.Tolerance;
            if (k == key) return;
            key = k;
            var t = Color.FromArgb(s.Target);
            Hsv(t.R, t.G, t.B, out double th, out double ts, out double tv);
            double tol = s.Tolerance / 100.0;
            double hueWin = 8 + 26 * tol;
            double satMin = Math.Max(0.22, ts * (0.78 - 0.4 * tol));
            double valMin = Math.Max(0.18, tv * (0.62 - 0.32 * tol));
            bool grey = ts < 0.15; // a white/grey reticle: match by brightness instead of hue
            for (int r = 0; r < 32; r++)
                for (int g = 0; g < 32; g++)
                    for (int b = 0; b < 32; b++)
                    {
                        // centre of the 8-level bucket
                        Hsv(r * 8 + 4, g * 8 + 4, b * 8 + 4, out double h, out double sat, out double v);
                        byte c = 0;
                        if (grey)
                        {
                            if (sat < 0.12 + 0.1 * tol && v >= tv - 0.08 - 0.2 * tol) c = 1;
                        }
                        else
                        {
                            double dh = Math.Abs(h - th); if (dh > 180) dh = 360 - dh;
                            if (dh <= hueWin && sat >= satMin && v >= valMin) c = 1;
                            else if (dh <= hueWin * 1.5 && sat >= 0.12 && v >= 0.5) c = 2; // bright core / halo of the glow
                        }
                        lut[(r << 10) | (g << 5) | b] = c;
                    }
        }

        public static void Hsv(int r, int g, int b, out double h, out double s, out double v)
        {
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            v = max / 255.0;
            s = max == 0 ? 0 : (max - min) / (double)max;
            if (max == min) { h = 0; return; }
            double d = max - min;
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public void Resize(int w, int h)
        {
            if (w == Width && h == Height) return;
            Width = w; Height = h;
            cls = new byte[w * h];
            hits = new int[Math.Max(1024, w * h / 16)];
            painted.Clear();
        }

        /// <summary>
        /// src: captured BGRA pixels (top-down, stride = w*4). dst: the overlay's premultiplied BGRA pixels, which
        /// still hold the previous frame; only the pixels that change are touched.
        /// </summary>
        public unsafe void Process(byte* src, byte* dst, Color output)
        {
            int w = Width, h = Height, n = w * h;
            hitCount = 0;
            bool overflow = false;
            fixed (byte* lp = lut, cp = cls)
            {
                for (int i = 0, o = 0; i < n; i++, o += 4)
                {
                    byte c = lp[((src[o + 2] >> 3) << 10) | ((src[o + 1] >> 3) << 5) | (src[o] >> 3)];
                    cp[i] = c;
                    if (c == 1)
                    {
                        if (hitCount < hits.Length) hits[hitCount++] = i;
                        else overflow = true;
                    }
                }

                // clear what we drew last frame
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                foreach (int i in painted)
                {
                    *(uint*)(dst + i * 4) = 0;
                    Grow(i, w, ref minX, ref minY, ref maxX, ref maxY);
                }
                painted.Clear();

                // a screen full of the target colour isn't a reticle: leave it alone
                if (!overflow && hitCount > 0)
                {
                    uint solid = Pack(output, 255), soft = Pack(output, 150);
                    const byte Done = 9;
                    // the reticle itself, plus glow pixels within 2 px of it
                    for (int k = 0; k < hitCount; k++)
                    {
                        int i = hits[k], x = i % w, y = i / w;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            int yy = y + dy; if (yy < 0 || yy >= h) continue;
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                                int j = yy * w + xx;
                                byte c = cp[j];
                                if (c == Done) continue;
                                if (c == 1 || c == 2 || (dx >= -1 && dx <= 1 && dy >= -1 && dy <= 1))
                                {
                                    // solid on matching pixels; a softer 1 px edge for anti-aliasing
                                    *(uint*)(dst + j * 4) = c == 0 ? soft : solid;
                                    cp[j] = Done;
                                    painted.Add(j);
                                    Grow(j, w, ref minX, ref minY, ref maxX, ref maxY);
                                }
                            }
                        }
                    }
                }
                Changed = painted.Count;
                Dirty = maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            }
        }

        static void Grow(int i, int w, ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            int x = i % w, y = i / w;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }

        static uint Pack(Color c, int a) =>
            (uint)a << 24 | (uint)(c.R * a / 255) << 16 | (uint)(c.G * a / 255) << 8 | (uint)(c.B * a / 255);

        /// <summary>Area of the screen to watch, as fractions of the display: aiming only, aim + hip fire, large.</summary>
        public static RectangleF AreaFraction(int area)
        {
            switch (area)
            {
                case 0: return RectangleF.FromLTRB(0.40f, 0.36f, 0.60f, 0.64f);
                case 2: return RectangleF.FromLTRB(0.15f, 0.12f, 0.92f, 0.96f);
                default: return RectangleF.FromLTRB(0.28f, 0.28f, 0.84f, 0.90f);
            }
        }

        public static Rectangle AreaOn(Rectangle display, int area)
        {
            var f = AreaFraction(area);
            return Rectangle.FromLTRB(
                display.X + (int)(display.Width * f.Left), display.Y + (int)(display.Height * f.Top),
                display.X + (int)(display.Width * f.Right), display.Y + (int)(display.Height * f.Bottom));
        }
    }

    /// <summary>
    /// Keeps the Rust holosight reticle black: every frame it copies the watched part of the screen,
    /// finds the reticle's pixels and covers them on a click-through overlay. Runs on its own thread.
    /// The overlay is left out of screen captures, so it never sees its own output.
    /// </summary>
    public sealed class HoloOverlay : IDisposable
    {
        public enum RunState { Off, WaitingForRust, Running, Unsupported }

        volatile HoloSettings settings;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;

        // live stats for the UI
        public volatile RunState State = RunState.Off;
        public volatile int Fps, Changed;
        public volatile float FrameMs;

        public void Update(HoloSettings s)
        {
            settings = s.Clone();
            if (!Native.IsWindows) { State = s.Enabled ? RunState.Unsupported : RunState.Off; return; }
            if (s.Enabled && thread == null)
            {
                stop = false;
                thread = new Thread(Run) { IsBackground = true, Name = "Vivid holo", Priority = ThreadPriority.AboveNormal };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            wake.Set();
        }

        public void Dispose()
        {
            stop = true;
            wake.Set();
            thread?.Join(1500);
            thread = null;
        }

        sealed class Win : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_NCHITTEST) { m.Result = new IntPtr(-1); return; }
                base.WndProc(ref m);
            }
        }

        unsafe void Run()
        {
            var win = new Win();
            win.CreateHandle(new CreateParams
            {
                Caption = "",
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            });
            IntPtr hwnd = win.Handle;
            try { HoloNative.SetWindowDisplayAffinity(hwnd, HoloNative.WDA_EXCLUDEFROMCAPTURE); } catch { }
            try { HoloNative.timeBeginPeriod(1); } catch { }

            var matcher = new HoloMatcher();
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr capDc = Native.CreateCompatibleDC(screenDc), outDc = Native.CreateCompatibleDC(screenDc);
            IntPtr capBmp = IntPtr.Zero, outBmp = IntPtr.Zero, capOld = IntPtr.Zero, outOld = IntPtr.Zero;
            byte* capBits = null, outBits = null;
            Rectangle area = Rectangle.Empty;
            bool shown = false, anything = false, full = false;
            var fpsClock = Stopwatch.StartNew();
            var frameClock = new Stopwatch();
            int frames = 0, lastTop = 0;
            int rustPid = -1; DateTime rustChecked = DateTime.MinValue; bool rustFront = false;

            void Hide()
            {
                if (!shown) return;
                HoloNative.ShowWindow(hwnd, 0);
                shown = false;
            }

            void Allocate(Rectangle a)
            {
                if (capBmp != IntPtr.Zero) { Native.SelectObject(capDc, capOld); Native.DeleteObject(capBmp); }
                if (outBmp != IntPtr.Zero) { Native.SelectObject(outDc, outOld); Native.DeleteObject(outBmp); }
                capBmp = HoloNative.Dib(screenDc, a.Width, a.Height, out IntPtr cb);
                outBmp = HoloNative.Dib(screenDc, a.Width, a.Height, out IntPtr ob);
                capBits = (byte*)cb; outBits = (byte*)ob;
                capOld = Native.SelectObject(capDc, capBmp);
                outOld = Native.SelectObject(outDc, outBmp);
                for (int i = 0; i < a.Width * a.Height; i++) ((uint*)outBits)[i] = 0;
                matcher.Resize(a.Width, a.Height);
                area = a;
                anything = true; // push the cleared surface once
                full = true;     // the window changed size: send all of it
            }

            try
            {
                while (!stop)
                {
                    HoloNative.Pump();
                    var s = settings;
                    if (s == null || !s.Enabled)
                    {
                        Hide(); State = RunState.Off; Fps = 0; Changed = 0;
                        wake.WaitOne(500);
                        continue;
                    }

                    // only while Rust is the window in front
                    if (s.OnlyInRust)
                    {
                        if ((DateTime.UtcNow - rustChecked).TotalMilliseconds > 250)
                        {
                            rustChecked = DateTime.UtcNow;
                            HoloNative.GetWindowThreadProcessId(HoloNative.GetForegroundWindow(), out int pid);
                            if (pid != rustPid)
                            {
                                rustPid = pid;
                                try { using (var p = Process.GetProcessById(pid)) rustFront = p.ProcessName.StartsWith("RustClient", StringComparison.OrdinalIgnoreCase); }
                                catch { rustFront = false; }
                            }
                        }
                        if (!rustFront) { Hide(); State = RunState.WaitingForRust; Fps = 0; Changed = 0; wake.WaitOne(150); continue; }
                    }

                    var screens = Screen.AllScreens;
                    var disp = screens[Math.Min(Math.Max(0, s.Display), screens.Length - 1)].Bounds;
                    var want = HoloMatcher.AreaOn(disp, s.Area);
                    if (want != area) Allocate(want);
                    matcher.Configure(s);

                    // wait for the next screen refresh, then work on the newest frame
                    try { HoloNative.DwmFlush(); } catch { Thread.Sleep(5); }
                    frameClock.Restart();
                    HoloNative.BitBlt(capDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, HoloNative.SRCCOPY);
                    HoloNative.GdiFlush();
                    matcher.Process(capBits, outBits, Color.FromArgb(s.Output));

                    if (matcher.Changed > 0 || anything)
                    {
                        var dirty = full || matcher.Dirty.IsEmpty ? new Rectangle(0, 0, area.Width, area.Height) : matcher.Dirty;
                        HoloNative.Push(hwnd, screenDc, outDc, area, dirty);
                        full = false;
                        if (!shown) { HoloNative.ShowWindow(hwnd, 4); shown = true; } // SW_SHOWNOACTIVATE
                    }
                    anything = matcher.Changed > 0;
                    if (Environment.TickCount - lastTop > 1000) { lastTop = Environment.TickCount; HoloNative.KeepOnTop(hwnd); }

                    FrameMs = (float)frameClock.Elapsed.TotalMilliseconds;
                    Changed = matcher.Changed;
                    State = RunState.Running;
                    frames++;
                    if (fpsClock.ElapsedMilliseconds >= 1000) { Fps = frames; frames = 0; fpsClock.Restart(); }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                State = RunState.Unsupported;
            }
            finally
            {
                Hide();
                if (capBmp != IntPtr.Zero) { Native.SelectObject(capDc, capOld); Native.DeleteObject(capBmp); }
                if (outBmp != IntPtr.Zero) { Native.SelectObject(outDc, outOld); Native.DeleteObject(outBmp); }
                Native.DeleteDC(capDc); Native.DeleteDC(outDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                try { HoloNative.timeEndPeriod(1); } catch { }
                win.DestroyHandle();
                State = RunState.Off;
            }
        }
    }

    static class HoloNative
    {
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        /// <summary>32-bit top-down bitmap whose pixels we can read and write directly.</summary>
        public static IntPtr Dib(IntPtr dc, int w, int h, out IntPtr bits)
        {
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            return CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct UPDATELAYEREDWINDOWINFO
        {
            public int cbSize; public IntPtr hdcDst; public IntPtr pptDst; public IntPtr psize; public IntPtr hdcSrc; public IntPtr pptSrc;
            public int crKey; public IntPtr pblend; public int dwFlags; public IntPtr prcDirty;
        }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);

        /// <summary>Shows the overlay's pixels; only the dirty part is sent to the compositor.</summary>
        public static unsafe void Push(IntPtr hwnd, IntPtr screenDc, IntPtr srcDc, Rectangle area, Rectangle dirty)
        {
            var dst = new Native.POINT(area.X, area.Y);
            var size = new Native.SIZE(area.Width, area.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            var rc = new RECT { Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom };
            var info = new UPDATELAYEREDWINDOWINFO
            {
                cbSize = Marshal.SizeOf(typeof(UPDATELAYEREDWINDOWINFO)),
                hdcDst = screenDc, pptDst = (IntPtr)(&dst), psize = (IntPtr)(&size), hdcSrc = srcDc, pptSrc = (IntPtr)(&src),
                pblend = (IntPtr)(&blend), dwFlags = Native.ULW_ALPHA, prcDirty = (IntPtr)(&rc),
            };
            if (!UpdateLayeredWindowIndirect(hwnd, ref info))
                Native.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, srcDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static void KeepOnTop(IntPtr hwnd) => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200); // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        public static void Pump()
        {
            while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
    }
}
