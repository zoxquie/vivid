using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Vivid
{
    /// <summary>
    /// The black holo's GPU side. Desktop Duplication hands over each finished picture of the monitor Rust is on;
    /// two small pixel shaders swap the holosight's colour (plus its bright core, glow and anti-aliased edge) and the
    /// result is shown in a click-through window over the game. Only tiles where the holo is get recoloured: a
    /// 16×16-px summary of every frame is read back (two frames late, never waiting on the GPU) and
    /// <see cref="HoloZones"/> works out where the holosight is. The window is left out of screen captures, so it never
    /// sees itself. Because every pixel of the picture goes through the same shader in the same frame, the
    /// recolour sits exactly on the reticle wherever it is, with no separate layer to fall behind.
    /// Nothing here touches the game's process, files or memory.
    /// Direct3D is reached through raw COM vtable calls; slot numbers come from the Windows SDK headers.
    /// </summary>
    internal sealed unsafe class HoloGpu : IDisposable
    {
        // ---------- vtable slots (d3d11.h, dxgi*.h, d3dcommon.h) ----------
        const int QueryInterface = 0, Release = 2;
        const int Dev_CreateBuffer = 3, Dev_CreateTexture2D = 5, Dev_CreateSRV = 7, Dev_CreateRTV = 9,
                  Dev_CreateVS = 12, Dev_CreatePS = 15, Dev_GetDeviceRemovedReason = 39;
        const int Ctx_PSSetShaderResources = 8, Ctx_PSSetShader = 9, Ctx_VSSetShader = 11, Ctx_GenerateMips = 54, Ctx_Draw = 13, Ctx_Map = 14, Ctx_Unmap = 15,
                  Ctx_PSSetConstantBuffers = 16, Ctx_IASetInputLayout = 17, Ctx_IASetTopology = 24, Ctx_OMSetRenderTargets = 33,
                  Ctx_RSSetViewports = 44, Ctx_CopySubresourceRegion = 46, Ctx_CopyResource = 47, Ctx_UpdateSubresource = 48, Ctx_ClearState = 110, Ctx_Flush = 111;
        const int Obj_GetParent = 6, Dev1_SetMaximumFrameLatency = 12, Adapter_EnumOutputs = 7, Output_GetDesc = 7,
                  Output1_DuplicateOutput = 22, Output5_DuplicateOutput1 = 26;
        const int Dup_GetDesc = 7, Dup_AcquireNextFrame = 8, Dup_ReleaseFrame = 14;
        const int Swap_Present = 8, Swap_GetBuffer = 9, Swap_ResizeBuffers = 13, Factory_MakeWindowAssociation = 8, Factory1_EnumAdapters1 = 12,
                  Factory2_CreateSwapChainForHwnd = 15, Blob_GetBufferPointer = 3, Blob_GetBufferSize = 4;

        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_IDXGIFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        static readonly Guid IID_IDXGIDevice1 = new Guid("77db970f-6276-48ba-ba28-070143b4392c");
        static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_IDXGIOutput5 = new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
        public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
        public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        public const int E_ACCESSDENIED = unchecked((int)0x80070005);

        const uint FMT_B8G8R8A8 = 87, FMT_R8G8B8A8 = 28, FMT_R8 = 61;
        public const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
        const int CoverageMip = 4; // 16×16 px tiles
        const uint BIND_CONSTANT_BUFFER = 0x4, BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
        const uint USAGE_DEFAULT = 0, USAGE_STAGING = 3, CPU_ACCESS_READ = 0x20000;

        // ---------- structs ----------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct OUTPUT_DESC { public fixed char DeviceName[32]; public RECT Desktop; public int Attached; public int Rotation; public IntPtr Monitor; }
        [StructLayout(LayoutKind.Sequential)]
        struct OUTDUPL_DESC { public uint Width, Height, RefreshNum, RefreshDen, Format, Scanline, Scaling; public int Rotation; public int InSystemMemory; }
        [StructLayout(LayoutKind.Sequential)]
        struct OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedMasked;
            public int PtrX, PtrY, PtrVisible; public uint MetadataSize, PointerShapeSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct SWAP_CHAIN_DESC1 { public uint Width, Height, Format; public int Stereo; public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct TEXTURE2D_DESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)]
        struct SUBRESOURCE_DATA { public void* Mem; public uint Pitch, SlicePitch; }
        [StructLayout(LayoutKind.Sequential)]
        struct MAPPED_SUBRESOURCE { public byte* Data; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)]
        struct VIEWPORT { public float X, Y, Width, Height, MinDepth, MaxDepth; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Params { public float TgtH, TgtS, TgtV, Grey, HueWin, SatMin, ValMin, Tol, OutR, OutG, OutB, Pad, W, H, Pad2, Pad3; }

        // ---------- native entry points ----------
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* levels, uint numLevels,
            uint sdkVersion, IntPtr* device, int* level, IntPtr* context);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        static extern int D3DCompile(byte* src, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target,
            uint flags1, uint flags2, IntPtr* code, IntPtr* errors);

        static void* Fn(IntPtr p, int slot) => (*(void***)p)[slot];
        static void Rel(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(p, Release))(p);
            p = IntPtr.Zero;
        }
        static IntPtr QI(IntPtr p, Guid iid)
        {
            IntPtr o;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Fn(p, QueryInterface))(p, &iid, &o);
            return hr < 0 ? IntPtr.Zero : o;
        }
        static void Check(int hr, string what) { if (hr < 0) throw new GpuException(what, hr); }

        public sealed class GpuException : Exception
        {
            public GpuException(string what, int hr) : base(what + " failed (0x" + hr.ToString("X8") + ")") { HResult = hr; }
        }

        // ---------- shaders ----------
        const string Hlsl = @"
cbuffer P : register(b0) { float4 Tgt; float4 Thr; float4 Outc; float4 Size; };
Texture2D<float4> Src : register(t0);
Texture2D<float4> Cls : register(t1);   // r: 1 reticle colour, 0.66 over-bright core, 0.33 glow; g: 1 on reticle colour; b: brightness
Texture2D<float> Zone : register(t2);   // 1 on the 16 px tiles where the holo is (worked out on the CPU)

float4 VS(uint id : SV_VertexID) : SV_Position {
    float2 t = float2((id << 1) & 2, id & 2);
    return float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
}
float3 Hsv(float3 c) {
    float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b)), d = mx - mn, h = 0;
    if (d > 0) {
        if (mx == c.r) h = 60 * ((c.g - c.b) / d);
        else if (mx == c.g) h = 60 * ((c.b - c.r) / d + 2);
        else h = 60 * ((c.r - c.g) / d + 4);
        if (h < 0) h += 360;
    }
    return float3(h, mx > 0 ? d / mx : 0, mx);
}
// same rules as HoloRule.Classify
float Classify(float3 c) {
    float3 h = Hsv(c);
    if (Tgt.w > 0.5) return (h.y < 0.12 + 0.1 * Thr.w && h.z >= Tgt.z - 0.06 - 0.15 * Thr.w) ? 1 : 0;
    float dh = abs(h.x - Tgt.x);
    if (dh > 180) dh = 360 - dh;
    if (dh <= Thr.x && h.y >= Thr.y && h.z >= Thr.z) return 1;
    if (h.z >= 0.82 && h.y <= 0.5 && (dh <= Thr.x * 2.5 || h.y < 0.1)) return 0.66;
    if (dh <= max(Thr.x * 1.8, 35) && h.y >= 0.15 && h.z >= 0.2) return 0.33;
    return 0;
}
float4 PSClass(float4 p : SV_Position) : SV_Target {
    float3 c = Src.Load(int3(p.xy, 0)).rgb;
    float k = Classify(c);
    return float4(k, k > 0.9 ? 1 : 0, dot(c, float3(0.299, 0.587, 0.114)), 1);
}
float4 PSMain(float4 p : SV_Position) : SV_Target {
    int2 q = int2(p.xy);
    float3 c = Src.Load(int3(q, 0)).rgb;
    int2 t = q >> COVERAGE_MIP;
    // only where the holo is; everything else on screen is passed through untouched
    if (Zone.Load(int3(t, 0)) < 0.5) return float4(c, 1);
    // A reticle is thin lines and a dot: it never fills much of a 16 px tile. Big patches of the colour
    // are left alone, edges included (this tile and the 8 around it).
    float cov = 0;
TILES
    if (cov > 0.45) return float4(c, 1);
    float self = Cls.Load(int3(q, 0)).r;
    if (self > 0.9) return float4(Outc.rgb, 1);
    // is there a reticle pixel within 3 px (for its core and glow) and within 1 px (soft edge)?
    float nearG = 0, near1 = 0;
NEIGHBOURS
    if (nearG > 0.9 && self > 0.2) return float4(Outc.rgb, 1);
    if (near1 > 0.9) return float4(lerp(c, Outc.rgb, 150.0 / 255.0), 1);
    return float4(c, 1);
}";

        // ---------- state ----------
        IntPtr factory, adapter, output, device, context, dup, swap, bbRtv;
        IntPtr vs, psClass, psMain, cbuf;
        Params pars;
        IntPtr frameTex, frameSrv, clsTex, clsRtv, clsSrv, zoneTex, zoneSrv;
        readonly IntPtr[] staging = new IntPtr[3];
        int width, height, tilesW, tilesH;
        long frameNo;
        byte[] tileBgra = new byte[0], tileCov = new byte[0], tileLuma = new byte[0];
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Where the holo is on screen, learned from the frames (kept by the overlay across restarts).</summary>
        public HoloZones Zones = new HoloZones();
        string paramKey;
        bool haveFrame;

        public IntPtr Monitor { get; private set; }
        public RECT Bounds { get; private set; }
        public int Width => width;
        public int Height => height;
        /// <summary>GPU time spent on the last frame's copy, shaders and present, measured on the CPU side.</summary>
        public float LastWorkMs { get; private set; }
        readonly System.Diagnostics.Stopwatch work = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Picks the GPU and output that drive the given monitor and creates the device there.
        /// Then: <see cref="AttachWindow"/>, <see cref="StartCapture"/>, and <see cref="Frame"/> in a loop.
        /// </summary>
        public static HoloGpu ForMonitor(IntPtr monitor, HoloZones zones = null)
        {
            var g = new HoloGpu();
            if (zones != null) g.Zones = zones;
            try
            {
                g.Open(monitor);
                return g;
            }
            catch { g.Dispose(); throw; }
        }

        /// <summary>A device on the default GPU with no monitor attached (for tests).</summary>
        public static HoloGpu Offscreen()
        {
            var g = new HoloGpu();
            try { g.CreateDevice(IntPtr.Zero); g.CreatePipeline(); return g; }
            catch { g.Dispose(); throw; }
        }

        void Open(IntPtr monitor)
        {
            Guid iid = IID_IDXGIFactory1;
            IntPtr f;
            Check(CreateDXGIFactory1(&iid, &f), "CreateDXGIFactory1");
            factory = f;
            for (uint a = 0; output == IntPtr.Zero; a++)
            {
                IntPtr ad;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(factory, Factory1_EnumAdapters1))(factory, a, &ad);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Check(hr, "EnumAdapters1");
                for (uint o = 0; ; o++)
                {
                    IntPtr op;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(ad, Adapter_EnumOutputs))(ad, o, &op);
                    if (hr == DXGI_ERROR_NOT_FOUND) break;
                    Check(hr, "EnumOutputs");
                    OUTPUT_DESC d;
                    ((delegate* unmanaged[Stdcall]<IntPtr, OUTPUT_DESC*, int>)Fn(op, Output_GetDesc))(op, &d);
                    if (d.Monitor == monitor)
                    {
                        output = op; adapter = ad; ad = IntPtr.Zero;
                        Monitor = monitor; Bounds = d.Desktop;
                        if (d.Rotation > 1) throw new NotSupportedException("rotated displays aren't supported");
                        break;
                    }
                    Rel(ref op);
                }
                Rel(ref ad);
            }
            if (output == IntPtr.Zero) throw new GpuException("Finding the display", DXGI_ERROR_NOT_FOUND);
            CreateDevice(adapter);
            CreatePipeline();
            CreateTargets(Bounds.Right - Bounds.Left, Bounds.Bottom - Bounds.Top);
        }

        void CreateDevice(IntPtr onAdapter)
        {
            int* levels = stackalloc int[3];
            levels[0] = 0xb000; levels[1] = 0xa100; levels[2] = 0xa000; // 11_0, 10_1, 10_0
            IntPtr dev, ctx; int fl;
            // With a specific adapter the driver type must be UNKNOWN (0); otherwise HARDWARE (1).
            Check(D3D11CreateDevice(onAdapter, onAdapter == IntPtr.Zero ? 1 : 0, IntPtr.Zero, 0x20 /* BGRA */, levels, 3, 7, &dev, &fl, &ctx),
                "D3D11CreateDevice");
            device = dev; context = ctx;
            IntPtr d1 = QI(device, IID_IDXGIDevice1);
            if (d1 != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Fn(d1, Dev1_SetMaximumFrameLatency))(d1, 1);
                Rel(ref d1);
            }
        }

        internal static string ShaderSource()
        {
            var n = new StringBuilder();
            int gr = HoloMatcher.Glow;
            for (int dy = -gr; dy <= gr; dy++)
                for (int dx = -gr; dx <= gr; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    n.Append("    { float v = Cls.Load(int3(q + int2(").Append(dx).Append(", ").Append(dy).Append("), 0)).r; nearG = max(nearG, v);");
                    if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) n.Append(" near1 = max(near1, v);");
                    n.Append(" }\n");
                }
            var tiles = new StringBuilder();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    tiles.Append("    cov = max(cov, Cls.Load(int3(t + int2(").Append(dx).Append(", ").Append(dy).Append("), COVERAGE_MIP)).g);\n");
            return Hlsl.Replace("NEIGHBOURS\n", n.ToString()).Replace("TILES\n", tiles.ToString()).Replace("COVERAGE_MIP", CoverageMip.ToString());
        }

        IntPtr Compile(string entry, string target)
        {
            var bytes = Encoding.ASCII.GetBytes(ShaderSource());
            IntPtr code, errors;
            int hr;
            fixed (byte* b = bytes)
                hr = D3DCompile(b, (UIntPtr)bytes.Length, "holo.hlsl", IntPtr.Zero, IntPtr.Zero, entry, target, 0x8000 /* O3 */, 0, &code, &errors);
            if (hr < 0)
            {
                string msg = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPtr(errors), (int)BlobSize(errors)) : "";
                Rel(ref errors);
                throw new GpuException("Compiling " + entry + ": " + msg.Trim(), hr);
            }
            Rel(ref errors);
            return code;
        }
        static IntPtr BlobPtr(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Fn(blob, Blob_GetBufferPointer))(blob);
        static UIntPtr BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr>)Fn(blob, Blob_GetBufferSize))(blob);

        void CreatePipeline()
        {
            IntPtr blob = Compile("VS", "vs_4_0");
            try
            {
                IntPtr s;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, IntPtr, IntPtr*, int>)Fn(device, Dev_CreateVS))(device, BlobPtr(blob), BlobSize(blob), IntPtr.Zero, &s), "CreateVertexShader");
                vs = s;
            }
            finally { Rel(ref blob); }
            psClass = Pixel("PSClass");
            psMain = Pixel("PSMain");
            var bd = new BUFFER_DESC { ByteWidth = (uint)sizeof(Params), Usage = USAGE_DEFAULT, BindFlags = BIND_CONSTANT_BUFFER };
            IntPtr cb;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, BUFFER_DESC*, void*, IntPtr*, int>)Fn(device, Dev_CreateBuffer))(device, &bd, null, &cb), "CreateBuffer");
            cbuf = cb;
        }

        IntPtr Pixel(string entry)
        {
            IntPtr blob = Compile(entry, "ps_4_0");
            try
            {
                IntPtr s;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UIntPtr, IntPtr, IntPtr*, int>)Fn(device, Dev_CreatePS))(device, BlobPtr(blob), BlobSize(blob), IntPtr.Zero, &s), "CreatePixelShader");
                return s;
            }
            finally { Rel(ref blob); }
        }

        /// <summary>Starts Desktop Duplication of the display.</summary>
        public void StartCapture()
        {
            Rel(ref dup);
            OUTPUT_DESC od;
            ((delegate* unmanaged[Stdcall]<IntPtr, OUTPUT_DESC*, int>)Fn(output, Output_GetDesc))(output, &od);
            Bounds = od.Desktop;
            IntPtr d = IntPtr.Zero;
            int hr = -1;
            IntPtr o5 = QI(output, IID_IDXGIOutput5);
            if (o5 != IntPtr.Zero)
            {
                // Asks for 8-bit BGRA even when the desktop runs in HDR.
                uint fmt = FMT_B8G8R8A8;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint*, IntPtr*, int>)Fn(o5, Output5_DuplicateOutput1))(o5, device, 0, 1, &fmt, &d);
                Rel(ref o5);
            }
            if (hr < 0)
            {
                IntPtr o1 = QI(output, IID_IDXGIOutput1);
                if (o1 == IntPtr.Zero) throw new GpuException("Screen capture (needs Windows 8 or newer)", DXGI_ERROR_UNSUPPORTED);
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Fn(o1, Output1_DuplicateOutput))(o1, device, &d);
                Rel(ref o1);
            }
            Check(hr, "Starting screen capture");
            dup = d;
            OUTDUPL_DESC dd;
            ((delegate* unmanaged[Stdcall]<IntPtr, OUTDUPL_DESC*, void>)Fn(dup, Dup_GetDesc))(dup, &dd);
            if (dd.Rotation > 1) throw new NotSupportedException("rotated displays aren't supported");
            if (width != (int)dd.Width || height != (int)dd.Height)
            {
                // the display mode changed: new textures, and a swap chain of the new size
                CreateTargets((int)dd.Width, (int)dd.Height);
                if (swap != IntPtr.Zero)
                {
                    Rel(ref bbRtv);
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(context, Ctx_OMSetRenderTargets))(context, 0, null, IntPtr.Zero);
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)Fn(swap, Swap_ResizeBuffers))(swap, 0, (uint)width, (uint)height, 0, 0), "ResizeBuffers");
                    CreateBackBufferView();
                }
            }
            haveFrame = false;
        }

        void CreateTargets(int w, int h)
        {
            ReleaseTargets();
            width = w; height = h;
            pars.W = w; pars.H = h;
            UploadParams();
            var td = new TEXTURE2D_DESC { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = FMT_B8G8R8A8, SampleCount = 1, Usage = USAGE_DEFAULT, BindFlags = BIND_SHADER_RESOURCE };
            frameTex = Texture(td, null, 0);
            frameSrv = View(Dev_CreateSRV, frameTex, "frame view");
            td.Format = FMT_R8G8B8A8; td.BindFlags = BIND_SHADER_RESOURCE | BIND_RENDER_TARGET;
            td.MipLevels = CoverageMip + 1; td.MiscFlags = 0x1; // GENERATE_MIPS
            clsTex = Texture(td, null, 0);
            clsRtv = View(Dev_CreateRTV, clsTex, "mask target");
            clsSrv = View(Dev_CreateSRV, clsTex, "mask view");

            // one value per 16 px tile, like mip 4 of the mask
            tilesW = Math.Max(1, w >> CoverageMip); tilesH = Math.Max(1, h >> CoverageMip);
            var zd = new TEXTURE2D_DESC { Width = (uint)tilesW, Height = (uint)tilesH, MipLevels = 1, ArraySize = 1, Format = FMT_R8, SampleCount = 1, Usage = USAGE_DEFAULT, BindFlags = BIND_SHADER_RESOURCE };
            zoneTex = Texture(zd, null, 0);
            zoneSrv = View(Dev_CreateSRV, zoneTex, "zone view");
            var sd = new TEXTURE2D_DESC { Width = (uint)tilesW, Height = (uint)tilesH, MipLevels = 1, ArraySize = 1, Format = FMT_R8G8B8A8, SampleCount = 1, Usage = USAGE_STAGING, CPUAccessFlags = CPU_ACCESS_READ };
            for (int i = 0; i < staging.Length; i++) staging[i] = Texture(sd, null, 0);
            tileBgra = new byte[tilesW * tilesH * 4];
            tileCov = new byte[tilesW * tilesH]; tileLuma = new byte[tilesW * tilesH];
            Zones.Resize(tilesW, tilesH, w, h);
            frameNo = 0;
            UploadZone(Zones.Zone);
        }

        void UploadZone(byte[] zone)
        {
            if (zoneTex == IntPtr.Zero) return;
            fixed (byte* z = zone)
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Fn(context, Ctx_UpdateSubresource))(context, zoneTex, 0, null, z, (uint)tilesW, 0);
        }

        /// <summary>Reads the tile summary of a frame from two frames ago (if the GPU is done with it) and updates the zones.</summary>
        void PumpZones()
        {
            if (frameNo < 2) return;
            IntPtr st = staging[(frameNo - 2) % staging.Length];
            MAPPED_SUBRESOURCE m;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MAPPED_SUBRESOURCE*, int>)Fn(context, Ctx_Map))(context, st, 0, 1 /* READ */, 0x100000 /* DO_NOT_WAIT */, &m);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING) return;
            Check(hr, "Reading the tile summary");
            try
            {
                for (int y = 0; y < tilesH; y++) Marshal.Copy((IntPtr)(m.Data + y * m.RowPitch), tileBgra, y * tilesW * 4, tilesW * 4);
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Fn(context, Ctx_Unmap))(context, st, 0); }
            for (int i = 0, o = 0; i < tileCov.Length; i++, o += 4) { tileCov[i] = tileBgra[o + 1]; tileLuma[i] = tileBgra[o + 2]; }
            Zones.Update(tileCov, tileLuma, clock.Elapsed.TotalMilliseconds);
            UploadZone(Zones.Zone);
        }

        /// <summary>Copies this frame's tile summary (mip 4 of the mask) to a CPU-readable texture.</summary>
        void QueueTileReadback()
        {
            IntPtr st = staging[frameNo % staging.Length];
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)Fn(context, Ctx_CopySubresourceRegion))(context, st, 0, 0, 0, 0, clsTex, (uint)CoverageMip, null);
            frameNo++;
        }

        IntPtr Texture(TEXTURE2D_DESC td, void* init, uint pitch)
        {
            var data = new SUBRESOURCE_DATA { Mem = init, Pitch = pitch };
            IntPtr t;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, TEXTURE2D_DESC*, SUBRESOURCE_DATA*, IntPtr*, int>)Fn(device, Dev_CreateTexture2D))(device, &td, init == null ? null : &data, &t), "CreateTexture2D");
            return t;
        }

        IntPtr View(int slot, IntPtr resource, string what)
        {
            IntPtr v;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(device, slot))(device, resource, null, &v), "Creating the " + what);
            return v;
        }

        void ReleaseTargets()
        {
            Rel(ref frameSrv); Rel(ref frameTex); Rel(ref clsSrv); Rel(ref clsRtv); Rel(ref clsTex);
            Rel(ref zoneSrv); Rel(ref zoneTex);
            for (int i = 0; i < staging.Length; i++) Rel(ref staging[i]);
        }

        /// <summary>Creates the swap chain on the overlay window (sized to the captured display).</summary>
        public void AttachWindow(IntPtr hwnd)
        {
            IntPtr f2 = QI(factory, IID_IDXGIFactory2);
            if (f2 == IntPtr.Zero) throw new GpuException("DXGI 1.2 (needs Windows 8 or newer)", DXGI_ERROR_UNSUPPORTED);
            try
            {
                var sd = new SWAP_CHAIN_DESC1
                {
                    Width = (uint)width, Height = (uint)height, Format = FMT_B8G8R8A8, SampleCount = 1,
                    BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */, BufferCount = 2, Scaling = 0 /* STRETCH */,
                    SwapEffect = 4 /* FLIP_DISCARD */, AlphaMode = 0,
                };
                IntPtr sc;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, SWAP_CHAIN_DESC1*, void*, IntPtr, IntPtr*, int>)Fn(f2, Factory2_CreateSwapChainForHwnd))(f2, device, hwnd, &sd, null, IntPtr.Zero, &sc);
                if (hr < 0)
                {
                    // Older systems: the classic blit model.
                    sd.SwapEffect = 0; sd.BufferCount = 1;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, SWAP_CHAIN_DESC1*, void*, IntPtr, IntPtr*, int>)Fn(f2, Factory2_CreateSwapChainForHwnd))(f2, device, hwnd, &sd, null, IntPtr.Zero, &sc);
                }
                Check(hr, "CreateSwapChainForHwnd");
                swap = sc;
                // Alt+Enter on our window must never switch anything to fullscreen.
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Fn(factory, Factory_MakeWindowAssociation))(factory, hwnd, 3);
                CreateBackBufferView();
            }
            finally { Rel(ref f2); }
        }

        void CreateBackBufferView()
        {
            Guid iid = IID_ID3D11Texture2D;
            IntPtr bb;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Fn(swap, Swap_GetBuffer))(swap, 0, &iid, &bb), "GetBuffer");
            try { bbRtv = View(Dev_CreateRTV, bb, "screen target"); }
            finally { Rel(ref bb); }
        }

        /// <summary>Uploads the colour rule to the shaders when it changed.</summary>
        public void Configure(HoloSettings s)
        {
            string k = s.Target + "|" + s.Tolerance + "|" + s.Output;
            if (k == paramKey) return;
            paramKey = k;
            var p = MakeParams(s);
            p.W = width; p.H = height;
            pars = p;
            UploadParams();
        }

        void UploadParams()
        {
            if (cbuf == IntPtr.Zero) return;
            var p = pars;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Fn(context, Ctx_UpdateSubresource))(context, cbuf, 0, null, &p, 0, 0);
        }

        /// <summary>The same thresholds as <see cref="HoloRule"/>, so the preview matches the game.</summary>
        internal static Params MakeParams(HoloSettings s)
        {
            var r = HoloRule.From(s);
            var o = System.Drawing.Color.FromArgb(s.Output);
            return new Params
            {
                TgtH = (float)r.H, TgtS = (float)r.S, TgtV = (float)r.V, Grey = r.Grey ? 1 : 0,
                HueWin = (float)r.HueWin, SatMin = (float)r.SatMin, ValMin = (float)r.ValMin, Tol = (float)r.Tol,
                OutR = o.R / 255f, OutG = o.G / 255f, OutB = o.B / 255f,
            };
        }

        public enum FrameResult { Presented, NoChange, Lost }

        /// <summary>
        /// Waits up to timeoutMs for the next picture of the display; when it changed, recolours it and shows it.
        /// </summary>
        public FrameResult Frame(uint timeoutMs)
        {
            OUTDUPL_FRAME_INFO info;
            IntPtr res;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, OUTDUPL_FRAME_INFO*, IntPtr*, int>)Fn(dup, Dup_AcquireNextFrame))(dup, timeoutMs, &info, &res);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return FrameResult.NoChange;
            if (hr == DXGI_ERROR_ACCESS_LOST) return FrameResult.Lost;
            Check(hr, "AcquireNextFrame");
            work.Restart();
            bool fresh = info.LastPresentTime != 0 || !haveFrame; // mouse-only updates don't change the picture
            try
            {
                if (fresh)
                {
                    IntPtr tex = QI(res, IID_ID3D11Texture2D);
                    if (tex == IntPtr.Zero) throw new GpuException("Reading the captured frame", DXGI_ERROR_UNSUPPORTED);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Fn(context, Ctx_CopyResource))(context, frameTex, tex);
                    Rel(ref tex);
                    haveFrame = true;
                }
            }
            finally
            {
                Rel(ref res);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(dup, Dup_ReleaseFrame))(dup);
            }
            if (!fresh) return FrameResult.NoChange;
            ShowFrame();
            LastWorkMs = (float)work.Elapsed.TotalMilliseconds;
            return FrameResult.Presented;
        }

        /// <summary>Recolours the current frame into the window and presents it.</summary>
        public void ShowFrame()
        {
            PumpZones();
            Draw(bbRtv);
            QueueTileReadback();
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Fn(swap, Swap_Present))(swap, 0, 0);
            if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) Check(hr, "Present");
        }

        void Draw(IntPtr target)
        {
            var ctx = context;
            var vp = new VIEWPORT { Width = width, Height = height, MaxDepth = 1 };
            IntPtr none = IntPtr.Zero, cb = cbuf;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Fn(ctx, Ctx_IASetTopology))(ctx, 4); // triangle list
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Fn(ctx, Ctx_IASetInputLayout))(ctx, IntPtr.Zero);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Ctx_VSSetShader))(ctx, vs, null, 0);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, VIEWPORT*, void>)Fn(ctx, Ctx_RSSetViewports))(ctx, 1, &vp);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetConstantBuffers))(ctx, 0, 1, &cb);

            // pass 1: which pixels have the reticle's colour
            IntPtr rt = clsRtv, src = frameSrv;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Ctx_OMSetRenderTargets))(ctx, 1, &rt, IntPtr.Zero);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetShaderResources))(ctx, 0, 1, &src);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Ctx_PSSetShader))(ctx, psClass, null, 0);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Fn(ctx, Ctx_Draw))(ctx, 3, 0);

            // pass 2: the picture with those pixels (and the glow next to them) swapped
            rt = target;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Ctx_OMSetRenderTargets))(ctx, 1, &rt, IntPtr.Zero);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Fn(ctx, Ctx_GenerateMips))(ctx, clsSrv); // coverage per tile
            IntPtr* srvs = stackalloc IntPtr[3];
            srvs[0] = frameSrv; srvs[1] = clsSrv; srvs[2] = zoneSrv;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetShaderResources))(ctx, 0, 3, srvs);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Ctx_PSSetShader))(ctx, psMain, null, 0);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Fn(ctx, Ctx_Draw))(ctx, 3, 0);

            // unbind the mask so the next frame can draw into it again
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetShaderResources))(ctx, 1, 1, &none);
        }

        /// <summary>For tests: runs the shaders on a BGRA picture and returns the result. zone: 255 per tile where
        /// the holo is (null = everywhere).</summary>
        public byte[] Process(byte[] bgra, int w, int h, byte[] zone = null)
        {
            if (w != width || h != height) CreateTargets(w, h);
            if (zone == null) { zone = new byte[tilesW * tilesH]; for (int i = 0; i < zone.Length; i++) zone[i] = 255; }
            UploadZone(zone);
            var td = new TEXTURE2D_DESC { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = FMT_B8G8R8A8, SampleCount = 1, Usage = USAGE_DEFAULT, BindFlags = BIND_SHADER_RESOURCE };
            IntPtr input, outTex = IntPtr.Zero, outRtv = IntPtr.Zero, staging = IntPtr.Zero;
            fixed (byte* p = bgra) input = Texture(td, p, (uint)(w * 4));
            haveFrame = true;
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Fn(context, Ctx_CopyResource))(context, frameTex, input);
                td.BindFlags = BIND_RENDER_TARGET;
                outTex = Texture(td, null, 0);
                outRtv = View(Dev_CreateRTV, outTex, "test target");
                Draw(outRtv);
                td.BindFlags = 0; td.Usage = USAGE_STAGING; td.CPUAccessFlags = CPU_ACCESS_READ;
                staging = Texture(td, null, 0);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Fn(context, Ctx_CopyResource))(context, staging, outTex);
                MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MAPPED_SUBRESOURCE*, int>)Fn(context, Ctx_Map))(context, staging, 0, 1 /* READ */, 0, &m), "Map");
                var result = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy((IntPtr)(m.Data + y * m.RowPitch), result, y * w * 4, w * 4);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Fn(context, Ctx_Unmap))(context, staging, 0);
                return result;
            }
            finally { Rel(ref staging); Rel(ref outRtv); Rel(ref outTex); Rel(ref input); }
        }

        public int DeviceRemovedReason() =>
            device == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(device, Dev_GetDeviceRemovedReason))(device);

        public void Dispose()
        {
            if (context != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)Fn(context, Ctx_ClearState))(context);
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)Fn(context, Ctx_Flush))(context);
            }
            Rel(ref dup);
            Rel(ref bbRtv); Rel(ref swap);
            ReleaseTargets();
            Rel(ref cbuf); Rel(ref psMain); Rel(ref psClass); Rel(ref vs);
            Rel(ref context); Rel(ref device);
            Rel(ref output); Rel(ref adapter); Rel(ref factory);
        }
    }
}
