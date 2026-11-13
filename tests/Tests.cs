using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Vivid;

/// <summary>Logic tests that run anywhere (no display changes). Build with tests/run.sh.</summary>
public static class VividTests
{
    static int fails, passes;

    static void Check(bool ok, string what)
    {
        if (ok) passes++;
        else { fails++; Console.WriteLine("FAIL: " + what); }
    }

    static bool Near(double a, double b, double eps = 1e-4) => Math.Abs(a - b) <= eps;

    public static int Main()
    {
        Theme.Init(1f);
        MatrixTests();
        RampTests();
        FormatTests();
        StorageTests();
        HoloTests();
        EditingStageTests();
        UpdateTests();
        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    // Row-vector multiply exactly the way Windows applies MAGCOLOREFFECT.
    static double[] MagApply(float[] m, double r, double g, double b)
    {
        double[] v = { r, g, b, 1, 1 };
        var o = new double[5];
        for (int j = 0; j < 5; j++)
            for (int i = 0; i < 5; i++) o[j] += v[i] * m[i * 5 + j];
        return o;
    }

    static void MatrixTests()
    {
        var neutral = new FilterValues();
        var m = ColorMath.BuildMagMatrix(neutral);
        var id = ColorMath.Identity();
        bool same = true;
        for (int i = 0; i < 25; i++) if (Math.Abs(m[i] - id[i]) > 1e-6) same = false;
        Check(same, "neutral values give the identity matrix");

        var rnd = new Random(1);
        for (int t = 0; t < 300; t++)
        {
            var v = FilterValues.Make(rnd.NextDouble() - 0.5, 0.5 + rnd.NextDouble() * 1.5, rnd.NextDouble() * 3, rnd.NextDouble() * 360 - 180, 1);
            double r = rnd.NextDouble(), g = rnd.NextDouble(), b = rnd.NextDouble();
            var o = MagApply(ColorMath.BuildMagMatrix(v), r, g, b);
            ColorMath.ApplyToPixel(v, r, g, b, out double r2, out double g2, out double b2);
            bool ok = Near(ColorMath.Clamp01(o[0]), r2, 1e-4) && Near(ColorMath.Clamp01(o[1]), g2, 1e-4) && Near(ColorMath.Clamp01(o[2]), b2, 1e-4);
            if (!ok) { Check(false, $"matrix layout matches reference math (case {t})"); break; }
            if (t == 299) Check(true, "matrix layout matches reference math");
            Check(Near(o[3], 1, 1e-6), "alpha untouched");
        }

        // Saturation 0 -> grey, and grey stays grey for any saturation/hue.
        var grey = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 1, 0, 0, 1)), 0.9, 0.2, 0.4);
        Check(Near(grey[0], grey[1]) && Near(grey[1], grey[2]), "saturation 0 makes grey");
        var g2v = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 1, 2.5, 77, 1)), 0.5, 0.5, 0.5);
        Check(Near(g2v[0], 0.5) && Near(g2v[1], 0.5) && Near(g2v[2], 0.5), "neutral grey unaffected by saturation and hue");

        // Brightness is a pure offset, contrast pivots on mid grey.
        var br = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0.1, 1, 1, 0, 1)), 0.3, 0.3, 0.3);
        Check(Near(br[0], 0.4), "brightness adds an offset");
        var ct = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 2, 1, 0, 1)), 0.75, 0.5, 0.25);
        Check(Near(ct[0], 1.0) && Near(ct[1], 0.5) && Near(ct[2], 0.0), "contrast pivots around 0.5");

        // Hue 360 == identity, hue 120 rotates red toward green.
        var h360 = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 1, 1, 180, 1)), 1, 0, 0);
        var h120 = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 1, 1, 120, 1)), 1, 0, 0);
        Check(h120[1] > h120[0] && h120[1] > h120[2], "hue +120 turns red green-ish");
        Check(h360[0] < 0.5, "hue 180 moves red away from red");

        // Saturation raises chroma.
        var s1 = MagApply(ColorMath.BuildMagMatrix(FilterValues.Make(0, 1, 1.5, 0, 1)), 0.6, 0.4, 0.3);
        Check(s1[0] - s1[2] > 0.6 - 0.3, "saturation > 1 increases colour spread");
    }

    static void RampTests()
    {
        var neutral = new FilterValues();
        var ramp = ColorMath.BuildRamp(neutral, true, 1);
        var id = ColorMath.IdentityRamp();
        bool same = true;
        for (int i = 0; i < 768; i++) if (Math.Abs(ramp[i] - id[i]) > 1) same = false;
        Check(same, "neutral ramp is identity");
        Check(ColorMath.RampIsIdentity(neutral, true), "RampIsIdentity for neutral");
        Check(ColorMath.RampIsIdentity(FilterValues.Make(0.2, 1.5, 2, 30, 1), false), "matrix mode leaves ramp alone without night vision");
        Check(!ColorMath.RampIsIdentity(FilterValues.Make(0, 1, 1, 0, 1.5), false), "night vision needs the ramp");

        var nv = ColorMath.BuildRamp(FilterValues.Make(0, 1, 1, 0, 2.0), false, 1);
        Check(nv[0] == 0 && nv[255] == 65535, "night vision keeps black and white points");
        Check(nv[25] > id[25] * 2, "night vision lifts shadows");
        bool mono = true;
        var hard = ColorMath.BuildRamp(FilterValues.Make(0.5, 2, 1, 0, 3), true, 1);
        for (int c = 0; c < 3; c++) for (int i = 1; i < 256; i++) if (hard[c * 256 + i] < hard[c * 256 + i - 1]) mono = false;
        Check(mono, "ramps stay monotonic at extreme settings");
        var half = ColorMath.BuildRamp(FilterValues.Make(0, 1, 1, 0, 2.0), false, 0.5);
        Check(Math.Abs(half[25] - (id[25] + nv[25]) / 2) < 3, "strength blends toward identity");
    }

    static void FormatTests()
    {
        Check(ParamSpec.Brightness.Format(0.04) == "+0.04", "brightness format");
        Check(ParamSpec.Brightness.Format(-0.0001) == "+0.00", "no negative zero");
        Check(ParamSpec.Brightness.Format(-0.1) == "-0.10", "negative brightness");
        Check(ParamSpec.Hue.Format(-6) == "-6°", "hue format");
        Check(ParamSpec.Contrast.Format(1.125) == "1.13", "rounding");
        Check(ParamSpec.TryParse("1,25", out double v) && Near(v, 1.25), "comma decimal");
        Check(ParamSpec.TryParse("+12°", out v) && Near(v, 12), "degree sign");
        Check(ParamSpec.TryParse("7 px", out v) && Near(v, 7), "px suffix");
        Check(ParamSpec.TryParse("80%", out v) && Near(v, 80), "percent");
        Check(!ParamSpec.TryParse("abc", out v), "garbage rejected");
        Check(new Hotkey(Keys.F8, Native.MOD_CONTROL | Native.MOD_SHIFT).ToString() == "Ctrl+Shift+F8", "hotkey text");
        Check(new Hotkey(Keys.D5).ToString() == "5", "digit key text");
        var fv = new FilterValues();
        fv.Set(FilterParam.Saturation, 99);
        Check(Near(fv.Saturation, 3), "values clamp to range");
    }

    static void StorageTests()
    {
        var s = new AppSettings();
        s.UserProfiles.Add(new Profile { Name = "Day \"quoted\"", Values = FilterValues.Make(0.05, 1.2, 1.4, -10, 1.3) });
        s.ProfileKeys.Add(new ProfileKey { ProfileId = s.UserProfiles[0].Id, Key = new Hotkey(Keys.F6, Native.MOD_ALT) });
        s.Engine = EngineMode.GammaRamp;
        s.Holo.Area = 2;
        s.Holo.Target = unchecked((int)0xFFFF2BD6);
        s.Recovery.NvOriginalLevels = new List<int> { 3, 0 };
        s.ToggleHoloKey = new Hotkey();

        var json = Storage.Pretty(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(s));
        var back = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AppSettings>(json);
        back.Sanitize();
        Check(back.UserProfiles.Count == 1 && back.UserProfiles[0].Name == "Day \"quoted\"", "profile name round trip (pretty JSON)");
        Check(Near(back.UserProfiles[0].Values.Hue, -10) && Near(back.UserProfiles[0].Values.NightVision, 1.3), "values round trip");
        Check(back.Engine == EngineMode.GammaRamp && back.Holo.Area == 2 && back.Holo.Target == unchecked((int)0xFFFF2BD6), "enums and holo settings round trip");
        Check(back.ProfileKeys.Count == 1 && back.ProfileKeys[0].Key.Mods == (int)Native.MOD_ALT, "profile keys round trip");
        Check(back.ToggleHoloKey.IsEmpty && !back.ToggleFiltersKey.IsEmpty, "cleared key stays cleared, others keep defaults");
        Check(back.Recovery.NvOriginalLevels != null && back.Recovery.NvOriginalLevels[0] == 3, "recovery state round trip");

        var partial = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AppSettings>("{\"FiltersOn\":false}");
        partial.Sanitize();
        Check(!partial.FiltersOn && partial.Holo != null && !partial.Holo.Enabled && partial.ToggleFiltersKey.Key == (int)Keys.F8, "old/partial settings files get defaults");

        var old = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AppSettings>(
            "{\"FiltersOn\":true,\"Crosshair\":{\"Enabled\":true,\"Style\":4,\"AimOnly\":true},\"ToggleCrosshairKey\":{\"Key\":119,\"Mods\":2},\"SelectedProfileId\":\"builtin.winter\"}");
        old.Sanitize();
        Check(old.FiltersOn && old.SelectedProfileId == "builtin.winter" && old.Holo != null && !old.Holo.Enabled, "1.3 settings with crosshair fields still load");

        var exported = Storage.ExportJson(s.UserProfiles);
        var imported = Storage.ImportJson(exported);
        Check(imported.Count == 1 && imported[0].Id != s.UserProfiles[0].Id && Near(imported[0].Values.Contrast, 1.2), "export/import file");
        var code = Storage.ToShareCode(s.UserProfiles[0]);
        var fromCode = Storage.FromShareCodeOrJson("  " + code + "\n");
        Check(code.StartsWith("VIVID1:") && fromCode.Count == 1 && Near(fromCode[0].Values.Saturation, 1.4), "share code round trip");
        var bare = Storage.ImportJson("{\"Brightness\":0.1,\"Saturation\":9}");
        Check(bare.Count == 1 && Near(bare[0].Values.Saturation, 3), "bare values import is clamped");
        var single = Storage.ImportJson("{\"Name\":\"X\",\"Values\":{\"Contrast\":1.5}}");
        Check(single.Count == 1 && single[0].Name == "X" && Near(single[0].Values.Contrast, 1.5) && Near(single[0].Values.Saturation, 1), "single profile import keeps defaults");
        Check(Storage.ImportJson("").Count == 0, "empty import");
    }

    /// <summary>Paints a fake scene with a glowing ring and dot in <paramref name="reticle"/>, BGRA top-down.</summary>
    static byte[] HoloScene(int w, int h, Color reticle, Color world, int ringR, out int cx, out int cy)
    {
        var px = new byte[w * h * 4];
        cx = w / 2; cy = h / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                // background: a green/brown world with some texture
                int n = ((x * 31 + y * 17) % 23) - 11;
                Color c = world;
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                double ring = Math.Abs(d - ringR);
                if (ring < 1.2 || d < 2.0) c = reticle;                          // the reticle
                else if (ring < 3.0) c = Mix(world, reticle, 0.55);             // its glow
                px[i] = (byte)Math.Max(0, Math.Min(255, c.B + n));
                px[i + 1] = (byte)Math.Max(0, Math.Min(255, c.G + n));
                px[i + 2] = (byte)Math.Max(0, Math.Min(255, c.R + n));
                px[i + 3] = 255;
            }
        return px;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static unsafe (int covered, int ringPixels, int stray, int changed) RunHolo(HoloMatcher m, HoloSettings s, byte[] src, byte[] dst, int w, int h, int cx, int cy, int ringR)
    {
        fixed (byte* sp = src, dp = dst) m.Process(sp, dp, Color.FromArgb(s.Output));
        int covered = 0, ringPixels = 0, stray = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                bool isRing = Math.Abs(d - ringR) < 1.2 || d < 2.0;
                int a = dst[(y * w + x) * 4 + 3];
                if (isRing) { ringPixels++; if (a == 255) covered++; }
                else if (a > 0 && Math.Abs(d - ringR) > 5 && d > 6) stray++;
            }
        return (covered, ringPixels, stray, m.Changed);
    }

    static void HoloTests()
    {
        int w = 160, h = 120, R = 22;
        var world = Color.FromArgb(70, 96, 52);
        var s = new HoloSettings();
        var m = new HoloMatcher();
        m.Resize(w, h);
        m.Configure(s);

        // Rust's red reticle on a green world: every reticle pixel turns black, nothing else changes
        var src = HoloScene(w, h, Color.FromArgb(255, 42, 34), world, R, out int cx, out int cy);
        var dst = new byte[w * h * 4];
        var r = RunHolo(m, s, src, dst, w, h, cx, cy, R);
        Check(r.covered == r.ringPixels && r.ringPixels > 100, $"red reticle fully covered ({r.covered}/{r.ringPixels})");
        Check(r.stray == 0, $"nothing away from the reticle is touched (stray {r.stray})");
        int i0 = ((cy) * w + cx + R) * 4;
        Check(dst[i0 + 3] == 255 && dst[i0] == 0 && dst[i0 + 1] == 0 && dst[i0 + 2] == 0, "covered pixels are solid black");

        // the next frame without a reticle clears last frame's pixels
        var empty = HoloScene(w, h, world, world, R, out _, out _);
        r = RunHolo(m, s, empty, dst, w, h, cx, cy, R);
        bool clear = true; for (int i = 3; i < dst.Length; i += 4) if (dst[i] != 0) clear = false;
        Check(clear && r.changed == 0 && !m.Dirty.IsEmpty, "overlay clears when the reticle goes away (and reports what to redraw)");

        // a darker, dimmer red (e.g. at night or through tinted glass) still counts
        src = HoloScene(w, h, Color.FromArgb(190, 30, 28), Color.FromArgb(25, 30, 40), R, out cx, out cy);
        r = RunHolo(m, s, src, dst, w, h, cx, cy, R);
        Check(r.covered >= r.ringPixels * 0.95, $"dim red reticle at night covered ({r.covered}/{r.ringPixels})");

        // other colours aren't touched by the red setting
        src = HoloScene(w, h, Color.FromArgb(40, 255, 70), world, R, out cx, out cy);
        r = RunHolo(m, s, src, dst, w, h, cx, cy, R);
        Check(r.covered == 0 && r.changed == 0, "a green reticle is left alone while looking for red");

        // a magenta reticle when set to magenta, and a custom output colour
        s.Target = unchecked((int)0xFFFF2BD6); s.Output = unchecked((int)0xFF00FF6A);
        m.Configure(s);
        src = HoloScene(w, h, Color.FromArgb(255, 43, 214), world, R, out cx, out cy);
        r = RunHolo(m, s, src, dst, w, h, cx, cy, R);
        int i1 = ((cy) * w + cx + R) * 4;
        Check(r.covered == r.ringPixels && dst[i1 + 1] == 0xFF && dst[i1 + 2] == 0x00, "magenta reticle recolored to the chosen colour");

        // a screen flooded with the target colour isn't a reticle
        s = new HoloSettings(); m.Configure(s);
        var flood = HoloScene(w, h, Color.FromArgb(255, 42, 34), Color.FromArgb(250, 40, 30), R, out cx, out cy);
        r = RunHolo(m, s, flood, dst, w, h, cx, cy, R);
        Check(r.changed == 0, "a screen full of red is left alone");

        // areas grow from aiming-only to large and stay inside the display
        var disp = new Rectangle(0, 0, 2560, 1440);
        var a0 = HoloMatcher.AreaOn(disp, 0); var a1 = HoloMatcher.AreaOn(disp, 1); var a2 = HoloMatcher.AreaOn(disp, 2);
        var centre = new Point(1280, 720);
        Check(a0.Contains(centre) && a1.Contains(a0) && a2.Contains(a1) && disp.Contains(a2), "watched areas nest and cover the centre");
        Check(a1.Bottom > 1100 && a1.Right > 2000, "aim + hip fire area reaches the lower right where the sight sits");

        // the matcher keeps up: a 1440p 'aim + hip fire' area well under a frame
        var big = HoloScene(a1.Width, a1.Height, Color.FromArgb(255, 42, 34), world, 60, out cx, out cy);
        var bigOut = new byte[big.Length];
        var hm = new HoloMatcher(); hm.Resize(a1.Width, a1.Height); hm.Configure(new HoloSettings());
        RunHolo(hm, new HoloSettings(), big, bigOut, a1.Width, a1.Height, cx, cy, 60);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < 10; k++) RunHoloQuick(hm, big, bigOut);
        double ms = sw.Elapsed.TotalMilliseconds / 10;
        Console.WriteLine($"  holo matcher: {a1.Width}x{a1.Height} in {ms:F2} ms");
        Check(ms < 12, $"matcher fast enough for every frame ({ms:F1} ms)");
    }

    static unsafe void RunHoloQuick(HoloMatcher m, byte[] src, byte[] dst)
    {
        fixed (byte* sp = src, dp = dst) m.Process(sp, dp, Color.Black);
    }

    /// <summary>Edits wait in a draft until Apply; the screen and saved settings only change then.</summary>
    static void EditingStageTests()
    {
        var s = new AppSettings();
        s.Sanitize();
        Check(!s.AutoApply, "new installs preview changes before applying");
        var old = new AppSettings { Version = 1, AutoApply = true };
        old.Sanitize();
        Check(!old.AutoApply && old.Version == 2, "older settings switch to the editing stage once");

        s.SelectedProfileId = "builtin.summer";
        s.FiltersOn = true;
        var ctl = new AppController(s);
        ctl.Engine.Init(s, null); // preview mode on non-Windows: no display changes
        var grass = ctl.Find("builtin.summer");
        var winter = ctl.Find("builtin.winter");
        var customBefore = s.Custom.Clone();

        ctl.Select(winter);
        Check(s.SelectedProfileId == "builtin.summer", "picking a preset doesn't change what's applied");
        Check(ctl.Editing && ctl.EditSelected == winter, "picked preset shows in the editor");
        Check(ctl.CurrentValues.SameAs(winter.Values) && ctl.AppliedValues.SameAs(grass.Values), "sliders show the pick, screen keeps Grass");

        ctl.SetValue(FilterParam.Saturation, 2.0);
        Check(ctl.EditSelected.Id == Presets.CustomId && ctl.EditName == "Winter · edited", "editing a preset becomes Custom: " + ctl.EditName);
        Check(Near(winter.Values.Saturation, 1.15), "the Winter preset itself is untouched");
        Check(s.Custom.SameAs(customBefore), "saved Custom values are untouched until Apply");
        Check(ctl.StatusLine == "Only in the preview", "status says preview only: " + ctl.StatusLine);

        ctl.Discard();
        Check(!ctl.Editing && ctl.CurrentValues.SameAs(grass.Values) && ctl.EditSelected == grass, "Discard goes back to what's on screen");

        ctl.Select(winter);
        ctl.SetValue(FilterParam.Hue, 12);
        s.FiltersOn = false;
        ctl.Commit();
        Check(!ctl.Editing && s.SelectedProfileId == Presets.CustomId && Near(s.Custom.Hue, 12) && Near(s.Custom.Contrast, 1.22), "Apply stores the edited preset as Custom");
        Check(s.FiltersOn, "Apply turns filters on");

        // selecting the applied profile again with no edits leaves nothing pending
        ctl.Select(ctl.Find(Presets.CustomId));
        Check(!ctl.Editing, "re-picking what's on screen isn't an edit");

        // nudging a value back to where it was clears the stage
        ctl.SetValue(FilterParam.Brightness, s.Custom.Brightness + 0.1);
        Check(ctl.Editing, "a slider move is pending");
        ctl.SetValue(FilterParam.Brightness, s.Custom.Brightness);
        Check(!ctl.Editing, "moving it back clears it");

        // user profiles: Apply writes into the profile, Discard leaves it
        var mine = new Profile { Name = "Mine", Values = FilterValues.Make(0, 1, 1.4, 0, 1) };
        s.UserProfiles.Add(mine);
        ctl.Select(mine);
        ctl.SetValue(FilterParam.Saturation, 1.9);
        Check(Near(mine.Values.Saturation, 1.4) && ctl.EditSelected == mine, "editing your profile waits for Apply");
        ctl.Commit();
        Check(Near(mine.Values.Saturation, 1.9) && s.SelectedProfileId == mine.Id, "Apply saves into your profile");

        // Save as profile saves the draft and applies it
        ctl.Select(grass);
        ctl.SetValue(FilterParam.NightVision, 1.5);
        var saved = ctl.SaveAsProfile("Grass night");
        Check(!ctl.Editing && s.SelectedProfileId == saved.Id && Near(saved.Values.NightVision, 1.5), "Save as profile applies the preview");

        // instant mode skips the stage
        ctl.SetAutoApply(true);
        ctl.Select(winter);
        Check(!ctl.Editing && s.SelectedProfileId == winter.Id, "Apply instantly puts picks straight on screen");
        ctl.SetValue(FilterParam.Contrast, 1.3);
        Check(!ctl.Editing && s.SelectedProfileId == Presets.CustomId && Near(s.Custom.Contrast, 1.3), "Apply instantly puts slider moves straight on screen");

        // turning instant on with edits waiting applies them
        ctl.SetAutoApply(false);
        ctl.Select(grass);
        ctl.SetAutoApply(true);
        Check(!ctl.Editing && s.SelectedProfileId == grass.Id, "switching on Apply instantly applies what's waiting");
        ctl.SetAutoApply(false);

        // hotkeys (tray menu) bypass the stage
        ctl.Select(winter);
        ctl.Select(ctl.Find("builtin.night"), fromHotkey: true);
        Check(!ctl.Editing && s.SelectedProfileId == "builtin.night", "hotkeys and the tray apply directly");

        // deleting the profile you're editing keeps the edits as Custom
        ctl.Select(mine);
        ctl.SetValue(FilterParam.Hue, -20);
        ctl.Delete(mine);
        Check(ctl.Editing && ctl.EditSelected.Id == Presets.CustomId && Near(ctl.CurrentValues.Hue, -20), "deleting the profile being edited keeps the edits");
        ctl.Discard();
    }

    static void UpdateTests()
    {
        Check(Updater.TryParseVersion("v1.4.0", out var a) && a == new Version(1, 4, 0), "v1.4.0 parses");
        Check(Updater.TryParseVersion("1.4", out var b) && b == new Version(1, 4, 0), "1.4 parses as 1.4.0");
        Check(Updater.TryParseVersion("v2.0.1-beta", out var c) && c == new Version(2, 0, 1), "pre-release suffix ignored");
        Check(!Updater.TryParseVersion("latest", out _), "non-version tags rejected");
        Check(Updater.TryParseVersion("v" + AppInfo.Version, out var cur) && cur == Updater.Current, "current version round-trips");
        Check(new Version(1, 3, 10) > new Version(1, 3, 9), "versions compare numerically");
        Check(System.Reflection.Assembly.GetAssembly(typeof(Updater)).GetName().Version.ToString(3) == AppInfo.Version, "exe version matches AppInfo");
        // Vibrant is gone and anyone who had it falls back to Custom
        var s = new AppSettings { SelectedProfileId = "builtin.vivid" };
        s.ProfileKeys.Add(new ProfileKey { ProfileId = "builtin.vivid", Key = new Hotkey(Keys.F6) });
        s.Sanitize();
        Check(s.SelectedProfileId == "custom" && s.ProfileKeys.Count == 0, "removed preset falls back to Custom");
        Check(!Presets.All.Exists(p => p.Name == "Vibrant"), "Vibrant preset removed");
    }
}
