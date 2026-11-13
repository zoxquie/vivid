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
    }

    /// <summary>
    /// Keeps the Rust holosight reticle black. While Rust is in front it shows Rust's monitor through
    /// <see cref="HoloGpu"/>: each finished frame is captured, the reticle's colour is swapped on the GPU and the
    /// result is shown in a click-through window on top. The whole picture goes through the same shader in the
    /// same frame, so the recolour stays exactly on the reticle as it sways and recoils, with no fringe and no
    /// patches left behind. The window is left out of screen captures, so it never sees its own output.
    /// Runs on its own thread.
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
        public volatile int Fps;
        public volatile float FrameMs;
        /// <summary>Why it can't run right now (shown on the Holo page), or null.</summary>
        public volatile string Problem;

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

        /// <summary>The click-through, never-activated window the recoloured picture is shown in.</summary>
        internal sealed class Win : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_NCHITTEST: m.Result = new IntPtr(-1); return;  // HTTRANSPARENT
                    case 0x0021: m.Result = new IntPtr(3); return;                 // WM_MOUSEACTIVATE: MA_NOACTIVATE
                    case 0x0014: m.Result = new IntPtr(1); return;                 // WM_ERASEBKGND: Direct3D draws everything
                    case 0x000F: HoloNative.ValidateRect(m.HWnd, IntPtr.Zero); m.Result = IntPtr.Zero; return; // WM_PAINT
                }
                base.WndProc(ref m);
            }

            public static Win Create()
            {
                var win = new Win();
                win.CreateHandle(new CreateParams
                {
                    Caption = AppInfo.Name + " holo",
                    Style = unchecked((int)0x80000000), // WS_POPUP
                    // Layered + transparent: clicks and keys go straight to the game, and Windows doesn't treat the
                    // window as covering Rust (so Rust keeps rendering normally).
                    ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
                });
                HoloNative.SetLayeredWindowAttributes(win.Handle, 0, 255, 2 /* LWA_ALPHA */);
                return win;
            }
        }

        static string Describe(Exception ex)
        {
            int hr = ex.HResult;
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException) return "Direct3D 11 isn't available on this PC";
            if (ex is NotSupportedException) return "Rotated displays aren't supported";
            if (hr == HoloGpu.E_ACCESSDENIED) return "Windows paused screen capture (UAC prompt or lock screen). Trying again…";
            if (hr == HoloGpu.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE) return "Too many apps are capturing this display. Close recording apps and it'll start";
            if (hr == HoloGpu.DXGI_ERROR_UNSUPPORTED) return "This display or graphics driver doesn't support screen capture";
            if (hr == HoloGpu.DXGI_ERROR_DEVICE_REMOVED || hr == HoloGpu.DXGI_ERROR_DEVICE_RESET) return "The graphics driver restarted. Trying again…";
            return "Couldn't start: " + ex.Message;
        }

        void Run()
        {
            // Physical pixels on every monitor, so the window lines up with the captured picture.
            try { HoloNative.SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
            var win = Win.Create();
            IntPtr hwnd = win.Handle;
            bool excluded = false;
            try { excluded = HoloNative.SetWindowDisplayAffinity(hwnd, HoloNative.WDA_EXCLUDEFROMCAPTURE); } catch { }

            HoloGpu gpu = null;
            bool shown = false;
            int hiddenAt = Environment.TickCount, lastTop = 0, lastFail = int.MinValue / 2, lastFsCheck = 0, lastFresh = 0;
            bool exclusive = false;
            string lastLogged = null;
            var fpsClock = Stopwatch.StartNew();
            int frames = 0;
            IntPtr lastFg = IntPtr.Zero; bool fgIsRust = false;

            void Hide()
            {
                if (!shown) return;
                HoloNative.ShowWindow(hwnd, 0);
                shown = false;
                hiddenAt = Environment.TickCount;
            }
            void Drop()
            {
                Hide();
                gpu?.Dispose();
                gpu = null;
            }
            void Fail(Exception ex)
            {
                Drop();
                lastFail = Environment.TickCount;
                Problem = Describe(ex);
                State = RunState.Unsupported;
                if (Problem != lastLogged) { lastLogged = Problem; Program.Log(ex); }
            }
            void Place()
            {
                var b = gpu.Bounds;
                HoloNative.SetWindowPos(hwnd, HoloNative.HWND_TOPMOST, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, 0x0010 | 0x0200); // NOACTIVATE|NOOWNERZORDER
            }

            try
            {
                while (!stop)
                {
                    HoloNative.Pump();
                    var s = settings;
                    if (s == null || !s.Enabled)
                    {
                        Drop(); State = RunState.Off; Problem = null; Fps = 0;
                        wake.WaitOne(500);
                        continue;
                    }
                    if (!excluded)
                    {
                        // Without this the capture would see our own window and feed back into itself.
                        Drop(); State = RunState.Unsupported; Problem = "Needs Windows 10 version 2004 or newer";
                        wake.WaitOne(1000);
                        continue;
                    }

                    IntPtr monitor;
                    if (s.OnlyInRust)
                    {
                        IntPtr fg = HoloNative.GetForegroundWindow();
                        if (fg != lastFg)
                        {
                            lastFg = fg; fgIsRust = false;
                            HoloNative.GetWindowThreadProcessId(fg, out int pid);
                            try { using (var p = Process.GetProcessById(pid)) fgIsRust = p.ProcessName.StartsWith("RustClient", StringComparison.OrdinalIgnoreCase); }
                            catch { }
                        }
                        if (!fgIsRust || HoloNative.IsIconic(fg))
                        {
                            Hide();
                            if (gpu != null && Environment.TickCount - hiddenAt > 3000) Drop(); // let go of the capture while away
                            State = RunState.WaitingForRust; Problem = null; Fps = 0;
                            wake.WaitOne(100);
                            continue;
                        }
                        if (Environment.TickCount - lastFsCheck > 1000)
                        {
                            lastFsCheck = Environment.TickCount;
                            exclusive = HoloNative.SHQueryUserNotificationState(out int qs) == 0 && qs == 3; // QUNS_RUNNING_D3D_FULL_SCREEN
                        }
                        if (exclusive)
                        {
                            Drop(); State = RunState.Unsupported;
                            Problem = "Rust is in exclusive fullscreen. Set Rust to borderless (or windowed) so the black holo can show on top";
                            wake.WaitOne(500);
                            continue;
                        }
                        monitor = HoloNative.MonitorFromWindow(fg, 2);
                    }
                    else
                    {
                        var screens = Screen.AllScreens;
                        var b = screens[Math.Min(Math.Max(0, s.Display), screens.Length - 1)].Bounds;
                        monitor = HoloNative.MonitorFromPoint(new Native.POINT(b.X + b.Width / 2, b.Y + b.Height / 2), 2);
                    }

                    if (gpu != null && gpu.Monitor != monitor) Drop();
                    if (gpu == null)
                    {
                        if (Environment.TickCount - lastFail < 2000) { wake.WaitOne(200); continue; }
                        try
                        {
                            gpu = HoloGpu.ForMonitor(monitor);
                            Place();
                            gpu.AttachWindow(hwnd);
                            gpu.StartCapture();
                            Place();
                        }
                        catch (Exception ex) { Fail(ex); continue; }
                    }

                    HoloGpu.FrameResult r;
                    try
                    {
                        gpu.Configure(s);
                        r = gpu.Frame(50);
                        if (r == HoloGpu.FrameResult.Lost)
                        {
                            // display mode change, UAC prompt or lock screen: pick up again with the new picture
                            Hide();
                            gpu.StartCapture();
                            Place();
                            continue;
                        }
                    }
                    catch (Exception ex) { Fail(ex); continue; }

                    if (r == HoloGpu.FrameResult.Presented)
                    {
                        if (!shown) { HoloNative.ShowWindow(hwnd, 4); shown = true; } // SW_SHOWNOACTIVATE, after the first new frame
                        lastFresh = Environment.TickCount;
                        frames++;
                        FrameMs = gpu.LastWorkMs;
                    }
                    else if (shown && Environment.TickCount - lastFresh > 600)
                    {
                        // No new picture for a while (a still menu, or capture stalled): step aside so the game
                        // itself shows. It comes back with the next frame that changes.
                        Hide();
                    }
                    if (shown && Environment.TickCount - lastTop > 1000) { lastTop = Environment.TickCount; HoloNative.KeepOnTop(hwnd); }
                    State = RunState.Running; Problem = null;
                    if (fpsClock.ElapsedMilliseconds >= 1000) { Fps = frames; frames = 0; fpsClock.Restart(); }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                State = RunState.Unsupported;
                Problem = Describe(ex);
            }
            finally
            {
                Drop();
                win.DestroyHandle();
                if (State != RunState.Unsupported) State = RunState.Off;
                thread = null;
            }
        }
    }

    static class HoloNative
    {
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

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
