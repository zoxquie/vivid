using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>
    /// A procedurally painted outdoor scene (sky, sun, lake, hills, a tent, and a dark cave with
    /// someone hiding inside) used to preview filters. It is run through the same color math as the
    /// real screen filter, so the preview matches what you will see in game.
    /// </summary>
    public static class Scene
    {
        public const int W = 640, H = 360;
        static Bitmap baseBmp;
        static readonly Bitmap[] filtered = new Bitmap[2];
        static readonly string[] filteredKey = new string[2];
        static int filteredNext;
        static byte[] srcBuf, dstBuf;

        static Bitmap procedural;
        /// <summary>The generated scene (used for preset artwork).</summary>
        public static Bitmap Procedural => procedural ?? (procedural = Generate());

        /// <summary>The preview image: your own screenshot if you set one, otherwise the generated scene.</summary>
        public static Bitmap Base
        {
            get
            {
                if (baseBmp != null) return baseBmp;
                var shot = CardArt.Preview();
                if (shot == null) return baseBmp = Procedural;
                baseBmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(baseBmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawCover(g, shot, new RectangleF(0, 0, W, H));
                }
                return baseBmp;
            }
        }

        public static bool UsingScreenshot => Base != Procedural;

        /// <summary>Call after the preview screenshot changes.</summary>
        public static void PreviewChanged()
        {
            if (baseBmp != null && baseBmp != procedural) baseBmp.Dispose();
            baseBmp = null;
            srcBuf = dstBuf = null;
            filteredKey[0] = filteredKey[1] = null;
            ClearFits();
        }

        // ---- display-size pictures: filtered at exactly the size they're shown, with rounded corners baked in,
        // so the preview is a plain copy each frame instead of a resample.
        sealed class Fit { public byte[] Src, Mask; public int W, H; public int[] Edge; }
        static readonly Dictionary<string, Fit> fits = new Dictionary<string, Fit>();
        static readonly LinkedList<string> fitOrder = new LinkedList<string>();
        static readonly Dictionary<string, Bitmap> fitted = new Dictionary<string, Bitmap>();
        static readonly LinkedList<string> fittedOrder = new LinkedList<string>();

        /// <summary>
        /// Picture for the before/after preview: your own preview screenshot if you set one, otherwise the
        /// picture of the first profile that has one (presets come with game screenshots), otherwise the generated scene.
        /// </summary>
        public static Image PreviewPicture(params Profile[] candidates)
        {
            var own = CardArt.Preview();
            if (own != null) return own;
            foreach (var p in candidates)
            {
                if (p == null) continue;
                var a = CardArt.For(p);
                if (a != null) return a;
            }
            return Procedural;
        }

        /// <summary>True when the preview shows real pictures rather than the generated scene.</summary>
        public static bool PreviewHasPictures(Profile fallback) => PreviewPicture(fallback) != Procedural;

        static void ClearFits()
        {
            foreach (var b in fitted.Values) b.Dispose();
            fitted.Clear(); fittedOrder.Clear();
            fits.Clear(); fitOrder.Clear();
        }

        /// <summary><paramref name="src"/> at w×h pixels with <paramref name="v"/> applied (null = unfiltered).</summary>
        public static Bitmap FilteredAt(Image src, FilterValues v, int w, int h, float radius, float focusX = 0.5f)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string sk = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(src) + ":" + src.Width + "x" + src.Height + "|" + w + "x" + h + "|" + radius.ToString("F1", inv) + "|" + focusX.ToString("F2", inv);
            string k = sk + "|" + (v == null ? "raw" : string.Format(inv, "{0:F3}|{1:F3}|{2:F3}|{3:F1}|{4:F3}", v.Brightness, v.Contrast, v.Saturation, v.Hue, v.NightVision));
            if (fitted.TryGetValue(k, out var hit))
            {
                fittedOrder.Remove(k); fittedOrder.AddFirst(k);
                return hit;
            }
            var fit = GetFit(src, sk, w, h, radius, focusX);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            unsafe
            {
                byte* basePtr = (byte*)bd.Scan0;
                int stride = bd.Stride;
                // the filter writes straight into the bitmap (no extra copies)
                if (v == null)
                    for (int y = 0; y < h; y++) Marshal.Copy(fit.Src, y * w * 4, bd.Scan0 + y * stride, w * 4);
                else ApplyFast(fit.Src, basePtr, stride, w, h, v);
                // rounded corners: premultiplied, so scale every channel by the mask (only the few edge pixels)
                var mask = fit.Mask;
                if (fit.Edge == null)
                {
                    var edge = new List<int>();
                    for (int p = 0; p < mask.Length; p++) if (mask[p] != 255) edge.Add(p);
                    fit.Edge = edge.ToArray();
                }
                foreach (int p in fit.Edge)
                {
                    int m = mask[p];
                    byte* q = basePtr + (p / w) * stride + (p % w) * 4;
                    q[0] = (byte)(q[0] * m / 255); q[1] = (byte)(q[1] * m / 255); q[2] = (byte)(q[2] * m / 255); q[3] = (byte)m;
                }
            }
            bmp.UnlockBits(bd);
            fitted[k] = bmp;
            fittedOrder.AddFirst(k);
            while (fittedOrder.Count > 10) { var old = fittedOrder.Last.Value; fittedOrder.RemoveLast(); fitted[old].Dispose(); fitted.Remove(old); }
            return bmp;
        }

        static Fit GetFit(Image src, string key, int w, int h, float radius, float focusX)
        {
            if (fits.TryGetValue(key, out var f)) { fitOrder.Remove(key); fitOrder.AddFirst(key); return f; }
            f = new Fit { W = w, H = h };
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawCover(g, src, new RectangleF(0, 0, w, h), focusX);
                }
                f.Src = ReadPixels(bmp);
                for (int i = 3; i < f.Src.Length; i += 4) f.Src[i] = 255;
                // anti-aliased rounded-rectangle mask
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var path = Theme.Round(new RectangleF(0, 0, w, h), radius)) g.FillPath(Brushes.White, path);
                }
                var mb = ReadPixels(bmp);
                f.Mask = new byte[w * h];
                for (int p = 0; p < f.Mask.Length; p++) f.Mask[p] = mb[p * 4 + 3];
            }
            fits[key] = f;
            fitOrder.AddFirst(key);
            while (fitOrder.Count > 6) { fits.Remove(fitOrder.Last.Value); fitOrder.RemoveLast(); }
            return f;
        }

        static byte[] ReadPixels(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var buf = new byte[w * h * 4];
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, buf, y * w * 4, w * 4);
            bmp.UnlockBits(d);
            return buf;
        }

        /// <summary>Same math as <see cref="Apply"/>, with lookup tables (fixed point) so it runs a few times faster.</summary>
        static unsafe void ApplyFast(byte[] s, byte* dBase, int stride, int w, int h, FilterValues v)
        {
            var a = new double[3, 3];
            var off = new double[3];
            ColorMath.BuildAffine(v, a, off);
            const double F = 1023.0 * 64; // 10-bit output with 6 fraction bits
            var t = new int[9][];
            for (int j = 0; j < 9; j++)
            {
                t[j] = new int[256];
                double c = a[j / 3, j % 3];
                for (int i = 0; i < 256; i++) t[j][i] = (int)Math.Round(c * i / 255.0 * F);
            }
            int o0 = (int)Math.Round(off[0] * F), o1 = (int)Math.Round(off[1] * F), o2 = (int)Math.Round(off[2] * F);
            var lut = new byte[1024];
            for (int i = 0; i < 1024; i++) lut[i] = (byte)Math.Round(ColorMath.NightCurve(i / 1023.0, v.NightVision) * 255);
            int[] t00 = t[0], t01 = t[1], t02 = t[2], t10 = t[3], t11 = t[4], t12 = t[5], t20 = t[6], t21 = t[7], t22 = t[8];
            // split big pictures across cores (by rows) so dragging a slider over a large preview stays smooth
            int parts = w * h < 60000 ? 1 : Math.Min(Environment.ProcessorCount, 8);
            int per = (h + parts - 1) / parts;
            long baseAddr = (long)dBase;
            System.Threading.Tasks.Parallel.For(0, parts, part =>
            {
                byte* dRow0 = (byte*)baseAddr;
                int y1 = Math.Min(h, (part + 1) * per);
                for (int y = part * per; y < y1; y++)
                {
                    byte* d = dRow0 + y * stride;
                    for (int x = 0, i = y * w * 4; x < w; x++, i += 4, d += 4)
                    {
                        int b = s[i], g = s[i + 1], r = s[i + 2];
                        int ro = (t00[r] + t01[g] + t02[b] + o0 + 32) >> 6;
                        int go = (t10[r] + t11[g] + t12[b] + o1 + 32) >> 6;
                        int bo = (t20[r] + t21[g] + t22[b] + o2 + 32) >> 6;
                        d[0] = lut[bo < 0 ? 0 : bo > 1023 ? 1023 : bo];
                        d[1] = lut[go < 0 ? 0 : go > 1023 ? 1023 : go];
                        d[2] = lut[ro < 0 ? 0 : ro > 1023 ? 1023 : ro];
                        d[3] = 255;
                    }
                }
            });
        }

        static readonly Dictionary<string, Bitmap> biomes = new Dictionary<string, Bitmap>();

        /// <summary>
        /// The same scene repainted for a biome (grass, winter, desert, night vision, vivid),
        /// used as artwork on the preset cards.
        /// </summary>
        public static Bitmap Biome(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            if (biomes.TryGetValue(kind, out var b)) return b;
            var src = Procedural;
            var rect = new Rectangle(0, 0, W, H);
            var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[sd.Stride * H];
            Marshal.Copy(sd.Scan0, buf, 0, buf.Length);
            src.UnlockBits(sd);
            int stride = W * 4;
            for (int i = 0; i < buf.Length; i += 4)
            {
                float bl = buf[i] / 255f, g = buf[i + 1] / 255f, r = buf[i + 2] / 255f;
                float lum = 0.3f * r + 0.59f * g + 0.11f * bl;
                bool green = g > r * 1.06f && g > bl * 1.04f;
                bool sky = bl > r * 1.08f && bl >= g && (i / stride) < H * 0.62f;
                switch (kind)
                {
                    case "summer":
                        { float k = 1.18f; float m = lum; r = m + (r - m) * k; g = m + (g - m) * k; bl = m + (bl - m) * k; r *= 1.03f; }
                        break;
                    case "winter":
                        if (green) { float v = Math.Min(1f, 0.38f + lum * 1.55f); r = v * 0.86f; g = v * 0.92f; bl = v; }
                        else if (sky) { r = r * 0.6f + 0.35f * 0.8f; g = g * 0.6f + 0.36f * 0.85f; bl = bl * 0.6f + 0.4f * 0.95f; }
                        break;
                    case "desert":
                        if (green) { float v = 0.42f + lum * 1.25f; r = v * 0.92f; g = v * 0.70f; bl = v * 0.44f; }
                        else if (sky) { r = Math.Min(1, r * 1.12f + 0.06f); g = Math.Min(1, g * 1.0f + 0.03f); bl *= 0.86f; }
                        break;
                    case "night":
                        {
                            float l = (float)Math.Pow(Math.Max(0, lum), 0.55) * 0.95f;
                            r = l * 0.30f; g = l; bl = l * 0.55f;
                            if ((i / stride) % 3 == 0) { r *= 0.82f; g *= 0.82f; bl *= 0.82f; }
                        }
                        break;
                    case "vivid":
                        { float k = 1.9f; float m = lum; r = m + (r - m) * k; g = m + (g - m) * k; bl = m + (bl - m) * k; }
                        break;
                }
                buf[i] = (byte)(Math.Max(0, Math.Min(1, bl)) * 255);
                buf[i + 1] = (byte)(Math.Max(0, Math.Min(1, g)) * 255);
                buf[i + 2] = (byte)(Math.Max(0, Math.Min(1, r)) * 255);
            }
            b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var dd = b.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buf, 0, dd.Scan0, buf.Length);
            b.UnlockBits(dd);
            biomes[kind] = b;
            return b;
        }

        public static Bitmap Filtered(FilterValues v)
        {
            string k = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3}|{1:F3}|{2:F3}|{3:F1}|{4:F3}",
                v.Brightness, v.Contrast, v.Saturation, v.Hue, v.NightVision);
            for (int i = 0; i < 2; i++) if (filtered[i] != null && filteredKey[i] == k) { filteredNext = 1 - i; return filtered[i]; }
            int slot = filteredNext;
            filteredNext = 1 - slot;
            if (filtered[slot] == null) filtered[slot] = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            Apply(Base, v, filtered[slot]);
            filteredKey[slot] = k;
            return filtered[slot];
        }

        static void Apply(Bitmap src, FilterValues v, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, W, H);
            if (srcBuf == null)
            {
                var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                srcBuf = new byte[sd.Stride * H];
                Marshal.Copy(sd.Scan0, srcBuf, 0, srcBuf.Length);
                src.UnlockBits(sd);
                dstBuf = new byte[srcBuf.Length];
            }
            var a = new double[3, 3];
            var off = new double[3];
            ColorMath.BuildAffine(v, a, off);
            float a00 = (float)a[0, 0], a01 = (float)a[0, 1], a02 = (float)a[0, 2];
            float a10 = (float)a[1, 0], a11 = (float)a[1, 1], a12 = (float)a[1, 2];
            float a20 = (float)a[2, 0], a21 = (float)a[2, 1], a22 = (float)a[2, 2];
            float o0 = (float)off[0], o1 = (float)off[1], o2 = (float)off[2];
            var lut = new byte[1024];
            for (int i = 0; i < 1024; i++) lut[i] = (byte)Math.Round(ColorMath.NightCurve(i / 1023.0, v.NightVision) * 255);
            const float inv = 1f / 255f;
            var s = srcBuf; var d = dstBuf;
            for (int i = 0; i < s.Length; i += 4)
            {
                float b = s[i] * inv, g = s[i + 1] * inv, r = s[i + 2] * inv;
                float ro = a00 * r + a01 * g + a02 * b + o0;
                float go = a10 * r + a11 * g + a12 * b + o1;
                float bo = a20 * r + a21 * g + a22 * b + o2;
                d[i] = lut[Idx(bo)];
                d[i + 1] = lut[Idx(go)];
                d[i + 2] = lut[Idx(ro)];
                d[i + 3] = s[i + 3];
            }
            var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(dstBuf, 0, dd.Scan0, dstBuf.Length);
            dst.UnlockBits(dd);
        }

        static int Idx(float x) => x <= 0 ? 0 : x >= 1 ? 1023 : (int)(x * 1023f + 0.5f);

        // ------------------------------------------------------------------ painting

        static Color C(string h) => Theme.Hex(h);
        static Color C(string h, int a) => Theme.WithAlpha(Theme.Hex(h), a);

        static Bitmap Generate()
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var rnd = new Random(42);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Sky
                var sky = new RectangleF(0, 0, W, H * 0.70f);
                using (var b = new LinearGradientBrush(new RectangleF(0, -1, W, sky.Height + 2), Color.Black, Color.Black, 90f))
                {
                    b.InterpolationColors = new ColorBlend(5)
                    {
                        Colors = new[] { C("#1A3E73"), C("#3C74B2"), C("#7FB0DA"), C("#D3E0E8"), C("#F6CB92") },
                        Positions = new[] { 0f, 0.35f, 0.66f, 0.86f, 1f },
                    };
                    g.FillRectangle(b, 0, 0, W, sky.Height + 1);
                }

                // Sun and haze
                var sun = new PointF(196, 150);
                Theme.RadialGlow(g, sun, 300, 170, C("#FFD7A0"), 150);
                Theme.RadialGlow(g, sun, 70, 60, C("#FFF1D2"), 220);
                using (var b = new SolidBrush(C("#FFF8E8"))) g.FillEllipse(b, sun.X - 16, sun.Y - 16, 32, 32);

                // Soft cloud banks (stacked translucent ellipses)
                using (var cloud = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                    foreach (var (cx, cy, cw) in new[] { (110f, 66f, 150f), (440f, 48f, 190f), (560f, 96f, 120f), (300f, 104f, 110f) })
                        for (int k = 0; k < 7; k++)
                        {
                            float w = cw * (0.45f + 0.09f * k), hgt = 10 + 2.2f * k;
                            g.FillEllipse(cloud, cx - w / 2 + (k % 3 - 1) * 12, cy - hgt / 2 + (k % 2) * 3, w, hgt);
                        }

                // Far mountains with haze
                Ridge(g, 214, 58, 0.016, 0.9, 3, rnd, Gradient(150, 240, C("#4E6788"), C("#93A9C2")), jag: 9);
                Ridge(g, 236, 30, 0.022, 2.4, 3, rnd, Gradient(200, 250, C("#2E4862"), C("#5B7690")), jag: 5);

                // Lake with sun reflection
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(30, 238, 470, 26);
                    using (var b = new LinearGradientBrush(new RectangleF(0, 237, W, 28), C("#C2D5E0"), C("#4F7898"), 90f)) g.FillPath(b, path);
                }
                for (int i = 0; i < 9; i++)
                    using (var b = new SolidBrush(C("#FFE5B5", 150 - i * 12)))
                        g.FillEllipse(b, sun.X - 18 + i * 1.5f, 242 + i * 2.4f, 36 - i * 3, 2.2f);

                // Grass hills
                Ridge(g, 266, 18, 0.012, 4.0, 2, rnd, Gradient(240, 300, C("#5E9A3E"), C("#2C5A24")), jag: 2);

                // Pines
                foreach (var (x, h) in new[] { (300f, 46f), (318f, 34f), (334f, 52f), (352f, 38f), (372f, 30f), (40f, 34f), (58f, 44f), (76f, 30f) })
                    Pine(g, x, 268 + (x % 7), h);

                // Tent, tarp and barrel (colour checks for saturation and hue)
                g.FillPolygon(new SolidBrush(C("#D9392B")), new[] { new PointF(236, 304), new PointF(268, 268), new PointF(300, 304) });
                g.FillPolygon(new SolidBrush(C("#A3271E")), new[] { new PointF(268, 268), new PointF(300, 304), new PointF(272, 304) });
                g.FillPolygon(new SolidBrush(C("#3B0D0A")), new[] { new PointF(262, 304), new PointF(268, 286), new PointF(274, 304) });
                using (var b = new SolidBrush(C("#2F6FD6"))) g.FillRectangle(b, 306, 292, 30, 12);
                using (var b = new SolidBrush(C("#1F4FA0"))) g.FillRectangle(b, 306, 300, 30, 4);
                using (var b = new SolidBrush(C("#F0B92E"))) g.FillRectangle(b, 340, 287, 11, 17);
                using (var b = new SolidBrush(C("#B78714"))) g.FillRectangle(b, 340, 292, 11, 2);

                // Foreground meadow
                using (var path = new GraphicsPath())
                {
                    var pts = new List<PointF>();
                    for (int x = 0; x <= W; x += 16) pts.Add(new PointF(x, 300 + 6 * (float)Math.Sin(x * 0.018 + 1.3)));
                    pts.Add(new PointF(W, H)); pts.Add(new PointF(0, H));
                    path.AddPolygon(pts.ToArray());
                    using (var b = new LinearGradientBrush(new RectangleF(0, 290, W, 72), C("#3E6A2C"), C("#14240F"), 90f)) g.FillPath(b, path);
                }
                for (int i = 0; i < 1400; i++)
                {
                    float x = (float)rnd.NextDouble() * W, y = 302 + (float)rnd.NextDouble() * 60;
                    float len = 3 + (float)rnd.NextDouble() * 7 * (y - 290) / 70f;
                    int shade = rnd.Next(3);
                    var col = shade == 0 ? C("#6FA84A", 150) : shade == 1 ? C("#25461C", 170) : C("#4F8436", 140);
                    using (var p = new Pen(col, 1.1f)) g.DrawLine(p, x, y, x + (float)(rnd.NextDouble() - 0.5) * 3, y - len);
                }
                for (int i = 0; i < 46; i++)
                {
                    float x = 150 + (float)rnd.NextDouble() * 170, y = 320 + (float)rnd.NextDouble() * 36;
                    var col = rnd.Next(3) == 0 ? C("#F4F1E6") : C("#F2C94C");
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, x, y, 3.2f, 3.2f);
                }

                // Rock and cave on the right, with someone hiding in the dark
                var rock = new[]
                {
                    new PointF(640, 92), new PointF(606, 98), new PointF(560, 120), new PointF(520, 132), new PointF(486, 170),
                    new PointF(452, 214), new PointF(430, 262), new PointF(420, 318), new PointF(430, 360), new PointF(640, 360),
                };
                using (var b = new LinearGradientBrush(new RectangleF(420, 90, 220, 272), C("#232B1E"), C("#0C100A"), 70f)) g.FillPolygon(b, rock);
                using (var p = new Pen(C("#3C4A31", 200), 2f)) g.DrawLines(p, new[] { rock[1], rock[2], rock[3], rock[4], rock[5], rock[6] });
                for (int i = 0; i < 14; i++)
                    using (var p = new Pen(C("#2A3323", 140), 1.2f))
                    {
                        float x = 470 + (float)rnd.NextDouble() * 160, y = 150 + (float)rnd.NextDouble() * 120;
                        g.DrawLine(p, x, y, x + 10 + (float)rnd.NextDouble() * 20, y + 4);
                    }
                using (var cave = new GraphicsPath())
                {
                    cave.AddEllipse(470, 236, 150, 150);
                    using (var b = new PathGradientBrush(cave) { CenterColor = C("#0D100B"), SurroundColors = new[] { C("#050604") } }) g.FillPath(b, cave);
                }
                using (var b = new SolidBrush(C("#17140D"))) g.FillRectangle(b, 496, 316, 34, 28);
                using (var p = new Pen(C("#1E1A11"), 1.5f)) { g.DrawLine(p, 496, 330, 530, 330); g.DrawLine(p, 513, 316, 513, 344); }
                using (var b = new SolidBrush(C("#1C211A")))
                {
                    g.FillEllipse(b, 563, 270, 14, 15);                       // head
                    using (var body = Theme.Round(new RectangleF(558, 286, 24, 40), 7)) g.FillPath(b, body);
                    g.FillRectangle(b, 561, 322, 7, 22); g.FillRectangle(b, 572, 322, 7, 22);
                    g.FillRectangle(b, 580, 294, 22, 5);                       // rifle
                }

                // Vignette
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(-W * 0.25f, -H * 0.35f, W * 1.5f, H * 1.7f);
                    using (var region = new Region(new RectangleF(0, 0, W, H)))
                    using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(110, 0, 0, 0) } })
                    {
                        b.Blend = new Blend { Factors = new[] { 0f, 0.6f, 1f, 1f }, Positions = new[] { 0f, 0.25f, 0.55f, 1f } };
                        g.FillRectangle(b, 0, 0, W, H);
                    }
                }
            }
            AddGrain(bmp, rnd);
            return bmp;
        }

        static Brush Gradient(float y1, float y2, Color a, Color b) =>
            new LinearGradientBrush(new RectangleF(0, y1 - 1, W, y2 - y1 + 2), a, b, 90f);

        static void Ridge(Graphics g, float baseY, float amp, double freq, double phase, int octaves, Random rnd, Brush brush, float jag)
        {
            var pts = new List<PointF>();
            for (int x = -8; x <= W + 8; x += 6)
            {
                double y = 0, a = 1, f = freq;
                for (int o = 0; o < octaves; o++) { y += a * Math.Sin(x * f + phase * (o + 1)); a *= 0.45; f *= 2.3; }
                pts.Add(new PointF(x, baseY - amp * (float)(y * 0.5 + 0.5) + (float)(rnd.NextDouble() - 0.5) * jag));
            }
            pts.Add(new PointF(W + 8, H)); pts.Add(new PointF(-8, H));
            g.FillPolygon(brush, pts.ToArray());
            brush.Dispose();
        }

        static void Pine(Graphics g, float x, float baseY, float h)
        {
            using (var trunk = new SolidBrush(C("#20160F"))) g.FillRectangle(trunk, x - 1.5f, baseY - h * 0.15f, 3, h * 0.15f + 2);
            for (int i = 0; i < 3; i++)
            {
                float w = h * (0.42f - i * 0.1f), top = baseY - h * (0.55f + i * 0.22f), bot = baseY - h * (0.1f + i * 0.22f);
                using (var b = new SolidBrush(i % 2 == 0 ? C("#1C3A22") : C("#244A2A")))
                    g.FillPolygon(b, new[] { new PointF(x, top - h * 0.18f), new PointF(x - w, bot), new PointF(x + w, bot) });
            }
        }

        static void AddGrain(Bitmap bmp, Random rnd)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var d = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var buf = new byte[d.Stride * bmp.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            for (int i = 0; i < buf.Length; i += 4)
            {
                int n = rnd.Next(-4, 5);
                for (int c = 0; c < 3; c++) buf[i + c] = (byte)Math.Max(0, Math.Min(255, buf[i + c] + n));
                buf[i + 3] = 255;
            }
            Marshal.Copy(buf, 0, d.Scan0, buf.Length);
            bmp.UnlockBits(d);
        }

        /// <summary>Draws a bitmap to fill a rectangle, cropping the overflow (like CSS object-fit: cover).</summary>
        public static void DrawCover(Graphics g, Image img, RectangleF dest, float focusX = 0.5f, float focusY = 0.5f, float zoom = 1f)
        {
            float sa = img.Width / (float)img.Height, da = dest.Width / dest.Height;
            RectangleF src;
            if (zoom > 1f)
            {
                // crop a smaller window of the image (closer view), keeping the destination aspect
                float w = img.Width / zoom, h = w / da;
                if (h > img.Height) { h = img.Height; w = h * da; }
                src = new RectangleF((img.Width - w) * focusX, (img.Height - h) * focusY, w, h);
            }
            else if (da > sa) { float h = img.Width / da; src = new RectangleF(0, (img.Height - h) * focusY, img.Width, h); }
            else { float w = img.Height * da; src = new RectangleF((img.Width - w) * focusX, 0, w, img.Height); }
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, new[] { dest.Location, new PointF(dest.Right, dest.Y), new PointF(dest.X, dest.Bottom) }, src, GraphicsUnit.Pixel, ia);
            }
        }
    }

    /// <summary>Before/after comparison with a draggable divider.</summary>
    public sealed class LivePreview : Widget
    {
        public float Split = 0.5f;
        bool dragging;
        /// <summary>Big main-page look: the picture fills the card and the label sits on it.</summary>
        public bool Hero;
        static AppController Ctl => Ui.Form.Ctl;

        public override bool Interactive => true;
        public override Cursor Cursor => Cursors.SizeWE;
        public override string Tooltip => dragging ? null : "Before: the picture with no filter. After: with the preset or changes you're looking at. Drag to compare, right-click to use your own screenshot.";

        public RectangleF ImageRect
        {
            get
            {
                if (Hero) { float hp = 8 * S; return RectangleF.Inflate(Bounds, -hp, -hp); }
                float pad = 12 * S, top = 50 * S;
                return new RectangleF(Bounds.X + pad, Bounds.Y + top, Bounds.Width - 2 * pad, Bounds.Height - top - pad);
            }
        }

        public static float HeightFor(float width) => 50 * Theme.Scale + (width - 24 * Theme.Scale) * 9f / 16f + 12 * Theme.Scale;

        public override void MouseDown(PointF p, MouseButtons b) { if (b == MouseButtons.Left) { dragging = true; Move(p); } }
        public override void MouseDrag(PointF p) { if (dragging) Move(p); }
        public override void MouseUp(PointF p, bool inside, MouseButtons b) { dragging = false; }
        public override void DoubleClick(PointF p) { Split = 0.5f; Ui.Invalidate(); }
        public override void RightClick(PointF p) => Ui.Form.PreviewMenu(p);

        void Move(PointF p)
        {
            var r = ImageRect;
            Split = Math.Max(0.02f, Math.Min(0.98f, (p.X - r.X) / r.Width));
            Ui.Invalidate();
        }

        /// <summary>Right half: the hovered profile, otherwise the values being edited.</summary>
        static (FilterValues values, string label, bool hovering, Profile profile) Source()
        {
            ProfileCard pc = Ui.Hover as ProfileCard ?? Ui.Hover?.Parent as ProfileCard;
            if (pc != null && pc.P.Id != Ctl.EditSelected.Id) return (Ctl.ValuesOf(pc.P), pc.P.Name, true, pc.P);
            return (Ctl.CurrentValues, Ctl.EditName, false, Ctl.EditSelected);
        }

        /// <summary>The picture to compare on: the shown preset's own screenshot, else the one it came from, else what's on screen, else the forest.</summary>
        static Image Picture(Profile shown) =>
            Scene.PreviewPicture(shown, Ctl.EditOrigin, Ctl.Selected, Ctl.Find("builtin.summer"));

        static FilterValues AsShown(FilterValues v)
        {
            if (Ctl.Settings.Engine != EngineMode.GammaRamp) return v;
            v = v.Clone();
            v.Hue = 0; // gamma mode can't rotate hue
            if (!Ctl.Settings.UseNvVibrance) v.Saturation = 1;
            return v;
        }

        public override void Paint(Graphics g)
        {
            var r = Bounds;
            Theme.Card(g, r, 18);
            var (vals, label, hovering, shown) = Source();
            vals = AsShown(vals);
            bool editing = Ctl.Editing;
            var picture = Picture(shown);

            var cf = Theme.F(12, semibold: true);
            string chip = hovering ? "Hovering " + label : label;
            Color chipCol = hovering ? Theme.Accent : editing ? Theme.Amber : Theme.White(255);
            bool lit = hovering || editing;
            if (!Hero)
            {
                Theme.Txt(g, "Preview", Theme.F(15, semibold: true), Theme.Text, new RectangleF(r.X + 18 * S, r.Y + 12 * S, r.Width / 2, 28 * S));
                float cw = Math.Min(r.Width * 0.66f, Theme.Measure(chip, cf).Width + 24 * S);
                var cr = new RectangleF(r.Right - 14 * S - cw, r.Y + 13 * S, cw, 26 * S);
                Theme.FillRound(g, cr, 13 * S, lit ? Theme.WithAlpha(chipCol, 50) : Theme.White(10));
                Theme.StrokeRound(g, cr, 13 * S, lit ? Theme.WithAlpha(chipCol, 170) : Theme.White(26), Math.Max(1, S));
                Theme.Txt(g, chip, cf, lit ? Theme.Text : Theme.TextMuted, RectangleF.Inflate(cr, -10 * S, 0), Theme.H.Center);
            }

            var imgF = ImageRect;
            float rad = (Hero ? 13 : 12) * S;
            {
                // both halves are pre-made at the exact on-screen size, so this is two straight copies
                var img = new RectangleF((float)Math.Round(imgF.X), (float)Math.Round(imgF.Y), (float)Math.Round(imgF.Width), (float)Math.Round(imgF.Height));
                int ix = (int)img.X, iy = (int)img.Y, iw = (int)img.Width, ih = (int)img.Height;
                var leftBmp = Scene.FilteredAt(picture, null, iw, ih, rad);   // before: no filter
                var rightBmp = Scene.FilteredAt(picture, vals, iw, ih, rad); // after: what you're looking at
                int cut = Math.Max(0, Math.Min(iw, (int)Math.Round(iw * Split)));
                float sx = img.X + cut;
                var im = g.InterpolationMode; var pm = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (cut > 0) g.DrawImage(leftBmp, new Rectangle(ix, iy, cut, ih), 0, 0, cut, ih, GraphicsUnit.Pixel);
                if (cut < iw) g.DrawImage(rightBmp, new Rectangle(ix + cut, iy, iw - cut, ih), cut, 0, iw - cut, ih, GraphicsUnit.Pixel);
                g.InterpolationMode = im; g.PixelOffsetMode = pm;

                // divider
                Theme.Glow(g, new RectangleF(sx - 1 * S, img.Y, 2 * S, img.Height), 1 * S, Color.White, 6 * S, 30);
                using (var p = new Pen(Color.White, 2 * S)) g.DrawLine(p, sx, img.Y, sx, img.Bottom);
                float hd = 30 * S;
                var hr = new RectangleF(sx - hd / 2, img.Y + img.Height / 2 - hd / 2, hd, hd);
                Theme.Glow(g, hr, hd / 2, Theme.Black(255), 8 * S, 60);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, hr);
                using (var p = new Pen(Theme.Bg, 2f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = hr.X + hd / 2, cy = hr.Y + hd / 2, a = 4 * S;
                    g.DrawLines(p, new[] { new PointF(cx - 3 * S, cy - a), new PointF(cx - 3 * S - a, cy), new PointF(cx - 3 * S, cy + a) });
                    g.DrawLines(p, new[] { new PointF(cx + 3 * S, cy - a), new PointF(cx + 3 * S + a, cy), new PointF(cx + 3 * S, cy + a) });
                }

                Badge(g, "BEFORE", new PointF(img.X + 12 * S, img.Bottom - 12 * S), left: true, show: Split > 0.2f, accent: Color.Empty);
                Badge(g, "AFTER", new PointF(img.Right - 12 * S, img.Bottom - 12 * S), left: false, show: Split < 0.8f, accent: editing && !hovering ? Theme.Amber : Theme.Accent);
                if (Hero)
                {
                    // label on the picture: a dark glass pill with a status dot
                    var hf = Theme.F(12.5f, semibold: true);
                    float tw = Math.Min(img.Width * 0.6f, Theme.Measure(chip, hf).Width);
                    var pill = new RectangleF(img.X + 12 * S, img.Y + 12 * S, tw + 38 * S, 30 * S);
                    Theme.FillRound(g, pill, 15 * S, Theme.Black(150));
                    Theme.StrokeRound(g, pill, 15 * S, lit ? Theme.WithAlpha(chipCol, 150) : Theme.White(40), Math.Max(1, S));
                    float dd = 7 * S;
                    var dotR = new RectangleF(pill.X + 13 * S, pill.Y + (pill.Height - dd) / 2, dd, dd);
                    using (var b = new SolidBrush(lit ? chipCol : Theme.Accent)) g.FillEllipse(b, dotR);
                    Theme.Txt(g, chip, hf, Theme.White(240), new RectangleF(dotR.Right + 8 * S, pill.Y, tw + 4 * S, pill.Height));
                }
                Theme.StrokeRound(g, img, rad, Theme.White(26), Math.Max(1, S));
            }
        }

        void Badge(Graphics g, string text, PointF anchor, bool left, bool show, Color accent)
        {
            if (!show) return;
            var f = Theme.F(10.5f, semibold: true);
            var ts = Theme.Measure(text, f);
            var r = new RectangleF(left ? anchor.X : anchor.X - ts.Width - 16 * S, anchor.Y - 22 * S, ts.Width + 16 * S, 22 * S);
            Theme.FillRound(g, r, 11 * S, Theme.Black(150));
            Theme.StrokeRound(g, r, 11 * S, accent.IsEmpty ? Theme.White(40) : Theme.WithAlpha(accent, 170), Math.Max(1, S));
            Theme.Txt(g, text, f, accent.IsEmpty ? Theme.White(230) : Theme.Mix(accent, Color.White, 0.55f), r, Theme.H.Center);
        }
    }
}
