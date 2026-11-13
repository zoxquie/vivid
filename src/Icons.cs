using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Vivid
{
    /// <summary>Simple stroke icons drawn on a 24x24 grid and scaled into any rectangle.</summary>
    public static class Icons
    {
        public delegate void Drawer(Graphics g, RectangleF r, Color c);

        static float U(RectangleF r) => r.Width / 24f;
        static PointF P(RectangleF r, float x, float y) => new PointF(r.X + x * r.Width / 24f, r.Y + y * r.Height / 24f);

        static Pen MakePen(RectangleF r, Color c, float w = 1.8f)
        {
            var p = new Pen(c, Math.Max(1f, w * U(r)));
            p.StartCap = p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        static void Line(Graphics g, Pen p, RectangleF r, float x1, float y1, float x2, float y2) =>
            g.DrawLine(p, P(r, x1, y1), P(r, x2, y2));

        static void Poly(Graphics g, Pen p, RectangleF r, params float[] xy)
        {
            var pts = new PointF[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = P(r, xy[i * 2], xy[i * 2 + 1]);
            g.DrawLines(p, pts);
        }

        static RectangleF Circle(RectangleF r, float cx, float cy, float rad) =>
            new RectangleF(r.X + (cx - rad) * U(r), r.Y + (cy - rad) * U(r), 2 * rad * U(r), 2 * rad * U(r));

        public static void Sun(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                g.DrawEllipse(p, Circle(r, 12, 12, 4));
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                    Line(g, p, r, 12 + cs * 7, 12 + sn * 7, 12 + cs * 9.5f, 12 + sn * 9.5f);
                }
            }
        }

        public static void Moon(Graphics g, RectangleF r, Color c)
        {
            // Crescent = big circle minus an offset circle, outlined.
            float ax = 12, ay = 12, ar = 8.5f, bx = 16.5f, by = 7.5f, br = 7f;
            double dx = bx - ax, dy = by - ay, d = Math.Sqrt(dx * dx + dy * dy);
            double a = (ar * ar - br * br + d * d) / (2 * d);
            double h = Math.Sqrt(Math.Max(0, ar * ar - a * a));
            double mx = ax + a * dx / d, my = ay + a * dy / d;
            double p1x = mx + h * dy / d, p1y = my - h * dx / d;
            double p2x = mx - h * dy / d, p2y = my + h * dx / d;
            float angA1 = (float)(Math.Atan2(p1y - ay, p1x - ax) * 180 / Math.PI);
            float angA2 = (float)(Math.Atan2(p2y - ay, p2x - ax) * 180 / Math.PI);
            float angB1 = (float)(Math.Atan2(p1y - by, p1x - bx) * 180 / Math.PI);
            float angB2 = (float)(Math.Atan2(p2y - by, p2x - bx) * 180 / Math.PI);
            using (var path = new GraphicsPath())
            {
                // Outer arc on A from p2 to p1 the long way (away from B), inner arc on B back from p1 to p2.
                float sweepA = Norm(angA1 - angA2);
                if (sweepA < 180) sweepA = sweepA - 360;
                path.AddArc(Circle(r, ax, ay, ar), angA2, sweepA);
                float sweepB = Norm(angB2 - angB1);
                if (sweepB > 180) sweepB -= 360;
                path.AddArc(Circle(r, bx, by, br), angB1, sweepB);
                path.CloseFigure();
                using (var p = MakePen(r, c)) g.DrawPath(p, path);
            }
        }

        static float Norm(float a) { while (a < 0) a += 360; while (a >= 360) a -= 360; return a; }

        public static void Drop(Graphics g, RectangleF r, Color c)
        {
            // Tangent lines from the tip to a circle, plus the lower arc.
            float cx = 12, cy = 14.5f, rad = 6.2f, tx = 12, ty = 2.8f;
            double d = cy - ty, alpha = Math.Acos(rad / d) * 180 / Math.PI;
            float a1 = (float)(-90 + alpha), a2 = (float)(-90 - alpha);
            using (var path = new GraphicsPath())
            {
                var tip = P(r, tx, ty);
                double ra1 = a1 * Math.PI / 180;
                var t1 = P(r, cx + rad * (float)Math.Cos(ra1), cy + rad * (float)Math.Sin(ra1));
                path.AddLine(tip, t1);
                path.AddArc(Circle(r, cx, cy, rad), a1, 360 - (float)(2 * alpha));
                path.CloseFigure();
                using (var p = MakePen(r, c)) g.DrawPath(p, path);
            }
            using (var p = MakePen(r, c, 1.5f))
                g.DrawArc(p, Circle(r, cx, cy, rad - 2.6f), 100, 70);
        }

        public static void Gear(Graphics g, RectangleF r, Color c)
        {
            int teeth = 8;
            using (var path = new GraphicsPath())
            {
                var pts = new PointF[teeth * 4];
                for (int i = 0; i < teeth; i++)
                {
                    double baseA = i * 2 * Math.PI / teeth;
                    double w1 = 0.20, w2 = 0.13;
                    pts[i * 4 + 0] = Polar(r, baseA - w1 - 0.06, 7.2f);
                    pts[i * 4 + 1] = Polar(r, baseA - w2, 9.6f);
                    pts[i * 4 + 2] = Polar(r, baseA + w2, 9.6f);
                    pts[i * 4 + 3] = Polar(r, baseA + w1 + 0.06, 7.2f);
                }
                path.AddPolygon(pts);
                using (var p = MakePen(r, c, 1.7f)) { g.DrawPath(p, path); g.DrawEllipse(p, Circle(r, 12, 12, 3)); }
            }
        }

        static PointF Polar(RectangleF r, double a, float rad) => P(r, 12 + rad * (float)Math.Cos(a), 12 + rad * (float)Math.Sin(a));

        public static void Power(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 2f))
            {
                g.DrawArc(p, Circle(r, 12, 13, 7.5f), -55, 290);
                Line(g, p, r, 12, 3, 12, 11);
            }
        }

        public static void Sliders(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            using (var b = new SolidBrush(c))
            {
                float[] ys = { 6.5f, 12, 17.5f };
                float[] ks = { 15.5f, 8.5f, 13 };
                for (int i = 0; i < 3; i++)
                {
                    Line(g, p, r, 4, ys[i], ks[i] - 2.6f, ys[i]);
                    Line(g, p, r, ks[i] + 2.6f, ys[i], 20, ys[i]);
                    g.DrawEllipse(p, Circle(r, ks[i], ys[i], 2.2f));
                }
            }
        }

        public static void Crosshair(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            using (var b = new SolidBrush(c))
            {
                g.DrawEllipse(p, Circle(r, 12, 12, 7.5f));
                Line(g, p, r, 12, 2.5f, 12, 7);
                Line(g, p, r, 12, 17, 12, 21.5f);
                Line(g, p, r, 2.5f, 12, 7, 12);
                Line(g, p, r, 17, 12, 21.5f, 12);
                g.FillEllipse(b, Circle(r, 12, 12, 1.4f));
            }
        }

        public static void Reset(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.9f))
            {
                g.DrawArc(p, Circle(r, 12, 12, 7.5f), -150, 290);
                // arrow head at the start of the arc (angle -150)
                double a = -150 * Math.PI / 180;
                float sx = 12 + 7.5f * (float)Math.Cos(a), sy = 12 + 7.5f * (float)Math.Sin(a);
                Poly(g, p, r, sx - 0.2f, sy - 4.2f, sx, sy, sx + 4.2f, sy + 0.4f);
            }
        }

        public static void Check(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 2.4f)) Poly(g, p, r, 6, 12.5f, 10, 16.5f, 18, 8);
        }

        public static void Import(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                Line(g, p, r, 12, 15, 12, 4);
                Poly(g, p, r, 7.5f, 8.5f, 12, 4, 16.5f, 8.5f);
                Poly(g, p, r, 4, 14.5f, 4, 20, 20, 20, 20, 14.5f);
            }
        }

        public static void Export(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                Line(g, p, r, 12, 4, 12, 15);
                Poly(g, p, r, 7.5f, 10.5f, 12, 15, 16.5f, 10.5f);
                Poly(g, p, r, 4, 14.5f, 4, 20, 20, 20, 20, 14.5f);
            }
        }

        public static void More(Graphics g, RectangleF r, Color c)
        {
            using (var b = new SolidBrush(c))
                foreach (float x in new[] { 6f, 12f, 18f }) g.FillEllipse(b, Circle(r, x, 12, 1.7f));
        }

        public static void Plus(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 2f)) { Line(g, p, r, 12, 5, 12, 19); Line(g, p, r, 5, 12, 19, 12); }
        }

        public static void Keyboard(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.6f))
            using (var b = new SolidBrush(c))
            {
                var rr = new RectangleF(P(r, 2.5f, 6).X, P(r, 2.5f, 6).Y, 19 * U(r), 12 * U(r));
                using (var path = Theme.Round(rr, 2.2f * U(r))) g.DrawPath(p, path);
                for (int i = 0; i < 4; i++) g.FillEllipse(b, Circle(r, 6.5f + i * 3.7f, 9.8f, 0.95f));
                for (int i = 0; i < 4; i++) g.FillEllipse(b, Circle(r, 6.5f + i * 3.7f, 12.8f, 0.95f));
                Line(g, p, r, 8, 15.6f, 16, 15.6f);
            }
        }

        public static void Pencil(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                Poly(g, p, r, 15.5f, 4.5f, 19.5f, 8.5f, 8.5f, 19.5f, 4, 20, 4.5f, 15.5f, 15.5f, 4.5f);
                Line(g, p, r, 13, 7, 17, 11);
            }
        }

        public static void Trash(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                Line(g, p, r, 4, 6.5f, 20, 6.5f);
                Poly(g, p, r, 9, 6.5f, 9.5f, 4, 14.5f, 4, 15, 6.5f);
                Poly(g, p, r, 6, 6.5f, 7, 20, 17, 20, 18, 6.5f);
                Line(g, p, r, 10, 10.5f, 10, 16);
                Line(g, p, r, 14, 10.5f, 14, 16);
            }
        }

        public static void Monitor(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                var tl = P(r, 3, 4.5f);
                using (var path = Theme.Round(new RectangleF(tl.X, tl.Y, 18 * U(r), 12 * U(r)), 2 * U(r))) g.DrawPath(p, path);
                Line(g, p, r, 12, 16.5f, 12, 20);
                Line(g, p, r, 8, 20, 16, 20);
            }
        }

        public static void Info(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            using (var b = new SolidBrush(c))
            {
                g.DrawEllipse(p, Circle(r, 12, 12, 9));
                Line(g, p, r, 12, 11, 12, 16.5f);
                g.FillEllipse(b, Circle(r, 12, 7.6f, 1.25f));
            }
        }

        public static void Bolt(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c)) Poly(g, p, r, 13, 3, 5.5f, 13.5f, 11.5f, 13.5f, 10.5f, 21, 18.5f, 10, 12.5f, 10, 13, 3);
        }

        /// <summary>Opens-in-browser arrow.</summary>
        public static void Link(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c))
            {
                Poly(g, p, r, 14, 4, 20, 4, 20, 10);
                Line(g, p, r, 20, 4, 11.5f, 12.5f);
                Poly(g, p, r, 10.5f, 6, 6, 6, 6, 18, 18, 18, 18, 13.5f);
            }
        }

        public static void Folder(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c)) Poly(g, p, r, 3.5f, 18.5f, 3.5f, 5.5f, 9.5f, 5.5f, 11.5f, 8, 20.5f, 8, 20.5f, 18.5f, 3.5f, 18.5f);
        }

        public static void Shield(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c)) Poly(g, p, r, 12, 3, 19.5f, 6, 19.5f, 11.5f, 12, 21, 4.5f, 11.5f, 4.5f, 6, 12, 3);
        }

        // ---------- window chrome ----------

        public static void WinMin(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.3f)) Line(g, p, r, 7, 12, 17, 12);
        }

        public static void WinMax(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.3f)) { var tl = P(r, 7, 7); g.DrawRectangle(p, tl.X, tl.Y, 10 * U(r), 10 * U(r)); }
        }

        public static void WinRestore(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.3f))
            {
                var tl = P(r, 7, 9); g.DrawRectangle(p, tl.X, tl.Y, 8 * U(r), 8 * U(r));
                Poly(g, p, r, 9, 9, 9, 7, 17, 7, 17, 15, 15, 15);
            }
        }

        public static void WinClose(Graphics g, RectangleF r, Color c)
        {
            using (var p = MakePen(r, c, 1.3f)) { Line(g, p, r, 7, 7, 17, 17); Line(g, p, r, 17, 7, 7, 17); }
        }
    }
}
