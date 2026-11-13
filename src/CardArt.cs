using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Vivid
{
    /// <summary>
    /// Your own screenshots for preset cards and the live preview.
    /// Where an image comes from, first match wins:
    ///   1. one you picked in the app        (%AppData%\Vivid\images\&lt;profile id&gt;.png, preview.png)
    ///   2. an "images" folder next to Vivid.exe  (grass.jpg, winter.png, night-vision.jpg, preview.jpg …)
    ///   3. images built into the exe from assets\presets when it was compiled
    ///   4. the generated artwork
    /// </summary>
    public static class CardArt
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        static readonly HashSet<string> missing = new HashSet<string>();
        static readonly HashSet<string> bundled = new HashSet<string>(); // ids whose picture came built into the exe

        /// <summary>True when the card shows the picture built into Vivid.exe (not one you picked).</summary>
        public static bool IsBundled(Profile p) => bundled.Contains(p.Id);
        static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".bmp" };

        public static string Folder => Path.Combine(Storage.Folder, "images");
        public static string ExeFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images");
        public const string PreviewKey = "preview";

        /// <summary>"Night Vision", "night-vision.jpg" and "night_vision" all become "nightvision".</summary>
        public static string Normalize(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>File names that count for a profile.</summary>
        public static IEnumerable<string> NamesFor(Profile p)
        {
            yield return Normalize(p.Name);
            switch (p.Art)
            {
                case "summer": yield return "grass"; yield return "summer"; break;
                case "winter": yield return "winter"; yield return "snow"; break;
                case "desert": yield return "desert"; yield return "sand"; break;
                case "night": yield return "nightvision"; yield return "night"; break;
                case "vivid": yield return "vibrant"; yield return "vivid"; break;
            }
            if (p.Id == Presets.CustomId) yield return "custom";
        }

        static string UserFile(string id) => Path.Combine(Folder, Normalize(id) + ".png");

        public static bool HasCustom(Profile p) => File.Exists(UserFile(p.Id));
        public static bool HasCustomPreview => File.Exists(UserFile(PreviewKey));

        public static Bitmap For(Profile p)
        {
            if (cache.TryGetValue(p.Id, out var b)) return b;
            if (missing.Contains(p.Id)) return null;
            b = Load(UserFile(p.Id), 960);
            if (b == null) { b = Find(NamesFor(p), 960, out bool fromExe); if (fromExe) bundled.Add(p.Id); }
            if (b == null) missing.Add(p.Id); else cache[p.Id] = b;
            return b;
        }

        /// <summary>Screenshot to use for the before/after preview, or null for the built-in scene.</summary>
        public static Bitmap Preview()
        {
            if (cache.TryGetValue(PreviewKey, out var b)) return b;
            if (missing.Contains(PreviewKey)) return null;
            b = Load(UserFile(PreviewKey), 1280) ?? Find(new[] { "preview" }, 1280, out _);
            if (b == null) missing.Add(PreviewKey); else cache[PreviewKey] = b;
            return b;
        }

        public static void Set(Profile p, string file) => Save(p.Id, file, 1280);
        public static void Clear(Profile p) => Remove(p.Id);
        public static void SetPreview(string file) { Save(PreviewKey, file, 1600); Scene.PreviewChanged(); }
        public static void ClearPreview() { Remove(PreviewKey); Scene.PreviewChanged(); }

        static void Save(string id, string file, int maxW)
        {
            Directory.CreateDirectory(Folder);
            using (var img = Load(file, maxW))
            {
                if (img == null) throw new InvalidDataException("Not an image");
                img.Save(UserFile(id), ImageFormat.Png);
            }
            Forget(id);
        }

        static void Remove(string id)
        {
            try { if (File.Exists(UserFile(id))) File.Delete(UserFile(id)); } catch { }
            Forget(id);
        }

        static void Forget(string id)
        {
            if (cache.TryGetValue(id, out var b)) { cache.Remove(id); b?.Dispose(); }
            missing.Remove(id);
            bundled.Remove(id);
        }

        static Bitmap Find(IEnumerable<string> names, int maxW, out bool fromExe)
        {
            fromExe = false;
            var wanted = new HashSet<string>(names.Where(n => n.Length > 0));
            try
            {
                if (Directory.Exists(ExeFolder))
                    foreach (var f in Directory.GetFiles(ExeFolder))
                        if (Exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && wanted.Contains(Normalize(Path.GetFileNameWithoutExtension(f))))
                        {
                            var b = Load(f, maxW);
                            if (b != null) return b;
                        }
            }
            catch { }
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var res in asm.GetManifestResourceNames())
                {
                    if (!res.StartsWith("preset.", StringComparison.OrdinalIgnoreCase)) continue;
                    string file = res.Substring("preset.".Length);
                    if (!Exts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    if (!wanted.Contains(Normalize(Path.GetFileNameWithoutExtension(file)))) continue;
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var img = Image.FromStream(s))
                    {
                        fromExe = true;
                        return Downscale(img, maxW);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Loads without keeping the file locked, and shrinks big screenshots.</summary>
        static Bitmap Load(string path, int maxW)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var img = Image.FromStream(ms))
                    return Downscale(img, maxW);
            }
            catch { return null; }
        }

        static Bitmap Downscale(Image img, int maxW)
        {
            float k = img.Width > maxW ? maxW / (float)img.Width : 1f;
            int w = Math.Max(1, (int)Math.Round(img.Width * k)), h = Math.Max(1, (int)Math.Round(img.Height * k));
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return b;
        }
    }
}
