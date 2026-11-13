using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Vivid
{
    /// <summary>
    /// The black holo's GPU side. Desktop Duplication hands over each finished picture of the monitor Rust is on;
    /// two small pixel shaders swap the holosight's colour (plus its glow and anti-aliased edge) and the result is
    /// shown in a click-through window over the game. The window is left out of screen captures, so it never
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
                  Ctx_RSSetViewports = 44, Ctx_CopyResource = 47, Ctx_UpdateSubresource = 48, Ctx_ClearState = 110, Ctx_Flush = 111;
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

        const uint FMT_B8G8R8A8 = 87, FMT_R8G8 = 49;
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
        struct Params { public float TgtH, TgtS, TgtV, Grey, HueWin, SatMin, ValMin, Tol, OutR, OutG, OutB, Pad, W, H, Pad2, Pad3; }

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
Texture2D<float2> Cls : register(t1);   // r: class, g: 1 on reticle-coloured pixels (mip 4: share of each 16x16 tile)

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
// 1 = the reticle's colour, 0.5 = its glow (only used next to the reticle), 0 = anything else
float Classify(float3 c) {
    float3 h = Hsv(c);
    if (Tgt.w > 0.5) return (h.y < 0.12 + 0.1 * Thr.w && h.z >= Tgt.z - 0.08 - 0.2 * Thr.w) ? 1 : 0;
    float dh = abs(h.x - Tgt.x);
    if (dh > 180) dh = 360 - dh;
    if (dh <= Thr.x && h.y >= Thr.y && h.z >= Thr.z) return 1;
    if (dh <= Thr.x * 1.5 && h.y >= 0.12 && h.z >= 0.5) return 0.5;
    return 0;
}
float2 PSClass(float4 p : SV_Position) : SV_Target { float k = Classify(Src.Load(int3(p.xy, 0)).rgb); return float2(k, k > 0.75 ? 1 : 0); }
float4 PSMain(float4 p : SV_Position) : SV_Target {
    int2 q = int2(p.xy);
    float3 c = Src.Load(int3(q, 0)).rgb;
    float self = Cls.Load(int3(q, 0)).r;
    // A reticle is thin lines and a dot: it never fills much of a 16 px tile. Big patches of the colour
    // (damage effects, signs, a red sky) are left alone, edges included (this tile and the 8 around it).
    int2 t = q >> COVERAGE_MIP;
    float cov = 0;
TILES
    if (cov > 0.45) return float4(c, 1);
    if (self > 0.75) return float4(Outc.rgb, 1);
    // strongest class within 1 px and within 2 px (only the reticle itself is above 0.75)
    float near1 = 0, near2 = 0;
NEIGHBOURS
    if (near2 > 0.75 && self > 0.25) return float4(Outc.rgb, 1);              // glow next to the reticle
    if (near1 > 0.75) return float4(lerp(c, Outc.rgb, 150.0 / 255.0), 1);     // soft 1 px edge (anti-aliasing)
    return float4(c, 1);
}";

        // ---------- state ----------
        IntPtr factory, adapter, output, device, context, dup, swap, bbRtv;
        IntPtr vs, psClass, psMain, cbuf;
        Params pars;
        IntPtr frameTex, frameSrv, clsTex, clsRtv, clsSrv;
        int width, height;
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
        public static HoloGpu ForMonitor(IntPtr monitor)
        {
            var g = new HoloGpu();
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

        static string ShaderSource()
        {
            var n = new StringBuilder();
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    n.Append("    { float v = Cls.Load(int3(q + int2(").Append(dx).Append(", ").Append(dy).Append("), 0)).r; near2 = max(near2, v);");
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
            td.Format = FMT_R8G8; td.BindFlags = BIND_SHADER_RESOURCE | BIND_RENDER_TARGET;
            td.MipLevels = CoverageMip + 1; td.MiscFlags = 0x1; // GENERATE_MIPS
            clsTex = Texture(td, null, 0);
            clsRtv = View(Dev_CreateRTV, clsTex, "mask target");
            clsSrv = View(Dev_CreateSRV, clsTex, "mask view");
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

        /// <summary>The same thresholds as <see cref="HoloMatcher.Configure"/>, so the preview matches the game.</summary>
        static Params MakeParams(HoloSettings s)
        {
            var t = System.Drawing.Color.FromArgb(s.Target);
            var o = System.Drawing.Color.FromArgb(s.Output);
            HoloMatcher.Hsv(t.R, t.G, t.B, out double th, out double ts, out double tv);
            double tol = s.Tolerance / 100.0;
            return new Params
            {
                TgtH = (float)th, TgtS = (float)ts, TgtV = (float)tv, Grey = ts < 0.15 ? 1 : 0,
                HueWin = (float)(8 + 26 * tol),
                SatMin = (float)Math.Max(0.22, ts * (0.78 - 0.4 * tol)),
                ValMin = (float)Math.Max(0.18, tv * (0.62 - 0.32 * tol)),
                Tol = (float)tol,
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
            Draw(bbRtv);
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
            IntPtr* srvs = stackalloc IntPtr[2];
            srvs[0] = frameSrv; srvs[1] = clsSrv;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetShaderResources))(ctx, 0, 2, srvs);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Ctx_PSSetShader))(ctx, psMain, null, 0);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Fn(ctx, Ctx_Draw))(ctx, 3, 0);

            // unbind the mask so the next frame can draw into it again
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Ctx_PSSetShaderResources))(ctx, 1, 1, &none);
        }

        /// <summary>For tests: runs the shaders on a BGRA picture and returns the result.</summary>
        public byte[] Process(byte[] bgra, int w, int h)
        {
            if (w != width || h != height) CreateTargets(w, h);
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
