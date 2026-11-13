using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vivid
{
    /// <summary>
    /// NVIDIA Digital Vibrance through nvapi64.dll (installed with every NVIDIA driver).
    /// This is the driver-level saturation boost from the NVIDIA Control Panel, so it also
    /// works in exclusive fullscreen. Used for the Saturation slider in Gamma ramp mode.
    /// </summary>
    internal sealed class NvVibrance
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr QueryInterfaceFn(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDisplayFn(int index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDvcInfoFn(IntPtr handle, int outputId, ref DvcInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetDvcLevelFn(IntPtr handle, int outputId, int level);

        [StructLayout(LayoutKind.Sequential)]
        struct DvcInfo
        {
            public uint version;
            public int currentLevel;
            public int minLevel;
            public int maxLevel;
        }

        const uint ID_Initialize = 0x0150E828;
        const uint ID_EnumNvidiaDisplayHandle = 0x9ABDD40D;
        const uint ID_GetDVCInfo = 0x4085DE45;
        const uint ID_SetDVCLevel = 0x172409B4;

        GetDvcInfoFn getInfo;
        SetDvcLevelFn setLevel;

        sealed class Display { public IntPtr Handle; public int Original, Min, Max, Current; }
        readonly List<Display> displays = new List<Display>();

        public bool Available { get; private set; }
        public int DisplayCount => displays.Count;
        public string Status { get; private set; } = "Not checked";

        public bool Init()
        {
            Available = false;
            displays.Clear();
            if (!Native.IsWindows || !Environment.Is64BitProcess) { Status = "Not available"; return false; }
            try
            {
                IntPtr lib = Native.LoadLibrary("nvapi64.dll");
                if (lib == IntPtr.Zero) { Status = "No NVIDIA driver found"; return false; }
                IntPtr qiPtr = Native.GetProcAddress(lib, "nvapi_QueryInterface");
                if (qiPtr == IntPtr.Zero) { Status = "NVAPI not usable"; return false; }
                var qi = (QueryInterfaceFn)Marshal.GetDelegateForFunctionPointer(qiPtr, typeof(QueryInterfaceFn));

                IntPtr pInit = qi(ID_Initialize), pEnum = qi(ID_EnumNvidiaDisplayHandle),
                       pGet = qi(ID_GetDVCInfo), pSet = qi(ID_SetDVCLevel);
                if (pInit == IntPtr.Zero || pEnum == IntPtr.Zero || pGet == IntPtr.Zero || pSet == IntPtr.Zero)
                { Status = "Driver does not expose vibrance"; return false; }

                var init = (InitializeFn)Marshal.GetDelegateForFunctionPointer(pInit, typeof(InitializeFn));
                var enumDisp = (EnumDisplayFn)Marshal.GetDelegateForFunctionPointer(pEnum, typeof(EnumDisplayFn));
                getInfo = (GetDvcInfoFn)Marshal.GetDelegateForFunctionPointer(pGet, typeof(GetDvcInfoFn));
                setLevel = (SetDvcLevelFn)Marshal.GetDelegateForFunctionPointer(pSet, typeof(SetDvcLevelFn));

                if (init() != 0) { Status = "NVAPI failed to start"; return false; }

                for (int i = 0; i < 16; i++)
                {
                    if (enumDisp(i, out IntPtr h) != 0 || h == IntPtr.Zero) break;
                    var info = new DvcInfo { version = (uint)Marshal.SizeOf(typeof(DvcInfo)) | 0x10000 };
                    if (getInfo(h, 0, ref info) != 0) continue;
                    if (info.maxLevel <= info.minLevel) continue;
                    displays.Add(new Display { Handle = h, Original = info.currentLevel, Current = info.currentLevel, Min = info.minLevel, Max = info.maxLevel });
                }
                Available = displays.Count > 0;
                Status = Available ? $"NVIDIA vibrance on {displays.Count} display{(displays.Count == 1 ? "" : "s")}" : "No NVIDIA displays found";
                return Available;
            }
            catch (Exception ex)
            {
                Status = "NVAPI error: " + ex.GetType().Name;
                Available = false;
                return false;
            }
        }

        public List<int> OriginalLevels()
        {
            var l = new List<int>();
            foreach (var d in displays) l.Add(d.Original);
            return l;
        }

        /// <summary>Use levels saved by a previous run as the "original" (crash recovery).</summary>
        public void AdoptOriginals(List<int> levels)
        {
            if (levels == null) return;
            for (int i = 0; i < displays.Count && i < levels.Count; i++)
                displays[i].Original = Math.Max(displays[i].Min, Math.Min(displays[i].Max, levels[i]));
        }

        /// <summary>
        /// saturation 1.0 = the user's own driver setting, 2.0 = maximum vibrance,
        /// below 1.0 fades toward the driver minimum.
        /// </summary>
        public void ApplySaturation(double saturation)
        {
            if (!Available) return;
            foreach (var d in displays)
            {
                double level;
                if (saturation >= 1) level = d.Original + (saturation - 1) * (d.Max - d.Min);
                else level = d.Min + (d.Original - d.Min) * saturation;
                int lv = (int)Math.Round(Math.Max(d.Min, Math.Min(d.Max, level)));
                if (lv == d.Current) continue;
                try { if (setLevel(d.Handle, 0, lv) == 0) d.Current = lv; } catch { }
            }
        }

        public bool IsModified
        {
            get { foreach (var d in displays) if (d.Current != d.Original) return true; return false; }
        }

        public void Restore()
        {
            if (!Available) return;
            foreach (var d in displays)
            {
                if (d.Current == d.Original) continue;
                try { if (setLevel(d.Handle, 0, d.Original) == 0) d.Current = d.Original; } catch { }
            }
        }

        public static void RestoreLevels(List<int> levels)
        {
            var nv = new NvVibrance();
            if (!nv.Init()) return;
            nv.AdoptOriginals(levels);
            foreach (var d in nv.displays) d.Current = int.MinValue; // force write
            nv.Restore();
        }
    }
}
