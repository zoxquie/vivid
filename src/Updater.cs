using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>
    /// Keeps Vivid.exe up to date from the GitHub releases of <see cref="AppInfo.Repo"/>.
    /// A newer Vivid.exe is downloaded next to the running one and swapped in place (Windows lets a
    /// running exe be renamed), so the next start, or "Restart to update", runs the new version.
    /// Nothing to reinstall and no old copies to delete.
    /// </summary>
    public static class Updater
    {
        public enum State { Idle, Checking, UpToDate, Downloading, Ready, Failed }

        public static State Status { get; private set; } = State.Idle;
        public static string Detail { get; private set; } = "";
        public static string ReadyVersion { get; private set; }
        public static DateTime LastCheck { get; private set; }
        /// <summary>Raised on the UI thread whenever Status changes.</summary>
        public static event Action Changed;

        static SynchronizationContext ui;
        static int busy;
        static System.Windows.Forms.Timer timer;

        static string ExePath => Application.ExecutablePath;
        static string OldPath => ExePath + ".old";
        static string TempPath => ExePath + ".download";

        public static Version Current => new Version(AppInfo.Version);

        /// <summary>Call once on the UI thread after startup.</summary>
        public static void Start(AppSettings settings)
        {
            ui = SynchronizationContext.Current;
            CleanUp();
            if (!Native.IsWindows) return;
            timer = new System.Windows.Forms.Timer { Interval = 10000 }; // first check shortly after startup
            timer.Tick += (s, e) =>
            {
                timer.Interval = 6 * 60 * 60 * 1000; // then every 6 hours
                if (settings.AutoUpdate) CheckAsync();
            };
            timer.Start();
        }

        /// <summary>Removes the previous version left behind by the last update.</summary>
        static void CleanUp()
        {
            foreach (var p in new[] { OldPath, TempPath })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch { /* the old copy may still be closing; next start gets it */ }
            }
        }

        public static void CheckAsync()
        {
            if (!Native.IsWindows || Status == State.Ready) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { Set(State.Failed, "Couldn't check for updates: " + Short(ex)); }
                finally { Interlocked.Exchange(ref busy, 0); }
            }) { IsBackground = true, Name = "Vivid updater" };
            t.Start();
        }

        static void Run()
        {
            Set(State.Checking, "Checking for updates…");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var release = Fetch("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            string tag = release.TryGetValue("tag_name", out var tg) ? tg as string : null;
            if (!TryParseVersion(tag, out var latest)) throw new InvalidDataException("the latest release has no version number");
            LastCheck = DateTime.Now;
            if (latest <= Current) { Set(State.UpToDate, "You have the latest version (" + AppInfo.Version + ")."); return; }

            string url = null; long size = -1;
            if (release.TryGetValue("assets", out var a) && a is System.Collections.ArrayList assets)
                foreach (Dictionary<string, object> asset in assets)
                    if (string.Equals(asset["name"] as string, "Vivid.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = asset["browser_download_url"] as string;
                        size = Convert.ToInt64(asset["size"]);
                    }
            if (url == null) throw new InvalidDataException("release " + tag + " has no Vivid.exe");

            string v = latest.ToString(3);
            Set(State.Downloading, "Downloading version " + v + "…");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Vivid/" + AppInfo.Version;
                wc.DownloadFile(url, TempPath);
            }

            // Make sure what arrived is a complete, newer Vivid before touching anything.
            if (size > 0 && new FileInfo(TempPath).Length != size) throw new InvalidDataException("the download was incomplete");
            var name = AssemblyName.GetAssemblyName(TempPath);
            if (!string.Equals(name.Name, "Vivid", StringComparison.OrdinalIgnoreCase) || name.Version < latest)
                throw new InvalidDataException("the downloaded file isn't Vivid " + v);

            // Swap: running exe → Vivid.exe.old, download → Vivid.exe. Put it back if the second step fails.
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            try { File.Move(TempPath, ExePath); }
            catch { File.Move(OldPath, ExePath); throw; }

            ReadyVersion = v;
            Set(State.Ready, "Version " + v + " is installed. Restart Vivid to use it.");
        }

        static Dictionary<string, object> Fetch(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Vivid/" + AppInfo.Version;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidDataException("no releases have been published yet");
            }
        }

        public static bool TryParseVersion(string tag, out Version v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string s = tag.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            if (!Version.TryParse(s, out var parsed)) return false;
            v = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        /// <summary>Starts the new version and closes this one.</summary>
        public static void RestartNow()
        {
            try { Process.Start(new ProcessStartInfo(ExePath, "--updated") { UseShellExecute = false }); }
            catch (Exception ex) { Set(State.Failed, "Couldn't restart: " + Short(ex)); return; }
            Ui.Form?.ExitApp();
        }

        static void Set(State s, string detail)
        {
            void apply() { Status = s; Detail = detail; Changed?.Invoke(); }
            if (ui != null) ui.Post(_ => apply(), null); else apply();
        }

        static string Short(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Vivid's folder is read-only. Move Vivid.exe to a folder you own (like Downloads or Documents).";
            if (ex is WebException) return "no internet connection or GitHub didn't answer.";
            return ex.Message.TrimEnd('.') + ".";
        }
    }
}
