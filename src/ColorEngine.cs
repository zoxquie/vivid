using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Vivid
{
    public enum EngineHealth { Off, Ok, Warning, Error }

    /// <summary>
    /// Applies filters to the whole screen. Two back ends:
    ///  * Screen matrix: Windows' full-screen color matrix (the same mechanism as the built-in
    ///    accessibility color filters). Handles brightness, contrast, saturation and hue.
    ///  * Gamma ramp: the display's hardware lookup table. Works in exclusive fullscreen too,
    ///    but can only do per-channel curves (no hue), so saturation uses NVIDIA vibrance.
    /// Night vision is a curve, so it always goes through the gamma ramp.
    /// Nothing here touches any game process or game files.
    /// </summary>
    public sealed class ColorEngine
    {
        sealed class DisplayGamma
        {
            public ushort[] Requested; // our curve as last asked for (before the monitor's own curve is added)
            public string Device;
            public ushort[] Original;   // ramp found at startup (keeps monitor calibration)
            public ushort[] Expected;   // what we last wrote, null when untouched
        }

        AppSettings settings;
        Action persist;
        bool magTried, magReady;
        bool matrixDirty;
        readonly List<DisplayGamma> displays = new List<DisplayGamma>();
        readonly NvVibrance nv = new NvVibrance();
        bool nvTried;
        double lastGammaStrength = 1;
        bool disabled; // preview builds on non-Windows

        public EngineHealth Health { get; private set; } = EngineHealth.Off;
        public string StatusTitle { get; private set; } = "Starting";
        public string StatusDetail { get; private set; } = "";
        public bool GammaLimited { get; private set; }
        bool lastRampFull = true;
        public string NvStatus => nvTried ? nv.Status : "Not checked yet";
        public bool NvAvailable => nv.Available;

        public void Init(AppSettings s, Action persistSettings)
        {
            settings = s;
            persist = persistSettings;
            if (!Native.IsWindows) { disabled = true; StatusTitle = "Preview mode"; return; }

            // 1) Undo anything a previous crashed run left on the display.
            if (s.Recovery.GammaDirty)
            {
                foreach (var scr in Screen.AllScreens) TrySetRamp(scr.DeviceName, ColorMath.IdentityRamp());
                s.Recovery.GammaDirty = false;
            }
            if (s.Recovery.NvOriginalLevels != null)
            {
                try { NvVibrance.RestoreLevels(s.Recovery.NvOriginalLevels); } catch { }
                s.Recovery.NvOriginalLevels = null;
            }
            persist?.Invoke();

            // 2) Remember each monitor's current ramp so we can put it back exactly.
            CaptureDisplays();
        }

        void CaptureDisplays()
        {
            displays.Clear();
            foreach (var scr in Screen.AllScreens)
            {
                var d = new DisplayGamma { Device = scr.DeviceName };
                var r = TryGetRamp(scr.DeviceName);
                d.Original = (r != null && LooksValid(r)) ? r : ColorMath.IdentityRamp();
                displays.Add(d);
            }
        }

        static bool LooksValid(ushort[] r)
        {
            // A sane ramp ends high and is mostly increasing.
            for (int c = 0; c < 3; c++) if (r[c * 256 + 255] < 20000) return false;
            return true;
        }

        public static bool GammaRangeUnlocked
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM"))
                    {
                        var v = k?.GetValue("GdiIcmGammaRange");
                        return v is int i && i >= 256;
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>Called when monitors are added/removed or resolution changes.</summary>
        public void OnDisplaysChanged(FilterValues v, bool on)
        {
            if (disabled) return;
            var old = new Dictionary<string, DisplayGamma>();
            foreach (var d in displays) old[d.Device] = d;
            displays.Clear();
            foreach (var scr in Screen.AllScreens)
            {
                if (old.TryGetValue(scr.DeviceName, out var d)) { d.Expected = null; displays.Add(d); continue; }
                var r = TryGetRamp(scr.DeviceName);
                displays.Add(new DisplayGamma { Device = scr.DeviceName, Original = (r != null && LooksValid(r)) ? r : ColorMath.IdentityRamp() });
            }
            matrixDirty = true;
            Apply(v, on);
        }

        public void Apply(FilterValues v, bool on)
        {
            if (disabled) return;
            if (!on)
            {
                ResetDisplay();
                Health = EngineHealth.Off;
                StatusTitle = "Filters off";
                StatusDetail = "Your screen shows normal colors.";
                return;
            }

            var mode = settings.Engine;
            bool gammaOk = true;
            GammaLimited = false;

            if (mode == EngineMode.ScreenMatrix)
            {
                bool okMatrix = SetMatrix(ColorMath.BuildMagMatrix(v));
                gammaOk = ApplyRamp(v, includeTone: false);
                RestoreNv();
                if (!okMatrix)
                {
                    Health = EngineHealth.Error;
                    StatusTitle = "Screen matrix unavailable";
                    StatusDetail = "Windows refused the color matrix. Close Windows Magnifier or other color-filter apps, or switch to Gamma ramp mode in Settings.";
                    return;
                }
                Health = gammaOk ? EngineHealth.Ok : EngineHealth.Warning;
                StatusTitle = gammaOk ? "Screen matrix active" : "Night vision limited";
                StatusDetail = gammaOk
                    ? "Works in windowed and borderless fullscreen. Use borderless if a game ignores the filter."
                    : $"Windows capped night vision at {Math.Round(lastGammaStrength * 100)}%. Unlock the full gamma range in Settings.";
            }
            else
            {
                if (magReady) SetMatrix(ColorMath.Identity());
                gammaOk = ApplyRamp(v, includeTone: true);
                bool satChanged = Math.Abs(v.Saturation - 1) > 1e-6;
                if (settings.UseNvVibrance)
                {
                    EnsureNv();
                    if (nv.Available)
                    {
                        if (satChanged || nv.IsModified)
                        {
                            if (satChanged && settings.Recovery.NvOriginalLevels == null)
                            {
                                settings.Recovery.NvOriginalLevels = nv.OriginalLevels();
                                persist?.Invoke();
                            }
                            nv.ApplySaturation(v.Saturation);
                            if (!nv.IsModified && settings.Recovery.NvOriginalLevels != null)
                            {
                                settings.Recovery.NvOriginalLevels = null;
                                persist?.Invoke();
                            }
                        }
                    }
                }
                else RestoreNv();

                Health = gammaOk ? EngineHealth.Ok : EngineHealth.Warning;
                StatusTitle = gammaOk ? "Gamma ramp active" : $"Gamma limited to {Math.Round(lastGammaStrength * 100)}%";
                string sat = !satChanged ? "" :
                    (settings.UseNvVibrance && nv.Available) ? " Saturation uses NVIDIA Digital Vibrance." :
                    " Saturation needs NVIDIA vibrance or Screen matrix mode.";
                string hue = Math.Abs(v.Hue) > 0.5 ? " Hue rotation only works in Screen matrix mode." : "";
                StatusDetail = (gammaOk
                    ? "Works in exclusive fullscreen too."
                    : "Windows blocked the full curve. Unlock the full gamma range in Settings.") + sat + hue;
            }
        }

        // ---------------- matrix ----------------

        bool EnsureMag()
        {
            if (magReady) return true;
            if (magTried) return false;
            magTried = true;
            try { magReady = Native.MagInitialize(); }
            catch { magReady = false; }
            return magReady;
        }

        bool SetMatrix(float[] m)
        {
            bool identity = IsIdentity(m);
            if (identity && !magReady) return true; // nothing to undo
            if (!EnsureMag()) return identity;
            var eff = new Native.MAGCOLOREFFECT { transform = m };
            bool ok;
            try { ok = Native.MagSetFullscreenColorEffect(ref eff); }
            catch { ok = false; }
            matrixDirty = !identity;
            return ok;
        }

        static bool IsIdentity(float[] m)
        {
            for (int i = 0; i < 25; i++)
            {
                float want = (i % 6 == 0) ? 1f : 0f;
                if (Math.Abs(m[i] - want) > 1e-5f) return false;
            }
            return true;
        }

        // ---------------- gamma ----------------

        bool ApplyRamp(FilterValues v, bool includeTone)
        {
            if (ColorMath.RampIsIdentity(v, includeTone))
            {
                RestoreRamps();
                lastGammaStrength = 1;
                return true;
            }

            // Setting a gamma ramp is slow (and searching for an accepted strength is slower still), so skip it
            // when the curve hasn't changed, e.g. while only saturation or hue are being dragged.
            var ours = ColorMath.BuildRamp(v, includeTone, 1.0);
            bool same = displays.Count > 0;
            foreach (var d in displays) if (d.Expected == null || d.Requested == null || !SameRamp(d.Requested, ours)) { same = false; break; }
            if (same) { GammaLimited = !lastRampFull; return lastRampFull; }

            MarkGammaDirty(true);
            bool allFull = true;
            foreach (var d in displays)
            {
                double strength = 1.0;
                d.Requested = ours;
                ushort[] target = Compose(d.Original, ours);
                if (!TrySetRamp(d.Device, target))
                {
                    // Windows limits how far a ramp may move from linear unless the range is unlocked.
                    // Find the strongest version it accepts.
                    double lo = 0, hi = 1;
                    ushort[] best = null;
                    for (int i = 0; i < 7; i++)
                    {
                        double mid = (lo + hi) / 2;
                        var t = Compose(d.Original, ColorMath.BuildRamp(v, includeTone, mid));
                        if (TrySetRamp(d.Device, t)) { lo = mid; best = t; } else hi = mid;
                    }
                    strength = lo;
                    target = best;
                    if (best != null) TrySetRamp(d.Device, best);
                    allFull = false;
                }
                d.Expected = target;
                if (d == displays[0]) lastGammaStrength = strength;
            }
            GammaLimited = !allFull;
            lastRampFull = allFull;
            return allFull;
        }

        static bool SameRamp(ushort[] a, ushort[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Applies our curve first, then the monitor's original curve (keeps calibration).</summary>
        static ushort[] Compose(ushort[] original, ushort[] ours)
        {
            var r = new ushort[768];
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 256; i++)
                {
                    double x = ours[c * 256 + i] / 65535.0 * 255.0;
                    int i0 = (int)Math.Floor(x);
                    int i1 = Math.Min(255, i0 + 1);
                    double f = x - i0;
                    double y = original[c * 256 + i0] * (1 - f) + original[c * 256 + i1] * f;
                    r[c * 256 + i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(y)));
                }
                for (int i = 1; i < 256; i++)
                    if (r[c * 256 + i] < r[c * 256 + i - 1]) r[c * 256 + i] = r[c * 256 + i - 1];
            }
            return r;
        }

        void RestoreRamps()
        {
            bool any = false;
            foreach (var d in displays)
            {
                if (d.Expected == null) continue;
                TrySetRamp(d.Device, d.Original);
                d.Expected = null;
                any = true;
            }
            if (any || settings.Recovery.GammaDirty) MarkGammaDirty(false);
        }

        void MarkGammaDirty(bool dirty)
        {
            if (settings.Recovery.GammaDirty == dirty) return;
            settings.Recovery.GammaDirty = dirty;
            persist?.Invoke();
        }

        /// <summary>Games sometimes reset the gamma ramp when they start or change mode. Put ours back.</summary>
        public void Watchdog()
        {
            if (disabled) return;
            foreach (var d in displays)
            {
                if (d.Expected == null) continue;
                var cur = TryGetRamp(d.Device);
                if (cur == null) continue;
                bool same = true;
                for (int i = 0; i < 768; i += 3) if (Math.Abs(cur[i] - d.Expected[i]) > 300) { same = false; break; }
                if (!same) TrySetRamp(d.Device, d.Expected);
            }
        }

        public bool NeedsWatchdog
        {
            get { foreach (var d in displays) if (d.Expected != null) return true; return false; }
        }

        static bool TrySetRamp(string device, ushort[] ramp)
        {
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = Native.CreateDC(device, device, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) return false;
                return Native.SetDeviceGammaRamp(dc, ramp);
            }
            catch { return false; }
            finally { if (dc != IntPtr.Zero) Native.DeleteDC(dc); }
        }

        static ushort[] TryGetRamp(string device)
        {
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = Native.CreateDC(device, device, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) return null;
                var r = new ushort[768];
                return Native.GetDeviceGammaRamp(dc, r) ? r : null;
            }
            catch { return null; }
            finally { if (dc != IntPtr.Zero) Native.DeleteDC(dc); }
        }

        // ---------------- NVIDIA ----------------

        void EnsureNv()
        {
            if (nvTried) return;
            nvTried = true;
            nv.Init();
        }

        public void ProbeNv() => EnsureNv();

        void RestoreNv()
        {
            if (!nvTried || !nv.Available) return;
            if (nv.IsModified) nv.Restore();
            if (settings.Recovery.NvOriginalLevels != null)
            {
                settings.Recovery.NvOriginalLevels = null;
                persist?.Invoke();
            }
        }

        // ---------------- reset ----------------

        public void ResetDisplay()
        {
            if (disabled) return;
            if (magReady && matrixDirty) SetMatrix(ColorMath.Identity());
            RestoreRamps();
            RestoreNv();
        }

        /// <summary>Puts the screen back to normal. Safe to call more than once and from crash handlers.</summary>
        public void Shutdown()
        {
            if (disabled) return;
            try { ResetDisplay(); } catch { }
            try { if (magReady) { Native.MagUninitialize(); magReady = false; magTried = false; } } catch { }
        }
    }
}
