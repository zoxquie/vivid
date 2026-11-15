using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace Vivid
{
    // ================================================================== profile card

    public sealed class ProfileCard : Widget
    {
        public readonly Profile P;
        readonly Button more;
        static AppController Ctl => Ui.Form.Ctl;

        // Sample colours used to preview what a user profile does.
        static readonly Color[] Samples = { Theme.Hex("#4F7A3A"), Theme.Hex("#6E8FB8"), Theme.Hex("#B9875C"), Theme.Hex("#8A8F96"), Theme.Hex("#B5443B") };

        public ProfileCard(Profile p)
        {
            P = p;
            if (!p.BuiltIn || p.Id != Presets.CustomId)
                more = Add(new Button { Icon = Icons.More, Style = BtnStyle.Ghost, Radius = 8, IconSize = 18, Hint = "More", OnClick = () => Ui.Form.ProfileMenu(P, new PointF(more.Bounds.X, more.Bounds.Bottom + 4 * S)) });
        }

        public override bool Interactive => true;
        public override void Click() => Ctl.Select(P);
        public override void RightClick(PointF p) => Ui.Form.ProfileMenu(P, p);

        public override Widget HitTest(PointF p)
        {
            if (!Visible || (Clip.HasValue && !Clip.Value.Contains(p)) || !Bounds.Contains(p)) return null;
            if (more != null && (Ui.Hover == this || Ui.Hover == more) && more.Bounds.Contains(p)) return more;
            return this;
        }

        public void Layout(RectangleF r)
        {
            Bounds = r;
            if (more != null) more.Bounds = new RectangleF(r.Right - 10 * S - 30 * S, r.Y + (r.Height - 30 * S) / 2, 30 * S, 30 * S);
        }

        static Color Filtered(FilterValues v, Color c)
        {
            ColorMath.ApplyToPixel(v, c.R / 255.0, c.G / 255.0, c.B / 255.0, out double ro, out double go, out double bo);
            return Color.FromArgb((int)(ColorMath.Clamp01(ro) * 255), (int)(ColorMath.Clamp01(go) * 255), (int)(ColorMath.Clamp01(bo) * 255));
        }

        // Where each preset's artwork is framed inside the scene: focusX, focusY, zoom.
        static (float fx, float fy, float zoom) Framing(string art)
        {
            switch (art)
            {
                case "summer": return (0.34f, 0.80f, 1.7f);
                case "winter": return (0.04f, 0.90f, 1.6f);
                case "desert": return (0.30f, 0.90f, 1.6f);
                case "night": return (0.97f, 0.88f, 1.9f);
                case "vivid": return (0.30f, 0.62f, 1.3f);
                default: return (0.5f, 0.6f, 1.4f);
            }
        }

        // Framing for the screenshots built into Vivid.exe: keep the interesting part clear of the name.
        static (float fx, float fy, float zoom) BundledFraming(string art)
        {
            switch (art)
            {
                case "winter": return (0.5f, 0.36f, 1f);    // trees, rock and skyline instead of plain snow
                case "night": return (0.22f, 0.56f, 1.35f); // moves the moon to the right of the name
                default: return (0.5f, 0.55f, 1f);
            }
        }

        /// <summary>Colour matrix that greys out and darkens artwork on cards that aren't picked.</summary>
        static System.Drawing.Imaging.ImageAttributes ArtLook(float t)
        {
            float sat = 0.35f + 0.65f * t, bri = 0.62f + 0.38f * t;
            float lr = 0.2126f * (1 - sat), lg = 0.7152f * (1 - sat), lb = 0.0722f * (1 - sat);
            var m = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { (lr + sat) * bri, lr * bri, lr * bri, 0f, 0f },
                new[] { lg * bri, (lg + sat) * bri, lg * bri, 0f, 0f },
                new[] { lb * bri, lb * bri, (lb + sat) * bri, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(m);
            ia.SetWrapMode(WrapMode.TileFlipXY);
            return ia;
        }


        public override void Paint(Graphics g)
        {
            Layout(Bounds);
            var r = Bounds;
            bool sel = Ctl.EditSelected.Id == P.Id;
            bool live = Ctl.Editing && Ctl.Settings.FiltersOn && Ctl.Settings.SelectedProfileId == P.Id && !sel;
            bool hov = Ui.Hover == this || (more != null && Ui.Hover == more);
            float h = Q(Anim.To(this, "hover", hov ? 1 : 0, 16f));
            float selT = Q(Anim.To(this, "sel", sel ? 1 : 0, 12f));
            string keyText = Ctl.KeyFor(P)?.ToString() ?? "";
            Image shot = CardArt.For(P);
            Image art = shot ?? (!string.IsNullOrEmpty(P.Art) ? Scene.Biome(P.Art) : null);
            var v = P.Values;
            string ck = RenderCache.Key("pcard", P.Id, P.Name, r.Width, r.Height, h, selT, sel, live, hov && more != null, keyText,
                art == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(art),
                P.BuiltIn ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3},{3:F1},{4:F3}", v.Brightness, v.Contrast, v.Saturation, v.Hue, v.NightVision));
            float pad = 10 * S + 2;
            RenderCache.Draw(g, ck, RectangleF.Inflate(r, pad, pad), gg => PaintCard(gg, r, sel, live, hov, h, selT, keyText, art, shot));
            if (more != null && hov) more.Paint(g);
        }

        void PaintCard(Graphics g, RectangleF r, bool sel, bool live, bool hov, float h, float selT, string keyText, Image art, Image shot)
        {
            float t = Math.Max(selT, h * 0.75f); // how "lit" the card is
            float rad = 11 * S;

            if (selT > 0) Theme.Glow(g, r, rad, Theme.Accent, 9 * S, (int)(60 * selT));

            using (var path = Theme.Round(r, rad))
            {
                var state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                using (var b = new SolidBrush(Theme.Mix(Theme.Surface2, Theme.Surface3, h))) g.FillRectangle(b, r);

                if (art != null)
                {
                    var (fx, fy, zoom) = shot == null ? Framing(P.Art) : CardArt.IsBundled(P) ? BundledFraming(P.Art) : (0.5f, 0.55f, 1f);
                    float sa = r.Width / r.Height;
                    float w = art.Width / zoom, hh = w / sa;
                    if (hh > art.Height) { hh = art.Height; w = hh * sa; }
                    var src = new RectangleF((art.Width - w) * fx, (art.Height - hh) * fy, w, hh);
                    var im = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    var dest = Rectangle.FromLTRB((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
                    using (var ia = ArtLook(t))
                        g.DrawImage(art, dest, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    g.InterpolationMode = im;
                }
                else if (!P.BuiltIn)
                {
                    // user profile: a gradient made from colours run through this profile
                    var artR = new RectangleF(r.X + r.Width * 0.3f, r.Y, r.Width * 0.7f, r.Height);
                    using (var b = Theme.Gradient(artR, Filtered(P.Values, Theme.Hex("#5E86B4")), Filtered(P.Values, Theme.Hex("#4C7A37")), 90f)) g.FillRectangle(b, artR);
                    Theme.RadialGlow(g, new PointF(artR.Right - 26 * S, artR.Y + 14 * S), 40 * S, 30 * S, Filtered(P.Values, Theme.Hex("#FFD27A")), 200);
                    using (var dim = new SolidBrush(Theme.WithAlpha(Theme.Bg, (int)(110 * (1 - t))))) g.FillRectangle(dim, artR);
                }
                else if (selT > 0)
                {
                    Theme.RadialGlow(g, new PointF(r.X + 30 * S, r.Y + r.Height / 2), 160 * S, 60 * S, Theme.Accent, (int)(40 * selT));
                }

                // darken the left side so the name always reads
                using (var fade = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Theme.WithAlpha(Theme.Bg, 230), Theme.WithAlpha(Theme.Bg, 0), 0f))
                {
                    fade.InterpolationColors = new ColorBlend(3)
                    {
                        Colors = new[] { Theme.WithAlpha(Theme.Bg, art != null || !P.BuiltIn ? 205 : 0), Theme.WithAlpha(Theme.Bg, art != null ? (int)(120 - 60 * t) : 0), Theme.WithAlpha(Theme.Bg, art != null ? (int)(40 - 30 * t) : 0) },
                        Positions = new[] { 0f, 0.55f, 1f },
                    };
                    g.FillRectangle(fade, r);
                }
                using (var sheen = Theme.Gradient(r, Theme.White((int)(10 + 12 * h)), Theme.White(0), 90f)) g.FillRectangle(sheen, r.X, r.Y, r.Width, r.Height * 0.5f);
                g.Restore(state);
            }

            if (selT > 0)
            {
                using (var p = new Pen(Theme.WithAlpha(Theme.AccentHover, (int)(255 * selT)), 2f * S))
                using (var path = Theme.Round(RectangleF.Inflate(r, -1f * S, -1f * S), rad - 1 * S))
                    g.DrawPath(p, path);
            }
            if (selT < 1)
                Theme.StrokeRound(g, r, rad, Theme.WithAlpha(Theme.Mix(Theme.Border, Theme.BorderStrong, h), (int)(255 * (1 - selT))), Math.Max(1, S));

            float x = r.X + 16 * S;
            float right = r.Right - 12 * S;
            if (more != null && hov) right = more.Bounds.X - 6 * S; // the ⋯ button is drawn on top afterwards

            if (live)
            {
                var lf = Theme.F(9.5f, semibold: true);
                const string lt = "ON SCREEN";
                float lw = Theme.Measure(lt, lf).Width + 14 * S, lh = 20 * S;
                var lr = new RectangleF(right - lw, r.Y + (r.Height - lh) / 2, lw, lh);
                Theme.FillRound(g, lr, lh / 2, Theme.WithAlpha(Theme.Bg, 200));
                Theme.StrokeRound(g, lr, lh / 2, Theme.WithAlpha(Theme.Accent, 150), Math.Max(1, S));
                Theme.Txt(g, lt, lf, Theme.AccentHover, lr, Theme.H.Center);
                right = lr.X - 8 * S;
            }

            if (keyText != "")
            {
                var kf = Theme.F(11.5f, semibold: true);
                string ks = keyText;
                float kw = Math.Max(26 * S, Theme.Measure(ks, kf).Width + 14 * S), kh = 24 * S;
                var kr = new RectangleF(right - kw, r.Y + (r.Height - kh) / 2, kw, kh);
                Theme.KeyCap(g, kr, ks, kf, lit: sel);
                right = kr.X - 8 * S;
            }

            bool user = !P.BuiltIn;
            var nameR = new RectangleF(x, r.Y, Math.Max(10, right - x), user ? r.Height * 0.62f : r.Height);
            Color nameCol = Theme.Mix(Theme.Hex("#8A9AA4"), Theme.Text, t);
            Theme.TxtShadow(g, P.Name, Theme.F(15, semibold: true, bold: true), nameCol, nameR);
            if (user)
            {
                float d = 8 * S, gap = 5 * S, y = r.Y + r.Height * 0.61f;
                for (int i = 0; i < Samples.Length; i++)
                    using (var b = new SolidBrush(Filtered(P.Values, Samples[i]))) g.FillEllipse(b, x + i * (d + gap), y, d, d);
            }
        }
    }

    // ================================================================== filters page

    public sealed class FiltersPage : Page
    {
        // Layout: presets and profiles on the left, the big before/after preview in the middle,
        // the adjustments on the right and one action bar under the preview and adjustments.
        Panel leftCard, adjustCard, bar;
        ScrollPanel leftScroll, center;
        Caption lblBuiltIn, lblMine;
        readonly List<ProfileCard> builtInCards = new List<ProfileCard>(), userCards = new List<ProfileCard>();
        Panel emptyBox;
        Section secTone, secColor, secNight;
        LivePreview preview;
        Toggle autoToggle;
        Label autoLabel;
        Button apply, discard, resetAll, save, importBtn, exportBtn;
        int barLevel; // 0 = full labels; higher levels shorten things so the bar fits narrow windows

        static FilterValues V => Ctl.CurrentValues;

        public override void Build()
        {
            Children.Clear();
            builtInCards.Clear();
            userCards.Clear();

            // ---- left: presets and profiles
            leftCard = Add(new Panel { Painter = (g, r) => Theme.Card(g, r, 20) });
            leftScroll = Add(new ScrollPanel { Relayout = LayoutLeft });
            lblBuiltIn = leftScroll.Add(new Caption("Presets"));
            builtInCards.Add(leftScroll.Add(new ProfileCard(AppController.CustomProfile)));
            foreach (var p in Ctl.BuiltIns) builtInCards.Add(leftScroll.Add(new ProfileCard(p)));
            lblMine = leftScroll.Add(new Caption("My profiles"));
            lblMine.TextFn = () => Ctl.UserProfiles.Count == 0 ? "MY PROFILES" : $"MY PROFILES  ·  {Ctl.UserProfiles.Count}";
            foreach (var p in Ctl.UserProfiles) userCards.Add(leftScroll.Add(new ProfileCard(p)));
            emptyBox = leftScroll.Add(new Panel { Painter = PaintEmpty });
            importBtn = leftScroll.Add(new Button { Icon = Icons.Import, Style = BtnStyle.Ghost, IconSize = 15, Hint = "Import profiles from a file or a share code" });
            importBtn.OnClick = () => Ui.Form.ImportMenu(new PointF(importBtn.Bounds.X, importBtn.Bounds.Bottom + 4 * S));
            exportBtn = leftScroll.Add(new Button { Icon = Icons.Export, Style = BtnStyle.Ghost, IconSize = 15, Hint = "Export profiles to a file or copy a share code" });
            exportBtn.OnClick = () => Ui.Form.ExportMenu(new PointF(exportBtn.Bounds.X, exportBtn.Bounds.Bottom + 4 * S));

            // ---- middle: the preview
            preview = Add(new LivePreview { Hero = true });

            // ---- right: adjustments in one panel
            adjustCard = Add(new Panel { Painter = PaintAdjust });
            resetAll = Add(new Button
            {
                Text = "Reset all", Icon = Icons.Reset, IconSize = 13, Style = BtnStyle.Link, Hint = "Set every slider back to neutral",
                EnabledWhen = () => !V.IsNeutral,
                OnClick = () => Ctl.ResetValues((FilterParam[])Enum.GetValues(typeof(FilterParam)))
            });
            center = Add(new ScrollPanel { Relayout = LayoutCenter });

            secTone = center.Add(new Section("Light and tone", null, null, Theme.Amber) { Compact = true });
            secTone.AddRow(MakeRow(FilterParam.Brightness, SliderLook.Neutral, Theme.Amber));
            secTone.AddRow(MakeRow(FilterParam.Contrast, SliderLook.Neutral, Theme.Amber));

            secColor = center.Add(new Section("Color", null, null, Theme.Green) { Compact = true });
            secColor.AddRow(MakeRow(FilterParam.Saturation, SliderLook.Green, Theme.Green,
                note: () => Ctl.Settings.Engine == EngineMode.GammaRamp ? (Ctl.Settings.UseNvVibrance ? "via NVIDIA vibrance" : "needs Screen matrix") : null));
            secColor.AddRow(MakeRow(FilterParam.Hue, SliderLook.Rainbow, Theme.Green,
                enabled: () => Ctl.Settings.Engine == EngineMode.ScreenMatrix,
                note: () => Ctl.Settings.Engine == EngineMode.GammaRamp ? "Screen matrix only" : null));

            secNight = center.Add(new Section("Night vision", null, null, Theme.Blue) { Compact = true });
            secNight.AddRow(MakeRow(FilterParam.NightVision, SliderLook.Accent, Theme.Blue));

            // ---- bottom bar
            bar = Add(new Panel { Painter = PaintBar });
            autoToggle = Add(new Toggle { Get = () => Ctl.Settings.AutoApply, Set = v => Ctl.SetAutoApply(v) });
            autoLabel = Add(new Label { Text = "Apply instantly", Font = Theme.F(13.5f), Color = Theme.TextMuted });
            autoToggle.Hint = "Off: changes wait in the preview until you press Apply";
            apply = Add(new Button { Text = "Apply", Style = BtnStyle.Primary, EnabledWhen = () => Ctl.PendingChanges, Hint = "Put the preview on your screen", OnClick = () => Ctl.Commit() });
            discard = Add(new Button { Text = "Discard", Style = BtnStyle.Ghost, Hint = "Throw away changes that aren't applied", OnClick = () => Ctl.Discard() });
            save = Add(new Button
            {
                TextFn = () => barLevel >= 2 ? "Save" : "Save as profile", Icon = Icons.Plus, Style = BtnStyle.Secondary, IconSize = 15, Hint = "Save these values as a profile",
                OnClick = () => Ui.Form.Prompt("Save as profile", "Saves the slider values to My profiles and applies them.", SuggestName(), "Save", n => { Ctl.SaveAsProfile(n); Ui.Form.ShowToast("Profile saved"); })
            });
        }

        static string SuggestName()
        {
            var p = Ctl.EditSelected;
            return p.Id == Presets.CustomId ? "My profile" : p.Name + " tweak";
        }

        static SliderRow MakeRow(FilterParam param, SliderLook look, Color tint, Func<bool> enabled = null, Func<string> note = null)
        {
            var spec = ParamSpec.Of(param);
            return new SliderRow(spec.Name, spec.Min, spec.Max, spec.Default, spec.Step,
                () => V.Get(param), v => Ctl.SetValue(param, v), spec.Format, look, tint, enabled, note) { Compact = true };
        }

        public override void Layout(RectangleF area)
        {
            Area = area;
            float gap = 16 * S;
            float top = area.Y + HeaderH * S;
            float bodyH = area.Bottom - top;
            float leftW = Math.Max(214 * S, Math.Min(258 * S, area.Width * 0.2f));
            float rightW = Math.Max(300 * S, Math.Min(372 * S, area.Width * 0.29f));
            float barH = 62 * S;

            leftCard.Bounds = new RectangleF(area.X, top, leftW, bodyH);
            leftScroll.Bounds = RectangleF.Inflate(leftCard.Bounds, -12 * S, -12 * S);
            LayoutLeft();

            float cx = leftCard.Bounds.Right + gap;
            float barY = area.Bottom - barH;
            bar.Bounds = new RectangleF(cx, barY, area.Right - cx, barH);
            float bodyBottom = barY - gap;

            float rx = area.Right - rightW;
            preview.Bounds = new RectangleF(cx, top, rx - gap - cx, bodyBottom - top);
            preview.Visible = preview.Bounds.Width > 160 * S && preview.Bounds.Height > 120 * S;

            adjustCard.Bounds = new RectangleF(rx, top, rightW, bodyBottom - top);
            float lw = Theme.Measure("Reset all", Theme.F(13, true)).Width + 34 * S;
            resetAll.Bounds = new RectangleF(adjustCard.Bounds.Right - 12 * S - lw, top + 13 * S, lw, 28 * S);
            center.Bounds = new RectangleF(rx + 20 * S, top + 54 * S, rightW - 40 * S, adjustCard.Bounds.Bottom - 10 * S - (top + 54 * S));
            LayoutCenter();
            LayoutBar();
        }

        void LayoutLeft()
        {
            var r = leftScroll.Bounds;
            float ch = 58 * S, cg = 8 * S;
            float content = 6 * S + 26 * S + builtInCards.Count * (ch + cg) + 16 * S + 30 * S
                + (userCards.Count == 0 ? 116 * S : userCards.Count * (ch + cg)) + 8 * S;
            float w = content > r.Height + 1 ? r.Width - 10 * S : r.Width;
            float y = r.Y + 6 * S - leftScroll.Offset;
            lblBuiltIn.Bounds = new RectangleF(r.X + 6 * S, y, w, 20 * S); y += 26 * S;
            foreach (var c in builtInCards) { c.Bounds = new RectangleF(r.X, y, w, ch); c.Clip = r; c.Layout(c.Bounds); y += ch + cg; }
            y += 16 * S;
            lblMine.Bounds = new RectangleF(r.X + 6 * S, y + 2 * S, w - 64 * S, 22 * S);
            float ib = 26 * S;
            exportBtn.Bounds = new RectangleF(r.X + w - ib, y, ib, ib);
            importBtn.Bounds = new RectangleF(exportBtn.Bounds.X - 4 * S - ib, y, ib, ib);
            y += 30 * S;
            foreach (var c in userCards) { c.Bounds = new RectangleF(r.X, y, w, ch); c.Clip = r; c.Layout(c.Bounds); y += ch + cg; }
            emptyBox.Visible = userCards.Count == 0;
            if (emptyBox.Visible) { emptyBox.Bounds = new RectangleF(r.X, y, w, 116 * S); y += 116 * S; }
            leftScroll.ContentHeight = y + leftScroll.Offset - r.Y + 8 * S;
            if (leftScroll.Offset > leftScroll.MaxOffset) { leftScroll.ClampOffset(); LayoutLeft(); }
        }

        void LayoutCenter()
        {
            var r = center.Bounds;
            float y = r.Y - center.Offset;
            float gap = 12 * S;
            var secs = new[] { secTone, secColor, secNight };
            var rows = secs.SelectMany(x => x.Rows).OfType<SliderRow>().ToList();
            foreach (var row in rows) row.Spare = 0;
            float total = secs.Sum(x => x.MeasureHeight(r.Width)) + gap * (secs.Length - 1);
            if (total < r.Height - 2 * S)
            {
                // room to spare: give the rows a little air (up to 12 px each), then the gaps between groups
                float extra = r.Height - 2 * S - total;
                float perRow = Math.Min(10f, extra / S * 0.75f / Math.Max(1, rows.Count));
                foreach (var row in rows) row.Spare = perRow;
                gap += Math.Min(8 * S, Math.Max(0, extra - perRow * S * rows.Count) / (secs.Length - 1));
                total = secs.Sum(x => x.MeasureHeight(r.Width)) + gap * (secs.Length - 1);
            }
            float w = total > r.Height + 1 ? r.Width - 12 * S : r.Width; // leave room for the scrollbar
            foreach (var s in secs)
            {
                float h = s.MeasureHeight(w);
                s.Layout(new RectangleF(r.X, y, w, h));
                y += h + gap;
            }
            center.ContentHeight = y - gap + center.Offset - r.Y;
            if (center.Offset > center.MaxOffset) { center.ClampOffset(); LayoutCenter(); }
        }

        void LayoutBar()
        {
            // Try full labels first, then shorter ones until the status text has room.
            for (barLevel = 0; barLevel <= 3; barLevel++)
            {
                LayoutBarAt();
                if (autoToggle.Bounds.X - 16 * S - (bar.Bounds.X + 44 * S) >= 150 * S) break;
            }
            if (barLevel > 3) barLevel = 3;
        }

        void LayoutBarAt()
        {
            var r = bar.Bounds;
            float cy = r.Y + r.Height / 2;
            float bh = 40 * S, gap = 8 * S;
            var bf = Theme.F(13.5f, true);
            float x = r.Right - 11 * S;
            apply.Visible = !Ctl.Settings.AutoApply;
            if (apply.Visible)
            {
                float aw = Math.Max(barLevel >= 3 ? 76 * S : 100 * S, Theme.Measure("Apply", bf).Width + 40 * S);
                apply.Bounds = new RectangleF(x - aw, cy - bh / 2, aw, bh); x = apply.Bounds.X - gap;
            }
            float sw = Theme.Measure(barLevel >= 2 ? "Save" : "Save as profile", bf).Width + 54 * S;
            save.Bounds = new RectangleF(x - sw, cy - bh / 2, sw, bh); x = save.Bounds.X - gap;
            discard.Visible = Ctl.PendingChanges;
            if (discard.Visible)
            {
                float dw = Theme.Measure("Discard", bf).Width + (barLevel >= 3 ? 22 : 34) * S;
                discard.Bounds = new RectangleF(x - dw, cy - bh / 2, dw, bh); x = discard.Bounds.X - gap;
            }
            x -= 10 * S; // separator
            autoLabel.Visible = barLevel < 3; // the switch keeps its tooltip
            float lw = autoLabel.Visible ? Theme.Measure("Apply instantly", autoLabel.Font).Width + 4 * S : 0;
            autoLabel.Bounds = new RectangleF(x - 10 * S - lw, r.Y, lw, r.Height);
            autoToggle.Bounds = new RectangleF(autoLabel.Bounds.X - (lw > 0 ? 10 * S : 0) - 42 * S, cy - 12 * S, 42 * S, 24 * S);
        }

        public override void Paint(Graphics g)
        {
            // Re-run layout: button widths depend on live text (status, pending changes).
            Layout(Area);
            PaintHeader(g, "Screen filters", Ctl.Settings.AutoApply
                ? "Color and visibility for your whole screen. Changes go on screen as you make them."
                : "Color and visibility for your whole screen. Try changes in the preview, then Apply.");
            base.Paint(g);
        }

        void PaintAdjust(Graphics g, RectangleF r)
        {
            RenderCache.Draw(g, RenderCache.Key("adjust", r.Width, r.Height), RectangleF.Inflate(r, 1, 1), gg =>
            {
                Theme.Card(gg, r, 20);
                Theme.Txt(gg, "Adjust", Theme.F(16, semibold: true), Theme.Text, new RectangleF(r.X + 20 * S, r.Y + 13 * S, r.Width / 2, 28 * S));
                Theme.Divider(gg, r.X + 20 * S, r.Right - 20 * S, r.Y + 50 * S, 18);
            });
        }

        void PaintEmpty(Graphics g, RectangleF r)
        {
            Theme.FillRound(g, r, 14 * S, Theme.White(4));
            using (var p = new Pen(Theme.WithAlpha(Theme.Accent, 90), Math.Max(1, S)) { DashPattern = new[] { 4f, 3f } })
            using (var path = Theme.Round(RectangleF.Inflate(r, -0.5f, -0.5f), 14 * S))
                g.DrawPath(p, path);
            float d = 30 * S;
            var ic = new RectangleF(r.X + (r.Width - d) / 2, r.Y + 16 * S, d, d);
            Theme.RadialGlow(g, new PointF(ic.X + d / 2, ic.Y + d / 2), d, d, Theme.AccentMid, 70);
            using (var b = Theme.AccentBrush(ic, 60)) g.FillEllipse(b, ic);
            Icons.Plus(g, RectangleF.Inflate(ic, -7 * S, -7 * S), Color.White);
            Theme.Txt(g, "No saved profiles", Theme.F(13.5f, semibold: true), Theme.Text, new RectangleF(r.X, r.Y + 52 * S, r.Width, 20 * S), Theme.H.Center);
            Theme.Txt(g, "Tune the sliders, then press\nSave as profile.", Theme.F(12), Theme.TextMuted,
                new RectangleF(r.X + 12 * S, r.Y + 74 * S, r.Width - 24 * S, 36 * S), Theme.H.Center, false, true);
        }

        void PaintBar(Graphics g, RectangleF r)
        {
            Theme.Card(g, r, 18);
            float x = r.X + 20 * S;
            var c = Ctl.StatusColor;
            float cy = r.Y + r.Height / 2;
            Theme.PulseDot(g, new PointF(x + 4 * S, cy), 8 * S, c, Ctl.Settings.FiltersOn && !Ctl.PendingChanges);
            float maxW = autoToggle.Bounds.X - 16 * S - (x + 20 * S);
            string line = Ctl.StatusLine;
            var eng = Ctl.Engine;
            bool engIssue = Ctl.Settings.FiltersOn && !Ctl.PendingChanges && (eng.Health == EngineHealth.Error || eng.Health == EngineHealth.Warning);
            var lf = Theme.F(14, semibold: true);
            float tw = Math.Min(Math.Max(20, maxW), Theme.Measure(line, lf).Width + 2 * S);
            Theme.Txt(g, line, lf, Theme.Text, new RectangleF(x + 20 * S, r.Y, tw, r.Height));
            if (engIssue && maxW - tw > 60 * S)
                Theme.Txt(g, "·  " + eng.StatusTitle, Theme.F(13), c, new RectangleF(x + 20 * S + tw + 6 * S, r.Y, maxW - tw - 6 * S, r.Height));
            float sx = autoLabel.Visible ? autoLabel.Bounds.Right + 14 * S : autoToggle.Bounds.Right + 14 * S;
            using (var p = new Pen(Theme.White(20), Math.Max(1, S))) g.DrawLine(p, sx, r.Y + 17 * S, sx, r.Bottom - 17 * S);
        }
    }

    // ================================================================== black holo page

    /// <summary>Before/after demo of the holo recolor on a picture, using the exact same matcher as the overlay.</summary>
    public sealed class HoloPreview : Widget
    {
        public float Split = 0.5f;
        bool dragging;
        Bitmap before, after;
        string key;
        readonly HoloMatcher matcher = new HoloMatcher();
        readonly HoloZones zones = new HoloZones();
        static AppController Ctl => Ui.Form.Ctl;

        public override bool Interactive => true;
        public override Cursor Cursor => Cursors.SizeWE;
        public override string Tooltip => dragging ? null : "Before: the holo as Rust draws it. After: what " + AppInfo.Name + " turns it into. The marker on the right has the same colour and stays as it is. Drag to compare.";
        public override void MouseDown(PointF p, MouseButtons b) { if (b == MouseButtons.Left) { dragging = true; Move(p); } }
        public override void MouseDrag(PointF p) { if (dragging) Move(p); }
        public override void MouseUp(PointF p, bool inside, MouseButtons b) { dragging = false; }
        public override void DoubleClick(PointF p) { Split = 0.5f; Ui.Invalidate(); }
        void Move(PointF p) { var r = ImageRect; Split = Math.Max(0.02f, Math.Min(0.98f, (p.X - r.X) / r.Width)); Ui.Invalidate(); }

        RectangleF ImageRect => RectangleF.Inflate(Bounds, -12 * S, -12 * S);

        unsafe void Build(int w, int h)
        {
            var hs = Ctl.Settings.Holo;
            var picture = Scene.PreviewPicture(Ctl.Find("builtin.summer"));
            string k = w + "x" + h + "|" + hs.Target + "|" + hs.Output + "|" + hs.Tolerance + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(picture);
            if (k == key && before != null) return;
            key = k;
            before?.Dispose(); after?.Dispose();
            before = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(before))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Scene.DrawCover(g, picture, new RectangleF(0, 0, w, h), 0.5f);
                using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, w, h);
                var col = Color.FromArgb(hs.Target);
                // something else in the world with the same colour (a painted marker): it must stay as it is
                var mk = new RectangleF(w * 0.84f, h * 0.6f, Math.Max(12, w * 0.07f), Math.Max(18, h * 0.12f));
                using (var post = new SolidBrush(Color.FromArgb(255, 58, 50, 44))) g.FillRectangle(post, mk.X + mk.Width * 0.42f, mk.Bottom, mk.Width * 0.16f, h * 0.2f);
                using (var paint = new SolidBrush(col)) using (var path = Theme.Round(mk, 3)) g.FillPath(paint, path);
                using (var band = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(band, mk.X, mk.Y + mk.Height * 0.45f, mk.Width, mk.Height * 0.12f);
                // a holosight seen from behind: dark frame, tinted glass, glowing ring and dot
                float cx = w / 2f, cy = h / 2f, R = Math.Min(w, h) * 0.08f;
                var glass = new RectangleF(cx - R * 2.6f, cy - R * 2.0f, R * 5.2f, R * 4.0f);
                using (var frame = new Pen(Color.FromArgb(235, 28, 30, 32), R * 0.45f)) using (var path = Theme.Round(glass, R * 0.5f)) g.DrawPath(frame, path);
                using (var tint = new SolidBrush(Color.FromArgb(40, 120, 200, 200))) using (var path = Theme.Round(glass, R * 0.5f)) g.FillPath(tint, path);
                for (int i = 4; i >= 1; i--)
                    using (var glow = new Pen(Color.FromArgb(28, col), R * 0.08f + i * R * 0.07f)) g.DrawEllipse(glow, cx - R, cy - R, 2 * R, 2 * R);
                using (var ring = new Pen(col, Math.Max(2f, R * 0.09f))) g.DrawEllipse(ring, cx - R, cy - R, 2 * R, 2 * R);
                float d = Math.Max(3f, R * 0.16f);
                using (var b = new SolidBrush(col)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                // the over-bright middle of the dot
                using (var hot = new SolidBrush(Color.FromArgb(235, 255, 236, 230))) g.FillEllipse(hot, cx - d * 0.22f, cy - d * 0.22f, d * 0.44f, d * 0.44f);
            }

            // run the real matcher over it, then lay its output on top
            after = (Bitmap)before.Clone();
            var rect = new Rectangle(0, 0, w, h);
            var src = before.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dst = after.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var overlay = new byte[w * h * 4];
            try
            {
                matcher.Resize(w, h);
                matcher.Configure(hs);
                // the same zones the live holo starts with: the aiming circle in the middle
                zones.Resize(matcher.TilesW, matcher.TilesH, w, h);
                matcher.Zone = zones.Zone;
                fixed (byte* op = overlay) matcher.Process((byte*)src.Scan0, op, Color.FromArgb(hs.Output));
                byte* dp = (byte*)dst.Scan0;
                for (int i = 0; i < w * h; i++)
                {
                    int a = overlay[i * 4 + 3];
                    if (a == 0) continue;
                    for (int c = 0; c < 3; c++) dp[i * 4 + c] = (byte)(overlay[i * 4 + c] + dp[i * 4 + c] * (255 - a) / 255);
                }
            }
            finally { before.UnlockBits(src); after.UnlockBits(dst); }
        }

        protected override void PaintSelf(Graphics g)
        {
            var r = Bounds;
            Theme.Card(g, r, 20);
            var img = ImageRect;
            int ix = (int)Math.Round(img.X), iy = (int)Math.Round(img.Y), iw = (int)Math.Round(img.Width), ih = (int)Math.Round(img.Height);
            if (iw < 10 || ih < 10) return;
            Build(iw, ih);
            using (var clip = Theme.Round(new RectangleF(ix, iy, iw, ih), 14 * S))
            {
                var st = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                int cut = Math.Max(0, Math.Min(iw, (int)Math.Round(iw * Split)));
                var im = g.InterpolationMode; var pm = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                if (cut > 0) g.DrawImage(before, new Rectangle(ix, iy, cut, ih), 0, 0, cut, ih, GraphicsUnit.Pixel);
                if (cut < iw) g.DrawImage(after, new Rectangle(ix + cut, iy, iw - cut, ih), cut, 0, iw - cut, ih, GraphicsUnit.Pixel);
                g.InterpolationMode = im; g.PixelOffsetMode = pm;
                float sx = ix + cut;
                using (var p = new Pen(Color.White, Math.Max(2, 2 * S))) g.DrawLine(p, sx, iy, sx, iy + ih);
                g.Restore(st);
                Theme.StrokeRound(g, new RectangleF(ix, iy, iw, ih), 14 * S, Theme.White(30), Math.Max(1, S));
                Badge(g, "BEFORE", new PointF(ix + 10 * S, iy + ih - 10 * S), true, Split > 0.2f, Color.Empty);
                Badge(g, "AFTER", new PointF(ix + iw - 10 * S, iy + ih - 10 * S), false, Split < 0.8f, Theme.Accent);
                var knob = new RectangleF(sx - 15 * S, iy + ih / 2f - 15 * S, 30 * S, 30 * S);
                Theme.FillRound(g, knob, 15 * S, Color.White);
                Theme.Txt(g, "‹›", Theme.F(12, semibold: true), Theme.Bg, knob, Theme.H.Center);
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

    public sealed class HoloPage : Page
    {
        HoloPreview preview;
        Panel bar;
        ScrollPanel right;
        Section secMain, secColor;
        Toggle onToggle;
        Label onLabel;
        Button reset, bindKey;
        readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        static HoloSettings H => Ctl.Settings.Holo;

        // colours a Rust holosight can be set to, and what it can turn into
        static readonly (string name, Color c)[] Reticles =
        {
            ("Red (Rust's default)", Theme.Hex("#FF2A22")), ("Green", Theme.Hex("#2BFF4A")), ("Blue", Theme.Hex("#2E8BFF")),
            ("Yellow", Theme.Hex("#FFE600")), ("Magenta", Theme.Hex("#FF2BD6")), ("Cyan", Theme.Hex("#00E5FF")), ("White", Theme.Hex("#FFFFFF")),
        };
        static readonly (string name, Color c)[] Outputs =
        {
            ("Black", Theme.Hex("#000000")), ("Dark grey", Theme.Hex("#2A2A2A")), ("White", Theme.Hex("#FFFFFF")),
            ("Green", Theme.Hex("#00FF6A")), ("Cyan", Theme.Hex("#00E5FF")), ("Yellow", Theme.Hex("#FFE600")),
        };

        public override void Build()
        {
            Children.Clear();
            preview = Add(new HoloPreview());
            right = Add(new ScrollPanel { Relayout = LayoutRight });

            secMain = right.Add(new Section("Rust", "Black holo", Icons.Crosshair, Theme.Accent, null));
            secMain.AddRow(new ControlRow("Black holo",
                () => "Turns just the holosight's reticle black, in hip fire and when aiming. Nothing else on screen changes. Rust has to run in borderless or windowed mode.",
                new Toggle { Get = () => H.Enabled, Set = v => Ctl.SetHoloEnabled(v) }, 44, 26));
            secMain.AddRow(new ControlRow("Only while Rust is in front",
                () => "Pauses whenever another window is in front, so nothing else on your screen changes.",
                new Toggle { Get = () => H.OnlyInRust, Set = v => { H.OnlyInRust = v; Ctl.HoloChanged(); } }, 44, 26));
            int n = Math.Max(1, Math.Min(4, Screen.AllScreens.Length));
            var opts = new string[n];
            opts[0] = "Main";
            for (int i = 1; i < n; i++) opts[i] = "Display " + (i + 1);
            var disp = secMain.AddRow(new WideRow("Display when Rust isn't in front", new Segmented { Options = opts, Get = () => Math.Min(H.Display, opts.Length - 1), Set = i => { H.Display = i; Ctl.HoloChanged(); } }, 120 * n));
            disp.Visible = n > 1;
            secMain.FooterNote = () => "How it finds the holo: aiming down sights, the reticle sits in the middle of your screen, so that spot is always watched. In hip fire it learns where the holo sits on your gun the first time you look around (about half a second of moving the mouse). Only those spots are recoloured, so red and orange things anywhere else stay as they are. It works from a copy of the picture on your graphics card and never touches Rust's files or memory.";

            secColor = right.Add(new Section("Colours", "Reticle colour", Icons.Drop, Theme.Green, null));
            secColor.AddRow(new WideRow("Reticle in Rust", Swatches(Reticles, () => H.Target, v => H.Target = v)));
            secColor.AddRow(new WideRow("Turns into", Swatches(Outputs, () => H.Output, v => H.Output = v)));
            secColor.AddRow(new SliderRow("Tolerance", 0, 100, 45, 1, () => H.Tolerance, v => { H.Tolerance = (int)Math.Round(v); Ctl.HoloChanged(); },
                v => (int)v + "%", SliderLook.Neutral, Theme.Text));
            secColor.FooterNote = () => "Pick the colour your holosight has in Rust. Raise Tolerance if parts of the reticle stay coloured, lower it if things right next to the reticle change.";

            bar = Add(new Panel { Painter = PaintBar });
            onToggle = Add(new Toggle { Get = () => H.Enabled, Set = v => Ctl.SetHoloEnabled(v) });
            onLabel = Add(new Label { Text = "Black holo", Font = Theme.F(14) });
            reset = Add(new Button { Text = "Reset", Icon = Icons.Reset, IconSize = 15, Style = BtnStyle.Secondary, Hint = "Back to the default settings", OnClick = () => Ctl.ResetHolo() });
            bindKey = Add(new Button
            {
                TextFn = () => !Ctl.Settings.ToggleHoloKey.IsEmpty ? Ctl.Settings.ToggleHoloKey.ToString() : "Bind key",
                Icon = Icons.Keyboard, Style = BtnStyle.Secondary, IconSize = 17, Hint = "Key that turns the black holo on or off",
                OnClick = () => Ui.Form.BindKeyDialog("Black holo on / off", () => Ctl.Settings.ToggleHoloKey, k => Ctl.SetGlobalKey("holo", k))
            });

            // keep the live numbers in the status bar fresh
            statsTimer.Tick -= OnStats;
            statsTimer.Tick += OnStats;
            statsTimer.Start();
        }

        void OnStats(object s, EventArgs e)
        {
            if (Ui.Form == null || Ui.Form.Current != PageId.Holo || !Ui.Form.Visible || !H.Enabled) return;
            Ui.Form.Invalidate(Rectangle.Ceiling(bar.Bounds));
        }

        static SwatchRow Swatches((string name, Color c)[] list, Func<int> get, Action<int> set)
        {
            var sw = new SwatchRow();
            foreach (var item in list)
            {
                var cc = item.c;
                sw.Add(new Swatch { Color = cc, Hint = item.name, SelectedWhen = () => (get() & 0xFFFFFF) == (cc.ToArgb() & 0xFFFFFF), OnClick = () => { set(cc.ToArgb()); Ctl.HoloChanged(); } });
            }
            var custom = sw.Add(new Swatch { IsCustom = true });
            custom.Color = Color.FromArgb(get());
            custom.SelectedWhen = () => list.All(p => (p.c.ToArgb() & 0xFFFFFF) != (get() & 0xFFFFFF));
            custom.OnClick = () =>
            {
                using (var dlg = new ColorDialog { Color = Color.FromArgb(get()), FullOpen = true })
                    if (dlg.ShowDialog(Ui.Form) == DialogResult.OK) { set(dlg.Color.ToArgb()); custom.Color = dlg.Color; Ctl.HoloChanged(); }
            };
            return sw;
        }

        public override void Layout(RectangleF area)
        {
            Area = area;
            float gap = 16 * S;
            float top = area.Y + HeaderH * S;
            float barH = 66 * S;
            float barY = area.Bottom - barH;
            float leftW = Math.Max(300 * S, Math.Min(520 * S, area.Width * 0.42f));
            preview.Bounds = new RectangleF(area.X, top, leftW, barY - gap - top);
            right.Bounds = new RectangleF(area.X + leftW + gap, top, area.Width - leftW - gap, barY - gap - top);
            LayoutRight();
            bar.Bounds = new RectangleF(area.X, barY, area.Width, barH);
            float cy = bar.Bounds.Y + barH / 2, bh = 40 * S;
            float x = bar.Bounds.Right - 12 * S;
            float kw = Math.Max(118 * S, Theme.Measure(bindKey.TextFn(), Theme.F(13.5f, true)).Width + 56 * S);
            bindKey.Bounds = new RectangleF(x - kw, cy - bh / 2, kw, bh); x = bindKey.Bounds.X - 10 * S;
            float rw = Theme.Measure("Reset", Theme.F(13.5f, true)).Width + 52 * S;
            reset.Bounds = new RectangleF(x - rw, cy - bh / 2, rw, bh); x = reset.Bounds.X - 18 * S;
            float lw = Theme.Measure("Black holo", Theme.F(14)).Width + 4 * S;
            onLabel.Bounds = new RectangleF(x - 8 * S - lw, bar.Bounds.Y, lw, barH);
            onToggle.Bounds = new RectangleF(onLabel.Bounds.X - 10 * S - 42 * S, cy - 12 * S, 42 * S, 24 * S);
        }

        void LayoutRight()
        {
            var r = right.Bounds;
            float y = r.Y - right.Offset, gap = 14 * S;
            var secs = new[] { secMain, secColor };
            float total = secs.Sum(x => x.MeasureHeight(r.Width)) + gap * (secs.Length - 1);
            float w = total > r.Height + 1 ? r.Width - 12 * S : r.Width;
            foreach (var s in secs)
            {
                float h = s.MeasureHeight(w);
                s.Layout(new RectangleF(r.X, y, w, h));
                y += h + gap;
            }
            right.ContentHeight = y - gap + right.Offset - r.Y;
            if (right.Offset > right.MaxOffset) { right.ClampOffset(); LayoutRight(); }
        }

        public override void Paint(Graphics g)
        {
            Layout(Area);
            PaintHeader(g, "Black holo", "Turns the Rust holosight's reticle black on screen, whether you're aiming or not.");
            base.Paint(g);
        }

        void PaintBar(Graphics g, RectangleF r)
        {
            Theme.Card(g, r, 18);
            float x = r.X + 22 * S;
            Theme.Txt(g, "STATUS", Theme.F(10.5f, semibold: true), Theme.TextDim, new RectangleF(x, r.Y + 12 * S, 200 * S, 16 * S));
            var o = Ctl.Holo;
            var st = o?.State ?? HoloOverlay.RunState.Off;
            string text; Color c; bool pulse = false;
            if (!H.Enabled) { text = "Off"; c = Theme.TextDim; }
            else if (st == HoloOverlay.RunState.Unsupported) { text = !Native.IsWindows ? "Only works on Windows" : o.Problem ?? "Stopped: something went wrong (see error.log)"; c = Theme.Red; }
            else if (st == HoloOverlay.RunState.WaitingForRust) { text = "On · waiting for Rust to be in front"; c = Theme.Amber; }
            else if (st == HoloOverlay.RunState.Running)
            {
                string where = o.HoloFound ? "holo found" : o.HipLearned ? "watching for the holo" : "look around once to find the hip-fire holo";
                text = "Working · " + where + " · " + o.Fps + " fps"; c = Theme.Green; pulse = true;
            }
            else { text = "Starting…"; c = Theme.Amber; }
            Theme.PulseDot(g, new PointF(x + 4 * S, r.Y + 41 * S), 8 * S, c, pulse);
            Theme.Txt(g, text, Theme.F(14, semibold: true), Theme.Text, new RectangleF(x + 22 * S, r.Y + 29 * S, Math.Max(20, onToggle.Bounds.X - x - 46 * S), 24 * S));
            float sx = onLabel.Bounds.Right + 9 * S;
            using (var p = new Pen(Theme.White(22), Math.Max(1, S))) g.DrawLine(p, sx, r.Y + 18 * S, sx, r.Bottom - 18 * S);
        }
    }

    // ================================================================== settings page

    public sealed class SettingsPage : Page
    {
        ScrollPanel scroll;
        readonly List<Section> sections = new List<Section>();
        static bool gammaUnlocked;

        public override void Build()
        {
            Children.Clear();
            sections.Clear();
            gammaUnlocked = ColorEngine.GammaRangeUnlocked;
            scroll = Add(new ScrollPanel { Relayout = LayoutScroll });
            var s = Ctl.Settings;

            var eng = new Section("Display", "Filter engine", Icons.Bolt, Theme.Accent);
            eng.AddRow(new ControlRow("Method",
                () => s.Engine == EngineMode.ScreenMatrix
                    ? "Screen matrix: full control over brightness, contrast, saturation and hue. Works in windowed and borderless fullscreen."
                    : "Gamma ramp: uses the monitor's lookup table, so it also works in exclusive fullscreen. No hue control; saturation uses NVIDIA Digital Vibrance.",
                new Segmented { Options = new[] { "Screen matrix", "Gamma ramp" }, Get = () => (int)s.Engine, Set = i => { Ctl.SetEngine((EngineMode)i); Ui.Form.DoLayout(); } }, 300));
            eng.AddRow(new ControlRow("NVIDIA Digital Vibrance",
                () => "In Gamma ramp mode, drive the Saturation slider with the driver's vibrance setting. Your own driver value is put back when you quit. Status: " + Ctl.Engine.NvStatus + ".",
                new Toggle { Get = () => s.UseNvVibrance, Set = v => { Ctl.SetUseNv(v); Ui.Form.DoLayout(); } }, 44, 24));
            eng.AddRow(new ControlRow("Full gamma range",
                () => "Windows limits how far gamma curves may move, which can cap strong night vision. Unlocking needs admin approval and a restart. Currently: " + (gammaUnlocked ? "unlocked." : "limited by Windows."),
                new Button { Text = "Unlock", Style = BtnStyle.Secondary, EnabledWhen = () => !gammaUnlocked, OnClick = () => { Ctl.UnlockGammaRange(); gammaUnlocked = ColorEngine.GammaRangeUnlocked; Ui.Form.DoLayout(); } }, 110, 38));
            sections.Add(scroll.Add(eng));

            var keys = new Section("Keyboard", "Hotkeys", Icons.Keyboard, Theme.Blue);
            keys.AddRow(KeyRow("Filters on / off", "Turns every filter off and back on.", () => s.ToggleFiltersKey, "filters"));
            keys.AddRow(KeyRow("Next profile", "Cycles through Custom, the built-in presets and your profiles.", () => s.NextProfileKey, "next"));
            keys.AddRow(KeyRow("Black holo on / off", "Turns the Rust black holo on or off.", () => s.ToggleHoloKey, "holo"));
            keys.AddRow(KeyRow("Show / hide " + AppInfo.Name, "Brings this window up from the tray.", () => s.ShowWindowKey, "window"));
            keys.FooterNote = () => "Hotkeys work while you play. A key bound here is reserved for " + AppInfo.Name + ", so pick keys your game doesn't use. Each profile can also get its own key from its ⋯ menu." +
                (Ctl.HotkeyFailures.Count > 0 ? "  Already used by another app: " + string.Join(", ", Ctl.HotkeyFailures) + "." : "");
            sections.Add(scroll.Add(keys));

            var beh = new Section("App", "Behavior", Icons.Gear, Theme.Green);
            beh.AddRow(new ControlRow("Launch with Windows", () => "Start " + AppInfo.Name + " in the tray when you sign in, with your last profile applied.",
                new Toggle { Get = () => s.StartWithWindows, Set = v => Ctl.SetStartWithWindows(v) }, 44, 24));
            beh.AddRow(new ControlRow("Start minimized", () => "Open straight to the tray instead of showing this window.",
                new Toggle { Get = () => s.StartMinimized, Set = v => { s.StartMinimized = v; Ctl.SaveSoon(); } }, 44, 24));
            beh.AddRow(new ControlRow("Keep running in the tray", () => "The close button hides " + AppInfo.Name + " so your filters stay on. Quit from the tray menu.",
                new Toggle { Get = () => s.CloseToTray, Set = v => { s.CloseToTray = v; Ctl.SaveSoon(); Ui.Form.Invalidate(); } }, 44, 24));
            beh.AddRow(new ControlRow("On-screen messages", () => "Show a short message at the top of the screen when a hotkey changes something.",
                new Toggle { Get = () => s.ShowOsd, Set = v => { s.ShowOsd = v; Ctl.SaveSoon(); } }, 44, 24));
            sections.Add(scroll.Add(beh));

            var maint = new Section("Maintenance", "Restore and files", Icons.Shield, Theme.Amber);
            maint.AddRow(new ControlRow("Restore normal colors", () => "Turns filters off and puts the screen, gamma and vibrance back to how they were.",
                new Button { Text = "Restore", Style = BtnStyle.Secondary, OnClick = () => { Ctl.SetFiltersOn(false); Ctl.Engine.ResetDisplay(); Ui.Form.ShowToast("Normal colors restored"); } }, 110, 38));
            maint.AddRow(new ControlRow("Preview screenshot",
                () => "Use one of your own game screenshots for the before/after preview. " + (CardArt.HasCustomPreview ? "Currently: your screenshot." : "Currently: each preset's picture."),
                new Button { Text = "Choose…", Style = BtnStyle.Secondary, OnClick = () => Ui.Form.PickPreviewImage() }, 110, 38));
            maint.AddRow(new ControlRow("Settings folder", () => Storage.Folder,
                new Button { Text = "Open", Style = BtnStyle.Secondary, Icon = Icons.Folder, IconSize = 16, OnClick = OpenFolder }, 110, 38));
            maint.AddRow(new ControlRow("About", () => AppInfo.Name + " " + Program.Version + ", made by fjord. Changes colors at the display level only. It never reads or modifies game files or game memory.",
                new Button { Text = AppInfo.SiteLabel, Style = BtnStyle.Secondary, Icon = Icons.Link, IconSize = 16, Hint = "Open " + AppInfo.Site, OnClick = Program.OpenSite }, 120, 38));
            sections.Add(scroll.Add(maint));

            var upd = new Section("Updates", "Stay up to date", Icons.Reset, Theme.Green);
            upd.AddRow(new ControlRow("Update automatically",
                () => AppInfo.Name + " checks GitHub (" + AppInfo.Repo + ") for new versions and swaps the new one in by itself. Nothing to reinstall.",
                new Toggle { Get = () => s.AutoUpdate, Set = v => { s.AutoUpdate = v; Ctl.SaveSoon(); if (v) Updater.CheckAsync(); Ui.Invalidate(); } }, 44, 24));
            upd.AddRow(new ControlRow("Version " + AppInfo.Version,
                () => string.IsNullOrEmpty(Updater.Detail) ? "Not checked yet this session." : Updater.Detail,
                new Button
                {
                    TextFn = () => Updater.Status == Updater.State.Ready ? "Restart" : Updater.Status == Updater.State.Checking || Updater.Status == Updater.State.Downloading ? "Checking…" : "Check now",
                    Style = BtnStyle.Secondary,
                    EnabledWhen = () => Updater.Status != Updater.State.Checking && Updater.Status != Updater.State.Downloading,
                    OnClick = () => { if (Updater.Status == Updater.State.Ready) Updater.RestartNow(); else Updater.CheckAsync(); }
                }, 120, 38));
            sections.Add(scroll.Add(upd));
        }

        static void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Storage.Folder);
                Ctl.SaveNow();
                Process.Start("explorer.exe", "\"" + Storage.Folder + "\"");
            }
            catch { Ui.Form.ShowToast("Could not open the folder"); }
        }

        static ControlRow KeyRow(string title, string desc, Func<Hotkey> get, string which) =>
            new ControlRow(title, () => desc, new KeyBindButton { Get = get, Set = k => Ctl.SetGlobalKey(which, k) }, 180, 38);

        public override void Layout(RectangleF area)
        {
            Area = area;
            float top = area.Y + HeaderH * S;
            float w = Math.Min(area.Width, 860 * S);
            scroll.Bounds = new RectangleF(area.X, top, w + 10 * S, area.Bottom - top);
            LayoutScroll();
        }

        void LayoutScroll()
        {
            var r = scroll.Bounds;
            float y = r.Y - scroll.Offset, gap = 14 * S;
            foreach (var s in sections)
            {
                float h = s.MeasureHeight(r.Width - 10 * S);
                s.Layout(new RectangleF(r.X, y, r.Width - 10 * S, h));
                y += h + gap;
            }
            scroll.ContentHeight = y - gap + scroll.Offset - r.Y;
            if (scroll.Offset > scroll.MaxOffset) { scroll.ClampOffset(); LayoutScroll(); }
        }

        public override void Paint(Graphics g)
        {
            PaintHeader(g, "Settings", "How " + AppInfo.Name + " applies filters, your hotkeys and startup behavior.");
            base.Paint(g);
        }
    }
}
