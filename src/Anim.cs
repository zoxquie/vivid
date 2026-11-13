using System;
using System.Collections.Generic;
using System.Drawing;

namespace Vivid
{
    /// <summary>
    /// Tiny animation helper for the custom-drawn UI. Values ease toward a target every frame;
    /// while anything is still moving, Busy stays true and the window keeps repainting the
    /// areas of the widgets that are moving.
    /// </summary>
    public static class Anim
    {
        static readonly Dictionary<(object, string), (float v, int frame)> vals = new Dictionary<(object, string), (float, int)>();
        static int frame;
        // values that haven't reached their target yet, with when they last moved
        static readonly Dictionary<(object, string), DateTime> moving = new Dictionary<(object, string), DateTime>();
        static readonly DateTime start = DateTime.UtcNow;
        static DateTime last = DateTime.UtcNow;
        static float dt = 1 / 60f;

        /// <summary>When false (preview renders), animations snap straight to their targets.</summary>
        public static bool Enabled = true;

        public static bool Busy
        {
            get
            {
                if (moving.Count == 0) return false;
                // a widget that was removed mid-animation never finishes: forget it after a moment
                var now = DateTime.UtcNow;
                List<(object, string)> stale = null;
                foreach (var kv in moving) if ((now - kv.Value).TotalSeconds > 1) (stale ?? (stale = new List<(object, string)>())).Add(kv.Key);
                if (stale != null) foreach (var k in stale) moving.Remove(k);
                return moving.Count > 0;
            }
        }

        /// <summary>The area covering everything still moving. False if something moving isn't a widget (repaint everything).</summary>
        public static bool DirtyArea(out Rectangle area)
        {
            var u = RectangleF.Empty;
            float m = 48 * Theme.Scale; // room for glows and the slider's value bubble
            foreach (var k in moving.Keys)
            {
                if (!(k.Item1 is Widget w) || w.Bounds.Width <= 0 || w.Bounds.Height <= 0) { area = Rectangle.Empty; return false; }
                var r = RectangleF.Inflate(w.Bounds, m, m);
                u = u.IsEmpty ? r : RectangleF.Union(u, r);
            }
            area = Rectangle.Ceiling(RectangleF.Inflate(u, 1, 1));
            return true;
        }

        /// <summary>Seconds since start, for looping effects like pulses.</summary>
        public static double Time => (DateTime.UtcNow - start).TotalSeconds;

        public static void BeginFrame()
        {
            var now = DateTime.UtcNow;
            dt = (float)Math.Max(0.001, Math.Min(0.08, (now - last).TotalSeconds));
            last = now;
            frame++;
            if (vals.Count > 4000) { vals.Clear(); moving.Clear(); } // widgets get rebuilt; drop stale entries now and then
        }

        public static float To(object owner, string name, float target, float speed = 16f)
        {
            var key = (owner, name);
            if (!Enabled || !vals.TryGetValue(key, out var e)) { vals[key] = (target, frame); return target; }
            if (e.frame == frame) return e.v; // asked again in the same frame: don't advance twice
            float v = e.v;
            float nv = v + (target - v) * (1 - (float)Math.Exp(-speed * dt));
            if (Math.Abs(nv - target) < 0.003f) { nv = target; moving.Remove(key); }
            else moving[key] = DateTime.UtcNow;
            vals[key] = (nv, frame);
            return nv;
        }

        public static float EaseOut(float t) => 1 - (1 - t) * (1 - t);
    }
}
