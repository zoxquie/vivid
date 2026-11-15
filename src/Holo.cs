using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Vivid
{
    /// <summary>The colour thresholds for one reticle colour and tolerance. The GPU shader uses the same numbers.</summary>
    public struct HoloRule
    {
        public double H, S, V, HueWin, SatMin, ValMin, Tol;
        public bool Grey;

        public static HoloRule From(HoloSettings s)
        {
            var t = Color.FromArgb(s.Target);
            HoloMatcher.Hsv(t.R, t.G, t.B, out double th, out double ts, out double tv);
            double tol = Math.Max(0, Math.Min(1, s.Tolerance / 100.0));
            // Tight on purpose: the reticle is drawn bright and fully saturated, while red and orange things in the
            // world (wood, rust, skin, fire, barrels) are darker or duller once Rust has lit them.
            return new HoloRule
            {
                H = th, S = ts, V = tv, Tol = tol, Grey = ts < 0.15,
                HueWin = 5 + 15 * tol,
                SatMin = Math.Max(0.42, ts * (0.9 - 0.4 * tol)),
                ValMin = Math.Max(0.42, tv * (0.86 - 0.4 * tol)),
            };
        }

        /// <summary>3 = the reticle's colour, 2 = its over-bright core, 1 = its glow, 0 = anything else.
        /// Core and glow only count right next to reticle pixels.</summary>
        public byte Classify(double h, double s, double v)
        {
            if (Grey) return (byte)(s < 0.12 + 0.1 * Tol && v >= V - 0.06 - 0.15 * Tol ? 3 : 0);
            double dh = Math.Abs(h - H); if (dh > 180) dh = 360 - dh;
            if (dh <= HueWin && s >= SatMin && v >= ValMin) return 3;
            if (v >= 0.82 && s <= 0.5 && (dh <= HueWin * 2.5 || s < 0.1)) return 2;
            if (dh <= Math.Max(HueWin * 1.8, 35) && s >= 0.15 && v >= 0.2) return 1; // glow blended into whatever is behind it
            return 0;
        }
    }

    /// <summary>
    /// The black holo's pixel rules on the CPU, matching the GPU shader in <see cref="HoloGpu"/>. Used for the
    /// preview on the Holo page and for tests. Only tiles inside <see cref="Zone"/> (where the holo is) change:
    /// <list type="bullet">
    /// <item>pixels of the reticle's colour turn into the output colour,</item>
    /// <item>its over-bright core and its glow do too when they're within 3 px of the reticle,</item>
    /// <item>pixels 1 px from the reticle get a soft edge,</item>
    /// <item>tiles where the colour covers big patches (a sign, a red sky) are left alone.</item>
    /// </list>
    /// </summary>
    public sealed class HoloMatcher
    {
        public const int Tile = 16, TileShift = 4, Glow = 3;
        HoloRule rule;
        byte[] cls = new byte[0];
        // within 3 px / within 1 px of a reticle pixel (spread with running counts, two passes each)
        byte[] nearG = new byte[0], near1 = new byte[0];
        int[] runs = new int[0];
        // direct-mapped cache of colour → class (exact, just faster than working out HSV for every pixel)
        readonly int[] cacheKey = new int[1 << 16];
        readonly byte[] cacheVal = new byte[1 << 16];
        int[] tileHits = new int[0];
        long[] tileLum = new long[0];
        string key;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TilesW { get; private set; }
        public int TilesH { get; private set; }
        /// <summary>Share of each 16×16 tile that has the reticle's colour (0–255), from the last frame.</summary>
        public byte[] TileCoverage = new byte[0];
        /// <summary>Average brightness of each tile (0–255), from the last frame.</summary>
        public byte[] TileLuma = new byte[0];
        /// <summary>255 where the holo is (per tile); null means everywhere.</summary>
        public byte[] Zone;
        /// <summary>Pixels recolored in the last frame.</summary>
        public int Changed { get; private set; }

        public void Configure(HoloSettings s)
        {
            string k = s.Target + "|" + s.Tolerance;
            if (k == key) return;
            key = k;
            rule = HoloRule.From(s);
            for (int i = 0; i < cacheKey.Length; i++) cacheKey[i] = -1;
        }

        public static void Hsv(int r, int g, int b, out double h, out double s, out double v)
        {
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            v = max / 255.0;
            s = max == 0 ? 0 : (max - min) / (double)max;
            if (max == min) { h = 0; return; }
            double d = max - min;
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public void Resize(int w, int h)
        {
            if (w == Width && h == Height) return;
            Width = w; Height = h;
            cls = new byte[w * h];
            nearG = new byte[w * h]; near1 = new byte[w * h];
            runs = new int[w * h];
            TilesW = Math.Max(1, w >> TileShift); TilesH = Math.Max(1, h >> TileShift);
            int n = TilesW * TilesH;
            tileHits = new int[n]; tileLum = new long[n];
            TileCoverage = new byte[n]; TileLuma = new byte[n];
        }

        bool InZone(int x, int y)
        {
            int tx = x >> TileShift, ty = y >> TileShift;
            if (tx >= TilesW || ty >= TilesH) return false;
            return Zone == null || Zone[ty * TilesW + tx] >= 128;
        }

        /// <summary>
        /// src: captured BGRA pixels (top-down, stride = w*4). dst: premultiplied BGRA overlay, rewritten each call
        /// (transparent where nothing changes).
        /// </summary>
        public unsafe void Process(byte* src, byte* dst, Color output)
        {
            int w = Width, h = Height, n = w * h, tw = TilesW, th = TilesH;
            Array.Clear(tileHits, 0, tileHits.Length);
            Array.Clear(tileLum, 0, tileLum.Length);
            fixed (byte* cp = cls)
            {
                // exact colours, like the shader (a small cache: neighbouring pixels repeat a lot)
                int lastRgb = -1; byte lastC = 0;
                for (int y = 0, i = 0; y < h; y++)
                {
                    int ty = y >> TileShift;
                    for (int x = 0; x < w; x++, i++)
                    {
                        int o = i * 4;
                        int rgb = src[o + 2] << 16 | src[o + 1] << 8 | src[o];
                        byte c;
                        if (rgb == lastRgb) c = lastC;
                        else
                        {
                            int slot = (int)(((uint)rgb * 0x9E3779B1u) >> 16) & 0xFFFF;
                            if (cacheKey[slot] == rgb) c = cacheVal[slot];
                            else
                            {
                                Hsv(src[o + 2], src[o + 1], src[o], out double hh, out double ss, out double vv);
                                c = rule.Classify(hh, ss, vv);
                                cacheKey[slot] = rgb; cacheVal[slot] = c;
                            }
                            lastRgb = rgb; lastC = c;
                        }
                        cp[i] = c;
                        int tx = x >> TileShift;
                        if (tx < tw && ty < th)
                        {
                            int t = ty * tw + tx;
                            if (c == 3) tileHits[t]++;
                            tileLum[t] += (src[o + 2] * 299 + src[o + 1] * 587 + src[o] * 114) / 1000;
                        }
                    }
                }
                const int per = Tile * Tile;
                for (int t = 0; t < tileHits.Length; t++)
                {
                    TileCoverage[t] = (byte)Math.Min(255, (tileHits[t] * 255 + per / 2) / per);
                    TileLuma[t] = (byte)Math.Min(255, tileLum[t] / per);
                }

                Spread(cls, nearG, Glow);
                Spread(cls, near1, 1);

                uint solid = Pack(output, 255), soft = Pack(output, 150);
                int changed = 0;
                for (int y = 0, i = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++, i++)
                    {
                        uint px = 0;
                        if (nearG[i] != 0 && InZone(x, y) && !BigPatch(x >> TileShift, y >> TileShift))
                        {
                            byte self = cp[i];
                            if (self >= 1) px = solid;   // the reticle, and its core and glow within 3 px
                            else if (near1[i] != 0) px = soft;        // a soft 1 px edge
                        }
                        *(uint*)(dst + i * 4) = px;
                        if (px != 0) changed++;
                    }
                }
                Changed = changed;
            }
        }

        /// <summary>out = 1 where a reticle pixel (class 3) is within r px (a (2r+1)² square), via running counts.</summary>
        void Spread(byte[] c, byte[] o, int r)
        {
            int w = Width, h = Height;
            // across: how many reticle pixels in x-r..x+r of this row
            for (int y = 0; y < h; y++)
            {
                int row = y * w, cnt = 0;
                for (int x = 0; x < Math.Min(r, w); x++) if (c[row + x] == 3) cnt++;
                for (int x = 0; x < w; x++)
                {
                    int add = x + r, rem = x - r - 1;
                    if (add < w && c[row + add] == 3) cnt++;
                    if (rem >= 0 && c[row + rem] == 3) cnt--;
                    runs[row + x] = cnt;
                }
            }
            // down: any of those within y-r..y+r
            for (int x = 0; x < w; x++)
            {
                int cnt = 0;
                for (int y = 0; y < Math.Min(r, h); y++) cnt += runs[y * w + x] > 0 ? 1 : 0;
                for (int y = 0; y < h; y++)
                {
                    int add = y + r, rem = y - r - 1;
                    if (add < h && runs[add * w + x] > 0) cnt++;
                    if (rem >= 0 && runs[rem * w + x] > 0) cnt--;
                    o[y * w + x] = (byte)(cnt > 0 ? 1 : 0);
                }
            }
        }

        /// <summary>Big patches of the colour (more than 45% of a tile, or of a tile next to it) aren't a reticle.</summary>
        bool BigPatch(int tx, int ty)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tx + dx, y = ty + dy;
                    if (x < 0 || y < 0 || x >= TilesW || y >= TilesH) continue;
                    if (TileCoverage[y * TilesW + x] > 115) return true;
                }
            return false;
        }

        static uint Pack(Color c, int a) =>
            (uint)a << 24 | (uint)(c.R * a / 255) << 16 | (uint)(c.G * a / 255) << 8 | (uint)(c.B * a / 255);
    }

    /// <summary>
    /// Works out where on screen the holosight is, from a 16×16-pixel summary of each frame (how much of each tile
    /// has the reticle's colour, and how bright it is). Only those tiles get recoloured, so red and orange things
    /// anywhere else are never touched.
    /// <para>Aiming down sights, the reticle sits in the middle of the screen: that circle is always watched.
    /// In hip fire the holo sits on the gun, and the gun moves with the camera. So while you look around, the
    /// picture shifts but the reticle stays in the same tiles. Every ~30 ms this compares the summary with one from
    /// a moment ago, measures how far the picture moved (by lining up the tile brightness), and checks every spot
    /// that has the reticle's colour: one that stayed put while the world moved at least three tiles is the holo,
    /// one that moved along with the world is part of the world and is dropped at once.</para>
    /// Pure logic, no Windows calls, so it's tested on its own.
    /// </summary>
    public sealed class HoloZones
    {
        public int TilesW { get; private set; }
        public int TilesH { get; private set; }
        int screenW, screenH;
        float[] score = new float[0];
        bool[] center = new bool[0];
        public byte[] Zone = new byte[0];
        readonly List<Snap> history = new List<Snap>();
        int[] stack = new int[0];
        bool[] seen = new bool[0];
        double lastStep = double.NegativeInfinity;

        /// <summary>Radius of the always-watched circle in the middle, as a share of the screen height.</summary>
        public float CenterRadius = 0.09f;
        /// <summary>Furthest the picture is searched for movement between two compared frames, in tiles.</summary>
        public int MaxShift = 6;
        /// <summary>Tiles the holo has been learned in (for the status line).</summary>
        public int LearnedTiles { get; private set; }
        /// <summary>True when the reticle's colour was seen in a watched tile last frame.</summary>
        public bool Found { get; private set; }
        /// <summary>How far the picture moved at the last learning step, in tiles (for tests).</summary>
        public int LastShiftX, LastShiftY;

        sealed class Snap { public double T; public byte[] Cov, Luma; }

        public void Resize(int tilesW, int tilesH, int width, int height)
        {
            if (tilesW == TilesW && tilesH == TilesH && width == screenW && height == screenH) return;
            TilesW = tilesW; TilesH = tilesH; screenW = width; screenH = height;
            int n = tilesW * tilesH;
            score = new float[n]; center = new bool[n]; Zone = new byte[n];
            stack = new int[n]; seen = new bool[n];
            history.Clear();
            float cx = width / 2f, cy = height / 2f, r = CenterRadius * height + HoloMatcher.Tile;
            for (int ty = 0; ty < tilesH; ty++)
                for (int tx = 0; tx < tilesW; tx++)
                {
                    // nearest point of the tile to the middle of the screen
                    float px = Math.Max(tx * 16f, Math.Min(cx, tx * 16f + 16)), py = Math.Max(ty * 16f, Math.Min(cy, ty * 16f + 16));
                    center[ty * tilesW + tx] = (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r;
                }
            BuildZone();
        }

        public void Reset()
        {
            Array.Clear(score, 0, score.Length);
            history.Clear();
            BuildZone();
        }

        /// <summary>Feeds one frame's tile summary (coverage and brightness, 0–255 each) taken at time tMs.</summary>
        public void Update(byte[] cov, byte[] luma, double tMs)
        {
            int n = TilesW * TilesH;
            if (tMs - lastStep >= 30)
            {
                lastStep = tMs;
                Learn(cov, luma, tMs);
                history.Add(new Snap { T = tMs, Cov = (byte[])cov.Clone(), Luma = (byte[])luma.Clone() });
                while (history.Count > 0 && tMs - history[0].T > 600) history.RemoveAt(0);
                DropBigBlobs();
                BuildZone();
            }
            bool found = false;
            for (int i = 0; i < n && !found; i++) if (Zone[i] != 0 && cov[i] > 0 && cov[i] <= 115) found = true;
            Found = found;
        }

        void Learn(byte[] cov, byte[] luma, double tMs)
        {
            int w = TilesW, h = TilesH;
            // the most recent earlier frame the picture has clearly moved against
            Snap old = null; int sx = 0, sy = 0;
            for (int k = history.Count - 1; k >= 0; k--)
            {
                var sn = history[k];
                if (tMs - sn.T < 25) continue;
                if (!BestShift(luma, sn.Luma, out int bx, out int by)) continue;
                // 3+ tiles: far enough that something moving with the world can't still look "in the same spot"
                if (Math.Max(Math.Abs(bx), Math.Abs(by)) >= 3) { old = sn; sx = bx; sy = by; break; }
            }
            LastShiftX = sx; LastShiftY = sy;
            for (int i = 0; i < score.Length; i++)
                if (cov[i] == 0 || cov[i] > 115) score[i] = Math.Max(0, score[i] - 0.01f);
            if (old == null) return; // standing still or barely moving: nothing to learn from

            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    int i = ty * w + tx, c = cov[i];
                    if (c == 0 || c > 115) continue;
                    // in the old frame, was the colour at the same spot, or where the world was?
                    bool stayed = Any(old.Cov, tx, ty), followed = Any(old.Cov, tx + sx, ty + sy);
                    if (stayed && !followed) score[i] = Math.Min(1, score[i] + 0.2f);
                    else if (followed && !stayed) score[i] = 0;
                }
        }

        bool Any(byte[] c, int tx, int ty)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int y = ty + dy; if (y < 0 || y >= TilesH) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tx + dx; if (x < 0 || x >= TilesW) continue;
                    int v = c[y * TilesW + x];
                    if (v > 0 && v <= 115) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// How far the picture moved between then and now, in tiles: the shift (sx, sy) where now[x, y] best matches
        /// then[x + sx, y + sy]. Counts tiles that line up (brightness within 4) so a gun or HUD that doesn't move
        /// can't outvote the world around it. False when no shift clearly beats standing still.
        /// </summary>
        bool BestShift(byte[] now, byte[] then, out int bx, out int by)
        {
            int w = TilesW, h = TilesH, m = MaxShift;
            int best = -1, bestAt0 = 0; bx = by = 0;
            for (int sy = -m; sy <= m; sy++)
                for (int sx = -m; sx <= m; sx++)
                {
                    int hits = 0, total = 0;
                    for (int y = Math.Max(0, -sy) + 1; y < Math.Min(h, h - sy) - 1; y += 2)
                        for (int x = Math.Max(0, -sx) + 1; x < Math.Min(w, w - sx) - 1; x += 2)
                        {
                            total++;
                            if (Math.Abs(now[y * w + x] - then[(y + sy) * w + x + sx]) <= 4) hits++;
                        }
                    if (total == 0) continue;
                    int score = hits * 1000 / total;
                    if (sx == 0 && sy == 0) bestAt0 = score;
                    if (score > best || (score == best && Math.Abs(sx) + Math.Abs(sy) < Math.Abs(bx) + Math.Abs(by))) { best = score; bx = sx; by = sy; }
                }
            // a real move lines up clearly better than not moving at all
            return best > 250 && best >= bestAt0 + 100;
        }

        /// <summary>A learned area much bigger than a holosight is the world, not the holo: forget it.</summary>
        void DropBigBlobs()
        {
            int w = TilesW, h = TilesH, n = w * h;
            float side = Math.Max(4f, 0.11f * screenH / HoloMatcher.Tile);
            int maxTiles = (int)(side * side);
            Array.Clear(seen, 0, n);
            var blob = new List<int>();
            for (int s0 = 0; s0 < n; s0++)
            {
                if (seen[s0] || score[s0] < 0.5f) continue;
                blob.Clear();
                int sp = 0; stack[sp++] = s0; seen[s0] = true;
                while (sp > 0)
                {
                    int i = stack[--sp]; blob.Add(i);
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = yy * w + xx;
                            if (seen[j] || score[j] < 0.5f) continue;
                            seen[j] = true; stack[sp++] = j;
                        }
                }
                if (blob.Count > maxTiles) foreach (int i in blob) score[i] = 0;
            }
        }

        void BuildZone()
        {
            int w = TilesW, h = TilesH, learned = 0;
            Array.Clear(Zone, 0, Zone.Length);
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    int i = ty * w + tx;
                    if (center[i]) Zone[i] = 255;
                    if (score[i] < 0.5f) continue;
                    learned++;
                    // learned tiles plus one tile around them, for the gun's sway
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = tx + dx, y = ty + dy;
                            if (x >= 0 && y >= 0 && x < w && y < h) Zone[y * w + x] = 255;
                        }
                }
            LearnedTiles = learned;
        }
    }

    /// <summary>
    /// Keeps the Rust holosight reticle black. While Rust is in front it shows Rust's monitor through
    /// <see cref="HoloGpu"/>: each finished frame is captured, the reticle's colour is swapped on the GPU and the
    /// result is shown in a click-through window on top. The whole picture goes through the same shader in the
    /// same frame, so the recolour stays exactly on the reticle as it sways and recoils, with no fringe and no
    /// patches left behind. The window is left out of screen captures, so it never sees its own output.
    /// Runs on its own thread.
    /// </summary>
    public sealed class HoloOverlay : IDisposable
    {
        public enum RunState { Off, WaitingForRust, Running, Unsupported }

        volatile HoloSettings settings;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;

        // live stats for the UI
        public volatile RunState State = RunState.Off;
        public volatile int Fps;
        public volatile float FrameMs;
        /// <summary>The reticle was seen where the holo is in the last frame.</summary>
        public volatile bool HoloFound;
        /// <summary>The hip-fire holo's spot has been learned (more than the aiming circle is watched).</summary>
        public volatile bool HipLearned;
        // what's been learned about where the holo sits; kept while the capture restarts (alt-tab, display changes)
        readonly HoloZones zones = new HoloZones();
        /// <summary>Why it can't run right now (shown on the Holo page), or null.</summary>
        public volatile string Problem;

        public void Update(HoloSettings s)
        {
            settings = s.Clone();
            if (!Native.IsWindows) { State = s.Enabled ? RunState.Unsupported : RunState.Off; return; }
            if (s.Enabled && thread == null)
            {
                stop = false;
                thread = new Thread(Run) { IsBackground = true, Name = "Vivid holo", Priority = ThreadPriority.AboveNormal };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            wake.Set();
        }

        public void Dispose()
        {
            stop = true;
            wake.Set();
            thread?.Join(1500);
            thread = null;
        }

        /// <summary>The click-through, never-activated window the recoloured picture is shown in.</summary>
        internal sealed class Win : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_NCHITTEST: m.Result = new IntPtr(-1); return;  // HTTRANSPARENT
                    case 0x0021: m.Result = new IntPtr(3); return;                 // WM_MOUSEACTIVATE: MA_NOACTIVATE
                    case 0x0014: m.Result = new IntPtr(1); return;                 // WM_ERASEBKGND: Direct3D draws everything
                    case 0x000F: HoloNative.ValidateRect(m.HWnd, IntPtr.Zero); m.Result = IntPtr.Zero; return; // WM_PAINT
                }
                base.WndProc(ref m);
            }

            public static Win Create()
            {
                var win = new Win();
                win.CreateHandle(new CreateParams
                {
                    Caption = AppInfo.Name + " holo",
                    Style = unchecked((int)0x80000000), // WS_POPUP
                    // Layered + transparent: clicks and keys go straight to the game, and Windows doesn't treat the
                    // window as covering Rust (so Rust keeps rendering normally).
                    ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
                });
                HoloNative.SetLayeredWindowAttributes(win.Handle, 0, 255, 2 /* LWA_ALPHA */);
                return win;
            }
        }

        static string Describe(Exception ex)
        {
            int hr = ex.HResult;
            if (ex is DllNotFoundException || ex is EntryPointNotFoundException) return "Direct3D 11 isn't available on this PC";
            if (ex is NotSupportedException) return "Rotated displays aren't supported";
            if (hr == HoloGpu.E_ACCESSDENIED) return "Windows paused screen capture (UAC prompt or lock screen). Trying again…";
            if (hr == HoloGpu.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE) return "Too many apps are capturing this display. Close recording apps and it'll start";
            if (hr == HoloGpu.DXGI_ERROR_UNSUPPORTED) return "This display or graphics driver doesn't support screen capture";
            if (hr == HoloGpu.DXGI_ERROR_DEVICE_REMOVED || hr == HoloGpu.DXGI_ERROR_DEVICE_RESET) return "The graphics driver restarted. Trying again…";
            return "Couldn't start: " + ex.Message;
        }

        void Run()
        {
            // Physical pixels on every monitor, so the window lines up with the captured picture.
            try { HoloNative.SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
            var win = Win.Create();
            IntPtr hwnd = win.Handle;
            bool excluded = false;
            try { excluded = HoloNative.SetWindowDisplayAffinity(hwnd, HoloNative.WDA_EXCLUDEFROMCAPTURE); } catch { }

            HoloGpu gpu = null;
            bool shown = false;
            int hiddenAt = Environment.TickCount, lastTop = 0, lastFail = int.MinValue / 2, lastFsCheck = 0, lastFresh = 0;
            bool exclusive = false;
            string lastLogged = null;
            var fpsClock = Stopwatch.StartNew();
            int frames = 0;
            IntPtr lastFg = IntPtr.Zero; bool fgIsRust = false;

            void Hide()
            {
                if (!shown) return;
                HoloNative.ShowWindow(hwnd, 0);
                shown = false;
                hiddenAt = Environment.TickCount;
            }
            void Drop()
            {
                Hide();
                gpu?.Dispose();
                gpu = null;
            }
            void Fail(Exception ex)
            {
                Drop();
                lastFail = Environment.TickCount;
                Problem = Describe(ex);
                State = RunState.Unsupported;
                if (Problem != lastLogged) { lastLogged = Problem; Program.Log(ex); }
            }
            void Place()
            {
                var b = gpu.Bounds;
                HoloNative.SetWindowPos(hwnd, HoloNative.HWND_TOPMOST, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, 0x0010 | 0x0200); // NOACTIVATE|NOOWNERZORDER
            }

            try
            {
                while (!stop)
                {
                    HoloNative.Pump();
                    var s = settings;
                    if (s == null || !s.Enabled)
                    {
                        Drop(); State = RunState.Off; Problem = null; Fps = 0;
                        wake.WaitOne(500);
                        continue;
                    }
                    if (!excluded)
                    {
                        // Without this the capture would see our own window and feed back into itself.
                        Drop(); State = RunState.Unsupported; Problem = "Needs Windows 10 version 2004 or newer";
                        wake.WaitOne(1000);
                        continue;
                    }

                    IntPtr monitor;
                    if (s.OnlyInRust)
                    {
                        IntPtr fg = HoloNative.GetForegroundWindow();
                        if (fg != lastFg)
                        {
                            lastFg = fg; fgIsRust = false;
                            HoloNative.GetWindowThreadProcessId(fg, out int pid);
                            try { using (var p = Process.GetProcessById(pid)) fgIsRust = p.ProcessName.StartsWith("RustClient", StringComparison.OrdinalIgnoreCase); }
                            catch { }
                        }
                        if (!fgIsRust || HoloNative.IsIconic(fg))
                        {
                            Hide();
                            if (gpu != null && Environment.TickCount - hiddenAt > 3000) Drop(); // let go of the capture while away
                            State = RunState.WaitingForRust; Problem = null; Fps = 0;
                            wake.WaitOne(100);
                            continue;
                        }
                        if (Environment.TickCount - lastFsCheck > 1000)
                        {
                            lastFsCheck = Environment.TickCount;
                            exclusive = HoloNative.SHQueryUserNotificationState(out int qs) == 0 && qs == 3; // QUNS_RUNNING_D3D_FULL_SCREEN
                        }
                        if (exclusive)
                        {
                            Drop(); State = RunState.Unsupported;
                            Problem = "Rust is in exclusive fullscreen. Set Rust to borderless (or windowed) so the black holo can show on top";
                            wake.WaitOne(500);
                            continue;
                        }
                        monitor = HoloNative.MonitorFromWindow(fg, 2);
                    }
                    else
                    {
                        var screens = Screen.AllScreens;
                        var b = screens[Math.Min(Math.Max(0, s.Display), screens.Length - 1)].Bounds;
                        monitor = HoloNative.MonitorFromPoint(new Native.POINT(b.X + b.Width / 2, b.Y + b.Height / 2), 2);
                    }

                    if (gpu != null && gpu.Monitor != monitor) Drop();
                    if (gpu == null)
                    {
                        if (Environment.TickCount - lastFail < 2000) { wake.WaitOne(200); continue; }
                        try
                        {
                            gpu = HoloGpu.ForMonitor(monitor, zones);
                            Place();
                            gpu.AttachWindow(hwnd);
                            gpu.StartCapture();
                            Place();
                        }
                        catch (Exception ex) { Fail(ex); continue; }
                    }

                    HoloGpu.FrameResult r;
                    try
                    {
                        gpu.Configure(s);
                        r = gpu.Frame(50);
                        if (r == HoloGpu.FrameResult.Lost)
                        {
                            // display mode change, UAC prompt or lock screen: pick up again with the new picture
                            Hide();
                            gpu.StartCapture();
                            Place();
                            continue;
                        }
                    }
                    catch (Exception ex) { Fail(ex); continue; }

                    if (r == HoloGpu.FrameResult.Presented)
                    {
                        if (!shown) { HoloNative.ShowWindow(hwnd, 4); shown = true; } // SW_SHOWNOACTIVATE, after the first new frame
                        lastFresh = Environment.TickCount;
                        frames++;
                        FrameMs = gpu.LastWorkMs;
                        HoloFound = zones.Found;
                        HipLearned = zones.LearnedTiles > 0;
                    }
                    else if (shown && Environment.TickCount - lastFresh > 600)
                    {
                        // No new picture for a while (a still menu, or capture stalled): step aside so the game
                        // itself shows. It comes back with the next frame that changes.
                        Hide();
                    }
                    if (shown && Environment.TickCount - lastTop > 1000) { lastTop = Environment.TickCount; HoloNative.KeepOnTop(hwnd); }
                    State = RunState.Running; Problem = null;
                    if (fpsClock.ElapsedMilliseconds >= 1000) { Fps = frames; frames = 0; fpsClock.Restart(); }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                State = RunState.Unsupported;
                Problem = Describe(ex);
            }
            finally
            {
                Drop();
                win.DestroyHandle();
                if (State != RunState.Unsupported) State = RunState.Off;
                thread = null;
            }
        }
    }

    static class HoloNative
    {
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

        public static void KeepOnTop(IntPtr hwnd) => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200); // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        public static void Pump()
        {
            while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
    }
}
