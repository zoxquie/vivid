using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Vivid
{
    /// <summary>
    /// Writes the black holo's shader, its constants and test pictures (with the CPU matcher's answers) so
    /// tests/gpu/holo_gpu.cpp can run the same passes through Direct3D 11 and compare (see tests/gpu/check.sh).
    /// Also takes real screenshots: tests/gpu/shots/*.png.
    /// </summary>
    static unsafe class HoloDump
    {
        static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : "bin/holo-dump";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "holo.hlsl"), HoloGpu.ShaderSource());
            var list = new StringWriter();

            var world = Color.FromArgb(70, 96, 52);
            var red = new HoloSettings();
            Case(dir, list, "ring", Scene(320, 240, Color.FromArgb(255, 40, 30), world, false), 320, 240, red, null);
            Case(dir, list, "ring-zones", Scene(320, 240, Color.FromArgb(255, 40, 30), world, true), 320, 240, red, "center");
            Case(dir, list, "night", Scene(320, 240, Color.FromArgb(200, 30, 28), Color.FromArgb(25, 30, 40), false), 320, 240, red, null);
            var mag = new HoloSettings { Target = unchecked((int)0xFFFF2BD6), Output = unchecked((int)0xFF00FF6A) };
            Case(dir, list, "magenta", Scene(320, 240, Color.FromArgb(255, 43, 214), world, false), 320, 240, mag, null);
            Case(dir, list, "flood", Scene(320, 240, Color.FromArgb(255, 40, 30), Color.FromArgb(250, 38, 30), false), 320, 240, red, null);

            string shots = Path.Combine("tests", "gpu", "shots");
            if (Directory.Exists(shots))
                foreach (var f in Directory.GetFiles(shots, "*.png"))
                    using (var b = new Bitmap(f))
                        Case(dir, list, Path.GetFileNameWithoutExtension(f), Bgra(b), b.Width, b.Height, red, "center");

            File.WriteAllText(Path.Combine(dir, "cases.txt"), list.ToString());
            Console.WriteLine("wrote " + dir);
            return 0;
        }

        static void Case(string dir, StringWriter list, string name, byte[] src, int w, int h, HoloSettings s, string zoneMode)
        {
            var m = new HoloMatcher();
            m.Resize(w, h); m.Configure(s);
            byte[] zone = null;
            if (zoneMode == "center")
            {
                var z = new HoloZones();
                z.Resize(m.TilesW, m.TilesH, w, h);
                zone = (byte[])z.Zone.Clone();
            }
            m.Zone = zone;
            var ov = new byte[w * h * 4];
            fixed (byte* sp = src) fixed (byte* op = ov) m.Process(sp, op, Color.FromArgb(s.Output));
            var outp = new byte[src.Length];
            for (int i = 0; i < w * h; i++)
            {
                int a = ov[i * 4 + 3];
                for (int c = 0; c < 3; c++) outp[i * 4 + c] = (byte)Math.Min(255, ov[i * 4 + c] + (src[i * 4 + c] * (255 - a) + 127) / 255);
                outp[i * 4 + 3] = 255;
            }
            var p = HoloGpu.MakeParams(s);
            p.W = w; p.H = h;
            var pb = new byte[Marshal.SizeOf(typeof(HoloGpu.Params))];
            fixed (byte* pp = pb) *(HoloGpu.Params*)pp = p;
            if (zone == null) { zone = new byte[m.TilesW * m.TilesH]; for (int i = 0; i < zone.Length; i++) zone[i] = 255; }
            var tiles = new byte[m.TilesW * m.TilesH * 2];
            for (int i = 0; i < m.TilesW * m.TilesH; i++) { tiles[i * 2] = m.TileCoverage[i]; tiles[i * 2 + 1] = m.TileLuma[i]; }
            File.WriteAllBytes(Path.Combine(dir, name + ".src"), src);
            File.WriteAllBytes(Path.Combine(dir, name + ".params"), pb);
            File.WriteAllBytes(Path.Combine(dir, name + ".zone"), zone);
            File.WriteAllBytes(Path.Combine(dir, name + ".cpu"), outp);
            File.WriteAllBytes(Path.Combine(dir, name + ".cputiles"), tiles);
            list.WriteLine(name + " " + w + " " + h + " " + m.TilesW + " " + m.TilesH);
            Console.WriteLine($"  {name}: {w}x{h}, cpu changed {m.Changed} px");
        }

        /// <summary>A holosight's glowing ring and dot (bright core, soft glow) over a plain world.
        /// withBarrel adds a small bright-red object away from the middle that must stay red.</summary>
        static byte[] Scene(int w, int h, Color reticle, Color world, bool withBarrel)
        {
            using (var b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(world);
                    var rnd = new Random(3);
                    for (int i = 0; i < 400; i++)
                        using (var br = new SolidBrush(Color.FromArgb(60, rnd.Next(40, 140), rnd.Next(60, 150), rnd.Next(30, 90))))
                            g.FillRectangle(br, rnd.Next(w), rnd.Next(h), rnd.Next(2, 9), rnd.Next(2, 9));
                    if (withBarrel)
                        using (var br = new SolidBrush(reticle)) g.FillRectangle(br, 18, 30, 10, 14);
                    float cx = w / 2f, cy = h / 2f, R = h * 0.09f;
                    for (int i = 4; i >= 1; i--)
                        using (var glow = new Pen(Color.FromArgb(30, reticle), 2 + i * 2)) g.DrawEllipse(glow, cx - R, cy - R, 2 * R, 2 * R);
                    using (var ring = new Pen(reticle, 2.5f)) g.DrawEllipse(ring, cx - R, cy - R, 2 * R, 2 * R);
                    using (var core = new SolidBrush(Color.FromArgb(255, 255, 220, 215))) g.FillEllipse(core, cx - 2.5f, cy - 2.5f, 5, 5);
                    using (var dot = new Pen(reticle, 1.5f)) g.DrawEllipse(dot, cx - 3.5f, cy - 3.5f, 7, 7);
                }
                return Bgra(b);
            }
        }

        static byte[] Bgra(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var o = new byte[w * h * 4];
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, o, y * w * 4, w * 4);
            b.UnlockBits(d);
            for (int i = 3; i < o.Length; i += 4) o[i] = 255;
            return o;
        }
    }
}
