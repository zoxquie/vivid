using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Vivid
{
    /// <summary>
    /// Keeps finished drawings (glows, panel backgrounds, cards, scaled pictures) as ready-made
    /// bitmaps, so repainting the window is mostly fast copies instead of redrawing paths,
    /// gradients and resampled images every frame. Least recently used entries are dropped
    /// once the cache passes its memory budget.
    /// </summary>
    public static class RenderCache
    {
        sealed class Entry { public Bitmap Bmp; public long Bytes; public LinkedListNode<string> Node; }

        static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>();
        static readonly LinkedList<string> lru = new LinkedList<string>();
        static long bytes;
        /// <summary>Memory the cache may use; the window raises it for big windows (a frame needs about 1.5× the window's pixels).</summary>
        public static long Budget = 96L * 1024 * 1024;

        public static void FitWindow(Size client) => Budget = Math.Max(96L * 1024 * 1024, 4L * client.Width * client.Height * 4);

        public static int Count => map.Count;
        public static long Bytes => bytes;

        /// <summary>Drops everything, e.g. after the window's scale changes.</summary>
        public static void Clear()
        {
            foreach (var e in map.Values) e.Bmp.Dispose();
            map.Clear(); lru.Clear(); bytes = 0;
        }

        /// <summary>Drops entries whose key starts with a prefix (e.g. one card's images).</summary>
        public static void Forget(string prefix)
        {
            var dead = new List<string>();
            foreach (var k in map.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) dead.Add(k);
            foreach (var k in dead) Remove(k);
        }

        static void Remove(string k)
        {
            if (!map.TryGetValue(k, out var e)) return;
            map.Remove(k); lru.Remove(e.Node); bytes -= e.Bytes; e.Bmp.Dispose();
        }

        /// <summary>
        /// Draws <paramref name="area"/> from the cache, painting it with <paramref name="paint"/> first if this key
        /// hasn't been drawn at this size and sub-pixel position yet. The painter draws in window coordinates.
        /// </summary>
        public static void Draw(Graphics g, string key, RectangleF area, Action<Graphics> paint, bool snap = false)
        {
            if (area.Width < 1 || area.Height < 1) return;
            if (!g.VisibleClipBounds.IntersectsWith(area)) return; // outside what's being repainted
            using (var m = g.Transform) if (!m.IsIdentity) { paint(g); return; }

            int x0 = (int)Math.Floor(area.X), y0 = (int)Math.Floor(area.Y);
            int w = (int)Math.Ceiling(area.Right) - x0, h = (int)Math.Ceiling(area.Bottom) - y0;
            float fx = snap ? 0 : area.X - x0, fy = snap ? 0 : area.Y - y0;
            // quarter-pixel positions are plenty; soft things (glows) snap to whole pixels
            int qx = (int)Math.Round(fx * 4), qy = (int)Math.Round(fy * 4);
            string k = key + "@" + w + "x" + h + "+" + qx + "," + qy;

            if (!map.TryGetValue(k, out var e))
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(bmp))
                {
                    Theme.Prepare(bg);
                    bg.Clear(Color.Transparent);
                    // the painter keeps drawing in window coordinates
                    bg.TranslateTransform(-x0 + (qx / 4f - fx), -y0 + (qy / 4f - fy));
                    paint(bg);
                }
                e = new Entry { Bmp = bmp, Bytes = (long)w * h * 4 };
                e.Node = lru.AddFirst(k);
                map[k] = e;
                bytes += e.Bytes;
                while (bytes > Budget && lru.Count > 1) Remove(lru.Last.Value);
            }
            else if (e.Node != lru.First) { lru.Remove(e.Node); lru.AddFirst(e.Node); }

            Blit(g, e.Bmp, x0, y0);
        }

        /// <summary>1:1 copy of a bitmap at whole-pixel coordinates.</summary>
        public static void Blit(Graphics g, Bitmap bmp, int x, int y)
        {
            var im = g.InterpolationMode;
            var pm = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, new Rectangle(x, y, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
            g.InterpolationMode = im;
            g.PixelOffsetMode = pm;
        }

        static string C(Color c) => c.ToArgb().ToString("X8");
        static string N(float v) => ((int)Math.Round(v * 4)).ToString();

        public static string Key(params object[] parts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                if (p is Color c) sb.Append(C(c));
                else if (p is float f) sb.Append(N(f));
                else if (p is bool b) sb.Append(b ? '1' : '0');
                else sb.Append(p);
                sb.Append('|');
            }
            return sb.ToString();
        }
    }
}
