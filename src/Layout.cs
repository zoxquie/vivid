using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Vivid
{
    public abstract class Page : Widget
    {
        protected static AppController Ctl => Ui.Form.Ctl;
        protected RectangleF Area;
        public abstract void Build();
        public abstract void Layout(RectangleF area);
        public void Relayout() => Layout(Area);

        protected void PaintHeader(Graphics g, string title, string subtitle)
        {
            var box = new RectangleF(Area.X - 4 * S, Area.Y - 8 * S, Area.Width + 8 * S, 70 * S);
            RenderCache.Draw(g, RenderCache.Key("header", title, subtitle, Area.Width), box, gg =>
            {
                Theme.GradientTitle(gg, title, 27, new PointF(Area.X, Area.Y - 1 * S), Theme.Text, Theme.Accent);
                Theme.Txt(gg, subtitle, Theme.F(13.5f), Theme.TextMuted, new RectangleF(Area.X, Area.Y + 36 * S, Area.Width, 22 * S));
            });
        }

        protected const float HeaderH = 74;
    }

    // ------------------------------------------------------------------ rows & sections

    public abstract class Row : Widget
    {
        public float H = 52;
        public abstract void Layout(RectangleF r);
    }

    /// <summary>Label | slider | reset | value.</summary>
    public sealed class SliderRow : Row
    {
        readonly Label name;
        public readonly Slider Slider;
        readonly ResetButton reset;
        readonly ValueBox value;
        readonly Label note;
        bool compact;

        /// <summary>Name and value on one line with the slider full width underneath (the main page's look).</summary>
        public bool Compact
        {
            get => compact;
            set { compact = value; value_Plain(value); H = value ? 60 : 52; }
        }

        /// <summary>Extra height the panel hands out when it has room to spare (compact rows only).</summary>
        public float Spare;

        bool HasNote => note != null && !string.IsNullOrEmpty(note.TextFn());

        /// <summary>Compact rows grow a line when a note shows under the slider.</summary>
        public void UpdateHeight() { if (compact) H = 60 + (HasNote ? 16 : 0) + Spare; }

        void value_Plain(bool on)
        {
            value.Plain = on; reset.Plain = on;
            name.Font = on ? Theme.F(13.5f, semibold: true) : Theme.F(14);
        }

        public SliderRow(string label, double min, double max, double def, double step, Func<double> get, Action<double> set,
            Func<double, string> format, SliderLook look = SliderLook.Neutral, Color? tint = null, Func<bool> enabled = null, Func<string> noteFn = null)
        {
            name = Add(new Label { Text = label, Font = Theme.F(14), ColorFn = () => enabled == null || enabled() ? Theme.Text : Theme.TextDim });
            Slider = Add(new Slider { Min = min, Max = max, Default = def, Step = step, Get = get, Set = set, Look = look, EnabledWhen = enabled, Format = format });
            reset = Add(new ResetButton { Changed = () => Math.Abs(get() - def) > step / 2, OnClick = () => set(def), Tint = tint ?? Theme.Text, EnabledWhen = enabled });
            value = Add(new ValueBox
            {
                TextFn = () => format(get()),
                EditTextFn = () => format(get()),
                OnCommit = s => { if (ParamSpec.TryParse(s, out double v)) set(Math.Max(min, Math.Min(max, Math.Round(v / step) * step))); },
                EnabledWhen = enabled,
            });
            if (noteFn != null) note = Add(new Label { TextFn = noteFn, Font = Theme.F(11.5f), Color = Theme.TextDim });
        }

        public override void Layout(RectangleF r)
        {
            Bounds = r;
            if (compact)
            {
                float lh = 26 * S, top = r.Y + (4 + Spare / 2) * S;
                float vbw = 70 * S, rbw = 26 * S;
                value.Bounds = new RectangleF(r.Right - vbw, top, vbw, lh);
                reset.Bounds = new RectangleF(value.Bounds.X - 2 * S - rbw, top, rbw, lh);
                name.Bounds = new RectangleF(r.X, top, reset.Bounds.X - r.X, lh);
                // the slider's track is inset 10 px, so widen it to line the track up with the text
                Slider.Bounds = new RectangleF(r.X - 10 * S, top + lh + 2 * S, r.Width + 20 * S, 26 * S);
                if (note != null) { note.Visible = HasNote; note.Bounds = new RectangleF(r.X, Slider.Bounds.Bottom - 3 * S, r.Width, 16 * S); }
                return;
            }
            float labelW = Math.Min(150 * S, r.Width * 0.28f);
            float vw = 86 * S, bh = 38 * S, rw = 38 * S, gap = 12 * S;
            float cy = r.Y + r.Height / 2;
            bool hasNote = note != null && !string.IsNullOrEmpty(note.TextFn());
            name.Bounds = new RectangleF(r.X, r.Y, labelW, hasNote ? r.Height * 0.62f : r.Height);
            if (note != null) note.Bounds = new RectangleF(r.X, r.Y + r.Height * 0.52f, labelW + 40 * S, r.Height * 0.4f);
            value.Bounds = new RectangleF(r.Right - vw, cy - bh / 2, vw, bh);
            reset.Bounds = new RectangleF(value.Bounds.X - gap - rw, cy - bh / 2, rw, bh);
            Slider.Bounds = new RectangleF(r.X + labelW + gap, cy - 14 * S, reset.Bounds.X - gap - (r.X + labelW + gap), 28 * S);
        }

        public override void Paint(Graphics g)
        {
            Layout(Bounds); // the note can appear or disappear when the engine changes
            base.Paint(g);
        }
    }

    /// <summary>Title + description on the left, any control on the right.</summary>
    public sealed class ControlRow : Row
    {
        readonly Label title, desc;
        public readonly Widget Control;
        readonly float controlW, controlH;

        public ControlRow(string t, Func<string> d, Widget control, float w, float h = 36)
        {
            title = Add(new Label { Text = t, Font = Theme.F(14) });
            if (d != null) desc = Add(new Label { TextFn = d, Font = Theme.F(12.5f), Color = Theme.TextMuted, Wrap = true, VCenter = false });
            Control = Add(control);
            controlW = w; controlH = h;
        }

        public float Measure(float width)
        {
            float textW = width - controlW * S - 24 * S;
            float h = 22 * S;
            if (desc != null) h += Theme.Measure(desc.TextFn(), desc.Font, textW).Height + 2 * S;
            H = Math.Max(58, (h + 26 * S) / S);
            return H * S;
        }

        public override void Layout(RectangleF r)
        {
            Bounds = r;
            float cw = controlW * S, ch = controlH * S;
            float textW = r.Width - cw - 24 * S;
            float th = 22 * S;
            float dh = desc != null ? Theme.Measure(desc.TextFn(), desc.Font, textW).Height + 2 * S : 0;
            float top = r.Y + (r.Height - th - dh) / 2;
            title.Bounds = new RectangleF(r.X, top, textW, th);
            if (desc != null) desc.Bounds = new RectangleF(r.X, top + th, textW, dh + 2 * S);
            Control.Bounds = new RectangleF(r.Right - cw, r.Y + (r.Height - ch) / 2, cw, ch);
        }
    }

    /// <summary>Label on the left, a wide control spanning the slider area.</summary>
    public sealed class WideRow : Row
    {
        readonly Label name;
        public readonly Widget Control;
        readonly float maxW;

        public WideRow(string label, Widget control, float maxWidth = 0, float h = 54)
        {
            name = Add(new Label { Text = label, Font = Theme.F(14) });
            Control = Add(control);
            maxW = maxWidth;
            H = h;
        }

        public override void Layout(RectangleF r)
        {
            Bounds = r;
            float labelW = Math.Min(150 * S, r.Width * 0.28f);
            float gap = 12 * S;
            name.Bounds = new RectangleF(r.X, r.Y, labelW, r.Height);
            float w = r.Width - labelW - gap;
            if (maxW > 0) w = Math.Min(w, maxW * S);
            float h = 36 * S;
            Control.Bounds = new RectangleF(r.X + labelW + gap, r.Y + (r.Height - h) / 2, w, h);
            if (Control is SwatchRow sr) sr.Layout(Control.Bounds);
        }
    }

    public sealed class SwatchRow : Widget
    {
        public void Layout(RectangleF r)
        {
            float d = 30 * S, gap = 8 * S;
            float x = r.X;
            foreach (var c in Children) { c.Bounds = new RectangleF(x, r.Y + (r.Height - d) / 2, d, d); x += d + gap; }
        }
    }

    /// <summary>Rounded card with an icon tile, caption, title, optional "Reset section" link and rows.</summary>
    public sealed class Section : Widget
    {
        public string Caption, Title;
        public Icons.Drawer Icon;
        public Color Tint = Theme.Text;
        public Button ResetLink;
        public readonly List<Row> Rows = new List<Row>();
        public float HeaderH = 66;
        public Func<string> FooterNote;
        /// <summary>A small coloured caption and rows, drawn inside a shared panel instead of its own card.</summary>
        public bool Compact;
        float Pad => Compact ? 0 : 22;
        float Head => Compact ? (Caption == null ? 0 : 30) : (Caption == null && Title == null ? 8 : HeaderH);

        public Section(string caption, string title, Icons.Drawer icon, Color tint, Action reset = null)
        {
            Caption = caption; Title = title; Icon = icon; Tint = tint;
            if (reset != null) ResetLink = Add(new Button { Text = "Reset", Icon = Icons.Reset, IconSize = 14, Style = BtnStyle.Link, Hint = "Reset this section", OnClick = reset });
        }

        public T AddRow<T>(T row) where T : Row { Rows.Add(row); Add(row); return row; }

        public float MeasureHeight(float width)
        {
            float h = Head * S;
            foreach (var r in Rows)
            {
                if (!r.Visible) continue;
                (r as SliderRow)?.UpdateHeight();
                h += r is ControlRow cr ? cr.Measure(width - 2 * Pad * S) : r.H * S;
            }
            h += (Compact ? 2 : 10) * S;
            if (FooterNote != null && !string.IsNullOrEmpty(FooterNote()))
                h += Theme.Measure(FooterNote(), Theme.F(12.5f), width - 2 * Pad * S).Height + 14 * S;
            return h;
        }

        public void Layout(RectangleF r)
        {
            Bounds = r;
            if (ResetLink != null)
            {
                float w = Theme.Measure("Reset", Theme.F(13, true)).Width + 44 * S;
                ResetLink.Bounds = new RectangleF(r.Right - 14 * S - w, r.Y + 20 * S, w, 30 * S);
            }
            float y = r.Y + Head * S;
            foreach (var row in Rows)
            {
                if (!row.Visible) continue;
                float h = row is ControlRow cr ? cr.Measure(r.Width - 2 * Pad * S) : row.H * S;
                row.Layout(new RectangleF(r.X + Pad * S, y, r.Width - 2 * Pad * S, h));
                y += h;
            }
        }

        protected override void PaintSelf(Graphics g)
        {
            // The panel, header and dividers only change with size and layout: draw them once and reuse.
            var r = Bounds;
            var sb = new System.Text.StringBuilder();
            foreach (var row in Rows) if (row.Visible) sb.Append((int)Math.Round((row.Bounds.Y - r.Y) * 4)).Append(',');
            string note = FooterNote?.Invoke() ?? "";
            string key = RenderCache.Key("section", Caption, Title, Tint, r.Width, r.Height, HeaderH, sb.ToString(), note,
                Icon == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Icon.Method));
            RenderCache.Draw(g, key, RectangleF.Inflate(r, 1, 1), gg => PaintSection(gg, note));
        }

        void PaintSection(Graphics g, string footer)
        {
            var r = Bounds;
            if (Compact)
            {
                if (Caption == null) return;
                float d = 6 * S;
                var dot = new RectangleF(r.X, r.Y + 10 * S, d, d);
                Theme.RadialGlow(g, new PointF(dot.X + d / 2, dot.Y + d / 2), 16 * S, 16 * S, Tint, 70);
                using (var b = new SolidBrush(Tint)) g.FillEllipse(b, dot);
                Theme.Txt(g, Caption.ToUpperInvariant(), Theme.F(10.5f, semibold: true), Theme.WithAlpha(Theme.Mix(Tint, Color.White, 0.35f), 215),
                    new RectangleF(dot.Right + 9 * S, r.Y + 3 * S, r.Width - 20 * S, 20 * S));
                return;
            }
            Theme.Card(g, r, 18);
            if (Caption != null || Title != null)
            {
                // tinted light leaking from the icon corner
                var st = g.Save();
                using (var clip = Theme.Round(r, 18 * S)) g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                Theme.RadialGlow(g, new PointF(r.X + 40 * S, r.Y + 34 * S), 220 * S, 120 * S, Tint, 26);
                g.Restore(st);

                var tile = new RectangleF(r.X + 22 * S, r.Y + 14 * S, 42 * S, 42 * S);
                Theme.Glow(g, tile, 12 * S, Tint, 7 * S, 30);
                using (var b = Theme.Gradient(tile, Theme.Mix(Theme.Surface3, Tint, 0.22f), Theme.Mix(Theme.Surface2, Tint, 0.06f), 90f)) Theme.FillRound(g, tile, 12 * S, b);
                using (var b = Theme.Gradient(tile, Theme.WithAlpha(Tint, 170), Theme.WithAlpha(Tint, 40), 90f)) Theme.StrokeRound(g, tile, 12 * S, b, Math.Max(1, S));
                Icon?.Invoke(g, RectangleF.Inflate(tile, -11 * S, -11 * S), Theme.Mix(Tint, Color.White, 0.15f));
                Theme.Txt(g, (Caption ?? "").ToUpperInvariant(), Theme.F(10.5f, semibold: true), Theme.WithAlpha(Tint, 200), new RectangleF(tile.Right + 14 * S, tile.Y + 1 * S, r.Width / 2, 16 * S));
                Theme.Txt(g, Title, Theme.F(17, semibold: true), Theme.Text, new RectangleF(tile.Right + 14 * S, tile.Y + 15 * S, r.Width / 2, 26 * S));
            }
            bool first = true;
            foreach (var row in Rows)
            {
                if (!row.Visible) continue;
                if (!first || Caption != null) Theme.Divider(g, row.Bounds.X - 6 * S, row.Bounds.Right + 6 * S, row.Bounds.Y, 22);
                first = false;
            }
            if (FooterNote != null)
            {
                string n = footer;
                if (!string.IsNullOrEmpty(n))
                {
                    var last = Rows.LastOrDefault(x => x.Visible);
                    float y = last != null ? last.Bounds.Bottom + 4 * S : r.Y + HeaderH * S;
                    Theme.Txt(g, n, Theme.F(12.5f), Theme.TextMuted, new RectangleF(r.X + 22 * S, y, r.Width - 44 * S, r.Bottom - y - 8 * S), Theme.H.Left, false, true);
                }
            }
        }
    }

    // ------------------------------------------------------------------ top bar

    public sealed class TopBar : Widget
    {
        Button tabFilters, tabHolo, settings, power, wMin, wMax, wClose, update;
        Panel tabsBg, pill;
        static AppController Ctl => Ui.Form.Ctl;

        public void Build()
        {
            Children.Clear();
            tabsBg = Add(new Panel
            {
                Painter = (g, r) => { Theme.Card(g, r, 14); }
            });
            tabFilters = Add(new Button { Text = "Filters", Icon = Icons.Sliders, Style = BtnStyle.Tab, IconSize = 17, Radius = 9, SelectedWhen = () => Ui.Form.Current == PageId.Filters, OnClick = () => Ui.Form.ShowPage(PageId.Filters) });
            tabHolo = Add(new Button { Text = "Holo", Icon = Icons.Crosshair, Style = BtnStyle.Tab, IconSize = 17, Radius = 9, SelectedWhen = () => Ui.Form.Current == PageId.Holo, OnClick = () => Ui.Form.ShowPage(PageId.Holo) });

            pill = Add(new Panel { Painter = PaintPill });
            settings = Add(new Button { Icon = Icons.Gear, Style = BtnStyle.Subtle, IconSize = 19, Hint = "Settings", SelectedWhen = () => Ui.Form.Current == PageId.Settings, OnClick = () => Ui.Form.ShowPage(PageId.Settings) });
            settings.IconColorFn = () => Ui.Form.Current == PageId.Settings ? Theme.AccentHover : Theme.TextMuted;
            power = Add(new PowerButton { Icon = Icons.Power, Style = BtnStyle.Subtle, IconSize = 19, OnClick = () => Ctl.SetFiltersOn(!Ctl.Settings.FiltersOn) });
            power.IconColorFn = () => Ctl.Settings.FiltersOn ? Theme.Green : Theme.Red;
            update = Add(new Button { Text = "Restart to update", Icon = Icons.Reset, IconSize = 15, Style = BtnStyle.Primary, Radius = 10, Hint = "A new version of " + AppInfo.Name + " is installed", OnClick = Updater.RestartNow });
            wMin = Add(new Button { Icon = Icons.WinMin, Style = BtnStyle.Ghost, Radius = 8, IconSize = 18, OnClick = () => Ui.Form.WindowState = FormWindowState.Minimized });
            wMax = Add(new Button { Icon = Icons.WinMax, Style = BtnStyle.Ghost, Radius = 8, IconSize = 18, OnClick = () => Ui.Form.ToggleMaximize() });
            wClose = Add(new CloseButton { Icon = Icons.WinClose, Style = BtnStyle.Ghost, Radius = 8, IconSize = 18, OnClick = () => Ui.Form.Close() });
            Sync();
        }

        public void Sync()
        {
            if (power == null) return;
            power.Hint = Ctl.Settings.FiltersOn ? "Turn filters off" : "Turn filters on";
            wMax.Icon = Ui.Form.WindowState == FormWindowState.Maximized ? (Icons.Drawer)Icons.WinRestore : Icons.WinMax;
            wClose.Hint = Ctl.Settings.CloseToTray ? "Hide to tray" : "Quit";
        }

        sealed class CloseButton : Button
        {
            protected override void PaintSelf(Graphics g)
            {
                float h = HoverT;
                if (h > 0) Theme.FillRound(g, Bounds, 8 * S, Theme.WithAlpha(Theme.Hex("#E5303F"), (int)(230 * h)));
                Icon(g, new RectangleF(Bounds.X + (Bounds.Width - 18 * S) / 2, Bounds.Y + (Bounds.Height - 18 * S) / 2, 18 * S, 18 * S), Theme.Mix(Theme.TextMuted, Color.White, h));
            }
        }

        /// <summary>Power button with a glowing ring while filters are on.</summary>
        sealed class PowerButton : Button
        {
            protected override void PaintSelf(Graphics g)
            {
                bool on = Ctl.Settings.FiltersOn;
                float t = Anim.To(this, "on", on ? 1 : 0, 10f);
                var c = Theme.Mix(Theme.Red, Theme.Green, t);
                Theme.Glow(g, Bounds, Radius * S, c, (6 + 4 * HoverT) * S, (int)(40 + 30 * HoverT));
                base.PaintSelf(g);
                Theme.StrokeRound(g, Bounds, Radius * S, Theme.WithAlpha(c, (int)(120 + 80 * HoverT)), Math.Max(1, 1.2f * S));
            }
        }

        public void Layout(RectangleF r)
        {
            Bounds = r;
            float cy = r.Y + r.Height / 2;
            float x = 24 * S;
            float tabsX = 64 * S + Theme.Measure(AppInfo.Name, Theme.F(20, semibold: true)).Width + 26 * S;
            float tw1 = Theme.Measure("Filters", Theme.F(13.5f, true)).Width + 62 * S;
            float tw2 = Theme.Measure("Holo", Theme.F(13.5f, true)).Width + 62 * S;
            tabsBg.Bounds = new RectangleF(tabsX, cy - 22 * S, tw1 + tw2 + 10 * S, 44 * S);
            tabFilters.Bounds = new RectangleF(tabsX + 4 * S, cy - 18 * S, tw1, 36 * S);
            tabHolo.Bounds = new RectangleF(tabFilters.Bounds.Right + 2 * S, cy - 18 * S, tw2, 36 * S);
            tabFilters.Radius = tabHolo.Radius = 10;

            float right = r.Right - 14 * S;
            float bw = 44 * S, bh = 34 * S;
            wClose.Bounds = new RectangleF(right - bw, cy - bh / 2, bw, bh);
            wMax.Bounds = new RectangleF(wClose.Bounds.X - bw - 2 * S, cy - bh / 2, bw, bh);
            wMin.Bounds = new RectangleF(wMax.Bounds.X - bw - 2 * S, cy - bh / 2, bw, bh);
            float sep = wMin.Bounds.X - 14 * S;
            float sq = 40 * S;
            power.Bounds = new RectangleF(sep - 14 * S - sq, cy - sq / 2, sq, sq);
            settings.Bounds = new RectangleF(power.Bounds.X - 10 * S - sq, cy - sq / 2, sq, sq);
            float edge = settings.Bounds.X;
            update.Visible = Updater.Status == Updater.State.Ready;
            if (update.Visible)
            {
                float uw = Theme.Measure("Restart to update", Theme.F(13.5f, true)).Width + 52 * S;
                update.Bounds = new RectangleF(edge - 10 * S - uw, cy - 18 * S, uw, 36 * S);
                edge = update.Bounds.X;
            }
            float pw = Math.Min(300 * S, Math.Max(150 * S, edge - 12 * S - (tabsBg.Bounds.Right + 20 * S)));
            pill.Bounds = new RectangleF(edge - 12 * S - pw, cy - sq / 2, pw, sq);
            pill.Visible = pill.Bounds.X > tabsBg.Bounds.Right + 10 * S;
        }

        public RectangleF PillRect => pill != null && pill.Visible ? pill.Bounds : RectangleF.Empty;

        void PaintPill(Graphics g, RectangleF r)
        {
            bool on = Ctl.Settings.FiltersOn;
            var health = Ctl.Engine.Health;
            Color c = !on ? Theme.TextMuted : health == EngineHealth.Error ? Theme.Red : health == EngineHealth.Warning ? Theme.Amber : Theme.Green;
            float rad = 12 * S;
            Theme.Card(g, r, 12, Theme.Surface, on ? Theme.WithAlpha(c, 150) : (Color?)null);
            if (on)
            {
                var st = g.Save();
                using (var clip = Theme.Round(r, rad)) g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                Theme.RadialGlow(g, new PointF(r.X + 20 * S, r.Y + r.Height / 2), 120 * S, 40 * S, c, 45);
                g.Restore(st);
            }
            float d = 8 * S;
            Theme.PulseDot(g, new PointF(r.X + 20 * S, r.Y + r.Height / 2), d, c, on && health != EngineHealth.Error);
            string state = on ? (health == EngineHealth.Error ? "ERROR" : "ACTIVE") : "OFF";
            var fs = Theme.F(11.5f, semibold: true);
            float sw = Theme.Measure(state, fs).Width;
            Theme.Txt(g, state, fs, c, new RectangleF(r.X + 34 * S, r.Y, sw + 4 * S, r.Height));
            float sx = r.X + 34 * S + sw + 12 * S;
            using (var p = new Pen(Theme.WithAlpha(c, on ? 70 : 30), Math.Max(1, S))) g.DrawLine(p, sx, r.Y + 11 * S, sx, r.Bottom - 11 * S);
            Theme.Txt(g, Ctl.Selected.Name, Theme.F(13.5f, semibold: true), on ? Theme.Text : Theme.TextMuted, new RectangleF(sx + 12 * S, r.Y, r.Right - sx - 24 * S, r.Height));
        }

        protected override void PaintSelf(Graphics g)
        {
            var r = Bounds;
            // hairline that glows in the accent colours toward the middle
            using (var lg = Theme.Gradient(new RectangleF(0, r.Bottom - 1, r.Width, 2), Color.Transparent, Color.Transparent, 0f))
            {
                lg.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(5)
                {
                    Colors = new[] { Theme.White(8), Theme.WithAlpha(Theme.Accent, 80), Theme.WithAlpha(Theme.AccentEnd, 70), Theme.WithAlpha(Theme.Cyan, 60), Theme.White(8) },
                    Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
                };
                using (var p = new Pen(lg, Math.Max(1, S))) g.DrawLine(p, 0, r.Bottom - 0.5f, r.Right, r.Bottom - 0.5f);
            }
            float cy = r.Y + r.Height / 2;
            Theme.DrawLogo(g, new RectangleF(22 * S, cy - 16 * S, 32 * S, 32 * S));
            Theme.GradientTitle(g, AppInfo.Name, 20, new PointF(64 * S, cy - 13 * S), Theme.Text, Theme.AccentHover);
            using (var p = new Pen(Theme.White(22), Math.Max(1, S)))
            {
                float sx = wMin.Bounds.X - 14 * S;
                g.DrawLine(p, sx, cy - 14 * S, sx, cy + 14 * S);
            }
        }
    }

    // ------------------------------------------------------------------ dialogs

    public abstract class Dialog : Widget
    {
        protected RectangleF Card;
        protected float W = 440, Hh = 220;
        public abstract void Build();
        public virtual void Opened() { }
        public virtual void Accept() { }

        public virtual void Layout(RectangleF full)
        {
            Bounds = full;
            Card = new RectangleF(full.X + (full.Width - W * S) / 2, full.Y + (full.Height - Hh * S) / 2, W * S, Hh * S);
        }

        public override Widget HitTest(PointF p)
        {
            var h = base.HitTest(p);
            return h ?? this; // block clicks to the page behind
        }

        public override bool Interactive => true;
        public override Cursor Cursor => null;

        protected override void PaintSelf(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(185, 3, 4, 7))) g.FillRectangle(b, Bounds);
            Theme.RadialGlow(g, new PointF(Card.X + Card.Width / 2, Card.Y + Card.Height / 2), Card.Width * 0.9f, Card.Height * 1.1f, Theme.AccentMid, 55);
            Theme.Glow(g, Card, 20 * S, Theme.Black(255), 18 * S, 90);
            Theme.Card(g, Card, 20, Theme.Hex("#0D1B27"), Theme.WithAlpha(Theme.Accent, 150));
        }
    }

    public sealed class PromptDialog : Dialog
    {
        readonly string title, message, initial, okText;
        readonly Action<string> ok;
        Label lt, lm;
        Button bOk, bCancel;
        RectangleF field;

        public PromptDialog(string title, string message, string initial, string okText, Action<string> ok)
        { this.title = title; this.message = message; this.initial = initial; this.okText = okText; this.ok = ok; Hh = 236; }

        public override void Build()
        {
            Children.Clear();
            lt = Add(new Label { Text = title, Font = Theme.F(18, semibold: true) });
            lm = Add(new Label { Text = message, Font = Theme.F(13.5f), Color = Theme.TextMuted, Wrap = true, VCenter = false });
            bCancel = Add(new Button { Text = "Cancel", Style = BtnStyle.Secondary, OnClick = () => Ui.Form.CloseDialog() });
            bOk = Add(new Button { Text = okText, Style = BtnStyle.Primary, OnClick = Accept });
        }

        public override void Layout(RectangleF full)
        {
            base.Layout(full);
            float p = 26 * S;
            lt.Bounds = new RectangleF(Card.X + p, Card.Y + 22 * S, Card.Width - 2 * p, 30 * S);
            lm.Bounds = new RectangleF(Card.X + p, Card.Y + 56 * S, Card.Width - 2 * p, 40 * S);
            field = new RectangleF(Card.X + p, Card.Y + 104 * S, Card.Width - 2 * p, 44 * S);
            float bw = 110 * S, bh = 40 * S;
            bOk.Bounds = new RectangleF(Card.Right - p - bw, Card.Bottom - p - bh + 4 * S, bw, bh);
            bCancel.Bounds = new RectangleF(bOk.Bounds.X - 10 * S - bw, bOk.Bounds.Y, bw, bh);
            if (Ui.Form != null && Ui.Form.IsEditingAny) Ui.Form.MoveEditor(field);
        }

        public override void Opened() => Ui.Form.BeginTextEdit(field, initial, null, center: false, persistent: true);

        public override void Accept()
        {
            string text = Ui.Form.EditText;
            if (string.IsNullOrWhiteSpace(text)) return;
            Ui.Form.CloseDialog();
            ok(text.Trim());
        }

        protected override void PaintSelf(Graphics g)
        {
            base.PaintSelf(g);
            Theme.Glow(g, field, 10 * S, Theme.AccentMid, 6 * S, 40);
            Theme.FillRound(g, field, 10 * S, Theme.Surface2);
            using (var b = Theme.AccentBrush(field)) Theme.StrokeRound(g, field, 10 * S, b, Math.Max(1, 1.5f * S));
        }
    }

    public sealed class ConfirmDialog : Dialog
    {
        readonly string title, message, okText;
        readonly Action ok;
        readonly bool danger;
        Label lt, lm;
        Button bOk, bCancel;

        public ConfirmDialog(string title, string message, string okText, Action ok, bool danger)
        { this.title = title; this.message = message; this.okText = okText; this.ok = ok; this.danger = danger; Hh = 196; }

        public override void Build()
        {
            Children.Clear();
            lt = Add(new Label { Text = title, Font = Theme.F(18, semibold: true) });
            lm = Add(new Label { Text = message, Font = Theme.F(13.5f), Color = Theme.TextMuted, Wrap = true, VCenter = false });
            bCancel = Add(new Button { Text = "Cancel", Style = BtnStyle.Secondary, OnClick = () => Ui.Form.CloseDialog() });
            bOk = Add(new Button { Text = okText, Style = danger ? BtnStyle.Danger : BtnStyle.Primary, OnClick = Accept });
        }

        public override void Layout(RectangleF full)
        {
            base.Layout(full);
            float p = 26 * S;
            lt.Bounds = new RectangleF(Card.X + p, Card.Y + 22 * S, Card.Width - 2 * p, 30 * S);
            lm.Bounds = new RectangleF(Card.X + p, Card.Y + 58 * S, Card.Width - 2 * p, 60 * S);
            float bw = 110 * S, bh = 40 * S;
            bOk.Bounds = new RectangleF(Card.Right - p - bw, Card.Bottom - p - bh + 4 * S, bw, bh);
            bCancel.Bounds = new RectangleF(bOk.Bounds.X - 10 * S - bw, bOk.Bounds.Y, bw, bh);
        }

        public override void Accept() { Ui.Form.CloseDialog(); ok(); }
    }

    public sealed class KeyDialog : Dialog
    {
        readonly string title;
        readonly Func<Hotkey> get;
        readonly Action<Hotkey> set;
        Label lt, lm;
        KeyBindButton kb;
        Button bDone, bClear;

        public KeyDialog(string title, Func<Hotkey> get, Action<Hotkey> set)
        { this.title = title; this.get = get; this.set = set; Hh = 250; }

        public override void Build()
        {
            Children.Clear();
            lt = Add(new Label { Text = title, Font = Theme.F(18, semibold: true) });
            lm = Add(new Label { Text = "Press the key or combination you want. It works while you are in game. Esc cancels.", Font = Theme.F(13.5f), Color = Theme.TextMuted, Wrap = true, VCenter = false });
            kb = Add(new KeyBindButton { Get = () => get() ?? new Hotkey(), Set = k => { set(k); Ui.Invalidate(); } });
            bClear = Add(new Button { Text = "Clear", Style = BtnStyle.Secondary, OnClick = () => { set(new Hotkey()); Ui.Invalidate(); } });
            bDone = Add(new Button { Text = "Done", Style = BtnStyle.Primary, OnClick = Accept });
        }

        public override void Layout(RectangleF full)
        {
            base.Layout(full);
            float p = 26 * S;
            lt.Bounds = new RectangleF(Card.X + p, Card.Y + 22 * S, Card.Width - 2 * p, 30 * S);
            lm.Bounds = new RectangleF(Card.X + p, Card.Y + 56 * S, Card.Width - 2 * p, 44 * S);
            kb.Bounds = new RectangleF(Card.X + p, Card.Y + 110 * S, Card.Width - 2 * p, 46 * S);
            float bw = 110 * S, bh = 40 * S;
            bDone.Bounds = new RectangleF(Card.Right - p - bw, Card.Bottom - p - bh + 4 * S, bw, bh);
            bClear.Bounds = new RectangleF(bDone.Bounds.X - 10 * S - bw, bDone.Bounds.Y, bw, bh);
        }

        public override void Opened() => Ui.Form.BeginKeyCapture(kb);

        public override void Accept() => Ui.Form.CloseDialog();
    }
}
