// Runs the black holo's shader passes exactly as src/HoloGpu.cs sets them up (same formats, mips and
// read-back), on the pictures tests/gpu/HoloDump.cs wrote, and saves what the GPU made for comparison.
// Build: x86_64-w64-mingw32-g++ -O1 -static holo_gpu.cpp -ld3d11 -ld3dcompiler_47 ; run on Windows or Wine.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static ID3D11Device* dev; static ID3D11DeviceContext* ctx;
#define CHECK(x) do { HRESULT _h = (x); if (FAILED(_h)) { printf("FAILED %s: %08lx\n", #x, (unsigned long)_h); exit(1); } } while (0)

static std::vector<char> Read(const std::string& p) {
    FILE* f = fopen(p.c_str(), "rb"); if (!f) { printf("missing %s\n", p.c_str()); exit(1); }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    std::vector<char> b(n); fread(b.data(), 1, n, f); fclose(f); return b;
}
static void Write(const std::string& p, const void* d, size_t n) { FILE* f = fopen(p.c_str(), "wb"); fwrite(d, 1, n, f); fclose(f); }

static ID3DBlob* Compile(const std::vector<char>& src, const char* entry, const char* target) {
    ID3DBlob *code = nullptr, *err = nullptr;
    HRESULT hr = D3DCompile(src.data(), src.size(), "holo.hlsl", nullptr, nullptr, entry, target, 1 << 15, 0, &code, &err);
    if (FAILED(hr)) { printf("compile %s failed %08lx: %s\n", entry, (unsigned long)hr, err ? (char*)err->GetBufferPointer() : ""); exit(1); }
    return code;
}

static ID3D11Texture2D* Tex(UINT w, UINT h, DXGI_FORMAT f, UINT mips, UINT bind, UINT misc, D3D11_USAGE use, UINT cpu, const void* init, UINT pitch) {
    D3D11_TEXTURE2D_DESC d = {}; d.Width = w; d.Height = h; d.MipLevels = mips; d.ArraySize = 1; d.Format = f;
    d.SampleDesc.Count = 1; d.Usage = use; d.BindFlags = bind; d.MiscFlags = misc; d.CPUAccessFlags = cpu;
    D3D11_SUBRESOURCE_DATA sd = { init, pitch, 0 };
    ID3D11Texture2D* t = nullptr; CHECK(dev->CreateTexture2D(&d, init ? &sd : nullptr, &t)); return t;
}

int main(int argc, char** argv) {
    std::string dir = argc > 1 ? argv[1] : ".";
    D3D_FEATURE_LEVEL lv[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 }, fl;
    CHECK(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, lv, 3, D3D11_SDK_VERSION, &dev, &fl, &ctx));
    auto hlsl = Read(dir + "/holo.hlsl");
    ID3DBlob* vsb = Compile(hlsl, "VS", "vs_4_0"); ID3DBlob* pcb = Compile(hlsl, "PSClass", "ps_4_0"); ID3DBlob* pmb = Compile(hlsl, "PSMain", "ps_4_0");
    ID3D11VertexShader* vs; ID3D11PixelShader *psClass, *psMain;
    CHECK(dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &vs));
    CHECK(dev->CreatePixelShader(pcb->GetBufferPointer(), pcb->GetBufferSize(), nullptr, &psClass));
    CHECK(dev->CreatePixelShader(pmb->GetBufferPointer(), pmb->GetBufferSize(), nullptr, &psMain));
    printf("device level %x, shaders compiled\n", fl);

    FILE* lf = fopen((dir + "/cases.txt").c_str(), "r");
    char name[256]; int w, h, tw, th;
    while (fscanf(lf, "%255s %d %d %d %d", name, &w, &h, &tw, &th) == 5) {
        std::string n = dir + "/" + name;
        auto src = Read(n + ".src"); auto par = Read(n + ".params"); auto zone = Read(n + ".zone");
        D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = (UINT)par.size(); bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ID3D11Buffer* cb; CHECK(dev->CreateBuffer(&bd, nullptr, &cb));
        ctx->UpdateSubresource(cb, 0, nullptr, par.data(), 0, 0);

        auto frame = Tex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 1, D3D11_BIND_SHADER_RESOURCE, 0, D3D11_USAGE_DEFAULT, 0, src.data(), w * 4);
        auto cls = Tex(w, h, DXGI_FORMAT_R8G8B8A8_UNORM, 5, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, D3D11_RESOURCE_MISC_GENERATE_MIPS, D3D11_USAGE_DEFAULT, 0, nullptr, 0);
        auto zt = Tex(tw, th, DXGI_FORMAT_R8_UNORM, 1, D3D11_BIND_SHADER_RESOURCE, 0, D3D11_USAGE_DEFAULT, 0, nullptr, 0);
        ctx->UpdateSubresource(zt, 0, nullptr, zone.data(), tw, 0); // as HoloGpu.UploadZone
        auto out = Tex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 1, D3D11_BIND_RENDER_TARGET, 0, D3D11_USAGE_DEFAULT, 0, nullptr, 0);
        ID3D11ShaderResourceView *frameSrv, *clsSrv, *zoneSrv; ID3D11RenderTargetView *clsRtv, *outRtv;
        CHECK(dev->CreateShaderResourceView(frame, nullptr, &frameSrv)); CHECK(dev->CreateShaderResourceView(cls, nullptr, &clsSrv));
        CHECK(dev->CreateShaderResourceView(zt, nullptr, &zoneSrv)); CHECK(dev->CreateRenderTargetView(cls, nullptr, &clsRtv));
        CHECK(dev->CreateRenderTargetView(out, nullptr, &outRtv));

        D3D11_VIEWPORT vp = { 0, 0, (float)w, (float)h, 0, 1 };
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST); ctx->IASetInputLayout(nullptr);
        ctx->VSSetShader(vs, nullptr, 0); ctx->RSSetViewports(1, &vp); ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->OMSetRenderTargets(1, &clsRtv, nullptr); ctx->PSSetShaderResources(0, 1, &frameSrv); ctx->PSSetShader(psClass, nullptr, 0); ctx->Draw(3, 0);
        ctx->OMSetRenderTargets(1, &outRtv, nullptr); ctx->GenerateMips(clsSrv);
        ID3D11ShaderResourceView* srvs[3] = { frameSrv, clsSrv, zoneSrv };
        ctx->PSSetShaderResources(0, 3, srvs); ctx->PSSetShader(psMain, nullptr, 0); ctx->Draw(3, 0);
        ID3D11ShaderResourceView* none = nullptr; ctx->PSSetShaderResources(1, 1, &none);

        // the picture
        auto st = Tex(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 1, 0, 0, D3D11_USAGE_STAGING, D3D11_CPU_ACCESS_READ, nullptr, 0);
        ctx->CopyResource(st, out);
        D3D11_MAPPED_SUBRESOURCE m; CHECK(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m));
        std::vector<unsigned char> res(w * h * 4);
        for (int y = 0; y < h; y++) memcpy(&res[y * w * 4], (char*)m.pData + y * m.RowPitch, w * 4);
        ctx->Unmap(st, 0); Write(n + ".gpu", res.data(), res.size());

        // the tile summary, read back the way HoloGpu.QueueTileReadback/PumpZones do
        auto ts = Tex(tw, th, DXGI_FORMAT_R8G8B8A8_UNORM, 1, 0, 0, D3D11_USAGE_STAGING, D3D11_CPU_ACCESS_READ, nullptr, 0);
        ctx->CopySubresourceRegion(ts, 0, 0, 0, 0, cls, 4, nullptr);
        HRESULT hr; int spins = 0;
        while ((hr = ctx->Map(ts, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m)) == DXGI_ERROR_WAS_STILL_DRAWING) { Sleep(1); spins++; }
        CHECK(hr);
        std::vector<unsigned char> tiles(tw * th * 2);
        for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) {
            unsigned char* p = (unsigned char*)m.pData + y * m.RowPitch + x * 4;
            tiles[(y * tw + x) * 2] = p[1]; tiles[(y * tw + x) * 2 + 1] = p[2];
        }
        ctx->Unmap(ts, 0); Write(n + ".gputiles", tiles.data(), tiles.size());
        printf("  %s: %dx%d done (tile read-back waited %d ms)\n", name, w, h, spins);
        st->Release(); ts->Release(); out->Release(); outRtv->Release(); clsRtv->Release(); zoneSrv->Release(); clsSrv->Release(); frameSrv->Release();
        zt->Release(); cls->Release(); frame->Release(); cb->Release();
    }
    return 0;
}
