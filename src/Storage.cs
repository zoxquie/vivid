using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Vivid
{
    /// <summary>Settings file, profile import/export and share codes.</summary>
    public static class Storage
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivid");
        public static string SettingsPath => Path.Combine(Folder, "settings.json");

        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = Json().Deserialize<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                    if (s != null) { s.Sanitize(); return s; }
                }
            }
            catch
            {
                try { File.Copy(SettingsPath, SettingsPath + ".broken", true); } catch { }
            }
            var fresh = new AppSettings();
            fresh.Sanitize();
            return fresh;
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, Pretty(Json().Serialize(s)), new UTF8Encoding(false));
                if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null);
                else File.Move(tmp, SettingsPath);
            }
            catch { /* never crash because of the settings file */ }
        }

        // ---------------- profiles exchange ----------------

        public class ProfileFile
        {
            public string Format { get; set; } = "vivid-profiles";
            public int Version { get; set; } = 1;
            public List<Profile> Profiles { get; set; } = new List<Profile>();
        }

        public static string ExportJson(IEnumerable<Profile> profiles)
        {
            var f = new ProfileFile();
            foreach (var p in profiles)
                f.Profiles.Add(new Profile { Id = p.Id, Name = p.Name, Values = p.Values.Clone() });
            return Pretty(Json().Serialize(f));
        }

        /// <summary>Accepts a profiles file, a single profile object, or a bare values object.</summary>
        public static List<Profile> ImportJson(string json)
        {
            var result = new List<Profile>();
            json = (json ?? "").Trim();
            if (json.Length == 0) return result;
            var js = Json();
            object raw = js.DeserializeObject(json);
            if (raw is Dictionary<string, object> dict)
            {
                if (dict.ContainsKey("Profiles") || dict.ContainsKey("profiles"))
                {
                    var f = js.Deserialize<ProfileFile>(json);
                    if (f?.Profiles != null) result.AddRange(f.Profiles);
                }
                else if (dict.ContainsKey("Values") || dict.ContainsKey("values"))
                {
                    var p = js.Deserialize<Profile>(json);
                    if (p != null) result.Add(p);
                }
                else
                {
                    var v = js.Deserialize<FilterValues>(json);
                    if (v != null) result.Add(new Profile { Name = "Imported", Values = v });
                }
            }
            else if (raw is object[])
            {
                var list = js.Deserialize<List<Profile>>(json);
                if (list != null) result.AddRange(list);
            }

            result.RemoveAll(p => p == null);
            foreach (var p in result)
            {
                p.Id = Guid.NewGuid().ToString("N"); // always new ids, never collide
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Imported";
                if (p.Name.Length > 40) p.Name = p.Name.Substring(0, 40);
                if (p.Values == null) p.Values = new FilterValues();
                p.Values.Sanitize();
            }
            return result;
        }

        const string SharePrefix = "VIVID1:";

        public static string ToShareCode(Profile p)
        {
            var mini = new Profile { Name = p.Name, Values = p.Values.Clone() };
            string json = Json().Serialize(new ProfileFile { Profiles = new List<Profile> { mini } });
            return SharePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static List<Profile> FromShareCodeOrJson(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string b64 = text.Substring(SharePrefix.Length).Trim();
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                return ImportJson(json);
            }
            return ImportJson(text);
        }

        /// <summary>Indents compact JSON for human-readable files.</summary>
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        indent++; NewLine(sb, indent); break;
                    case '}':
                    case ']':
                        indent--; NewLine(sb, indent); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, indent); break;
                    case ':': sb.Append(": "); break;
                    default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append("\r\n");
            sb.Append(' ', Math.Max(0, indent) * 2);
        }
    }
}
