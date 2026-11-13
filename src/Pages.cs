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
        Panel leftCard, bar, engineCard, keysCard;
        ScrollPanel leftScroll, center;
        Caption lblBuiltIn, lblMine;
        Label lblPresetsTitle;
        readonly List<ProfileCard> builtInCards = new List<ProfileCard>(), userCards = new List<ProfileCard>();
        Panel emptyBox;
        Section secTone, secColor, secNight;
        LivePreview preview;
        Toggle autoToggle;
        Label autoLabel;
        Button apply, discard, reset, bindKey, save, importBtn, exportBtn, engineLink, keysLink;
        int barLevel; // 0 = full labels; higher levels shorten buttons so the bar fits narrow windows

        static FilterValues V => Ctl.CurrentValues;

        public override void Build()
        {
            Children.Clear();
            builtInCards.Clear();
            userCards.Clear();

            // ---- left: profiles
            leftCard = Add(new Panel { Painter = (g, r) => Theme.Card(g, r, 20) });
            leftScroll = Add(new ScrollPanel { Relayout = LayoutLeft });
            lblBuiltIn = leftScroll.Add(new Caption("Presets"));
            lblPresetsTitle = leftScroll.Add(new Label { Text = "Ready presets", Font = Theme.F(16.5f, semibold: true, bold: true) });
            builtInCards.Add(leftScroll.Add(new ProfileCard(AppController.CustomProfile)));
            foreach (var p in Ctl.BuiltIns) builtInCards.Add(leftScroll.Add(new ProfileCard(p)));
            lblMine = leftScroll.Add(new Caption("My profiles"));
            lblMine.TextFn = () => Ctl.UserProfiles.Count == 0 ? "MY PROFILES" : $"MY PROFILES  ·  {Ctl.UserProfiles.Count}";
            foreach (var p in Ctl.UserProfiles) userCards.Add(leftScroll.Add(new ProfileCard(p)));
            emptyBox = leftScroll.Add(new Panel { Painter = PaintEmpty });
            importBtn = Add(new Button { Text = "Import", Icon = Icons.Import, Style = BtnStyle.Secondary, IconSize = 16, Hint = "Load profiles from a file or a share code" });
            importBtn.OnClick = () => Ui.Form.ImportMenu(new PointF(importBtn.Bounds.X, importBtn.Bounds.Bottom + 4 * S));
            exportBtn = Add(new Button { Text = "Export", Icon = Icons.Export, Style = BtnStyle.Secondary, IconSize = 16, Hint = "Save profiles to a file or copy a share code" });
            exportBtn.OnClick = () => Ui.Form.ExportMenu(new PointF(exportBtn.Bounds.X, exportBtn.Bounds.Bottom + 4 * S));

            // ---- centre: filter sections
            center = Add(new ScrollPanel { Relayout = LayoutCenter });

            secTone = center.Add(new Section("Image", "Light and tone", Icons.Sun, Theme.Amber, () => Ctl.ResetValues(FilterParam.Brightness, FilterParam.Contrast)));
            secTone.AddRow(MakeRow(FilterParam.Brightness, SliderLook.Neutral, Theme.Amber));
            secTone.AddRow(MakeRow(FilterParam.Contrast, SliderLook.Neutral, Theme.Amber));

            secColor = center.Add(new Section("Palette", "Color", Icons.Drop, Theme.Green, () => Ctl.ResetValues(FilterParam.Saturation, FilterParam.Hue)));
            secColor.AddRow(MakeRow(FilterParam.Saturation, SliderLook.Green, Theme.Green,
                note: () => Ctl.Settings.Engine == EngineMode.GammaRamp ? (Ctl.Settings.UseNvVibrance ? "via NVIDIA vibrance" : "needs Screen matrix") : null));
            secColor.AddRow(MakeRow(FilterParam.Hue, SliderLook.Rainbow, Theme.Green,
                enabled: () => Ctl.Settings.Engine == EngineMode.ScreenMatrix,
                note: () => Ctl.Settings.Engine == EngineMode.GammaRamp ? "Screen matrix mode only" : null));

            secNight = center.Add(new Section("Visibility", "Night vision", Icons.Moon, Theme.Blue, () => Ctl.ResetValues(FilterParam.NightVision)));
            secNight.AddRow(MakeRow(FilterParam.NightVision, SliderLook.Accent, Theme.Blue));
            secNight.FooterNote = () => Scene.PreviewHasPictures(Ctl.Find("builtin.summer"))
                ? "Lifts dark areas without washing out highlights. Hover Night Vision to try it on a night scene."
                : "Lifts dark areas without washing out highlights. Try it on the cave in the preview.";

            // ---- right column
            preview = Add(new LivePreview());
            engineCard = Add(new Panel { Painter = PaintEngine });
            engineLink = Add(new Button { Text = "Settings", Style = BtnStyle.Link, OnClick = () => Ui.Form.ShowPage(PageId.Settings) });
            keysCard = Add(new Panel { Painter = PaintKeys });
            keysLink = Add(new Button { Text = "Edit", Style = BtnStyle.Link, OnClick = () => Ui.Form.ShowPage(PageId.Settings) });

            // ---- bottom bar
            bar = Add(new Panel { Painter = PaintBar });
            autoToggle = Add(new Toggle { Get = () => Ctl.Settings.AutoApply, Set = v => Ctl.SetAutoApply(v) });
            autoLabel = Add(new Label { Text = "Apply instantly", Font = Theme.F(14) });
            autoToggle.Hint = "Off: changes wait in the preview until you press Apply";
            apply = Add(new Button { Text = "Apply", Style = BtnStyle.Primary, EnabledWhen = () => Ctl.PendingChanges, Hint = "Put the preview on your screen", OnClick = () => Ctl.Commit() });
            discard = Add(new Button { Text = "Discard", Style = BtnStyle.Secondary, Hint = "Throw away changes that aren't applied", OnClick = () => Ctl.Discard() });
            reset = Add(new Button { TextFn = () => barLevel >= 1 ? "" : "Reset", Icon = Icons.Reset, IconSize = 15, Style = BtnStyle.Secondary, Hint = "Set every slider back to neutral", OnClick = () => Ctl.ResetValues((FilterParam[])Enum.GetValues(typeof(FilterParam))) });
            bindKey = Add(new Button
            {
                TextFn = () => Ctl.KeyFor(Ctl.EditSelected)?.ToString() ?? (barLevel >= 1 ? "" : "Bind key"),
                Icon = Icons.Keyboard, Style = BtnStyle.Secondary, IconSize = 17,
                Hint = "Choose a key that switches to this profile while you play",
                OnClick = () => { var p = Ctl.EditSelected; Ui.Form.BindKeyDialog("Key for " + p.Name, () => Ctl.KeyFor(p), k => Ctl.SetProfileKey(p, k)); }
            });
            save = Add(new Button
            {
                TextFn = () => barLevel >= 2 ? "Save" : "Save as profile", Icon = Icons.Plus, Style = BtnStyle.Primary, IconSize = 16, Hint = "Save these values as a profile",
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
                () => V.Get(param), v => Ctl.SetValue(param, v), spec.Format, look, tint, enabled, note);
        }

        public override void Layout(RectangleF area)
        {
            Area = area;
            float gap = 16 * S;
            float top = area.Y + HeaderH * S;
            float bodyH = area.Bottom - top;
            float leftW = 256 * S;
            float rightW = Math.Max(280 * S, Math.Min(390 * S, area.Width * 0.28f));
            float barH = 66 * S;

            leftCard.Bounds = new RectangleF(area.X, top, leftW, bodyH);
            float ieH = 40 * S;
            var ieRow = new RectangleF(leftCard.Bounds.X + 12 * S, leftCard.Bounds.Bottom - 12 * S - ieH, leftW - 24 * S, ieH);
            importBtn.Bounds = new RectangleF(ieRow.X, ieRow.Y, (ieRow.Width - 8 * S) / 2, ieH);
            exportBtn.Bounds = new RectangleF(importBtn.Bounds.Right + 8 * S, ieRow.Y, (ieRow.Width - 8 * S) / 2, ieH);
            leftScroll.Bounds = new RectangleF(leftCard.Bounds.X + 12 * S, leftCard.Bounds.Y + 12 * S, leftW - 24 * S, ieRow.Y - 12 * S - (leftCard.Bounds.Y + 12 * S));
            LayoutLeft();

            float cx = area.X + leftW + gap;
            float cw = area.Right - cx - rightW - gap;
            float barY = area.Bottom - barH;
            bar.Bounds = new RectangleF(cx, barY, area.Right - cx, barH);
            center.Bounds = new RectangleF(cx, top, cw, barY - gap - top);
            LayoutCenter();
            LayoutBar();

            // right column: preview, engine status, hotkeys (each only if it fits)
            float rx = cx + cw + gap, ry = top, limit = barY - gap;
            float ph = Math.Min(LivePreview.HeightFor(rightW), limit - ry);
            preview.Bounds = new RectangleF(rx, ry, rightW, ph);
            preview.Visible = ph > 140 * S;
            ry = preview.Visible ? preview.Bounds.Bottom + 12 * S : ry;

            float engH = EngineCardHeight(rightW);
            engineCard.Bounds = new RectangleF(rx, ry, rightW, engH);
            engineCard.Visible = engineLink.Visible = ry + engH <= limit;
            float lw = Theme.Measure("Settings", Theme.F(13, true)).Width + 16 * S;
            engineLink.Bounds = new RectangleF(engineCard.Bounds.Right - 12 * S - lw, ry + 14 * S, lw, 28 * S);
            if (engineCard.Visible) ry += engH + 12 * S;

            float keysH = KeysCardHeight();
            keysCard.Bounds = new RectangleF(rx, ry, rightW, Math.Max(keysH, limit - ry));
            keysCard.Visible = keysLink.Visible = ry + keysH <= limit;
            float kl = Theme.Measure("Edit", Theme.F(13, true)).Width + 16 * S;
            keysLink.Bounds = new RectangleF(keysCard.Bounds.Right - 12 * S - kl, ry + 14 * S, kl, 28 * S);
        }

        void LayoutLeft()
        {
            var r = leftScroll.Bounds;
            float ch = 56 * S, cg = 8 * S;
            float content = 4 * S + 54 * S + builtInCards.Count * (ch + cg) + 14 * S + 28 * S
                + (userCards.Count == 0 ? 116 * S : userCards.Count * (ch + cg)) + 8 * S;
            float w = content > r.Height + 1 ? r.Width - 10 * S : r.Width;
            float y = r.Y + 4 * S - leftScroll.Offset;
            lblBuiltIn.Bounds = new RectangleF(r.X + 4 * S, y, w, 20 * S); y += 20 * S;
            lblPresetsTitle.Bounds = new RectangleF(r.X + 4 * S, y, w, 26 * S); y += 34 * S;
            foreach (var c in builtInCards) { c.Bounds = new RectangleF(r.X, y, w, ch); c.Clip = r; c.Layout(c.Bounds); y += ch + cg; }
            y += 14 * S;
            lblMine.Bounds = new RectangleF(r.X + 4 * S, y, w, 22 * S); y += 28 * S;
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
            float gap = 14 * S;
            var secs = new[] { secTone, secColor, secNight };
            float total = secs.Sum(x => x.MeasureHeight(r.Width)) + gap * (secs.Length - 1);
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
            float bh = 42 * S, gap = 10 * S;
            var bf = Theme.F(13.5f, true);
            float x = r.Right - 12 * S;
            float sw = Theme.Measure(barLevel >= 2 ? "Save" : "Save as profile", bf).Width + 56 * S;
            save.Bounds = new RectangleF(x - sw, cy - bh / 2, sw, bh); x = save.Bounds.X - gap;
            string kt = Ctl.KeyFor(Ctl.EditSelected)?.ToString() ?? (barLevel >= 1 ? "" : "Bind key");
            float kw = kt == "" ? 46 * S : Math.Max(barLevel >= 1 ? 46 * S : 120 * S, Theme.Measure(kt, bf).Width + 58 * S);
            bindKey.Bounds = new RectangleF(x - kw, cy - bh / 2, kw, bh); x = bindKey.Bounds.X - gap;
            float rw = barLevel >= 1 ? 46 * S : Theme.Measure("Reset", bf).Width + 52 * S;
            reset.Bounds = new RectangleF(x - rw, cy - bh / 2, rw, bh); x = reset.Bounds.X - gap;
            apply.Visible = !Ctl.Settings.AutoApply;
            if (apply.Visible) { float aw = barLevel >= 3 ? 76 * S : 92 * S; apply.Bounds = new RectangleF(x - aw, cy - bh / 2, aw, bh); x = apply.Bounds.X - gap; }
            discard.Visible = Ctl.PendingChanges;
            if (discard.Visible)
            {
                float dw = Theme.Measure("Discard", bf).Width + (barLevel >= 3 ? 24 : 36) * S;
                discard.Bounds = new RectangleF(x - dw, cy - bh / 2, dw, bh); x = discard.Bounds.X - gap;
            }
            x -= 8 * S; // separator
            autoLabel.Visible = barLevel < 3; // the switch keeps its tooltip
            float lw = autoLabel.Visible ? Theme.Measure("Apply instantly", Theme.F(14)).Width + 4 * S : 0;
            autoLabel.Bounds = new RectangleF(x - 8 * S - lw, r.Y, lw, r.Height);
            autoToggle.Bounds = new RectangleF(autoLabel.Bounds.X - (lw > 0 ? 10 * S : 0) - 44 * S, cy - 13 * S, 44 * S, 26 * S);
        }

        float EngineCardHeight(float w)
        {
            float h = 80 * S;
            h += Theme.Measure(Ctl.Engine.StatusDetail ?? "", Theme.F(12.5f), w - 40 * S).Height + 16 * S;
            return h;
        }

        float KeysCardHeight() => (56 + 3 * 36 + 10) * S + (Ctl.HotkeyFailures.Count > 0 ? 40 * S : 0);

        public override void Paint(Graphics g)
        {
            // Re-run layout: card heights and button widths depend on live text (status, key names).
            Layout(Area);
            PaintHeader(g, "Screen filters", Ctl.Settings.AutoApply
                ? "Color and visibility over your whole screen. Changes go on screen as you make them."
                : "Color and visibility over your whole screen. Changes show in the preview first, then Apply puts them on screen.");
            base.Paint(g);
        }

        void PaintEmpty(Graphics g, RectangleF r)
        {
            Theme.FillRound(g, r, 14 * S, Theme.White(5));
            using (var p = new Pen(Theme.WithAlpha(Theme.Accent, 110), Math.Max(1, S)) { DashPattern = new[] { 4f, 3f } })
            using (var path = Theme.Round(RectangleF.Inflate(r, -0.5f, -0.5f), 14 * S))
                g.DrawPath(p, path);
            float d = 30 * S;
            var ic = new RectangleF(r.X + (r.Width - d) / 2, r.Y + 16 * S, d, d);
            Theme.RadialGlow(g, new PointF(ic.X + d / 2, ic.Y + d / 2), d, d, Theme.AccentMid, 80);
            using (var b = Theme.AccentBrush(ic, 60)) g.FillEllipse(b, ic);
            Icons.Plus(g, RectangleF.Inflate(ic, -7 * S, -7 * S), Color.White);
            Theme.Txt(g, "No saved profiles", Theme.F(13.5f, semibold: true), Theme.Text, new RectangleF(r.X, r.Y + 52 * S, r.Width, 20 * S), Theme.H.Center);
            Theme.Txt(g, "Tune the sliders, then press\nSave as profile.", Theme.F(12), Theme.TextMuted,
                new RectangleF(r.X + 12 * S, r.Y + 74 * S, r.Width - 24 * S, 36 * S), Theme.H.Center, false, true);
        }

        void PaintBar(Graphics g, RectangleF r)
        {
            Theme.Card(g, r, 18);
            float x = r.X + 22 * S;
            Theme.Txt(g, "STATUS", Theme.F(10.5f, semibold: true), Theme.TextDim, new RectangleF(x, r.Y + 12 * S, 200 * S, 16 * S));
            var c = Ctl.StatusColor;
            Theme.PulseDot(g, new PointF(x + 4 * S, r.Y + 41 * S), 8 * S, c, Ctl.Settings.FiltersOn && !Ctl.PendingChanges);
            if (Ctl.PendingChanges) Theme.Txt(g, "NOT APPLIED", Theme.F(10.5f, semibold: true), Theme.Amber, new RectangleF(x + 60 * S, r.Y + 12 * S, 200 * S, 16 * S));
            float maxW = autoToggle.Bounds.X - 16 * S - (x + 22 * S);
            Theme.Txt(g, Ctl.StatusLine, Theme.F(14, semibold: true), Theme.Text, new RectangleF(x + 22 * S, r.Y + 29 * S, Math.Max(20, maxW), 24 * S));
            float sx = autoLabel.Bounds.Right + 16 * S;
            using (var p = new Pen(Theme.White(22), Math.Max(1, S))) g.DrawLine(p, sx, r.Y + 18 * S, sx, r.Bottom - 18 * S);
        }

        void PaintEngine(Graphics g, RectangleF r)
        {
            var eng = Ctl.Engine;
            Color c = eng.Health == EngineHealth.Error ? Theme.Red : eng.Health == EngineHealth.Warning ? Theme.Amber : eng.Health == EngineHealth.Off ? Theme.TextMuted : Theme.Green;
            Theme.Card(g, r, 18);
            var st = g.Save();
            using (var clip = Theme.Round(r, 18 * S)) g.SetClip(clip, CombineMode.Intersect);
            Theme.RadialGlow(g, new PointF(r.X + 36 * S, r.Y + 34 * S), 160 * S, 80 * S, c, 30);
            g.Restore(st);
            var tile = new RectangleF(r.X + 18 * S, r.Y + 16 * S, 38 * S, 38 * S);
            Theme.Glow(g, tile, 11 * S, c, 6 * S, 30);
            using (var b = Theme.Gradient(tile, Theme.Mix(Theme.Surface3, c, 0.22f), Theme.Mix(Theme.Surface2, c, 0.05f), 90f)) Theme.FillRound(g, tile, 11 * S, b);
            Theme.StrokeRound(g, tile, 11 * S, Theme.WithAlpha(c, 140), Math.Max(1, S));
            Icons.Bolt(g, RectangleF.Inflate(tile, -10 * S, -10 * S), c);
            string mode = Ctl.Settings.Engine == EngineMode.ScreenMatrix ? "SCREEN MATRIX" : "GAMMA RAMP";
            Theme.Txt(g, mode, Theme.F(10.5f, semibold: true), Theme.WithAlpha(c, 210), new RectangleF(tile.Right + 12 * S, tile.Y, engineLink.Bounds.X - tile.Right - 16 * S, 16 * S));
            Theme.Txt(g, eng.StatusTitle, Theme.F(15, semibold: true), Theme.Text, new RectangleF(tile.Right + 12 * S, tile.Y + 14 * S, r.Right - tile.Right - 30 * S, 24 * S));
            Theme.Txt(g, eng.StatusDetail, Theme.F(12.5f), Theme.TextMuted, new RectangleF(r.X + 20 * S, tile.Bottom + 12 * S, r.Width - 40 * S, r.Bottom - tile.Bottom - 16 * S), Theme.H.Left, false, true);
        }

        void PaintKeys(Graphics g, RectangleF r)
        {
            Theme.Card(g, r, 18);
            Theme.Txt(g, "Hotkeys", Theme.F(15, semibold: true), Theme.Text, new RectangleF(r.X + 20 * S, r.Y + 14 * S, r.Width - 100 * S, 28 * S));
            var s = Ctl.Settings;
            var rows = new[] { ("Filters on / off", s.ToggleFiltersKey), ("Next profile", s.NextProfileKey), ("Black holo", s.ToggleHoloKey) };
            float y = r.Y + 54 * S;
            var kf = Theme.F(12, semibold: true);
            foreach (var (label, key) in rows)
            {
                Theme.Txt(g, label, Theme.F(13), Theme.TextMuted, new RectangleF(r.X + 20 * S, y, r.Width / 2, 28 * S));
                if (key.IsEmpty)
                    Theme.Txt(g, "Not set", Theme.F(12.5f), Theme.TextDim, new RectangleF(r.X, y, r.Width - 20 * S, 28 * S), Theme.H.Right);
                else
                {
                    float xr = r.Right - 20 * S;
                    var parts = key.ToString().Split('+');
                    for (int i = parts.Length - 1; i >= 0; i--)
                    {
                        float w = Math.Max(28 * S, Theme.Measure(parts[i], kf).Width + 14 * S);
                        Theme.KeyCap(g, new RectangleF(xr - w, y + 1 * S, w, 26 * S), parts[i], kf);
                        xr -= w + 4 * S;
                    }
                }
                y += 36 * S;
            }
            var fails = Ctl.HotkeyFailures;
            if (fails.Count > 0)
                Theme.Txt(g, "In use by another app: " + string.Join(", ", fails), Theme.F(12), Theme.Amber,
                    new RectangleF(r.X + 20 * S, y + 2 * S, r.Width - 40 * S, 34 * S), Theme.H.Left, false, true);
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
        static AppController Ctl => Ui.Form.Ctl;

        public override bool Interactive => true;
        public override Cursor Cursor => Cursors.SizeWE;
        public override string Tooltip => dragging ? null : "Before: the holo as Rust draws it. After: what " + AppInfo.Name + " turns it into. Drag to compare.";
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
                // a holosight seen from behind: dark frame, tinted glass, glowing ring and dot
                float cx = w / 2f, cy = h / 2f, R = Math.Min(w, h) * 0.11f;
                var glass = new RectangleF(cx - R * 2.6f, cy - R * 2.0f, R * 5.2f, R * 4.0f);
                using (var frame = new Pen(Color.FromArgb(235, 28, 30, 32), R * 0.45f)) using (var path = Theme.Round(glass, R * 0.5f)) g.DrawPath(frame, path);
                using (var tint = new SolidBrush(Color.FromArgb(40, 120, 200, 200))) using (var path = Theme.Round(glass, R * 0.5f)) g.FillPath(tint, path);
                var col = Color.FromArgb(hs.Target);
                for (int i = 4; i >= 1; i--)
                    using (var glow = new Pen(Color.FromArgb(28, col), R * 0.08f + i * R * 0.07f)) g.DrawEllipse(glow, cx - R, cy - R, 2 * R, 2 * R);
                using (var ring = new Pen(col, Math.Max(2f, R * 0.09f))) g.DrawEllipse(ring, cx - R, cy - R, 2 * R, 2 * R);
                float d = Math.Max(3f, R * 0.16f);
                using (var b = new SolidBrush(col)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
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
                () => "Turns the holosight's reticle black wherever it is on screen, in hip fire and when aiming. Rust has to run in borderless or windowed mode.",
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
            secMain.FooterNote = () => "While it's on, you see Rust through " + AppInfo.Name + ": every frame is copied, the reticle's colour is swapped on your graphics card and the result is shown on top, about a frame behind the game. It never touches Rust's files or memory.";

            secColor = right.Add(new Section("Colours", "Reticle colour", Icons.Drop, Theme.Green, null));
            secColor.AddRow(new WideRow("Reticle in Rust", Swatches(Reticles, () => H.Target, v => H.Target = v)));
            secColor.AddRow(new WideRow("Turns into", Swatches(Outputs, () => H.Output, v => H.Output = v)));
            secColor.AddRow(new SliderRow("Tolerance", 0, 100, 45, 1, () => H.Tolerance, v => { H.Tolerance = (int)Math.Round(v); Ctl.HoloChanged(); },
                v => (int)v + "%", SliderLook.Neutral, Theme.Text));
            secColor.FooterNote = () => "Anything on screen with exactly the reticle's colour turns black too. If red things you care about change, set the holosight to an unusual colour in Rust's accessibility settings (like magenta) and pick the same one here. Raise Tolerance if parts of the glow stay coloured, lower it if other things turn black.";

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
            else if (st == HoloOverlay.RunState.Running) { text = "Working · " + o.Fps + " fps · " + o.FrameMs.ToString("0.0") + " ms per frame"; c = Theme.Green; pulse = true; }
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
