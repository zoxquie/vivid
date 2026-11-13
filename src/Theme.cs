using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Vivid
{
    /// <summary>Colors, fonts and drawing helpers for the custom-drawn "neon glass" UI.</summary>
    public static class Theme
    {
        public static float Scale = 1f;

        // Base (matches the fjord.wtf palette)
        public static readonly Color Bg = Hex("#070F17");
        public static readonly Color Surface = Color.FromArgb(232, 13, 27, 39);   // glass card #0D1B27
        public static readonly Color Surface2 = Hex("#122230");                    // inputs, inner tiles
        public static readonly Color Surface3 = Hex("#1A2D3B");
        public static readonly Color Border = Hex("#263E4B");
        public static readonly Color BorderStrong = Hex("#335363");
        public static readonly Color Text = Hex("#F1F9F7");
        public static readonly Color TextMuted = Hex("#A8BCC6");
        public static readonly Color TextDim = Hex("#5F7885");

        // Accents
        public static readonly Color Accent = Hex("#65E4C4");      // mint
        public static readonly Color AccentLight = Hex("#B0FFE0");
        public static readonly Color AccentEnd = Hex("#38BDBB");   // teal
        public static readonly Color AccentMid = Hex("#4FD3C0");
        public static readonly Color AccentHover = Hex("#78E9D0");
        public static readonly Color AccentSoft = Hex("#0F2E2C");
        public static readonly Color OnAccent = Hex("#08221C");    // text on mint buttons
        public static readonly Color Cyan = Hex("#207C87");        // deep teal glow
        public static readonly Color Green = Hex("#00D294");
        public static readonly Color GreenDeep = Hex("#00A877");
        public static readonly Color GreenSoft = Hex("#0B2A22");
        public static readonly Color Amber = Hex("#FFC14D");
        public static readonly Color Blue = Hex("#5CC8FF");
        public static readonly Color Red = Hex("#FF6568");
        public static readonly Color RedSoft = Hex("#3A161B");

        public static string RegularFamily = "Segoe UI";
        public static string SemiboldFamily = "Segoe UI Semibold";
        public static string BoldFamily = "Segoe UI";

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static void Init(float scale)
        {
            Scale = scale;
            RenderCache.Clear();
            measured.Clear();
            foreach (var f in fonts.Values) f.Dispose();
            fonts.Clear();
            if (!FontExists(SemiboldFamily)) SemiboldFamily = RegularFamily;
        }

        static bool FontExists(string family)
        {
            try { using (var f = new Font(family, 10)) return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Font sized in logical pixels (scaled by DPI).</summary>
        public static Font F(float px, bool semibold = false, bool bold = false)
        {
            string key = px + (semibold ? "s" : "") + (bold ? "b" : "");
            if (!fonts.TryGetValue(key, out var f))
            {
                f = new Font(semibold ? SemiboldFamily : RegularFamily, px * Scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static Color Hex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(255, Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color WithAlpha(Color c, int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), c);
        public static Color White(int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), 255, 255, 255);
        public static Color Black(int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), 0, 0, 0);

        // ------------------------------------------------------------------ shapes

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color c)
        {
            if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Brush b)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var p = Round(r, radius)) g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color c, float width = 1f)
        {
            if (c.A == 0) return;
            var rr = new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width);
            using (var p = Round(rr, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Brush b, float width = 1f)
        {
            var rr = new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width);
            using (var p = Round(rr, radius)) using (var pen = new Pen(b, width)) g.DrawPath(pen, p);
        }

        public static LinearGradientBrush Gradient(RectangleF r, Color a, Color b, float angle = 0f)
        {
            var rr = new RectangleF(r.X - 1, r.Y - 1, Math.Max(2, r.Width + 2), Math.Max(2, r.Height + 2));
            return new LinearGradientBrush(rr, a, b, angle);
        }

        public static LinearGradientBrush AccentBrush(RectangleF r, int alpha = 255) =>
            Gradient(r, WithAlpha(AccentLight, alpha), WithAlpha(AccentEnd, alpha), 20f);

        /// <summary>Soft outer glow made of stacked translucent rounded rectangles.</summary>
        public static void Glow(Graphics g, RectangleF r, float radius, Color c, float spread, int alpha)
        {
            if (alpha <= 0 || spread <= 0.5f) return;
            alpha = Math.Min(255, (alpha + 2) / 4 * 4); // a few steps are enough for a soft glow, and keep the cache small
            if (alpha <= 0) return;
            var area = RectangleF.Inflate(r, spread + 1, spread + 1);
            RenderCache.Draw(g, RenderCache.Key("glow", r.Width, r.Height, radius, c, spread, alpha), area, gg => GlowDirect(gg, r, radius, c, spread, alpha), snap: true);
        }

        static void GlowDirect(Graphics g, RectangleF r, float radius, Color c, float spread, int alpha)
        {
            const int n = 8;
            int a = Math.Max(1, (int)(alpha * 1.6f / n));
            using (var b = new SolidBrush(WithAlpha(c, a)))
                for (int i = n; i >= 1; i--)
                {
                    float t = i / (float)n;
                    float s = spread * t * t;
                    using (var p = Round(RectangleF.Inflate(r, s, s), radius + s)) g.FillPath(b, p);
                }
        }

        /// <summary>Soft round light, used for ambient background and icon tiles.</summary>
        public static void RadialGlow(Graphics g, PointF c, float rx, float ry, Color col, int alpha)
        {
            if (rx < 1 || ry < 1 || alpha <= 0) return;
            alpha = Math.Min(255, (alpha + 1) / 3 * 3);
            if (alpha <= 0) return;
            var area = new RectangleF(c.X - rx - 1, c.Y - ry - 1, rx * 2 + 2, ry * 2 + 2);
            RenderCache.Draw(g, RenderCache.Key("rglow", rx, ry, col, alpha), area, gg => RadialGlowDirect(gg, c, rx, ry, col, alpha), snap: true);
        }

        static void RadialGlowDirect(Graphics g, PointF c, float rx, float ry, Color col, int alpha)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(c.X - rx, c.Y - ry, rx * 2, ry * 2);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterPoint = c;
                    b.CenterColor = WithAlpha(col, alpha);
                    b.SurroundColors = new[] { WithAlpha(col, 0) };
                    b.Blend = new Blend { Factors = new[] { 0f, 0.18f, 0.5f, 0.82f, 1f }, Positions = new[] { 0f, 0.3f, 0.6f, 0.85f, 1f } };
                    g.FillPath(b, path);
                }
            }
        }

        /// <summary>Frosted glass card: translucent fill, top sheen, gradient hairline border.</summary>
        public static void Card(Graphics g, RectangleF r, float radius = 18, Color? fill = null, Color? border = null)
        {
            RenderCache.Draw(g, RenderCache.Key("card", r.Width, r.Height, radius, fill ?? Color.Empty, border ?? Color.Empty), RectangleF.Inflate(r, 1, 1),
                gg => CardDirect(gg, r, radius, fill, border));
        }

        static void CardDirect(Graphics g, RectangleF r, float radius, Color? fill, Color? border)
        {
            float rad = radius * Scale;
            using (var path = Round(r, rad))
            {
                using (var b = new SolidBrush(fill ?? Surface)) g.FillPath(b, path);
                using (var sheen = Gradient(r, White(14), White(0), 90f))
                {
                    var cb = new ColorBlend(3) { Colors = new[] { White(13), White(0), White(0) }, Positions = new[] { 0f, 0.45f, 1f } };
                    sheen.InterpolationColors = cb;
                    g.FillPath(sheen, path);
                }
            }
            float w = Math.Max(1f, Scale);
            using (var lg = Gradient(r, border ?? White(40), White(9), 90f))
                StrokeRound(g, r, rad, lg, w);
        }

        public static void Divider(Graphics g, float x1, float x2, float y, int alpha = 20)
        {
            if (x2 - x1 < 2) return;
            var r = new RectangleF(x1, y - 1, x2 - x1, 2);
            using (var lg = Gradient(r, White(0), White(0), 0f))
            {
                lg.InterpolationColors = new ColorBlend(4)
                {
                    Colors = new[] { White(0), White(alpha), White(alpha), White(0) },
                    Positions = new[] { 0f, 0.12f, 0.88f, 1f },
                };
                using (var p = new Pen(lg, Math.Max(1f, Scale))) g.DrawLine(p, x1, y, x2, y);
            }
        }

        /// <summary>Keyboard keycap chip.</summary>
        public static void KeyCap(Graphics g, RectangleF r, string text, Font f, bool lit = false)
        {
            float rad = 6 * Scale, depth = 2.5f * Scale;
            FillRound(g, new RectangleF(r.X, r.Y + depth, r.Width, r.Height - depth), rad, Hex("#03080C"));
            var top = new RectangleF(r.X, r.Y, r.Width, r.Height - depth);
            using (var b = Gradient(top, lit ? Hex("#1E4A47") : Hex("#22394A"), lit ? Hex("#123331") : Hex("#152633"), 90f)) FillRound(g, top, rad, b);
            StrokeRound(g, top, rad, lit ? WithAlpha(Accent, 160) : White(34), Math.Max(1f, Scale));
            Txt(g, text, f, Text, top, H.Center);
        }

        /// <summary>Large title filled with a gradient.</summary>
        public static void GradientTitle(Graphics g, string text, float px, PointF at, Color a, Color b)
        {
            using (var fam = new FontFamily(SemiboldFamily))
            using (var path = new GraphicsPath())
            {
                path.AddString(text, fam, (int)FontStyle.Regular, px * Scale, at, StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                if (bounds.Width < 1) return;
                using (var br = Gradient(bounds, a, b, 0f)) g.FillPath(br, path);
            }
        }

        // ------------------------------------------------------------------ text

        static readonly StringFormat sfBase = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        public enum H { Left, Center, Right }

        public static void Txt(Graphics g, string s, Font f, Color c, RectangleF r, H h = H.Left, bool vcenter = true, bool wrap = false)
        {
            if (string.IsNullOrEmpty(s) || c.A == 0) return;
            if (r.Width < 1 || r.Height < 1) return;
            // text is drawn once into a small bitmap and reused while it doesn't change
            RenderCache.Draw(g, RenderCache.Key("txt", s, f.Name, f.Size, (int)f.Style, c, r.Width, r.Height, (int)h, vcenter, wrap), RectangleF.Inflate(r, 1, 1),
                gg => TxtDirect(gg, s, f, c, r, h, vcenter, wrap));
        }

        static void TxtDirect(Graphics g, string s, Font f, Color c, RectangleF r, H h, bool vcenter, bool wrap)
        {
            using (var sf = (StringFormat)sfBase.Clone())
            {
                if (wrap) sf.FormatFlags &= ~StringFormatFlags.NoWrap;
                sf.Alignment = h == H.Left ? StringAlignment.Near : h == H.Center ? StringAlignment.Center : StringAlignment.Far;
                sf.LineAlignment = vcenter ? StringAlignment.Center : StringAlignment.Near;
                if (wrap) sf.Trimming = StringTrimming.Word;
                using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
            }
        }

        /// <summary>Text with a soft dark shadow, for labels on top of artwork.</summary>
        public static void TxtShadow(Graphics g, string s, Font f, Color c, RectangleF r, H h = H.Left)
        {
            var o = Scale;
            Txt(g, s, f, Black(150), new RectangleF(r.X, r.Y + o, r.Width, r.Height), h);
            Txt(g, s, f, c, r, h);
        }

        static readonly Bitmap measureBmp = new Bitmap(1, 1);
        static readonly Graphics measureG = Graphics.FromImage(measureBmp);

        static readonly Dictionary<(string, Font, int), SizeF> measured = new Dictionary<(string, Font, int), SizeF>();

        /// <summary>Text size, remembered: layout runs every frame and asks for the same strings over and over.</summary>
        public static SizeF Measure(string s, Font f, float maxWidth = 0)
        {
            if (string.IsNullOrEmpty(s)) return SizeF.Empty;
            int mw = maxWidth > 0 ? (int)Math.Ceiling(maxWidth) : 0;
            var key = (s, f, mw);
            if (measured.TryGetValue(key, out var sz)) return sz;
            if (measured.Count > 4000) measured.Clear();
            measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            sz = mw > 0 ? measureG.MeasureString(s, f, mw) : measureG.MeasureString(s, f);
            measured[key] = sz;
            return sz;
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear; // big pictures are pre-scaled and cached
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }

        // ------------------------------------------------------------------ color helpers

        public static Color HueColor(double deg, double s = 0.85, double v = 1.0)
        {
            deg = ((deg % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((deg / 60) % 2 - 1)), m = v - c;
            double r = 0, gg = 0, b = 0;
            if (deg < 60) { r = c; gg = x; }
            else if (deg < 120) { r = x; gg = c; }
            else if (deg < 180) { gg = c; b = x; }
            else if (deg < 240) { gg = x; b = c; }
            else if (deg < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(255, (int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
        }

        public static LinearGradientBrush Rainbow(RectangleF r, double startDeg = -180, double endDeg = 180, int alpha = 255)
        {
            var br = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, Math.Max(1, r.Height)), Color.Red, Color.Red, 0f);
            var blend = new ColorBlend(13);
            for (int i = 0; i < 13; i++)
            {
                double deg = startDeg + (endDeg - startDeg) * i / 12.0;
                blend.Colors[i] = WithAlpha(HueColor(deg, 0.72, 1.0), alpha);
                blend.Positions[i] = i / 12f;
            }
            br.InterpolationColors = blend;
            return br;
        }

        /// <summary>The app logo: a glowing prism ring.</summary>
        static Bitmap mark;
        static bool markLoaded;

        /// <summary>The fjord "F" badge built into the exe (assets/brand/mark.png), or null in builds without it.</summary>
        public static Bitmap Mark
        {
            get
            {
                if (markLoaded) return mark;
                markLoaded = true;
                try
                {
                    using (var s = typeof(Theme).Assembly.GetManifestResourceStream("brand.mark.png"))
                        if (s != null) using (var img = Image.FromStream(s)) mark = new Bitmap(img);
                }
                catch { mark = null; }
                return mark;
            }
        }

        /// <summary>The fjord logo badge with a soft mint glow behind it.</summary>
        public static void DrawLogo(Graphics g, RectangleF r, bool glow = true)
        {
            var m = Mark;
            if (m == null) { DrawRingLogo(g, r, glow); return; }
            if (glow) RadialGlow(g, new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.55f), r.Width * 1.15f, r.Height * 1.0f, Accent, 55);
            RenderCache.Draw(g, RenderCache.Key("logo", r.Width, r.Height), r, gg =>
            {
                var im = gg.InterpolationMode; var pom = gg.PixelOffsetMode; var cq = gg.CompositingQuality;
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.CompositingQuality = CompositingQuality.HighQuality;
                gg.DrawImage(m, r);
                gg.InterpolationMode = im; gg.PixelOffsetMode = pom; gg.CompositingQuality = cq;
            });
            // thin mint rim so the dark badge doesn't sink into the dark title bar
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            StrokeRound(g, RectangleF.Inflate(r, -0.5f, -0.5f), r.Width * 0.24f, WithAlpha(Accent, 60), Math.Max(1, Scale));
            g.SmoothingMode = sm;
        }

        static void DrawRingLogo(Graphics g, RectangleF r, bool glow)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (glow) RadialGlow(g, new PointF(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width * 1.2f, r.Height * 1.2f, AccentMid, 100);
            int n = 60;
            for (int i = 0; i < n; i++)
            {
                // sweep from light mint through teal and back
                float t = (float)(0.5 - 0.5 * Math.Cos(i * 2 * Math.PI / n));
                using (var b = new SolidBrush(Mix(AccentLight, Cyan, t)))
                    g.FillPie(b, r.X, r.Y, r.Width, r.Height, i * 360f / n - 90.5f, 360f / n + 1f);
            }
            float inset = r.Width * 0.24f;
            var inner = RectangleF.Inflate(r, -inset, -inset);
            using (var b = new SolidBrush(Bg)) g.FillEllipse(b, inner);
            float d = r.Width * 0.16f;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, r.X + r.Width / 2 - d / 2, r.Y + r.Height / 2 - d / 2, d, d);
            g.SmoothingMode = sm;
        }

        /// <summary>Pulsing "live" dot.</summary>
        public static void PulseDot(Graphics g, PointF c, float d, Color col, bool animate)
        {
            if (animate)
            {
                float t = (float)(Anim.Time % 1.8 / 1.8);
                float rr = d / 2 + d * 1.1f * Anim.EaseOut(t);
                using (var p = new Pen(WithAlpha(col, (int)(150 * (1 - t))), 1.5f * Scale))
                    g.DrawEllipse(p, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            }
            RadialGlow(g, c, d * 1.6f, d * 1.6f, col, 90);
            using (var b = new SolidBrush(col)) g.FillEllipse(b, c.X - d / 2, c.Y - d / 2, d, d);
        }
    }
}
