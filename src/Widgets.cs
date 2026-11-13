using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>Shared UI state for the single custom-drawn window.</summary>
    public sealed class Ui
    {
        public static Widget Hover, Pressed, Capturing;
        public static MainForm Form;
        public static float S => Theme.Scale;
        public static void Invalidate() => Form?.Invalidate();
    }

    public abstract class Widget
    {
        public RectangleF Bounds;
        public bool Visible = true;
        public Func<bool> EnabledWhen;
        public bool Enabled => EnabledWhen == null || EnabledWhen();
        public readonly List<Widget> Children = new List<Widget>();
        public Widget Parent;
        public RectangleF? Clip; // set by scroll panels

        public T Add<T>(T w) where T : Widget { w.Parent = this; Children.Add(w); return w; }

        protected static float S => Theme.Scale;
        protected bool IsHover => Ui.Hover == this;
        protected bool IsPressed => Ui.Pressed == this;
        /// <summary>Animated 0..1 hover amount.</summary>
        protected float HoverT => Anim.To(this, "hover", IsHover && Enabled ? 1 : 0, 18f);

        public virtual bool Interactive => false;
        public virtual Cursor Cursor => Interactive && Enabled ? Cursors.Hand : null;

        public virtual void Paint(Graphics g)
        {
            // skip widgets outside the area being repainted (with room for glows)
            if (Bounds.Width > 0 && !g.VisibleClipBounds.IntersectsWith(RectangleF.Inflate(Bounds, 48 * S, 48 * S))) return;
            string key = CacheKey();
            if (key != null) RenderCache.Draw(g, key, RectangleF.Inflate(Bounds, CachePad, CachePad), PaintSelf);
            else PaintSelf(g);
            foreach (var c in Children) if (c.Visible) c.Paint(g);
        }

        protected virtual void PaintSelf(Graphics g) { }

        /// <summary>
        /// Everything that changes how PaintSelf looks, or null to always draw it. With a key, the drawing is
        /// kept as a bitmap and reused until something in the key changes.
        /// </summary>
        protected virtual string CacheKey() => null;
        /// <summary>How far PaintSelf draws outside Bounds (glows).</summary>
        protected virtual float CachePad => 18 * S;
        /// <summary>Animation value rounded to steps that cached drawings can share.</summary>
        protected static float Q(float v) => (float)Math.Round(v * 20) / 20f;
        protected static string Id(Delegate d) => d == null ? "" : d.Method.DeclaringType?.Name + "." + d.Method.Name;

        public virtual Widget HitTest(PointF p)
        {
            if (!Visible) return null;
            if (Clip.HasValue && !Clip.Value.Contains(p)) return null;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var h = Children[i].HitTest(p);
                if (h != null) return h;
            }
            return Interactive && Bounds.Contains(p) ? this : null;
        }

        public virtual void MouseDown(PointF p, MouseButtons b) { }
        public virtual void MouseDrag(PointF p) { }
        public virtual void MouseUp(PointF p, bool inside, MouseButtons b) { if (inside && Enabled && b == MouseButtons.Left) Click(); }
        public virtual void Click() { }
        public virtual void DoubleClick(PointF p) { }
        public virtual void RightClick(PointF p) { }
        public virtual bool Wheel(int delta) => false;
        public virtual string Tooltip => null;
    }

    /// <summary>A plain container.</summary>
    public class Panel : Widget
    {
        public Action<Graphics, RectangleF> Painter;
        protected override void PaintSelf(Graphics g) => Painter?.Invoke(g, Bounds);
    }

    public class Label : Widget
    {
        public Func<string> TextFn;
        public string Text { set { var v = value; TextFn = () => v; } }
        public Font Font = Theme.F(13);
        public Func<Color> ColorFn = () => Theme.Text;
        public Color Color { set { var v = value; ColorFn = () => v; } }
        public Theme.H Align = Theme.H.Left;
        public bool Wrap, VCenter = true;

        protected override void PaintSelf(Graphics g) =>
            Theme.Txt(g, TextFn?.Invoke(), Font, ColorFn(), Bounds, Align, VCenter && !Wrap, Wrap);
    }

    /// <summary>Small uppercase caption used above groups.</summary>
    public class Caption : Label
    {
        public Caption(string text) { Text = text.ToUpperInvariant(); Font = Theme.F(11, semibold: true); Color = Theme.TextDim; }
    }

    public enum BtnStyle { Primary, Secondary, Ghost, Danger, Tab, Link, Subtle }

    public class Button : Widget
    {
        public Func<string> TextFn;
        public string Text { set { var v = value; TextFn = () => v; } }
        public Icons.Drawer Icon;
        public BtnStyle Style = BtnStyle.Secondary;
        public Func<bool> SelectedWhen;
        public Action OnClick;
        public Action<PointF> OnRightClick;
        public float Radius = 11;
        public float IconSize = 18;
        public Func<Color> IconColorFn;
        public string Hint;

        public override bool Interactive => true;
        public override string Tooltip => Hint;
        protected bool Selected => SelectedWhen != null && SelectedWhen();

        public override void Click() => OnClick?.Invoke();
        public override void RightClick(PointF p) => OnRightClick?.Invoke(p);

        protected override string CacheKey()
        {
            if (GetType() != typeof(Button)) return null; // subclasses draw extra things
            bool en = Enabled;
            return RenderCache.Key("btn", Style, TextFn?.Invoke(), Id(Icon), IconSize, IconColorFn != null && en ? IconColorFn() : Color.Empty,
                en, IsPressed && en, Q(HoverT), Q(Anim.To(this, "sel", Selected ? 1 : 0, 14f)), Radius);
        }

        protected override void PaintSelf(Graphics g)
        {
            bool en = Enabled, pr = IsPressed && en, sel = Selected;
            float h = HoverT;
            float selT = Anim.To(this, "sel", sel ? 1 : 0, 14f);
            float rad = Radius * S;
            var r = Bounds;
            Color fg = Theme.Text;
            float bw = Math.Max(1f, S);

            switch (Style)
            {
                case BtnStyle.Primary:
                    if (en) Theme.Glow(g, r, rad, Theme.AccentMid, (7 + 7 * h) * S, (int)(55 + 55 * h));
                    using (var b = en ? Theme.AccentBrush(r) : Theme.Gradient(r, Theme.Surface3, Theme.Surface3)) Theme.FillRound(g, r, rad, b);
                    if (h > 0) Theme.FillRound(g, r, rad, Theme.White((int)(22 * h)));
                    if (pr) Theme.FillRound(g, r, rad, Theme.Black(50));
                    using (var hl = Theme.Gradient(r, Theme.White(en ? 120 : 20), Theme.White(0), 90f)) Theme.StrokeRound(g, r, rad, hl, bw);
                    fg = en ? Theme.OnAccent : Theme.TextDim;
                    break;
                case BtnStyle.Secondary:
                    Theme.FillRound(g, r, rad, Theme.White((int)(9 + 9 * h + (pr ? 8 : 0))));
                    using (var lg = Theme.Gradient(r, Theme.White((int)(42 + 30 * h)), Theme.White((int)(14 + 14 * h)), 90f)) Theme.StrokeRound(g, r, rad, lg, bw);
                    fg = en ? Theme.Text : Theme.TextDim;
                    break;
                case BtnStyle.Subtle:
                    Theme.FillRound(g, r, rad, Theme.Mix(Theme.Surface, Theme.Surface3, h));
                    using (var lg = Theme.Gradient(r, Theme.White((int)(40 + 30 * h)), Theme.White(12), 90f)) Theme.StrokeRound(g, r, rad, lg, bw);
                    fg = Theme.Mix(Theme.TextMuted, Theme.Text, h);
                    break;
                case BtnStyle.Danger:
                    Theme.Glow(g, r, rad, Theme.Red, 6 * S * h, (int)(60 * h));
                    Theme.FillRound(g, r, rad, Theme.Mix(Theme.RedSoft, Theme.Mix(Theme.RedSoft, Theme.Red, 0.35f), h));
                    Theme.StrokeRound(g, r, rad, Theme.WithAlpha(Theme.Red, (int)(120 + 80 * h)), bw);
                    fg = Theme.Mix(Theme.Red, Color.White, 0.3f * h);
                    break;
                case BtnStyle.Ghost:
                    Theme.FillRound(g, r, rad, Theme.White((int)(16 * h + (pr ? 10 : 0))));
                    fg = Theme.Mix(Theme.TextMuted, Theme.Text, h);
                    break;
                case BtnStyle.Tab:
                    if (selT > 0)
                    {
                        Theme.Glow(g, r, rad, Theme.AccentMid, 8 * S, (int)(45 * selT));
                        using (var b = Theme.AccentBrush(r, (int)(70 * selT))) Theme.FillRound(g, r, rad, b);
                        using (var b = Theme.AccentBrush(r, (int)(220 * selT))) Theme.StrokeRound(g, r, rad, b, bw);
                    }
                    if (h > 0 && !sel) Theme.FillRound(g, r, rad, Theme.White((int)(12 * h)));
                    fg = Theme.Mix(Theme.Mix(Theme.TextMuted, Theme.Text, h), Theme.AccentLight, selT);
                    break;
                case BtnStyle.Link:
                    fg = Theme.Mix(Theme.TextMuted, Theme.Text, h);
                    if (h > 0) Theme.FillRound(g, r, 8 * S, Theme.White((int)(10 * h)));
                    break;
            }
            if (!en && Style != BtnStyle.Primary) fg = Theme.TextDim;

            string text = TextFn?.Invoke();
            var font = Style == BtnStyle.Link ? Theme.F(13, semibold: true) : Theme.F(13.5f, semibold: true);
            float iconPx = IconSize * S;
            var ts = string.IsNullOrEmpty(text) ? SizeF.Empty : Theme.Measure(text, font);
            float gap = (Icon != null && ts.Width > 0) ? 8 * S : 0;
            float total = (Icon != null ? iconPx : 0) + gap + ts.Width;
            float x = r.X + (r.Width - total) / 2;
            if (Icon != null)
            {
                var ir = new RectangleF(x, r.Y + (r.Height - iconPx) / 2, iconPx, iconPx);
                Color ic = IconColorFn != null && en ? IconColorFn() : fg;
                Icon(g, ir, ic);
                x += iconPx + gap;
            }
            if (ts.Width > 0)
                Theme.Txt(g, text, font, fg, new RectangleF(x - 2, r.Y, ts.Width + 6, r.Height));
        }
    }

    /// <summary>Pill switch with an animated knob.</summary>
    public class Toggle : Widget
    {
        public Func<bool> Get;
        public Action<bool> Set;
        public string Hint;
        public override bool Interactive => true;
        public override string Tooltip => Hint;

        public override void Click() { if (Enabled) Set?.Invoke(!Get()); }

        protected override string CacheKey() =>
            RenderCache.Key("toggle", Q(Anim.To(this, "on", Get() ? 1 : 0, 15f)), Q(HoverT), Enabled);

        protected override void PaintSelf(Graphics g)
        {
            bool on = Get();
            float t = Anim.To(this, "on", on ? 1 : 0, 15f);
            float h = HoverT;
            var r = Bounds;
            float rad = r.Height / 2;
            if (t > 0) Theme.Glow(g, r, rad, Theme.Green, 6 * S, (int)(55 * t));
            Theme.FillRound(g, r, rad, Theme.Mix(Theme.Surface3, Theme.Mix(Theme.Surface3, Theme.White(255), 0.06f), h));
            if (t > 0) using (var b = Theme.Gradient(r, Theme.WithAlpha(Theme.GreenDeep, (int)(255 * t)), Theme.WithAlpha(Theme.Green, (int)(255 * t)), 0f)) Theme.FillRound(g, r, rad, b);
            Theme.StrokeRound(g, r, rad, Theme.White((int)(40 * (1 - t) + 30 * t)), Math.Max(1, S));
            if (!Enabled) Theme.FillRound(g, r, rad, Theme.Black(120));
            float d = r.Height - 6 * S;
            float x = r.X + 3 * S + (r.Width - 6 * S - d) * t;
            var kr = new RectangleF(x, r.Y + 3 * S, d, d);
            using (var sh = new SolidBrush(Theme.Black(90))) g.FillEllipse(sh, kr.X, kr.Y + 1.5f * S, d, d);
            using (var b = new SolidBrush(Theme.Mix(Theme.Mix(Theme.TextMuted, Color.White, h * 0.5f), Color.White, t))) g.FillEllipse(b, kr);
        }
    }

    public enum SliderLook { Neutral, Green, Rainbow, Accent }

    /// <summary>Horizontal slider with origin-based glowing fill, value bubble, wheel and double-click-to-reset.</summary>
    public class Slider : Widget
    {
        public double Min, Max, Default, Step = 0.01;
        public Func<double> Get;
        public Action<double> Set;
        public Action Commit;
        public Func<double, string> Format;
        public SliderLook Look = SliderLook.Neutral;
        bool dragging;

        public override bool Interactive => true;

        RectangleF Track => new RectangleF(Bounds.X + 10 * S, Bounds.Y + Bounds.Height / 2 - 3 * S, Bounds.Width - 20 * S, 6 * S);

        float XOf(double v) { var t = Track; return t.X + (float)((v - Min) / (Max - Min)) * t.Width; }

        double ValueAt(float x)
        {
            var t = Track;
            double f = Math.Max(0, Math.Min(1, (x - t.X) / t.Width));
            double v = Min + f * (Max - Min);
            v = Math.Round(v / Step) * Step;
            if (Math.Abs(XOf(v) - XOf(Default)) < 4 * S) v = Default; // snap to neutral
            return Math.Max(Min, Math.Min(Max, v));
        }

        public override void MouseDown(PointF p, MouseButtons b)
        {
            if (!Enabled || b != MouseButtons.Left) return;
            dragging = true;
            Set(ValueAt(p.X));
        }

        public override void MouseDrag(PointF p) { if (dragging) Set(ValueAt(p.X)); }

        public override void MouseUp(PointF p, bool inside, MouseButtons b)
        {
            if (dragging) { dragging = false; Commit?.Invoke(); }
        }

        public override void DoubleClick(PointF p) { if (Enabled) { Set(Default); Commit?.Invoke(); } }

        public override bool Wheel(int delta)
        {
            if (!Enabled) return false;
            double v = Get() + Math.Sign(delta) * Step;
            Set(Math.Max(Min, Math.Min(Max, Math.Round(v / Step) * Step)));
            Commit?.Invoke();
            return true;
        }

        Color[] FillColors()
        {
            switch (Look)
            {
                case SliderLook.Green: return new[] { Theme.GreenDeep, Theme.Green };
                case SliderLook.Accent: return new[] { Theme.Cyan, Theme.Blue };
                default: return new[] { Theme.Hex("#8E93AD"), Color.White };
            }
        }

        protected override string CacheKey()
        {
            float drag = Anim.To(this, "drag", dragging ? 1 : 0, 20f);
            if (drag > 0.001f) return null;
            return RenderCache.Key("slider", Get().ToString("R", System.Globalization.CultureInfo.InvariantCulture), Min, Max, Default, Look, Enabled, Q(HoverT));
        }
        protected override float CachePad => 34 * S;

        protected override void PaintSelf(Graphics g)
        {
            var t = Track;
            double v = Get();
            bool en = Enabled;
            float xv = XOf(v), x0 = XOf(Math.Max(Min, Math.Min(Max, Default)));
            float h = HoverT;
            float drag = Anim.To(this, "drag", dragging ? 1 : 0, 20f);
            float rad = t.Height / 2;

            // track
            Theme.FillRound(g, RectangleF.Inflate(t, 0, 1 * S), rad + S, Theme.Black(120));
            Theme.FillRound(g, t, rad, Theme.White(16));
            Color ring;
            if (Look == SliderLook.Rainbow)
            {
                using (var br = Theme.Rainbow(t, Min, Max, en ? 255 : 90)) Theme.FillRound(g, t, rad, br);
                ring = en ? Theme.HueColor(v, 0.65, 1) : Theme.TextDim;
                if (en) using (var br = Theme.Rainbow(t, Min, Max, 40)) Theme.FillRound(g, RectangleF.Inflate(t, 0, 4 * S), rad + 4 * S, br);
            }
            else
            {
                var cols = FillColors();
                ring = en ? cols[1] : Theme.TextDim;
                float a = Math.Min(x0, xv), b = Math.Max(x0, xv);
                if (b - a > 0.5f)
                {
                    var fr = new RectangleF(a, t.Y, b - a, t.Height);
                    if (en) Theme.Glow(g, fr, rad, cols[1], 6 * S, Look == SliderLook.Neutral ? 22 : 45);
                    bool right = xv >= x0;
                    using (var lg = Theme.Gradient(fr, en ? (right ? cols[0] : cols[1]) : Theme.TextDim, en ? (right ? cols[1] : cols[0]) : Theme.TextDim, 0f))
                        Theme.FillRound(g, fr, rad, lg);
                }
                if (Default > Min && Default < Max)
                    using (var b2 = new SolidBrush(Theme.White(70))) g.FillEllipse(b2, x0 - 1.5f * S, t.Y + t.Height / 2 - 1.5f * S, 3 * S, 3 * S);
            }

            // thumb
            float r = (8f + 1.5f * h + 1.5f * drag) * S;
            var thumb = new RectangleF(xv - r, t.Y + t.Height / 2 - r, 2 * r, 2 * r);
            if (en) Theme.RadialGlow(g, new PointF(xv, thumb.Y + r), r * 2.6f, r * 2.6f, ring, (int)(50 + 50 * h + 40 * drag));
            using (var sh = new SolidBrush(Theme.Black(110))) g.FillEllipse(sh, thumb.X, thumb.Y + 1.5f * S, thumb.Width, thumb.Height);
            using (var b = new SolidBrush(en ? Color.White : Theme.TextMuted)) g.FillEllipse(b, thumb);
            using (var p = new Pen(ring, 2.6f * S)) g.DrawEllipse(p, RectangleF.Inflate(thumb, -2.2f * S, -2.2f * S));

            // value bubble while dragging
            if (drag > 0.02f && Format != null)
            {
                var f = Theme.F(12, semibold: true);
                string s = Format(v);
                var ts = Theme.Measure(s, f);
                float bw = ts.Width + 18 * S, bh = 24 * S;
                var br = new RectangleF(xv - bw / 2, thumb.Y - 10 * S - bh + (1 - drag) * 6 * S, bw, bh);
                int a = (int)(255 * drag);
                Theme.Glow(g, br, 8 * S, Theme.Black(255), 6 * S, (int)(70 * drag));
                using (var bb = Theme.Gradient(br, Theme.WithAlpha(Theme.Accent, a), Theme.WithAlpha(Theme.AccentEnd, a), 0f)) Theme.FillRound(g, br, 8 * S, bb);
                using (var tri = new SolidBrush(Theme.WithAlpha(Theme.AccentMid, a)))
                    g.FillPolygon(tri, new[] { new PointF(xv - 5 * S, br.Bottom - 0.5f), new PointF(xv + 5 * S, br.Bottom - 0.5f), new PointF(xv, br.Bottom + 5 * S) });
                Theme.Txt(g, s, f, Theme.WithAlpha(Theme.OnAccent, a), br, Theme.H.Center);
            }
        }
    }

    /// <summary>Shows a formatted number; click to type a value.</summary>
    public class ValueBox : Widget
    {
        public Func<string> TextFn;
        public Func<string> EditTextFn;
        public Action<string> OnCommit;
        public override bool Interactive => true;
        public override Cursor Cursor => Enabled ? Cursors.IBeam : null;
        public override string Tooltip => Enabled ? "Click to type a value" : null;

        public override void Click()
        {
            if (!Enabled) return;
            Ui.Form.BeginTextEdit(Bounds, (EditTextFn ?? TextFn)(), OnCommit, center: true);
        }

        protected override string CacheKey() =>
            Ui.Form.IsEditing(Bounds) ? null : RenderCache.Key("vbox", TextFn(), Enabled, Q(HoverT));

        protected override void PaintSelf(Graphics g)
        {
            float h = HoverT;
            bool editing = Ui.Form.IsEditing(Bounds);
            float rad = 10 * S;
            Theme.FillRound(g, Bounds, rad, Theme.Mix(Theme.Surface2, Theme.Surface3, h));
            using (var inner = Theme.Gradient(Bounds, Theme.Black(70), Theme.Black(0), 90f))
            {
                inner.InterpolationColors = new ColorBlend(3) { Colors = new[] { Theme.Black(70), Theme.Black(0), Theme.Black(0) }, Positions = new[] { 0f, 0.35f, 1f } };
                Theme.FillRound(g, Bounds, rad, inner);
            }
            if (editing || h > 0)
            {
                using (var b = Theme.AccentBrush(Bounds, (int)(editing ? 255 : 150 * h))) Theme.StrokeRound(g, Bounds, rad, b, Math.Max(1, 1.2f * S));
                if (editing) Theme.Glow(g, Bounds, rad, Theme.AccentMid, 6 * S, 40);
            }
            else Theme.StrokeRound(g, Bounds, rad, Theme.White(24), Math.Max(1, S));
            if (editing) return;
            Theme.Txt(g, TextFn(), Theme.F(14, semibold: true), Enabled ? Theme.Text : Theme.TextDim, Bounds, Theme.H.Center);
        }
    }

    /// <summary>Reset button; lit when the value differs from default, spins a little on hover.</summary>
    public class ResetButton : Widget
    {
        public Func<bool> Changed;
        public Action OnClick;
        public Color Tint = Theme.Text;
        public override bool Interactive => true;
        public override string Tooltip => Changed() ? "Reset to default" : null;
        public override void Click() { if (Enabled && Changed()) OnClick?.Invoke(); }
        public override Cursor Cursor => Changed() && Enabled ? Cursors.Hand : null;

        protected override string CacheKey()
        {
            bool ch = Changed() && Enabled;
            return RenderCache.Key("reset", Q(Anim.To(this, "lit", ch ? 1 : 0, 12f)), ch ? Q(HoverT) : 0f, Tint);
        }

        protected override void PaintSelf(Graphics g)
        {
            bool ch = Changed() && Enabled;
            float lit = Anim.To(this, "lit", ch ? 1 : 0, 12f);
            float h = ch ? HoverT : 0;
            float rad = 10 * S;
            if (lit > 0) Theme.Glow(g, Bounds, rad, Tint, 5 * S, (int)((18 + 30 * h) * lit));
            Theme.FillRound(g, Bounds, rad, Theme.Mix(Theme.Surface2, Theme.Mix(Theme.Surface2, Tint, 0.10f), lit));
            Theme.StrokeRound(g, Bounds, rad, Theme.Mix(Theme.White(22), Theme.WithAlpha(Tint, 150 + (int)(90 * h)), lit), Math.Max(1, S));
            float s = 17 * S;
            var cx = Bounds.X + Bounds.Width / 2; var cy = Bounds.Y + Bounds.Height / 2;
            // put the transform back by hand: Save/Restore around a transform also resets the clip on some GDI+ builds
            var old = g.Transform;
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-60 * h);
            Icons.Reset(g, new RectangleF(-s / 2, -s / 2, s, s), Theme.Mix(Theme.TextDim, Tint, lit));
            g.Transform = old;
            old.Dispose();
        }
    }

    /// <summary>Row of mutually exclusive options with a sliding highlight.</summary>
    public class Segmented : Widget
    {
        public string[] Options;
        public Func<int> Get;
        public Action<int> Set;
        int hoverIndex = -1;
        public override bool Interactive => true;

        RectangleF Cell(float i)
        {
            float pad = 3 * S, w = (Bounds.Width - pad * 2) / Options.Length;
            return new RectangleF(Bounds.X + pad + i * w, Bounds.Y + pad, w, Bounds.Height - pad * 2);
        }

        int IndexAt(PointF p)
        {
            for (int i = 0; i < Options.Length; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }

        public override void MouseDown(PointF p, MouseButtons b)
        {
            int i = IndexAt(p);
            if (i >= 0 && Enabled && b == MouseButtons.Left) Set(i);
        }

        public override void MouseUp(PointF p, bool inside, MouseButtons b) { }

        public void TrackHover(PointF p) { int i = IndexAt(p); if (i != hoverIndex) { hoverIndex = i; Ui.Invalidate(); } }

        protected override void PaintSelf(Graphics g)
        {
            float rad = 11 * S;
            Theme.FillRound(g, Bounds, rad, Theme.Black(90));
            Theme.StrokeRound(g, Bounds, rad, Theme.White(22), Math.Max(1, S));
            int sel = Get();
            if (!IsHover) hoverIndex = -1;
            for (int i = 0; i < Options.Length; i++)
                if (i == hoverIndex && i != sel) Theme.FillRound(g, Cell(i), 8 * S, Theme.White(12));
            float pos = Anim.To(this, "pos", sel, 16f);
            var c = Cell(pos);
            Theme.Glow(g, c, 8 * S, Theme.AccentMid, 6 * S, 40);
            using (var b = Theme.AccentBrush(c, 80)) Theme.FillRound(g, c, 8 * S, b);
            using (var b = Theme.AccentBrush(c, 230)) Theme.StrokeRound(g, c, 8 * S, b, Math.Max(1, S));
            for (int i = 0; i < Options.Length; i++)
            {
                float closeness = 1 - Math.Min(1, Math.Abs(pos - i));
                var col = Theme.Mix(Enabled ? (i == hoverIndex ? Theme.Text : Theme.TextMuted) : Theme.TextDim, Theme.AccentLight, closeness);
                Theme.Txt(g, Options[i], Theme.F(12.5f, semibold: true), col, Cell(i), Theme.H.Center);
            }
        }
    }

    /// <summary>Click, then press a key combination. Esc cancels, Backspace/Delete clears.</summary>
    public class KeyBindButton : Widget
    {
        public Func<Hotkey> Get;
        public Action<Hotkey> Set;
        public string EmptyText = "Not set";
        public override bool Interactive => true;
        bool Capturing => Ui.Capturing == this;

        public override void Click() { if (Enabled) Ui.Form.BeginKeyCapture(this); }
        public override void RightClick(PointF p) { Set(new Hotkey()); Ui.Invalidate(); }
        public override string Tooltip => "Click and press a key. Right-click to clear.";

        public bool HandleKey(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Escape) return true;
            if (code == Keys.Back || code == Keys.Delete) { Set(new Hotkey()); return true; }
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin || code == Keys.None)
                return false;
            uint mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
            Set(new Hotkey(code, mods));
            return true;
        }

        protected override void PaintSelf(Graphics g)
        {
            var k = Get();
            bool cap = Capturing;
            float h = HoverT;
            float rad = 10 * S;
            var r = Bounds;
            if (cap)
            {
                float pulse = 0.5f + 0.5f * (float)Math.Sin(Anim.Time * 6);
                Theme.Glow(g, r, rad, Theme.AccentMid, 8 * S, (int)(40 + 40 * pulse));
                Theme.FillRound(g, r, rad, Theme.AccentSoft);
                using (var b = Theme.AccentBrush(r)) Theme.StrokeRound(g, r, rad, b, Math.Max(1, 1.4f * S));
                Theme.Txt(g, "Press a key…", Theme.F(13, semibold: true), Theme.Text, r, Theme.H.Center);
                Ui.Form?.RequestAnimationFrame();
                return;
            }
            Theme.FillRound(g, r, rad, Theme.Mix(Theme.Surface2, Theme.Surface3, h));
            Theme.StrokeRound(g, r, rad, Theme.White((int)(24 + 26 * h)), Math.Max(1, S));
            if (k == null || k.IsEmpty)
            {
                float s = 16 * S;
                var ir = new RectangleF(r.X + 12 * S, r.Y + (r.Height - s) / 2, s, s);
                Icons.Keyboard(g, ir, Theme.TextDim);
                Theme.Txt(g, EmptyText, Theme.F(12.5f), Theme.TextDim, new RectangleF(ir.Right + 8 * S, r.Y, r.Right - ir.Right - 16 * S, r.Height));
                return;
            }
            // one keycap per part: Ctrl + Shift + F8
            var f = Theme.F(12, semibold: true);
            string[] parts = k.ToString().Split('+');
            float x = r.X + 8 * S, ch = r.Height - 12 * S;
            foreach (var part in parts)
            {
                string p = part.Length == 0 ? "+" : part;
                float w = Math.Max(ch, Theme.Measure(p, f).Width + 14 * S);
                if (x + w > r.Right - 6 * S) break;
                Theme.KeyCap(g, new RectangleF(x, r.Y + 6 * S, w, ch), p, f);
                x += w + 5 * S;
            }
        }
    }

    /// <summary>Round color swatch.</summary>
    public class Swatch : Widget
    {
        public Color Color;
        public bool IsCustom;
        public Func<bool> SelectedWhen;
        public Action OnClick;
        public override bool Interactive => true;
        public override void Click() => OnClick?.Invoke();
        public override string Tooltip => IsCustom ? "Pick any color" : null;

        protected override void PaintSelf(Graphics g)
        {
            bool sel = SelectedWhen();
            float h = HoverT;
            float selT = Anim.To(this, "sel", sel ? 1 : 0, 14f);
            var r = RectangleF.Inflate(Bounds, -4 * S + 1.5f * S * h, -4 * S + 1.5f * S * h);
            if (selT > 0) Theme.RadialGlow(g, new PointF(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width * 1.2f, r.Height * 1.2f, IsCustom ? Theme.AccentMid : Color, (int)(110 * selT));
            if (IsCustom)
            {
                int n = 36;
                for (int i = 0; i < n; i++)
                    using (var b = new SolidBrush(Theme.HueColor(i * 360.0 / n, 0.8, 1)))
                        g.FillPie(b, r.X, r.Y, r.Width, r.Height, i * 360f / n - 0.5f, 360f / n + 1f);
                var inner = RectangleF.Inflate(r, -r.Width * 0.26f, -r.Height * 0.26f);
                using (var b = new SolidBrush(sel ? Color : Theme.Surface2)) g.FillEllipse(b, inner);
            }
            else using (var b = new SolidBrush(Color)) g.FillEllipse(b, r);
            using (var p = new Pen(Theme.White(40), Math.Max(1, S))) g.DrawEllipse(p, r);
            if (selT > 0)
                using (var p = new Pen(Theme.White((int)(255 * selT)), 2 * S)) g.DrawEllipse(p, RectangleF.Inflate(Bounds, -0.5f * S, -0.5f * S));
        }
    }

    /// <summary>Vertical scrolling region. Children are laid out by the owner with the current Offset.</summary>
    public class ScrollPanel : Widget
    {
        public float Offset, ContentHeight;
        public Action Relayout;
        float target;

        public float MaxOffset => Math.Max(0, ContentHeight - Bounds.Height);

        public override bool Wheel(int delta)
        {
            if (ContentHeight <= Bounds.Height) return false;
            target = Math.Max(0, Math.Min(MaxOffset, target - Math.Sign(delta) * 70 * S));
            Ui.Form?.RequestAnimationFrame();
            return true;
        }

        public void ClampOffset() { Offset = Math.Max(0, Math.Min(MaxOffset, Offset)); target = Math.Max(0, Math.Min(MaxOffset, target)); }

        public override Widget HitTest(PointF p)
        {
            if (!Visible || !Bounds.Contains(p)) return null;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var h = Children[i].HitTest(p);
                if (h != null) return h;
            }
            return this; // so the wheel reaches us
        }

        public override Cursor Cursor => null;

        public override void Paint(Graphics g)
        {
            // smooth scrolling
            float o = Anim.To(this, "scroll", target, 18f);
            if (Math.Abs(o - Offset) > 0.1f) { Offset = o; Relayout?.Invoke(); }

            if (!g.VisibleClipBounds.IntersectsWith(RectangleF.Inflate(Bounds, 32 * S, 32 * S))) return;
            var state = g.Save();
            g.SetClip(new RectangleF(Bounds.X - 12 * S, Bounds.Y, Bounds.Width + 24 * S, Bounds.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
            foreach (var c in Children) if (c.Visible && c.Bounds.IntersectsWith(RectangleF.Inflate(Bounds, 12 * S, 12 * S))) c.Paint(g);
            g.Restore(state);
            if (ContentHeight > Bounds.Height + 1)
            {
                float trackH = Bounds.Height - 8 * S;
                float h = Math.Max(30 * S, trackH * Bounds.Height / ContentHeight);
                float y = Bounds.Y + 4 * S + (trackH - h) * (MaxOffset <= 0 ? 0 : Offset / MaxOffset);
                Theme.FillRound(g, new RectangleF(Bounds.Right - 3.5f * S, y, 3 * S, h), 1.5f * S, Theme.White(IsHover ? 70 : 40));
            }
        }
    }
}
