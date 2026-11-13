using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Vivid
{
    /// <summary>Procedural landscape thumbnails for the built-in presets (no image files needed).</summary>
    public static class Art
    {
        public static void Draw(Graphics g, RectangleF r, string kind)
        {
            switch (kind)
            {
                case "summer": Landscape(g, r, "#5B8FD6", "#BFD9F2", "#F7E08A", new[] { "#4E8B3A", "#3B7330", "#2A5A24" }, true, false); break;
                case "winter": Landscape(g, r, "#7F93AE", "#D5DEE9", "#F4F6FA", new[] { "#E6ECF3", "#C9D3DF", "#AEBACA" }, false, true); break;
                case "desert": Landscape(g, r, "#E39A5B", "#F6D3A1", "#FFF1C9", new[] { "#D9A15E", "#C58A49", "#A86F38" }, true, false); break;
                case "night": Night(g, r); break;
                case "vivid": Vivid(g, r); break;
                default:
                    using (var b = new SolidBrush(Theme.Surface2)) g.FillRectangle(b, r);
                    break;
            }
        }

        static void Landscape(Graphics g, RectangleF r, string skyTop, string skyBottom, string sunCol, string[] hills, bool sun, bool snow)
        {
            using (var sky = new LinearGradientBrush(r, Theme.Hex(skyTop), Theme.Hex(skyBottom), 90f))
                g.FillRectangle(sky, r);
            if (sun)
            {
                float d = r.Height * 0.42f;
                using (var b = new SolidBrush(Theme.Hex(sunCol)))
                    g.FillEllipse(b, r.Right - r.Width * 0.30f, r.Y + r.Height * 0.14f, d, d);
            }
            for (int i = 0; i < hills.Length; i++)
            {
                float baseY = r.Y + r.Height * (0.48f + i * 0.16f);
                float amp = r.Height * (0.16f - i * 0.03f);
                float phase = i * 1.7f + 0.6f;
                using (var path = new GraphicsPath())
                {
                    int n = 24;
                    var pts = new PointF[n + 3];
                    for (int k = 0; k <= n; k++)
                    {
                        float x = r.X + r.Width * k / n;
                        float y = baseY - amp * (float)(0.6 * Math.Sin(k / (float)n * 5.2 + phase) + 0.4 * Math.Sin(k / (float)n * 11.3 + phase * 2));
                        pts[k] = new PointF(x, y);
                    }
                    pts[n + 1] = new PointF(r.Right, r.Bottom);
                    pts[n + 2] = new PointF(r.X, r.Bottom);
                    path.AddPolygon(pts);
                    using (var b = new SolidBrush(Theme.Hex(hills[i]))) g.FillPath(b, path);
                }
                if (snow && i == 1)
                {
                    // a few dark pines on the middle ridge
                    using (var pine = new SolidBrush(Theme.Hex("#3E5568")))
                        for (int k = 0; k < 6; k++)
                        {
                            float x = r.X + r.Width * (0.42f + k * 0.09f);
                            float y = baseY - amp * 0.2f + (k % 2) * r.Height * 0.04f;
                            float h = r.Height * (0.22f + (k % 3) * 0.05f);
                            g.FillPolygon(pine, new[] { new PointF(x, y - h), new PointF(x - h * 0.28f, y), new PointF(x + h * 0.28f, y) });
                        }
                }
            }
            if (snow)
            {
                var rnd = new Random(7);
                using (var b = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    for (int k = 0; k < 28; k++)
                    {
                        float x = r.X + (float)rnd.NextDouble() * r.Width, y = r.Y + (float)rnd.NextDouble() * r.Height * 0.7f;
                        float d = 1.2f + (float)rnd.NextDouble() * 1.6f;
                        g.FillEllipse(b, x, y, d * Theme.Scale, d * Theme.Scale);
                    }
            }
        }

        static void Night(Graphics g, RectangleF r)
        {
            using (var b = new LinearGradientBrush(r, Theme.Hex("#0B2A1A"), Theme.Hex("#1C5A33"), 90f)) g.FillRectangle(b, r);
            using (var path = new GraphicsPath())
            {
                int n = 20;
                var pts = new PointF[n + 3];
                for (int k = 0; k <= n; k++)
                    pts[k] = new PointF(r.X + r.Width * k / n, r.Y + r.Height * (0.62f - 0.12f * (float)Math.Sin(k / (float)n * 6 + 1)));
                pts[n + 1] = new PointF(r.Right, r.Bottom); pts[n + 2] = new PointF(r.X, r.Bottom);
                path.AddPolygon(pts);
                using (var b = new SolidBrush(Theme.Hex("#2F8A4E"))) g.FillPath(b, path);
            }
            using (var p = new Pen(Color.FromArgb(40, 140, 255, 170), 1))
                for (float y = r.Y; y < r.Bottom; y += 3 * Theme.Scale) g.DrawLine(p, r.X, y, r.Right, y);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(r.X + r.Width * 0.25f, r.Y - r.Height * 0.3f, r.Width * 0.9f, r.Height * 1.6f);
                using (var pg = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, 170, 255, 190), SurroundColors = new[] { Color.FromArgb(0, 170, 255, 190) } })
                    g.FillPath(pg, glow);
            }
        }

        static void Vivid(Graphics g, RectangleF r)
        {
            using (var b = new LinearGradientBrush(r, Theme.Hex("#6D3CF0"), Theme.Hex("#F0458F"), 20f)) g.FillRectangle(b, r);
            using (var path = new GraphicsPath())
            {
                int n = 20;
                var pts = new PointF[n + 3];
                for (int k = 0; k <= n; k++)
                    pts[k] = new PointF(r.X + r.Width * k / n, r.Y + r.Height * (0.66f - 0.14f * (float)Math.Sin(k / (float)n * 4.5 + 2.2)));
                pts[n + 1] = new PointF(r.Right, r.Bottom); pts[n + 2] = new PointF(r.X, r.Bottom);
                path.AddPolygon(pts);
                using (var b = new LinearGradientBrush(r, Theme.Hex("#18C8E8"), Theme.Hex("#3EE08A"), 0f)) g.FillPath(b, path);
            }
            float d = r.Height * 0.36f;
            using (var b = new SolidBrush(Theme.Hex("#FFE15A"))) g.FillEllipse(b, r.Right - r.Width * 0.32f, r.Y + r.Height * 0.12f, d, d);
        }
    }
}
