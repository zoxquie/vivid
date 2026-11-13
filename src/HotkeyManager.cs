using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>System-wide hotkeys via RegisterHotKey (no keyboard hooks, nothing injected into games).</summary>
    public sealed class HotkeyManager : NativeWindow, IDisposable
    {
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        int nextId = 1;
        bool suspended;

        public List<string> Failures { get; } = new List<string>();

        public HotkeyManager()
        {
            if (Native.IsWindows) CreateHandle(new CreateParams { Caption = "VividHotkeys" });
        }

        public void Clear()
        {
            if (Handle != IntPtr.Zero)
                foreach (var id in actions.Keys) Native.UnregisterHotKey(Handle, id);
            actions.Clear();
            names.Clear();
            Failures.Clear();
        }

        /// <summary>Registers a hotkey. Returns false if another app already owns it.</summary>
        public bool Add(Hotkey key, string name, Action action)
        {
            if (key == null || key.IsEmpty || Handle == IntPtr.Zero) return false;
            int id = nextId++;
            if (nextId > 0xBFFF) nextId = 1;
            bool ok = !suspended && Native.RegisterHotKey(Handle, id, (uint)key.Mods | Native.MOD_NOREPEAT, (uint)key.Key);
            if (!ok && !suspended) { Failures.Add($"{key} ({name})"); return false; }
            actions[id] = action;
            names[id] = name;
            return ok;
        }

        /// <summary>While the user is choosing a new key we must not swallow it.</summary>
        public bool Suspended
        {
            get => suspended;
            set { suspended = value; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && !suspended)
            {
                int id = m.WParam.ToInt32();
                if (actions.TryGetValue(id, out var a))
                {
                    try { a(); } catch { }
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Clear();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
