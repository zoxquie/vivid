using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Vivid
{
    /// <summary>App state and behaviour, independent of how the UI looks.</summary>
    public sealed class AppController : IDisposable
    {
        public AppSettings Settings { get; }
        public ColorEngine Engine { get; } = new ColorEngine();
        public HotkeyManager Hotkeys { get; private set; }
        public CrosshairOverlay Crosshair { get; private set; }
        public OsdToast Osd { get; private set; }

        public event Action Changed;          // values / status changed -> repaint
        public event Action StructureChanged; // profile list changed -> rebuild widgets
        public event Action<string> Toast;    // short in-app message

        readonly Timer applyTimer, saveTimer, watchdogTimer;
        bool applyPending;
        FilterValues applied;

        /// <summary>
        /// The editing stage: slider and preset changes land here first and only reach the screen
        /// (and your saved settings) when you press Apply. Null when nothing is waiting.
        /// </summary>
        sealed class Draft { public string Id; public FilterValues V; public string From, FromId; }
        Draft draft;

        /// <summary>True while there are edits that aren't on screen yet.</summary>
        public bool PendingChanges => draft != null && (draft.Id != Settings.SelectedProfileId || !draft.V.SameAs(AppliedValues));
        public bool Editing => PendingChanges;

        public static readonly Profile CustomProfile = new Profile { Id = Presets.CustomId, Name = "Custom", BuiltIn = true, Art = "" };

        public AppController(AppSettings s)
        {
            Settings = s;
            applyTimer = new Timer { Interval = 16 };
            applyTimer.Tick += (o, e) => { applyTimer.Stop(); if (applyPending) ApplyNow(); };
            saveTimer = new Timer { Interval = 600 };
            saveTimer.Tick += (o, e) => { saveTimer.Stop(); Storage.Save(Settings); };
            watchdogTimer = new Timer { Interval = 2000 };
            watchdogTimer.Tick += (o, e) => { if (Engine.NeedsWatchdog) Engine.Watchdog(); };
        }

        System.Threading.SynchronizationContext ui;

        public void Start()
        {
            ui = System.Threading.SynchronizationContext.Current;
            Engine.Init(Settings, SaveNow); // recovery flags must hit the disk before the display changes
            Hotkeys = new HotkeyManager();
            Crosshair = new CrosshairOverlay();
            Osd = new OsdToast();
            RegisterHotkeys();
            ApplyNow();
            Crosshair.Update(Settings.Crosshair);
            watchdogTimer.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        bool OnUiThread(Action a)
        {
            if (ui == null || System.Threading.SynchronizationContext.Current == ui) return true;
            ui.Post(_ => a(), null);
            return false;
        }

        void OnDisplaysChanged(object sender, EventArgs e)
        {
            if (!OnUiThread(() => OnDisplaysChanged(sender, e))) return;
            Engine.OnDisplaysChanged(AppliedValues, Settings.FiltersOn);
            Crosshair?.Update(Settings.Crosshair);
            Changed?.Invoke();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!OnUiThread(() => OnSessionSwitch(sender, e))) return;
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                Engine.OnDisplaysChanged(AppliedValues, Settings.FiltersOn);
        }

        // ---------------- profiles ----------------

        public IEnumerable<Profile> BuiltIns => Presets.All;
        public List<Profile> UserProfiles => Settings.UserProfiles;

        public IEnumerable<Profile> AllProfiles()
        {
            yield return CustomProfile;
            foreach (var p in Presets.All) yield return p;
            foreach (var p in Settings.UserProfiles) yield return p;
        }

        public Profile Find(string id) => AllProfiles().FirstOrDefault(p => p.Id == id);

        /// <summary>The profile that's on screen.</summary>
        public Profile Selected => Find(Settings.SelectedProfileId) ?? CustomProfile;

        /// <summary>The values that are on screen (when filters are on).</summary>
        public FilterValues AppliedValues => ValuesOf(Selected);

        /// <summary>The profile the editor is showing: the one you're editing, otherwise the one on screen.</summary>
        public Profile EditSelected => draft != null ? (Find(draft.Id) ?? CustomProfile) : Selected;

        /// <summary>The preset an edited draft started from (e.g. Winter for "Winter · edited"), if any.</summary>
        public Profile EditOrigin => draft?.FromId != null ? Find(draft.FromId) : null;

        /// <summary>The values the sliders show and edit.</summary>
        public FilterValues CurrentValues => draft != null ? draft.V : AppliedValues;

        /// <summary>Short name for what's being edited, e.g. "Winter · edited".</summary>
        public string EditName
        {
            get
            {
                if (draft == null) return Selected.Name;
                if (draft.From != null) return draft.From + " · edited";
                var p = EditSelected;
                return p.Name + (draft.V.SameAs(ValuesOf(p)) ? "" : " · edited");
            }
        }

        Draft EnsureDraft() => draft ?? (draft = new Draft { Id = Settings.SelectedProfileId, V = AppliedValues.Clone() });

        /// <summary>Picks a profile. From the window it goes to the preview first; hotkeys and the tray put it straight on screen.</summary>
        public void Select(Profile p, bool fromHotkey = false)
        {
            if (p == null) return;
            if (fromHotkey)
            {
                draft = null;
                Settings.SelectedProfileId = p.Id;
                RequestApply(immediate: true);
                SaveSoon();
                Changed?.Invoke();
                return;
            }
            draft = new Draft { Id = p.Id, V = ValuesOf(p).Clone() };
            OnValuesEdited(immediate: true);
        }

        /// <summary>Editing a built-in preset turns it into Custom, so presets stay intact.</summary>
        public void SetValue(FilterParam param, double v)
        {
            var d = EnsureDraft();
            d.V.Set(param, v);
            var p = Find(d.Id) ?? CustomProfile;
            if (p.BuiltIn && p.Id != Presets.CustomId && !d.V.SameAs(p.Values)) { d.From = p.Name; d.FromId = p.Id; d.Id = Presets.CustomId; }
            OnValuesEdited();
        }

        public void ResetValues(params FilterParam[] ps)
        {
            var d = EnsureDraft();
            foreach (var x in ps) d.V.Set(x, ParamSpec.Of(x).Default);
            var p = Find(d.Id) ?? CustomProfile;
            if (p.BuiltIn && p.Id != Presets.CustomId && !d.V.SameAs(p.Values)) { d.From = p.Name; d.FromId = p.Id; d.Id = Presets.CustomId; }
            OnValuesEdited();
        }

        void OnValuesEdited(bool immediate = false)
        {
            if (draft != null && !PendingChanges) draft = null; // back to exactly what's on screen
            if (Settings.AutoApply) { Commit(quiet: true, immediate: immediate); return; }
            Changed?.Invoke();
        }

        /// <summary>Apply: puts the edits on screen and into your settings.</summary>
        public void Commit(bool quiet = false, bool immediate = true)
        {
            if (draft == null) { if (!quiet) ApplyNow(); return; }
            var d = draft;
            draft = null;
            var p = Find(d.Id) ?? CustomProfile;
            if (p.Id == Presets.CustomId) { Settings.Custom = d.V.Clone(); Settings.SelectedProfileId = Presets.CustomId; }
            else if (!p.BuiltIn) { p.Values = d.V.Clone(); Settings.SelectedProfileId = p.Id; }
            else if (d.V.SameAs(p.Values)) Settings.SelectedProfileId = p.Id;
            else { Settings.Custom = d.V.Clone(); Settings.SelectedProfileId = Presets.CustomId; }
            if (!quiet) Settings.FiltersOn = true; // pressing Apply means "show me this"
            RequestApply(immediate);
            SaveSoon();
            if (!quiet) Toast?.Invoke("Applied " + Selected.Name);
            Changed?.Invoke();
        }

        /// <summary>Drops the edits; the editor goes back to what's on screen.</summary>
        public void Discard()
        {
            if (draft == null) return;
            draft = null;
            Changed?.Invoke();
        }

        public Profile SaveAsProfile(string name)
        {
            var p = new Profile { Name = UniqueName(string.IsNullOrWhiteSpace(name) ? "My profile" : name.Trim()), Values = CurrentValues.Clone() };
            Settings.UserProfiles.Add(p);
            draft = null;
            Settings.SelectedProfileId = p.Id; // saving also applies it
            RequestApply(immediate: true);
            SaveSoon();
            StructureChanged?.Invoke();
            Changed?.Invoke();
            return p;
        }

        public void Rename(Profile p, string name)
        {
            if (p == null || p.BuiltIn || string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length > 40) name = name.Substring(0, 40);
            p.Name = name == p.Name ? name : UniqueName(name);
            SaveSoon();
            StructureChanged?.Invoke();
        }

        public FilterValues ValuesOf(Profile p) => p.Id == Presets.CustomId ? Settings.Custom : p.Values;

        public void Duplicate(Profile p)
        {
            var copy = new Profile { Name = UniqueName(p.Id == Presets.CustomId ? "My profile" : p.Name + " copy"), Values = ValuesOf(p).Clone() };
            Settings.UserProfiles.Add(copy);
            SaveSoon();
            StructureChanged?.Invoke();
        }

        public void Delete(Profile p)
        {
            if (p == null || p.BuiltIn) return;
            Settings.UserProfiles.Remove(p);
            Settings.ProfileKeys.RemoveAll(k => k.ProfileId == p.Id);
            if (draft != null && draft.Id == p.Id) { draft.Id = Presets.CustomId; draft.From = p.Name; }
            if (Settings.SelectedProfileId == p.Id)
            {
                Settings.Custom = p.Values.Clone();
                Settings.SelectedProfileId = Presets.CustomId;
            }
            RegisterHotkeys();
            SaveSoon();
            StructureChanged?.Invoke();
            Changed?.Invoke();
        }

        public int Import(List<Profile> list)
        {
            foreach (var p in list)
            {
                p.Name = UniqueName(p.Name);
                Settings.UserProfiles.Add(p);
            }
            if (list.Count > 0)
            {
                SaveSoon();
                StructureChanged?.Invoke();
            }
            return list.Count;
        }

        string UniqueName(string name)
        {
            var names = new HashSet<string>(AllProfiles().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(name)) return name;
            for (int i = 2; ; i++) if (!names.Contains($"{name} {i}")) return $"{name} {i}";
        }

        // ---------------- applying ----------------

        public void RequestApply(bool immediate = false)
        {
            applyPending = true;
            if (immediate) { applyTimer.Stop(); ApplyNow(); }
            else if (!applyTimer.Enabled) applyTimer.Start();
        }

        /// <summary>Sends what's applied to the screen. Draft edits are never sent here; Commit them first.</summary>
        public void ApplyNow()
        {
            applyPending = false;
            applied = AppliedValues.Clone();
            Engine.Apply(applied, Settings.FiltersOn);
            Changed?.Invoke();
        }

        public void SetFiltersOn(bool on, bool fromHotkey = false)
        {
            Settings.FiltersOn = on;
            ApplyNow();
            SaveSoon();
            if (fromHotkey) ShowOsd(on ? "Filters on" : "Filters off", on ? Selected.Name : "", on ? Theme.Green : Theme.TextMuted);
            Changed?.Invoke();
        }

        public void SetAutoApply(bool on)
        {
            Settings.AutoApply = on;
            if (on && PendingChanges) Commit(quiet: true);
            SaveSoon();
            Changed?.Invoke();
        }

        public void SetEngine(EngineMode m)
        {
            if (Settings.Engine == m) return;
            Engine.ResetDisplay();
            Settings.Engine = m;
            ApplyNow();
            SaveSoon();
        }

        public void SetUseNv(bool on)
        {
            Settings.UseNvVibrance = on;
            ApplyNow();
            SaveSoon();
        }

        /// <summary>The status line under the sliders.</summary>
        public string StatusLine
        {
            get
            {
                if (PendingChanges) return "Only in the preview";
                if (!Settings.FiltersOn) return "Filters are off";
                var p = Selected;
                if (p.Id == Presets.CustomId) return AppliedValues.IsNeutral ? "Neutral, no change to the screen" : "Custom values applied";
                return (p.BuiltIn ? "Preset " : "Profile ") + p.Name + " applied";
            }
        }

        public Color StatusColor =>
            PendingChanges ? Theme.Amber :
            !Settings.FiltersOn ? Theme.TextDim :
            Engine.Health == EngineHealth.Error ? Theme.Red :
            Engine.Health == EngineHealth.Warning ? Theme.Amber : Theme.Green;

        // ---------------- hotkeys ----------------

        public Hotkey KeyFor(Profile p) => Settings.ProfileKeys.FirstOrDefault(k => k.ProfileId == p.Id)?.Key;

        public void SetProfileKey(Profile p, Hotkey key)
        {
            Settings.ProfileKeys.RemoveAll(k => k.ProfileId == p.Id);
            if (key != null && !key.IsEmpty)
            {
                RemoveKeyEverywhere(key);
                Settings.ProfileKeys.Add(new ProfileKey { ProfileId = p.Id, Key = key });
            }
            RegisterHotkeys();
            SaveSoon();
            Changed?.Invoke();
        }

        public void SetGlobalKey(string which, Hotkey key)
        {
            if (key != null && !key.IsEmpty) RemoveKeyEverywhere(key);
            key = key ?? new Hotkey();
            switch (which)
            {
                case "filters": Settings.ToggleFiltersKey = key; break;
                case "next": Settings.NextProfileKey = key; break;
                case "crosshair": Settings.ToggleCrosshairKey = key; break;
                case "window": Settings.ShowWindowKey = key; break;
            }
            RegisterHotkeys();
            SaveSoon();
            Changed?.Invoke();
        }

        /// <summary>One key can only do one thing.</summary>
        void RemoveKeyEverywhere(Hotkey key)
        {
            Settings.ProfileKeys.RemoveAll(k => k.Key.SameAs(key));
            if (Settings.ToggleFiltersKey.SameAs(key)) Settings.ToggleFiltersKey = new Hotkey();
            if (Settings.NextProfileKey.SameAs(key)) Settings.NextProfileKey = new Hotkey();
            if (Settings.ToggleCrosshairKey.SameAs(key)) Settings.ToggleCrosshairKey = new Hotkey();
            if (Settings.ShowWindowKey.SameAs(key)) Settings.ShowWindowKey = new Hotkey();
        }

        public Action ShowWindowRequested;

        public void RegisterHotkeys()
        {
            if (Hotkeys == null) return;
            Hotkeys.Clear();
            if (Hotkeys.Suspended) return;
            Hotkeys.Add(Settings.ToggleFiltersKey, "Toggle filters", () => SetFiltersOn(!Settings.FiltersOn, true));
            Hotkeys.Add(Settings.NextProfileKey, "Next profile", NextProfile);
            Hotkeys.Add(Settings.ToggleCrosshairKey, "Toggle crosshair", () => SetCrosshairEnabled(!Settings.Crosshair.Enabled, true));
            Hotkeys.Add(Settings.ShowWindowKey, "Show window", () => ShowWindowRequested?.Invoke());
            foreach (var pk in Settings.ProfileKeys)
            {
                var id = pk.ProfileId;
                var p = Find(id);
                if (p == null) continue;
                Hotkeys.Add(pk.Key, p.Name, () => ProfileHotkey(id));
            }
            Changed?.Invoke();
        }

        public List<string> HotkeyFailures => Hotkeys?.Failures ?? new List<string>();

        public void SuspendHotkeys(bool suspend)
        {
            if (Hotkeys == null) return;
            Hotkeys.Suspended = suspend;
            if (suspend) Hotkeys.Clear(); else RegisterHotkeys();
        }

        void ProfileHotkey(string id)
        {
            var p = Find(id);
            if (p == null) return;
            if (Settings.SelectedProfileId == id && Settings.FiltersOn)
            {
                SetFiltersOn(false, true); // pressing the same profile key again turns filters off
                return;
            }
            draft = null;
            Settings.SelectedProfileId = id;
            Settings.FiltersOn = true;
            ApplyNow();
            SaveSoon();
            ShowOsd(p.Name, "applied", Theme.Green);
            Changed?.Invoke();
        }

        void NextProfile()
        {
            var list = AllProfiles().ToList();
            int i = list.FindIndex(p => p.Id == Settings.SelectedProfileId);
            var next = list[(i + 1 + list.Count) % list.Count];
            draft = null;
            Settings.SelectedProfileId = next.Id;
            Settings.FiltersOn = true;
            ApplyNow();
            SaveSoon();
            ShowOsd(next.Name, "applied", Theme.Green);
            Changed?.Invoke();
        }

        void ShowOsd(string title, string detail, Color dot)
        {
            if (!Settings.ShowOsd || Osd == null) return;
            if (Form.ActiveForm is MainForm) return; // the app itself is in front, the UI already shows it
            Osd.Show(title, detail, dot);
        }

        // ---------------- crosshair ----------------

        public void CrosshairChanged()
        {
            Settings.Crosshair.Sanitize();
            Crosshair?.Update(Settings.Crosshair);
            SaveSoon();
            Changed?.Invoke();
        }

        public void SetCrosshairEnabled(bool on, bool fromHotkey = false)
        {
            Settings.Crosshair.Enabled = on;
            CrosshairChanged();
            if (fromHotkey) ShowOsd(on ? "Crosshair on" : "Crosshair off", "", on ? Theme.Green : Theme.TextMuted);
        }

        /// <summary>Black ring and dot sized for the AK holosight, shown only while aiming.</summary>
        public void SetupBlackHolo()
        {
            var c = Settings.Crosshair;
            var b = CrosshairOverlay.TargetBounds(c);
            double k = Math.Max(0.75, b.Height / 1080.0); // the reticle grows with resolution
            c.Style = CrossStyle.CircleDot;
            c.Color = unchecked((int)0xFF000000);
            c.CircleRadius = (int)Math.Round(9 * k);
            c.Thickness = Math.Max(2, (int)Math.Round(2 * k));
            c.DotSize = Math.Max(2, (int)Math.Round(3 * k));
            c.Outline = 0;
            c.Opacity = 1;
            c.OffsetX = c.OffsetY = 0;
            c.AimOnly = true;
            c.Enabled = true;
            CrosshairChanged();
            Toast?.Invoke("Black holo on: hold right-click in game");
        }

        public void ResetCrosshair()
        {
            bool en = Settings.Crosshair.Enabled;
            int disp = Settings.Crosshair.Display;
            Settings.Crosshair = new CrosshairSettings { Enabled = en, Display = disp };
            CrosshairChanged();
        }

        // ---------------- system ----------------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public void SetStartWithWindows(bool on)
        {
            Settings.StartWithWindows = on;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Vivid", "\"" + Application.ExecutablePath + "\" --tray");
                    else if (k.GetValue("Vivid") != null) k.DeleteValue("Vivid", false);
                }
            }
            catch { Toast?.Invoke("Could not change the startup setting"); }
            SaveSoon();
            Changed?.Invoke();
        }

        /// <summary>Keeps the Run entry pointing at this exe if the user moved it.</summary>
        public void SyncStartupEntry()
        {
            if (Settings.StartWithWindows) SetStartWithWindows(true);
        }

        public void UnlockGammaRange()
        {
            try
            {
                var psi = new ProcessStartInfo("reg.exe",
                    "add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p?.WaitForExit(15000);
                Toast?.Invoke(ColorEngine.GammaRangeUnlocked ? "Gamma range unlocked. Restart Windows to finish." : "Gamma range was not changed");
            }
            catch { Toast?.Invoke("Unlock cancelled"); }
            Changed?.Invoke();
        }

        public void RaiseToast(string s) => Toast?.Invoke(s);

        public void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        public void SaveNow() { saveTimer.Stop(); Storage.Save(Settings); }

        public void Dispose()
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            applyTimer.Stop(); watchdogTimer.Stop();
            Hotkeys?.Dispose();
            Crosshair?.Dispose();
            Osd?.Dispose();
            Engine.Shutdown();
            SaveNow();
        }
    }
}
