using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Vivid
{
    // Runs the black holo shaders on a synthetic picture and compares them with the CPU matcher.
    // Build with tests/gpu/run.sh and run under Windows (or Wine).
    static class GpuSmoke
    {
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point p, uint flags);

        static unsafe int Main()
        {
            int w = 96, h = 64;
            var src = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte g = (byte)(60 + (x + y) % 40);
                    src[i] = g; src[i + 1] = (byte)(g + 10); src[i + 2] = g; src[i + 3] = 255;
                    double d = Math.Sqrt((x - 40) * (x - 40) + (y - 30) * (y - 30));
                    if (Math.Abs(d - 12) < 1.5 || d < 1.5) { src[i] = 30; src[i + 1] = 40; src[i + 2] = 255; } // reticle red
                    else if (Math.Abs(d - 12) < 2.5) { src[i] = 90; src[i + 1] = 100; src[i + 2] = 230; }     // glow
                    if (x > 80 && y > 50) { src[i] = 20; src[i + 1] = 20; src[i + 2] = 220; }               // another red patch
                }
            var s = new HoloSettings();
            s.Sanitize();

            // CPU reference
            var cpu = new byte[w * h * 4];
            var ov = new byte[w * h * 4];
            var m = new HoloMatcher();
            m.Configure(s); m.Resize(w, h);
            fixed (byte* sp = src) fixed (byte* op = ov) m.Process(sp, op, Color.FromArgb(s.Output));
            for (int i = 0; i < w * h; i++)
            {
                int a = ov[i * 4 + 3];
                for (int c = 0; c < 3; c++) cpu[i * 4 + c] = (byte)(ov[i * 4 + c] + src[i * 4 + c] * (255 - a) / 255);
                cpu[i * 4 + 3] = 255;
            }

            byte[] gpu;
            using (var g = HoloGpu.Offscreen())
            {
                g.Configure(s);
                gpu = g.Process(src, w, h);
            }
            int recolGpu = 0, recolCpu = 0, diff = 0, patch = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (i % w > 76 && i / w > 46)
                {
                    // the big red patch: the GPU leaves it alone (too much of a tile is red to be a reticle)
                    if (gpu[i * 4 + 2] != src[i * 4 + 2]) patch++;
                    continue;
                }
                bool cg = gpu[i * 4] != src[i * 4] || gpu[i * 4 + 1] != src[i * 4 + 1] || gpu[i * 4 + 2] != src[i * 4 + 2];
                bool cc = cpu[i * 4] != src[i * 4] || cpu[i * 4 + 1] != src[i * 4 + 1] || cpu[i * 4 + 2] != src[i * 4 + 2];
                if (cg) recolGpu++; if (cc) recolCpu++;
                for (int c = 0; c < 3; c++) if (Math.Abs(gpu[i * 4 + c] - cpu[i * 4 + c]) > 3) { diff++; break; }
            }
            Console.WriteLine("recolored gpu=" + recolGpu + " cpu=" + recolCpu + " differing=" + diff + " patch pixels changed=" + patch);
            // ascii view of the GPU result around the ring
            for (int y = 14; y < 48; y += 2)
            {
                var sb = new System.Text.StringBuilder();
                for (int x = 22; x < 96; x++) { int i = (y * w + x) * 4; sb.Append(gpu[i + 2] < 20 ? '#' : gpu[i + 2] < 120 ? '+' : gpu[i + 2] > 200 ? 'R' : '.'); }
                Console.WriteLine(sb);
            }

            // whole path on a real display (Wine has no desktop duplication; Windows does)
            try
            {
                var mon = MonitorFromPoint(new Point(0, 0), 1);
                using (var g = HoloGpu.ForMonitor(mon))
                {
                    Console.WriteLine("display " + g.Width + "x" + g.Height);
                    // the synthetic picture, scaled up 4x, shown through the swap chain in a window at 0,0
                    int W = g.Width, H = g.Height;
                    var big = new byte[W * H * 4];
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                        { int sx = Math.Min(w - 1, x / 4), sy = Math.Min(h - 1, y / 4); Buffer.BlockCopy(src, (sy * w + sx) * 4, big, (y * W + x) * 4, 4); }
                    NativeWindow f;
                    if (Environment.GetEnvironmentVariable("NOLAYER") != null)
                    {
                        f = new NativeWindow();
                        f.CreateHandle(new CreateParams { Style = unchecked((int)0x80000000), ExStyle = 0x8 /* TOPMOST */ });
                    }
                    else f = HoloOverlay.Win.Create();
                    HoloNative.SetWindowPos(f.Handle, HoloNative.HWND_TOPMOST, 0, 0, W, H, 0x0010);
                    g.AttachWindow(f.Handle);
                    g.Configure(s);
                    g.Process(big, W, H);
                    g.ShowFrame();
                    HoloNative.ShowWindow(f.Handle, 4);
                    Application.DoEvents();
                    Console.WriteLine("showing"); Console.Out.Flush();
                    for (int k = 0; k < (Environment.GetEnvironmentVariable("HOLD") != null ? 60 : 3); k++) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
                    Console.WriteLine("presented");
                    g.StartCapture();
                    for (int k = 0; k < 10; k++) Console.Write(g.Frame(50) + " ");
                    Console.WriteLine();
                }
            }
            catch (Exception e) { Console.WriteLine("display path: " + e.Message); }

            // the overlay thread end to end: on Windows it should say Running; Wine has no screen capture
            var ovl = new HoloOverlay();
            ovl.Update(new HoloSettings { Enabled = true, OnlyInRust = false });
            for (int k = 0; k < 30 && ovl.State != HoloOverlay.RunState.Running; k++) System.Threading.Thread.Sleep(100);
            Console.WriteLine("overlay: " + ovl.State + " " + ovl.Fps + " fps · " + (ovl.Problem ?? "no problem"));
            ovl.Update(new HoloSettings { Enabled = true, OnlyInRust = true });
            System.Threading.Thread.Sleep(500);
            Console.WriteLine("overlay with Rust closed: " + ovl.State);
            ovl.Dispose();
            return diff <= recolCpu / 20 && patch == 0 ? 0 : 1;
        }
    }
}
