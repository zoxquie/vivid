using System;

namespace Vivid
{
    /// <summary>
    /// Pure math: turns FilterValues into a 5x5 screen color matrix and 256-entry gamma curves.
    /// All work happens on display-encoded RGB in [0,1], the same space the old driver filters used.
    /// </summary>
    public static class ColorMath
    {
        // Rec.709 luma weights (also used by the SVG/CSS hue-rotate and saturate filters).
        const double LR = 0.2126, LG = 0.7152, LB = 0.0722;

        /// <summary>3x3 linear part (column-vector form: out = A * in) and per-channel offset.</summary>
        public static void BuildAffine(FilterValues v, double[,] a, double[] offset)
        {
            var s = Saturation(v.Saturation);
            var h = HueRotation(v.Hue);
            var hs = Mul(h, s);
            double c = v.Contrast;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    a[i, j] = c * hs[i, j];
            double o = 0.5 * (1 - c) + v.Brightness;
            offset[0] = offset[1] = offset[2] = o;
        }

        /// <summary>
        /// Matrix in the layout MagSetFullscreenColorEffect expects: a row vector [r g b a 1] is
        /// multiplied by the matrix, so row i holds how input channel i feeds each output and
        /// row 4 holds the offsets.
        /// </summary>
        public static float[] BuildMagMatrix(FilterValues v)
        {
            var a = new double[3, 3];
            var off = new double[3];
            BuildAffine(v, a, off);
            var m = new float[25];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i * 5 + j] = (float)a[j, i];
            m[3 * 5 + 3] = 1f;
            for (int j = 0; j < 3; j++) m[4 * 5 + j] = (float)off[j];
            m[4 * 5 + 4] = 1f;
            return m;
        }

        public static float[] Identity()
        {
            var m = new float[25];
            for (int i = 0; i < 5; i++) m[i * 5 + i] = 1f;
            return m;
        }

        /// <summary>Applies the full filter (matrix part, then the night-vision curve) to one pixel. Used for previews/tests.</summary>
        public static void ApplyToPixel(FilterValues v, double r, double g, double b, out double ro, out double go, out double bo)
        {
            var a = new double[3, 3];
            var off = new double[3];
            BuildAffine(v, a, off);
            double[] inp = { r, g, b };
            double[] outp = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double x = off[i];
                for (int j = 0; j < 3; j++) x += a[i, j] * inp[j];
                outp[i] = NightCurve(Clamp01(x), v.NightVision);
            }
            ro = outp[0]; go = outp[1]; bo = outp[2];
        }

        /// <summary>Shadow lift used by "Night vision": a gamma curve x^(1/nv). nv = 1 is identity.</summary>
        public static double NightCurve(double x, double nv)
        {
            if (nv <= 1.0001) return x;
            return Math.Pow(Clamp01(x), 1.0 / nv);
        }

        /// <summary>
        /// Gamma-ramp transfer function. In matrix mode only the night-vision curve lives here;
        /// in gamma mode brightness and contrast are folded in as well (same order as matrix mode).
        /// </summary>
        public static double RampValue(double x, FilterValues v, bool includeToneControls)
        {
            double y = x;
            if (includeToneControls)
                y = Clamp01(v.Contrast * (x - 0.5) + 0.5 + v.Brightness);
            return NightCurve(y, v.NightVision);
        }

        /// <summary>Builds a 3x256 ramp (R, G, B) blended between identity (t=0) and the full effect (t=1).</summary>
        public static ushort[] BuildRamp(FilterValues v, bool includeToneControls, double strength)
        {
            var ramp = new ushort[768];
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                double y = RampValue(x, v, includeToneControls);
                y = x + (y - x) * strength;
                int val = (int)Math.Round(Clamp01(y) * 65535.0);
                ushort u = (ushort)Math.Max(0, Math.Min(65535, val));
                ramp[i] = ramp[256 + i] = ramp[512 + i] = u;
            }
            // Windows rejects ramps that are not monotonic.
            for (int c = 0; c < 3; c++)
                for (int i = 1; i < 256; i++)
                    if (ramp[c * 256 + i] < ramp[c * 256 + i - 1]) ramp[c * 256 + i] = ramp[c * 256 + i - 1];
            return ramp;
        }

        public static ushort[] IdentityRamp()
        {
            var ramp = new ushort[768];
            for (int i = 0; i < 256; i++)
            {
                ushort u = (ushort)(i * 257);
                ramp[i] = ramp[256 + i] = ramp[512 + i] = u;
            }
            return ramp;
        }

        public static bool RampIsIdentity(FilterValues v, bool includeToneControls)
        {
            if (v.NightVision > 1.0001) return false;
            if (!includeToneControls) return true;
            return Math.Abs(v.Brightness) < 1e-6 && Math.Abs(v.Contrast - 1) < 1e-6;
        }

        public static double[,] Saturation(double s)
        {
            double r = (1 - s) * LR, g = (1 - s) * LG, b = (1 - s) * LB;
            return new double[,]
            {
                { r + s, g, b },
                { r, g + s, b },
                { r, g, b + s },
            };
        }

        /// <summary>Luminance-preserving hue rotation (same coefficients as the SVG hueRotate filter).</summary>
        public static double[,] HueRotation(double degrees)
        {
            double rad = degrees * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            return new double[,]
            {
                { 0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928 },
                { 0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283 },
                { 0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072 },
            };
        }

        static double[,] Mul(double[,] x, double[,] y)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += x[i, k] * y[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    }
}
