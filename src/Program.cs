using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyDescription("Screen color filters, presets, hotkeys and a black holo for Rust")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Vivid
{
    internal static class Program
    {
        public const string Version = AppInfo.Version;
        static AppController ctl;
        static Icon appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (appIcon != null) return appIcon;
                try { if (Native.IsWindows) appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                if (appIcon == null)
                {
                    using (var bmp = new Bitmap(64, 64))
                    {
                        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Theme.DrawLogo(g, new RectangleF(2, 2, 60, 60)); }
                        appIcon = Icon.FromHandle(bmp.GetHicon());
                    }
                }
                return appIcon;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--preview") return Preview(args);
            if (args.Length > 0 && args[0] == "--bench") return Bench(args);
            if (args.Length > 1 && args[0] == "--export-art") return ExportArt(args[1]);

            using (var mutex = new Mutex(true, @"Local\Vivid.ScreenFilters.SingleInstance", out bool first))
            {
                // "--updated": the previous version is closing; wait for it instead of handing off
                if (!first && args.Contains("--updated"))
                {
                    try { first = mutex.WaitOne(15000); } catch (AbandonedMutexException) { first = true; }
                }
                if (!first && !TakeOver(mutex))
                {
                    // Already running: ask the other copy to show its window.
                    try { Native.PostMessage(Native.HWND_BROADCAST, Native.RegisterWindowMessage("Vivid.ShowWindow"), IntPtr.Zero, IntPtr.Zero); } catch { }
                    return 0;
                }
                try { Directory.CreateDirectory(Storage.Folder); File.WriteAllText(InstanceFile, Application.ExecutablePath); } catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log(e.ExceptionObject as Exception);
                    try { ctl?.Engine.Shutdown(); } catch { }
                };
                Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => { try { ctl?.Engine.Shutdown(); ctl?.SaveNow(); } catch { } };

                float scale = 1f;
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }
                Theme.Init(Math.Max(1f, scale));

                var settings = Storage.Load();
                ctl = new AppController(settings);

                // Create the window first so a WinForms SynchronizationContext exists before
                // anything subscribes to system events, then start the engine, hotkeys and overlay.
                bool hidden = args.Contains("--tray") || settings.StartMinimized;
                var form = new MainForm(ctl, hidden);
                ctl.Start();
                Updater.Start(settings);
                ctl.SyncStartupEntry();
                try { Application.Run(form); }
                finally
                {
                    ctl.Dispose();
                }
            }
            return 0;
        }

        static string InstanceFile => Path.Combine(Storage.Folder, "running.txt");

        /// <summary>
        /// If the copy that's already running is a different Vivid.exe (for example an update you just opened),
        /// ask it to close and start in its place. Opening the same exe twice just shows the running window.
        /// </summary>
        static bool TakeOver(Mutex mutex)
        {
            try
            {
                string other = File.Exists(InstanceFile) ? File.ReadAllText(InstanceFile).Trim() : null;
                if (string.IsNullOrEmpty(other)) return false;
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
                Native.PostMessage(Native.HWND_BROADCAST, Native.RegisterWindowMessage("Vivid.Quit"), IntPtr.Zero, IntPtr.Zero);
                try { return mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { return true; }
            }
            catch { return false; }
        }

        /// <summary>Developer tool: times window repaints (whole frame and per widget).</summary>
        static int Bench(string[] args)
        {
            float scale = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.5f;
            Theme.Init(scale);
            var s = new AppSettings();
            s.Sanitize();
            s.SelectedProfileId = "builtin.summer";
            s.UserProfiles.Add(new Profile { Name = "Rust day", Values = FilterValues.Make(0.02, 1.12, 1.45, 0, 1.05) });
            s.UserProfiles.Add(new Profile { Name = "Monument night", Values = FilterValues.Make(0.04, 1.05, 1.25, -6, 2.10) });
            ctl = new AppController(s);
            ctl.Engine.Init(s, null);
            var form = new MainForm(ctl, true);
            var size = new Size((int)(1280 * scale), (int)(830 * scale));
            form.MinimumSize = new Size(10, 10);
            form.ClientSize = size;
            form.DoLayout();
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var sw = new System.Diagnostics.Stopwatch();
            double Frame(Action before = null, Rectangle? clip = null)
            {
                before?.Invoke();
                using (var g = Graphics.FromImage(bmp))
                {
                    if (clip.HasValue) g.SetClip(clip.Value);
                    sw.Restart();
                    Theme.Prepare(g);
                    Anim.BeginFrame();
                    form.PaintAll(g, size);
                    sw.Stop();
                }
                return sw.Elapsed.TotalMilliseconds;
            }
            double Avg(string name, int n, Func<int, double> f)
            {
                f(0); f(1);
                double t = 0; for (int i = 0; i < n; i++) t += f(i);
                Console.WriteLine($"{name,-34} {t / n,8:F2} ms");
                return t / n;
            }
            Widget Card(string id) { Widget F(Widget w) { if (w is ProfileCard pc && pc.P.Id == id) return w; foreach (var c in w.Children) { var x = F(c); if (x != null) return x; } return null; } return F(form.CurrentPage); }
            Avg("full frame, idle", 20, i => Frame());
            Avg("full frame, hovering a card", 20, i => Frame(() => Ui.Hover = Card(i % 2 == 0 ? "builtin.winter" : "builtin.desert")));
            Ui.Hover = null;
            Avg("full frame, dragging a slider", 20, i => Frame(() => ctl.SetValue(FilterParam.Saturation, 1 + (i % 10) * 0.07)));
            ctl.Discard();
            // hover animation frames only repaint the card's area
            var card = Card("builtin.desert");
            var cardClip = Rectangle.Ceiling(RectangleF.Inflate(card.Bounds, 32 * scale, 32 * scale));
            Avg("hover animation frame (card area)", 20, i => Frame(null, cardClip));
            using (var g = Graphics.FromImage(bmp))
            {
                g.SetClip(cardClip);
                Theme.Prepare(g);
                var bg = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                sw.Restart(); for (int i = 0; i < 10; i++) g.DrawImageUnscaled(bg, 0, 0); sw.Stop();
                Console.WriteLine($"  clipped: background blit {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
                sw.Restart(); for (int i = 0; i < 10; i++) RenderCache.Blit(g, bg, 0, 0); sw.Stop();
                Console.WriteLine($"  clipped: background Blit() {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
                var tb = form.GetType().GetField("topBar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form) as Widget;
                sw.Restart(); for (int i = 0; i < 10; i++) tb.Paint(g); sw.Stop();
                Console.WriteLine($"  clipped: top bar {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
                sw.Restart(); for (int i = 0; i < 10; i++) form.CurrentPage.Paint(g); sw.Stop();
                Console.WriteLine($"  clipped: page {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
                var page = form.CurrentPage;
                sw.Restart(); for (int i = 0; i < 10; i++) page.Relayout(); sw.Stop();
                Console.WriteLine($"  page layout {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
                foreach (var c in page.Children)
                {
                    if (!c.Visible) continue;
                    sw.Restart(); for (int i = 0; i < 10; i++) c.Paint(g); sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds / 10;
                    if (ms > 0.05) Console.WriteLine($"  clipped child {c.GetType().Name} {ms,8:F2} ms");
                    if (ms > 1) foreach (var cc in c.Children)
                    {
                        if (!cc.Visible) continue;
                        sw.Restart(); for (int i = 0; i < 10; i++) cc.Paint(g); sw.Stop();
                        Console.WriteLine($"     {cc.GetType().Name} {sw.Elapsed.TotalMilliseconds / 10,8:F3} ms  bounds {cc.Bounds}  clip {g.VisibleClipBounds}");
                    }
                }
                bg.Dispose();
            }
            // per widget
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.Prepare(g);
                void Time(Widget w, string path, int depth)
                {
                    sw.Restart();
                    for (int i = 0; i < 10; i++) w.Paint(g);
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds / 10;
                    if (ms > 0.4) Console.WriteLine($"{new string(' ', depth * 2)}{path,-40} {ms,8:F2} ms");
                    if (depth < 3 && ms > 1.5) foreach (var c in w.Children) if (c.Visible) Time(c, c.GetType().Name, depth + 1);
                }
                Time(form.CurrentPage, "FiltersPage", 0);
            }
            sw.Restart(); for (int i = 0; i < 10; i++) Scene.FilteredAt(Scene.Procedural, FilterValues.Make(0, 1, 1 + i * 0.01, 0, 1), (int)(330 * scale), (int)(180 * scale), 12 * scale); sw.Stop();
            Console.WriteLine($"{"Preview filter (new values)",-34} {sw.Elapsed.TotalMilliseconds / 10,8:F2} ms");
            Console.WriteLine($"cache: {RenderCache.Count} bitmaps, {RenderCache.Bytes / 1048576.0:F1} MB");
            RenderCache.Clear(); Frame(); Frame();
            Console.WriteLine($"one idle frame needs: {RenderCache.Count} bitmaps, {RenderCache.Bytes / 1048576.0:F1} MB (window {size.Width * size.Height * 4 / 1048576.0:F1} MB)");
            return 0;
        }

        /// <summary>Developer tool: writes the generated preview scene and preset artwork as image files.</summary>
        static int ExportArt(string dir)
        {
            Directory.CreateDirectory(dir);
            Scene.Procedural.Save(Path.Combine(dir, "scene.png"), ImageFormat.Png);
            var jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L) } };
            foreach (var (art, name) in new[] { ("summer", "grass"), ("winter", "winter"), ("desert", "desert"), ("night", "night-vision"), ("vivid", "vibrant") })
                Scene.Biome(art).Save(Path.Combine(dir, name + ".jpg"), jpg, ep);
            return 0;
        }

        static int lastLogged;

        internal static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Storage.Folder);
                File.AppendAllText(Path.Combine(Storage.Folder, "error.log"), DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            // Tell the user once per session, not on every repaint.
            if (Interlocked.Exchange(ref lastLogged, 1) == 0 && Ui.Form != null && !Ui.Form.IsDisposed)
                try { Ui.Form.ShowToast("Something went wrong. Details are in error.log in the settings folder."); } catch { }
        }

        /// <summary>
        /// Developer tool: renders every page to PNG without touching the display.
        /// vivid.exe --preview &lt;folder&gt; [scale]
        /// </summary>
        static int Preview(string[] args)
        {
            string dir = args.Length > 1 ? args[1] : ".";
            float scale = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            Directory.CreateDirectory(dir);
            Anim.Enabled = false; // snapshots show the settled state
            if (!Native.IsWindows) { Theme.RegularFamily = "Segoe UI"; Theme.SemiboldFamily = "Segoe UI Semibold"; }
            Theme.Init(scale);

            var s = new AppSettings();
            s.Sanitize();
            s.SelectedProfileId = "builtin.summer";
            s.UserProfiles.Add(new Profile { Name = "Rust day", Values = FilterValues.Make(0.02, 1.12, 1.45, 0, 1.05) });
            s.UserProfiles.Add(new Profile { Name = "Monument night", Values = FilterValues.Make(0.04, 1.05, 1.25, -6, 2.10) });
            s.ProfileKeys.Add(new ProfileKey { ProfileId = "builtin.night", Key = new Hotkey(Keys.F5) });
            s.Holo.Enabled = true;
            ctl = new AppController(s);
            ctl.Engine.Init(s, null);

            var form = new MainForm(ctl, true);
            var size = new Size((int)(1280 * scale), (int)(830 * scale));
            form.MinimumSize = new Size(10, 10);
            form.ClientSize = size;
            form.DoLayout();

            void Shot(string name, Action setup = null)
            {
                setup?.Invoke();
                form.DoLayout();
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Theme.Prepare(g);
                        g.Clear(Theme.Bg);
                        Anim.BeginFrame();
                        form.PaintAll(g, size);
                    }
                    bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
                }
            }

            Shot("filters");
            Shot("editing", () => { ctl.Select(ctl.Find("builtin.winter")); ctl.SetValue(FilterParam.Hue, 18); ctl.SetValue(FilterParam.Saturation, 1.6); });
            ctl.Discard();
            Shot("hover-winter", () =>
            {
                Widget Find(Widget w) { if (w is ProfileCard pc && pc.P.Id == "builtin.winter") return w; foreach (var c in w.Children) { var f = Find(c); if (f != null) return f; } return null; }
                Ui.Hover = Find(form.CurrentPage);
            });
            Shot("select-night", () => { Ui.Hover = null; s.SelectedProfileId = "builtin.night"; });
            Shot("select-desert", () => { s.SelectedProfileId = "builtin.desert"; });
            s.SelectedProfileId = "builtin.summer";
            Ui.Hover = null;
            Shot("filters-custom", () => { s.SelectedProfileId = s.UserProfiles[1].Id; });
            Shot("holo", () => form.ShowPage(PageId.Holo));
            var normal = size;
            Shot("settings-tall", () => { size = new Size(normal.Width, (int)(2000 * scale)); form.ClientSize = size; form.ShowPage(PageId.Settings); });
            size = normal; form.ClientSize = size;
            typeof(Updater).GetProperty("Status").GetSetMethod(true).Invoke(null, new object[] { Updater.State.Ready });
            typeof(Updater).GetProperty("ReadyVersion").GetSetMethod(true).Invoke(null, new object[] { "1.3.1" });
            Shot("update-ready", () => form.ShowPage(PageId.Filters));
            typeof(Updater).GetProperty("Status").GetSetMethod(true).Invoke(null, new object[] { Updater.State.Idle });
            Shot("settings", () => form.ShowPage(PageId.Settings));
            Shot("dialog", () => { form.ShowPage(PageId.Filters); form.Confirm("Delete profile", "Delete \"Rust day\"? This can't be undone.", "Delete", () => { }, true); });
            form.CloseDialog();
            Shot("small", () =>
            {
                size = new Size((int)(1060 * scale), (int)(620 * scale));
                form.ClientSize = size;
                s.UserProfiles.Clear();
                s.SelectedProfileId = "custom";
                s.Custom = FilterValues.Make(-0.1, 1.3, 0.7, 40, 1.4);
                s.Engine = EngineMode.GammaRamp;
                form.RebuildAll();
                form.ShowPage(PageId.Filters);
            });
            Shot("small-holo", () => form.ShowPage(PageId.Holo));
            Shot("small-editing", () => { form.ShowPage(PageId.Filters); ctl.Select(ctl.Find("builtin.night")); ctl.SetValue(FilterParam.NightVision, 2.2); });
            ctl.Discard();
            return 0;
        }
    }
}
