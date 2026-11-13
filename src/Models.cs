using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Vivid
{
    public enum FilterParam { Brightness, Contrast, Saturation, Hue, NightVision }

    /// <summary>The five filter values. Defaults are neutral (no change to the screen).</summary>
    public class FilterValues
    {
        public double Brightness { get; set; } = 0;   // -0.50 .. +0.50  (offset)
        public double Contrast { get; set; } = 1;     //  0.50 .. 2.00   (around mid-grey)
        public double Saturation { get; set; } = 1;   //  0.00 .. 3.00
        public double Hue { get; set; } = 0;          // -180 .. +180 degrees
        public double NightVision { get; set; } = 1;  //  1.00 .. 3.00   (shadow lift / gamma)

        public FilterValues Clone() => (FilterValues)MemberwiseClone();

        public double Get(FilterParam p)
        {
            switch (p)
            {
                case FilterParam.Brightness: return Brightness;
                case FilterParam.Contrast: return Contrast;
                case FilterParam.Saturation: return Saturation;
                case FilterParam.Hue: return Hue;
                default: return NightVision;
            }
        }

        public void Set(FilterParam p, double v)
        {
            var spec = ParamSpec.Of(p);
            v = spec.Clamp(v);
            switch (p)
            {
                case FilterParam.Brightness: Brightness = v; break;
                case FilterParam.Contrast: Contrast = v; break;
                case FilterParam.Saturation: Saturation = v; break;
                case FilterParam.Hue: Hue = v; break;
                default: NightVision = v; break;
            }
        }

        public void Sanitize()
        {
            foreach (FilterParam p in Enum.GetValues(typeof(FilterParam)))
            {
                double v = Get(p);
                if (double.IsNaN(v) || double.IsInfinity(v)) v = ParamSpec.Of(p).Default;
                Set(p, v);
            }
        }

        public bool IsNeutral
        {
            get
            {
                foreach (FilterParam p in Enum.GetValues(typeof(FilterParam)))
                    if (Math.Abs(Get(p) - ParamSpec.Of(p).Default) > 1e-6) return false;
                return true;
            }
        }

        public bool SameAs(FilterValues o)
        {
            if (o == null) return false;
            foreach (FilterParam p in Enum.GetValues(typeof(FilterParam)))
                if (Math.Abs(Get(p) - o.Get(p)) > 1e-6) return false;
            return true;
        }

        public static FilterValues Make(double b, double c, double s, double h, double nv) =>
            new FilterValues { Brightness = b, Contrast = c, Saturation = s, Hue = h, NightVision = nv };
    }

    /// <summary>Range, default and display format of a filter parameter.</summary>
    public sealed class ParamSpec
    {
        public string Name;
        public double Min, Max, Default, Step;
        public bool Signed;
        public string Suffix = "";
        public int Decimals = 2;

        public double Clamp(double v) => Math.Max(Min, Math.Min(Max, v));

        public string Format(double v)
        {
            string fmt = Decimals == 0 ? "0" : "0." + new string('0', Decimals);
            double r = Math.Round(v, Decimals, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0; // drop negative zero
            string s = r.ToString(fmt, CultureInfo.InvariantCulture);
            if (Signed && r >= 0) s = "+" + s;
            return s + Suffix;
        }

        public static readonly ParamSpec Brightness = new ParamSpec { Name = "Brightness", Min = -0.5, Max = 0.5, Default = 0, Step = 0.01, Signed = true };
        public static readonly ParamSpec Contrast = new ParamSpec { Name = "Contrast", Min = 0.5, Max = 2.0, Default = 1, Step = 0.01 };
        public static readonly ParamSpec Saturation = new ParamSpec { Name = "Saturation", Min = 0, Max = 3.0, Default = 1, Step = 0.01 };
        public static readonly ParamSpec Hue = new ParamSpec { Name = "Hue rotation", Min = -180, Max = 180, Default = 0, Step = 1, Signed = true, Decimals = 0, Suffix = "°" };
        public static readonly ParamSpec NightVision = new ParamSpec { Name = "Night vision", Min = 1, Max = 3.0, Default = 1, Step = 0.01 };

        public static ParamSpec Of(FilterParam p)
        {
            switch (p)
            {
                case FilterParam.Brightness: return Brightness;
                case FilterParam.Contrast: return Contrast;
                case FilterParam.Saturation: return Saturation;
                case FilterParam.Hue: return Hue;
                default: return NightVision;
            }
        }

        /// <summary>Parses user typed numbers, accepting both "1.25" and "1,25".</summary>
        public static bool TryParse(string text, out double v)
        {
            text = (text ?? "").Trim().ToLowerInvariant().Replace("°", "").Replace("px", "").Replace("%", "").Replace(",", ".").Replace("+", "").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>A keyboard shortcut. Key is a System.Windows.Forms.Keys key code (no modifiers).</summary>
    public class Hotkey
    {
        public int Key { get; set; }
        public int Mods { get; set; } // Native.MOD_* flags

        public Hotkey() { }
        public Hotkey(Keys key, uint mods = 0) { Key = (int)key; Mods = (int)mods; }

        public bool IsEmpty => Key == 0;
        public Hotkey Clone() => new Hotkey { Key = Key, Mods = Mods };
        public bool SameAs(Hotkey o) => o != null && o.Key == Key && o.Mods == Mods && !IsEmpty;

        public override string ToString()
        {
            if (IsEmpty) return "";
            var sb = new StringBuilder();
            if ((Mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((Mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((Mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((Mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName((Keys)Key));
            return sb.ToString();
        }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (int)(k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Multiply: return "Num *";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                case Keys.Prior: return "PgUp";
                case Keys.Next: return "PgDn";
                case Keys.Capital: return "Caps";
                case Keys.Scroll: return "ScrLk";
                case Keys.Pause: return "Pause";
                case Keys.Insert: return "Ins";
                case Keys.Delete: return "Del";
                case Keys.XButton1: return "Mouse 4";
                case Keys.XButton2: return "Mouse 5";
            }
            return k.ToString();
        }
    }

    public class Profile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Profile";
        public FilterValues Values { get; set; } = new FilterValues();

        [System.Web.Script.Serialization.ScriptIgnore]
        public bool BuiltIn { get; set; }

        /// <summary>Which procedural thumbnail to draw for built-in presets.</summary>
        [System.Web.Script.Serialization.ScriptIgnore]
        public string Art { get; set; } = "";
    }

    public class ProfileKey
    {
        public string ProfileId { get; set; }
        public Hotkey Key { get; set; }
    }

    public enum CrossStyle { Cross = 0, CrossDot = 1, Dot = 2, Circle = 3, CircleDot = 4, TShape = 5 }

    public class CrosshairSettings
    {
        public bool Enabled { get; set; } = false;
        public CrossStyle Style { get; set; } = CrossStyle.Cross;
        public int Color { get; set; } = unchecked((int)0xFF00FF6A);
        public int Length { get; set; } = 7;
        public int Thickness { get; set; } = 2;
        public int Gap { get; set; } = 4;
        public int DotSize { get; set; } = 3;
        public int CircleRadius { get; set; } = 10;
        public int Outline { get; set; } = 1;
        public double Opacity { get; set; } = 1.0;
        public int OffsetX { get; set; } = 0;
        public int OffsetY { get; set; } = 0;
        public int Display { get; set; } = 0; // index into Screen.AllScreens, 0 = primary
        /// <summary>Only show the crosshair while the right mouse button is held (aiming down sights).</summary>
        public bool AimOnly { get; set; } = false;

        public CrosshairSettings Clone() => (CrosshairSettings)MemberwiseClone();

        public void Sanitize()
        {
            Length = Clamp(Length, 1, 60);
            Thickness = Clamp(Thickness, 1, 12);
            Gap = Clamp(Gap, 0, 40);
            DotSize = Clamp(DotSize, 1, 16);
            CircleRadius = Clamp(CircleRadius, 2, 60);
            Outline = Clamp(Outline, 0, 4);
            OffsetX = Clamp(OffsetX, -400, 400);
            OffsetY = Clamp(OffsetY, -400, 400);
            if (double.IsNaN(Opacity)) Opacity = 1;
            Opacity = Math.Max(0.1, Math.Min(1, Opacity));
            if (Display < 0) Display = 0;
            if (!Enum.IsDefined(typeof(CrossStyle), Style)) Style = CrossStyle.Cross;
        }

        static int Clamp(int v, int a, int b) => Math.Max(a, Math.Min(b, v));
    }

    public enum EngineMode { ScreenMatrix = 0, GammaRamp = 1 }

    /// <summary>State written before touching the display, so a crash can be undone at next launch.</summary>
    public class RecoveryState
    {
        public bool GammaDirty { get; set; }
        public List<int> NvOriginalLevels { get; set; }
    }

    public class AppSettings
    {
        public int Version { get; set; } = 1;
        public EngineMode Engine { get; set; } = EngineMode.ScreenMatrix;
        public bool UseNvVibrance { get; set; } = true;
        public bool FiltersOn { get; set; } = true;
        /// <summary>Off: changes wait in the preview until you press Apply.</summary>
        public bool AutoApply { get; set; } = false;
        /// <summary>Download new versions from GitHub and swap them in automatically.</summary>
        public bool AutoUpdate { get; set; } = true;
        public string SelectedProfileId { get; set; } = "custom";
        public FilterValues Custom { get; set; } = new FilterValues();
        public List<Profile> UserProfiles { get; set; } = new List<Profile>();
        public List<ProfileKey> ProfileKeys { get; set; } = new List<ProfileKey>();

        public Hotkey ToggleFiltersKey { get; set; } = new Hotkey(Keys.F8);
        public Hotkey NextProfileKey { get; set; } = new Hotkey(Keys.F9);
        public Hotkey ToggleCrosshairKey { get; set; } = new Hotkey(Keys.F8, Native.MOD_CONTROL);
        public Hotkey ShowWindowKey { get; set; } = new Hotkey();

        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool CloseToTray { get; set; } = true;
        public bool ShowOsd { get; set; } = true;
        public bool TrayHintShown { get; set; } = false;

        public CrosshairSettings Crosshair { get; set; } = new CrosshairSettings();
        public RecoveryState Recovery { get; set; } = new RecoveryState();

        public void Sanitize()
        {
            if (Custom == null) Custom = new FilterValues();
            Custom.Sanitize();
            if (UserProfiles == null) UserProfiles = new List<Profile>();
            UserProfiles.RemoveAll(p => p == null);
            foreach (var p in UserProfiles)
            {
                if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profile";
                if (p.Values == null) p.Values = new FilterValues();
                p.Values.Sanitize();
            }
            if (ProfileKeys == null) ProfileKeys = new List<ProfileKey>();
            ProfileKeys.RemoveAll(k => k == null || k.Key == null || k.Key.IsEmpty || string.IsNullOrEmpty(k.ProfileId));
            if (ToggleFiltersKey == null) ToggleFiltersKey = new Hotkey();
            if (NextProfileKey == null) NextProfileKey = new Hotkey();
            if (ToggleCrosshairKey == null) ToggleCrosshairKey = new Hotkey();
            if (ShowWindowKey == null) ShowWindowKey = new Hotkey();
            if (Crosshair == null) Crosshair = new CrosshairSettings();
            Crosshair.Sanitize();
            if (Recovery == null) Recovery = new RecoveryState();
            if (!Enum.IsDefined(typeof(EngineMode), Engine)) Engine = EngineMode.ScreenMatrix;
            if (string.IsNullOrEmpty(SelectedProfileId)) SelectedProfileId = "custom";
            // presets that were removed (e.g. Vibrant) fall back to Custom
            if (SelectedProfileId.StartsWith("builtin.", StringComparison.Ordinal) && !Presets.All.Any(p => p.Id == SelectedProfileId)) SelectedProfileId = "custom";
            ProfileKeys?.RemoveAll(k => k.ProfileId != null && k.ProfileId.StartsWith("builtin.", StringComparison.Ordinal) && !Presets.All.Any(p => p.Id == k.ProfileId));
            if (Version < 2) { AutoApply = false; Version = 2; } // v2: the editing stage is on by default
        }
    }

    /// <summary>Built-in presets. Values are tuned for outdoor survival-game lighting.</summary>
    public static class Presets
    {
        public const string CustomId = "custom";

        public static readonly List<Profile> All = new List<Profile>
        {
            new Profile { Id = "builtin.summer", Name = "Grass", BuiltIn = true, Art = "summer",
                Values = FilterValues.Make(0.03, 1.10, 1.35, 0, 1.00) },
            new Profile { Id = "builtin.winter", Name = "Winter", BuiltIn = true, Art = "winter",
                Values = FilterValues.Make(-0.06, 1.22, 1.15, 0, 1.00) },
            new Profile { Id = "builtin.desert", Name = "Desert", BuiltIn = true, Art = "desert",
                Values = FilterValues.Make(-0.03, 1.16, 1.12, 4, 1.00) },
            new Profile { Id = "builtin.night", Name = "Night Vision", BuiltIn = true, Art = "night",
                Values = FilterValues.Make(0.02, 1.06, 1.20, 0, 1.85) },
        };
    }
}
