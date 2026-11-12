#include "WgcCapture.h"

#include <dxgi1_2.h>
#include <dxgiformat.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <Unknwn.h>
#include <inspectable.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <mutex>
#include <string>
#include <algorithm>
#include <vector>
#include <cmath>
#include <cstdio>

namespace wg = winrt::Windows::Graphics;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgdx = winrt::Windows::Graphics::DirectX;
namespace wgd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {
    inline void SafeRelease(IUnknown* p) { if (p) p->Release(); }
}

struct WgcCapture::Impl {
    ID3D11Device* unityDevice = nullptr; // not owned
    ID3D11DeviceContext* unityContext = nullptr; // not owned

    wgd3d::IDirect3DDevice winrtDevice{ nullptr };
    wgc::GraphicsCaptureItem item{ nullptr };
    wgc::Direct3D11CaptureFramePool framePool{ nullptr };
    wgc::GraphicsCaptureSession session{ nullptr };

    std::mutex frameMutex;
    ID3D11Texture2D* latestTexture = nullptr; // AddRef'd copy ref obtained from frame surface; must be released
    UINT latestContentW = 0, latestContentH = 0; // frame.ContentSize
    UINT latestTexW = 0, latestTexH = 0;         // actual ID3D11Texture2D desc
    DXGI_FORMAT latestFormat = DXGI_FORMAT_UNKNOWN;
    uint64_t frameSeq = 0;
    uint64_t copiedSeq = 0;
    bool frameArrivedHooked = false;
    winrt::event_token frameArrivedToken{};
    winrt::Windows::Graphics::SizeInt32 lastItemSize{ 0,0 };

    // Blit pipeline/resources (created on Unity D3D11 device, used on render thread)
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11BlendState* blend = nullptr;
    ID3D11RasterizerState* rs = nullptr;
    bool blitReady = false;

    // Cached blit targets/intermediates (stable across frames in common case)
    ID3D11Texture2D* cachedTarget = nullptr;
    DXGI_FORMAT cachedTargetFmt = DXGI_FORMAT_UNKNOWN;
    UINT cachedTargetW = 0, cachedTargetH = 0, cachedTargetBind = 0;
    ID3D11RenderTargetView* cachedRtv = nullptr;
    ID3D11Texture2D* cachedRtIntermediate = nullptr;
    bool cachedNeedCopyBack = false;

    DXGI_FORMAT cachedSrcFmt = DXGI_FORMAT_UNKNOWN;
    UINT cachedSrcW = 0, cachedSrcH = 0, cachedSrcBind = 0;
    ID3D11Texture2D* cachedSrvIntermediate = nullptr;

    void ClearLatestLocked() {
        if (latestTexture) { latestTexture->Release(); latestTexture = nullptr; }
        latestContentW = latestContentH = 0;
        latestTexW = latestTexH = 0;
        latestFormat = DXGI_FORMAT_UNKNOWN;
    }
    void ReleaseBlit() {
        if (cachedRtv) { cachedRtv->Release(); cachedRtv = nullptr; }
        if (cachedRtIntermediate) { cachedRtIntermediate->Release(); cachedRtIntermediate = nullptr; }
        if (cachedSrvIntermediate) { cachedSrvIntermediate->Release(); cachedSrvIntermediate = nullptr; }
        cachedTarget = nullptr; cachedTargetFmt = DXGI_FORMAT_UNKNOWN; cachedTargetW = cachedTargetH = cachedTargetBind = 0; cachedNeedCopyBack = false;
        cachedSrcFmt = DXGI_FORMAT_UNKNOWN; cachedSrcW = cachedSrcH = cachedSrcBind = 0;
        if (blend) { blend->Release(); blend = nullptr; }
        if (rs) { rs->Release(); rs = nullptr; }
        if (cb) { cb->Release(); cb = nullptr; }
        if (sampler) { sampler->Release(); sampler = nullptr; }
        if (ps) { ps->Release(); ps = nullptr; }
        if (vs) { vs->Release(); vs = nullptr; }
        blitReady = false;
    }
};

static bool CreateWinRTDeviceFromD3D11(ID3D11Device* d3dDevice, wgd3d::IDirect3DDevice& outDevice, std::string& outError) {
    outDevice = nullptr;
    if (!d3dDevice) { outError = "null D3D11 device"; return false; }
    winrt::com_ptr<IDXGIDevice> dxgiDevice;
    HRESULT hr = d3dDevice->QueryInterface(IID_PPV_ARGS(dxgiDevice.put()));
    if (FAILED(hr) || !dxgiDevice) { outError = "QueryInterface IDXGIDevice failed hr=0x" + std::to_string((unsigned)hr); return false; }
    winrt::com_ptr<::IInspectable> inspectable;
    hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put());
    if (FAILED(hr) || !inspectable) { outError = "CreateDirect3D11DeviceFromDXGIDevice failed hr=0x" + std::to_string((unsigned)hr); return false; }
    try {
        outDevice = inspectable.as<wgd3d::IDirect3DDevice>();
    } catch (...) {
        outError = "Failed to cast WinRT Direct3D device";
        return false;
    }
    if (!outDevice) { outError = "WinRT Direct3D device null"; return false; }
    return true;
}

static bool GetTextureFromFrame(const wgc::Direct3D11CaptureFrame& frame, ID3D11Texture2D** outTex,
    UINT& outContentW, UINT& outContentH, UINT& outTexW, UINT& outTexH, DXGI_FORMAT& outFmt, std::string& outError) {
    if (outTex) *outTex = nullptr;
    outContentW = outContentH = outTexW = outTexH = 0; outFmt = DXGI_FORMAT_UNKNOWN;
    if (!frame) { outError = "null frame"; return false; }
    try {
        auto surface = frame.Surface();
        auto contentSize = frame.ContentSize();
        outContentW = (UINT)contentSize.Width;
        outContentH = (UINT)contentSize.Height;
        auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<ID3D11Texture2D> tex;
        HRESULT hr = access->GetInterface(IID_PPV_ARGS(tex.put()));
        if (FAILED(hr) || !tex) { outError = "GetInterface ID3D11Texture2D failed hr=0x" + std::to_string((unsigned)hr); return false; }
        D3D11_TEXTURE2D_DESC desc{};
        tex->GetDesc(&desc);
        outFmt = desc.Format;
        outTexW = desc.Width;
        outTexH = desc.Height;
        if (outContentW == 0) outContentW = outTexW;
        if (outContentH == 0) outContentH = outTexH;
        *outTex = tex.detach(); // caller owns ref
        return true;
    } catch (const winrt::hresult_error&) {
        outError = "GetTextureFromFrame hresult_error";
        return false;
    } catch (...) {
        outError = "GetTextureFromFrame exception";
        return false;
    }
}

void WgcCapture::SetDiag(const std::string& s) { std::lock_guard<std::mutex> lk(m_diagMutex); m_diag = s; }
void WgcCapture::SetError(const std::string& s) { std::lock_guard<std::mutex> lk(m_diagMutex); m_diag = s; m_status.store(-1); }

bool WgcCapture::Start(HWND hwnd, ID3D11Device* unityDevice, ID3D11DeviceContext* unityContext, std::string& outError) {
    Stop();
    outError.clear();
    if (!hwnd || !IsWindow(hwnd)) { outError = "Invalid HWND"; SetError(outError); return false; }
    if (!unityDevice || !unityContext) { outError = "Unity D3D11 device/context unavailable"; SetError(outError); return false; }

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (...) {
        // apartment may already be initialized; continue
    }

    auto* impl = new Impl();
    impl->unityDevice = unityDevice;
    impl->unityContext = unityContext;

    try {
        std::string err;
        if (!CreateWinRTDeviceFromD3D11(unityDevice, impl->winrtDevice, err)) {
            outError = err; SetError(outError); delete impl; return false;
        }

        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        wgc::GraphicsCaptureItem item{ nullptr };
        HRESULT hr = interop->CreateForWindow(hwnd, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(item));
        if (FAILED(hr) || !item) {
            outError = "CreateForWindow failed hr=0x" + std::to_string((unsigned)hr);
            SetError(outError); delete impl; return false;
        }
        impl->item = item;

        auto size = item.Size();
        int w = (std::max)(1, size.Width);
        int h = (std::max)(1, size.Height);

        impl->framePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            impl->winrtDevice,
            wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            { w, h });
        if (!impl->framePool) { outError = "Failed to create Direct3D11CaptureFramePool"; SetError(outError); delete impl; return false; }

        impl->lastItemSize = item.Size();
        impl->frameArrivedToken = impl->framePool.FrameArrived([impl](auto const& sender, auto const&) {
            try {
                auto frame = sender.TryGetNextFrame();
                if (!frame) return;
                auto cs = frame.ContentSize();
                // Recreate frame pool if content size changed (WGC requirement/common practice)
                if ((cs.Width != impl->lastItemSize.Width || cs.Height != impl->lastItemSize.Height) && cs.Width > 0 && cs.Height > 0) {
                    impl->lastItemSize = cs;
                    try { sender.Recreate(impl->winrtDevice, wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, cs); } catch (...) {}
                }
                ID3D11Texture2D* tex = nullptr;
                UINT cw=0,ch=0,tw=0,th=0; DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN; std::string ferr;
                if (!GetTextureFromFrame(frame, &tex, cw, ch, tw, th, fmt, ferr)) return;
                std::lock_guard<std::mutex> lk(impl->frameMutex);
                impl->ClearLatestLocked();
                impl->latestTexture = tex; // take ownership of AddRef from GetTextureFromFrame
                impl->latestContentW = cw; impl->latestContentH = ch;
                impl->latestTexW = tw; impl->latestTexH = th;
                impl->latestFormat = fmt;
                impl->frameSeq++;
            } catch (...) {
                // swallow per-frame exceptions
            }
        });
        impl->frameArrivedHooked = true;

        impl->session = impl->framePool.CreateCaptureSession(item);
        if (!impl->session) { outError = "Failed to create capture session"; SetError(outError); Stop(); delete impl; return false; }
        try { impl->session.IsCursorCaptureEnabled(false); } catch (...) {}
        impl->session.StartCapture();

        m_impl = impl;
        m_active.store(true);
        m_status.store(1);
        SetDiag("WGC capture started (hwnd ok, frame pool/session created)");
        return true;
    } catch (const winrt::hresult_error&) {
        outError = "WGC start failed (hresult_error)";
    } catch (...) {
        outError = "WGC start failed (exception)";
    }
    SetError(outError);
    delete impl;
    return false;
}

void WgcCapture::Stop() {
    Impl* impl = m_impl;
    m_impl = nullptr;
    m_active.store(false);
    m_status.store(0);
    if (!impl) return;
    try {
        if (impl->framePool && impl->frameArrivedHooked) {
            impl->framePool.FrameArrived(impl->frameArrivedToken);
            impl->frameArrivedHooked = false;
        }
    } catch (...) {}
    try { if (impl->session) impl->session.Close(); } catch (...) {}
    try { if (impl->framePool) impl->framePool.Close(); } catch (...) {}
    {
        std::lock_guard<std::mutex> lk(impl->frameMutex);
        impl->ClearLatestLocked();
    }
    impl->ReleaseBlit();
    impl->session = nullptr;
    impl->framePool = nullptr;
    impl->item = nullptr;
    impl->winrtDevice = nullptr;
    impl->unityDevice = nullptr;
    impl->unityContext = nullptr;
    delete impl;
    SetDiag("WGC capture stopped");
}

void WgcCapture::GetLatestFrameSize(UINT& w, UINT& h) const {
    w = 0; h = 0;
    Impl* impl = m_impl;
    if (!impl) return;
    std::lock_guard<std::mutex> lk(impl->frameMutex);
    // Prefer content size for presentation; texture may include padding/borders.
    w = impl->latestContentW ? impl->latestContentW : impl->latestTexW;
    h = impl->latestContentH ? impl->latestContentH : impl->latestTexH;
}

namespace {
struct BlitCB {
    float srcRect[4]; // x,y,w,h in source texture pixels (content region)
    float dstSize[2];
    float srcTexSize[2];
    float flags[4];   // x: srcIsBGRA(1/0), y: dstIsSRGB(1/0)
};

static const char* kBlitVS = R"(
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut main(uint vid : SV_VertexID) {
    float2 p;
    if (vid == 0) p = float2(-1.0, -1.0);
    else if (vid == 1) p = float2(-1.0,  3.0);
    else p = float2( 3.0, -1.0);
    VSOut o;
    o.pos = float4(p, 0.0, 1.0);
    // fullscreen tri -> uv 0..2 mapped later in PS via dest position
    o.uv = float2((p.x + 1.0) * 0.5, 1.0 - (p.y + 1.0) * 0.5);
    return o;
}
)";

static const char* kBlitPS = R"(
Texture2D srcTex : register(t0);
SamplerState samp0 : register(s0);
cbuffer CB : register(b0) {
    float4 srcRect;    // x,y,w,h in source tex pixels
    float2 dstSize;
    float2 srcTexSize;
    float4 flags;      // x srcIsBGRA, y dstIsSRGB
};
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
float3 toLinear(float3 c) { return pow(saturate(c), 2.2); }
float3 toSRGB(float3 c) { return pow(saturate(c), 1.0/2.2); }
float4 main(VSOut i) : SV_Target {
    float2 dstPos = i.pos.xy; // pixel coords in render target
    // Fit (contain) source content rect into destination while preserving aspect ratio.
    float srcW = max(1.0, srcRect.z);
    float srcH = max(1.0, srcRect.w);
    float scale = min(dstSize.x / srcW, dstSize.y / srcH);
    float drawW = srcW * scale;
    float drawH = srcH * scale;
    float offX = (dstSize.x - drawW) * 0.5;
    float offY = (dstSize.y - drawH) * 0.5;
    if (dstPos.x < offX || dstPos.x >= offX + drawW || dstPos.y < offY || dstPos.y >= offY + drawH) {
        return float4(0,0,0,1); // letterbox
    }
    float2 local = (dstPos - float2(offX, offY)) / float2(drawW, drawH); // 0..1 within content
    local.y = 1.0 - local.y; // correct vertical orientation for Unity/D3D texture presentation
    float2 srcPx = srcRect.xy + local * float2(srcW, srcH);
    float2 uv = (srcPx + 0.5) / max(srcTexSize, float2(1,1));
    float4 c = srcTex.Sample(samp0, uv);
    // Do not manually swizzle BGRA formats: DXGI BGRA formats are sampled with correct RGBA channel semantics in shaders.
    // (Manual .bgra swizzle would invert red/blue.)
    if (flags.x > 1.5) c = c.bgra;
    // Source is UNORM (linear in our pipeline assumption for WGC). If destination RTV is SRGB, hardware will encode;
    // but we also allow manual handling if needed. Keep shader output in linear-ish space and let SRGB RTV encode when used.
    if (flags.y > 1.5) { c.rgb = toSRGB(c.rgb); } // explicit encode path (unused by default)
    else if (flags.y < -0.5) { c.rgb = toLinear(c.rgb); }
    c.a = 1.0;
    return c;
}
)";
}

// (blit pipeline initialization inlined in CopyLatestTo to avoid private nested type access issues)

static DXGI_FORMAT MakeSRVFormat(DXGI_FORMAT fmt) {
    switch (fmt) {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    default: return fmt;
    }
}
static DXGI_FORMAT MakeRTVFormat(DXGI_FORMAT fmt) {
    switch (fmt) {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    default: return fmt; // keep SRGB if destination is SRGB
    }
}

bool WgcCapture::CopyLatestTo(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, std::string& outDiag) {
    outDiag.clear();
    if (!ctx || !target) { outDiag = "null ctx/target"; return false; }
    Impl* impl = m_impl;
    if (!impl || !m_active.load()) { outDiag = "inactive"; return false; }

    ID3D11Texture2D* src = nullptr;
    UINT contentW=0,contentH=0,texW=0,texH=0; DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN; uint64_t seq=0;
    {
        std::lock_guard<std::mutex> lk(impl->frameMutex);
        if (!impl->latestTexture) { outDiag = "no frame yet"; return false; }
        if (impl->copiedSeq == impl->frameSeq) { outDiag = "no new frame"; return false; }
        src = impl->latestTexture;
        src->AddRef();
        contentW = impl->latestContentW; contentH = impl->latestContentH;
        texW = impl->latestTexW; texH = impl->latestTexH;
        fmt = impl->latestFormat; seq = impl->frameSeq;
    }

    D3D11_TEXTURE2D_DESC dstDesc{}; target->GetDesc(&dstDesc);
    D3D11_TEXTURE2D_DESC srcDesc{}; src->GetDesc(&srcDesc);
    ID3D11Device* dev = nullptr; target->GetDevice(&dev); // AddRef'd

    // Log content vs texture dims (border/non-client investigation)
    char dimInfo[160];
    sprintf_s(dimInfo, "content=%ux%u tex=%ux%u srcFmt=%d dstFmt=%d dst=%ux%u",
        contentW, contentH, srcDesc.Width, srcDesc.Height, (int)srcDesc.Format, (int)dstDesc.Format, dstDesc.Width, dstDesc.Height);

    std::string err;
    if (!(impl->blitReady && impl->vs && impl->ps && impl->sampler && impl->cb && impl->rs && impl->blend)) {
        impl->ReleaseBlit();
        HRESULT hrInit;
        winrt::com_ptr<ID3DBlob> vsBlob, psBlob, errBlob;
        hrInit = D3DCompile(kBlitVS, strlen(kBlitVS), "wgc_blit_vs", nullptr, nullptr, "main", "vs_4_0", 0, 0, vsBlob.put(), errBlob.put());
        if (FAILED(hrInit) || !vsBlob) { err = "VS compile failed"; }
        else {
            errBlob = nullptr;
            hrInit = D3DCompile(kBlitPS, strlen(kBlitPS), "wgc_blit_ps", nullptr, nullptr, "main", "ps_4_0", 0, 0, psBlob.put(), errBlob.put());
            if (FAILED(hrInit) || !psBlob) {
                err = "PS compile failed";
                if (errBlob) err += std::string(": ") + std::string((const char*)errBlob->GetBufferPointer(), errBlob->GetBufferSize());
            } else {
                hrInit = dev->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &impl->vs);
                if (SUCCEEDED(hrInit)) hrInit = dev->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &impl->ps);
                D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
                if (SUCCEEDED(hrInit)) hrInit = dev->CreateSamplerState(&sd, &impl->sampler);
                D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(BlitCB); bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
                if (SUCCEEDED(hrInit)) hrInit = dev->CreateBuffer(&bd, nullptr, &impl->cb);
                D3D11_RASTERIZER_DESC rd{}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
                if (SUCCEEDED(hrInit)) hrInit = dev->CreateRasterizerState(&rd, &impl->rs);
                D3D11_BLEND_DESC bld{}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
                if (SUCCEEDED(hrInit)) hrInit = dev->CreateBlendState(&bld, &impl->blend);
                if (FAILED(hrInit) || !impl->vs || !impl->ps || !impl->sampler || !impl->cb || !impl->rs || !impl->blend) {
                    char hb[64]; sprintf_s(hb, " (hr=0x%08X)", (unsigned)hrInit);
                    if (err.empty()) err = std::string("blit pipeline create failed") + hb;
                    impl->ReleaseBlit();
                } else {
                    impl->blitReady = true;
                }
            }
        }
    }
    if (!impl->blitReady) {
        outDiag = std::string("blit pipeline init failed: ") + err + " | " + dimInfo;
        src->Release(); if (dev) dev->Release();
        return false;
    }

    // Prepare source for shader read: ensure SRV-capable. If not, copy to cached intermediate when desc stable.
    ID3D11Texture2D* srvTex = src;
    if ((srcDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0) {
        bool needNewInter = (!impl->cachedSrvIntermediate ||
            impl->cachedSrcFmt != srcDesc.Format || impl->cachedSrcW != srcDesc.Width || impl->cachedSrcH != srcDesc.Height || impl->cachedSrcBind != srcDesc.BindFlags);
        if (needNewInter) {
            if (impl->cachedSrvIntermediate) { impl->cachedSrvIntermediate->Release(); impl->cachedSrvIntermediate = nullptr; }
            D3D11_TEXTURE2D_DESC tmp = srcDesc;
            tmp.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            tmp.Usage = D3D11_USAGE_DEFAULT;
            tmp.CPUAccessFlags = 0;
            tmp.MiscFlags = 0;
            tmp.MipLevels = 1; tmp.ArraySize = 1;
            if (FAILED(dev->CreateTexture2D(&tmp, nullptr, &impl->cachedSrvIntermediate)) || !impl->cachedSrvIntermediate) {
                outDiag = std::string("failed to create intermediate SRV texture | ") + dimInfo;
                src->Release(); if (dev) dev->Release();
                return false;
            }
            impl->cachedSrcFmt = srcDesc.Format; impl->cachedSrcW = srcDesc.Width; impl->cachedSrcH = srcDesc.Height; impl->cachedSrcBind = srcDesc.BindFlags;
        }
        ctx->CopyResource(impl->cachedSrvIntermediate, src);
        srvTex = impl->cachedSrvIntermediate;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC srvd{};
    srvd.Format = MakeSRVFormat(srcDesc.Format);
    srvd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvd.Texture2D.MipLevels = 1;
    ID3D11ShaderResourceView* srv = nullptr;
    HRESULT hr = dev->CreateShaderResourceView(srvTex, &srvd, &srv);
    if (FAILED(hr) || !srv) {
        srvd.Format = srcDesc.Format;
        hr = dev->CreateShaderResourceView(srvTex, &srvd, &srv);
    }
    if (FAILED(hr) || !srv) {
        outDiag = std::string("CreateSRV failed | ") + dimInfo;
        src->Release(); if (dev) dev->Release();
        return false;
    }

    // Cache RTV/intermediate RT for stable destination target
    bool needNewRtv = (!impl->cachedRtv || impl->cachedTarget != target || impl->cachedTargetFmt != dstDesc.Format ||
        impl->cachedTargetW != dstDesc.Width || impl->cachedTargetH != dstDesc.Height || impl->cachedTargetBind != dstDesc.BindFlags);
    if (needNewRtv) {
        if (impl->cachedRtv) { impl->cachedRtv->Release(); impl->cachedRtv = nullptr; }
        if (impl->cachedRtIntermediate) { impl->cachedRtIntermediate->Release(); impl->cachedRtIntermediate = nullptr; }
        impl->cachedNeedCopyBack = false;
        impl->cachedTarget = target;
        impl->cachedTargetFmt = dstDesc.Format; impl->cachedTargetW = dstDesc.Width; impl->cachedTargetH = dstDesc.Height; impl->cachedTargetBind = dstDesc.BindFlags;

        ID3D11Texture2D* rtTex = target;
        if ((dstDesc.BindFlags & D3D11_BIND_RENDER_TARGET) == 0) {
            D3D11_TEXTURE2D_DESC rtDesc = dstDesc;
            rtDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            rtDesc.Usage = D3D11_USAGE_DEFAULT;
            rtDesc.CPUAccessFlags = 0;
            rtDesc.MiscFlags = 0;
            rtDesc.MipLevels = 1; rtDesc.ArraySize = 1;
            hr = dev->CreateTexture2D(&rtDesc, nullptr, &impl->cachedRtIntermediate);
            if (FAILED(hr) || !impl->cachedRtIntermediate) {
                outDiag = std::string("Create intermediate RT failed | ") + dimInfo;
                srv->Release(); src->Release(); if (dev) dev->Release();
                return false;
            }
            rtTex = impl->cachedRtIntermediate;
            impl->cachedNeedCopyBack = true;
        }
        D3D11_RENDER_TARGET_VIEW_DESC rtvd{};
        rtvd.Format = MakeRTVFormat(dstDesc.Format);
        rtvd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        rtvd.Texture2D.MipSlice = 0;
        hr = dev->CreateRenderTargetView(rtTex, &rtvd, &impl->cachedRtv);
        if (FAILED(hr) || !impl->cachedRtv) {
            rtvd.Format = dstDesc.Format;
            hr = dev->CreateRenderTargetView(rtTex, &rtvd, &impl->cachedRtv);
        }
        if (FAILED(hr) || !impl->cachedRtv) {
            outDiag = std::string("CreateRTV failed | ") + dimInfo;
            if (impl->cachedRtIntermediate) { impl->cachedRtIntermediate->Release(); impl->cachedRtIntermediate = nullptr; }
            srv->Release(); src->Release(); if (dev) dev->Release();
            return false;
        }
    }
    ID3D11RenderTargetView* rtv = impl->cachedRtv;
    bool needCopyBack = impl->cachedNeedCopyBack;
    ID3D11Texture2D* rtIntermediate = impl->cachedRtIntermediate;

    // Update constants (use content region to avoid border/non-client padding when present)
    float srcX = 0.0f, srcY = 0.0f, srcW = (float)srcDesc.Width, srcH = (float)srcDesc.Height;
    if (contentW > 0 && contentH > 0 && contentW <= srcDesc.Width && contentH <= srcDesc.Height) {
        // Assume content aligned at top-left of captured texture; log delta via dimInfo.
        srcW = (float)contentW; srcH = (float)contentH;
    }
    bool dstIsSRGB = ((int)dstDesc.Format == 29 /*DXGI_FORMAT_R8G8B8A8_UNORM_SRGB*/ || (int)dstDesc.Format == 91 /*DXGI_FORMAT_B8G8R8A8_UNORM_SRGB*/);

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(ctx->Map(impl->cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        auto* cb = (BlitCB*)mapped.pData;
        cb->srcRect[0] = srcX; cb->srcRect[1] = srcY; cb->srcRect[2] = srcW; cb->srcRect[3] = srcH;
        cb->dstSize[0] = (float)dstDesc.Width; cb->dstSize[1] = (float)dstDesc.Height;
        cb->srcTexSize[0] = (float)srcDesc.Width; cb->srcTexSize[1] = (float)srcDesc.Height;
        cb->flags[0] = 0.0f; // no manual BGRA swizzle (format-correct sampling)
        // WebView2/WGC content is sRGB-encoded. Decode to linear for correct appearance in linear-space pipelines.
        // sRGB RTVs will re-encode on store; UNORM targets receive linear values (avoids oversaturation when engine samples as linear).
        cb->flags[1] = -1.0f;
        cb->flags[2] = dstIsSRGB ? 1.0f : 0.0f;
        cb->flags[3] = 0.0f;
        ctx->Unmap(impl->cb, 0);
    }

    // Save minimal state we touch (viewport/scissor/RTs/shaders) - lightweight save/restore
    D3D11_VIEWPORT oldVP[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]{}; UINT vpCount = 1; ctx->RSGetViewports(&vpCount, oldVP);
    ID3D11RenderTargetView* oldRTV = nullptr; ID3D11DepthStencilView* oldDSV = nullptr; ctx->OMGetRenderTargets(1, &oldRTV, &oldDSV);

    FLOAT clear[4] = {0,0,0,1};
    ctx->ClearRenderTargetView(rtv, clear);
    ID3D11RenderTargetView* rtvs[] = { rtv };
    ctx->OMSetRenderTargets(1, rtvs, nullptr);
    D3D11_VIEWPORT vp{}; vp.Width = (FLOAT)dstDesc.Width; vp.Height = (FLOAT)dstDesc.Height; vp.MinDepth = 0; vp.MaxDepth = 1;
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(impl->vs, nullptr, 0);
    ctx->PSSetShader(impl->ps, nullptr, 0);
    ID3D11SamplerState* samps[] = { impl->sampler };
    ctx->PSSetSamplers(0, 1, samps);
    ID3D11ShaderResourceView* srvs[] = { srv };
    ctx->PSSetShaderResources(0, 1, srvs);
    ID3D11Buffer* cbs[] = { impl->cb };
    ctx->PSSetConstantBuffers(0, 1, cbs);
    float blendFactor[4] = {0,0,0,0};
    ctx->OMSetBlendState(impl->blend, blendFactor, 0xFFFFFFFF);
    ctx->RSSetState(impl->rs);
    ctx->Draw(3, 0);

    // Unbind SRV/RTV to avoid hazards
    ID3D11ShaderResourceView* nullSRV[] = { nullptr };
    ctx->PSSetShaderResources(0, 1, nullSRV);
    ID3D11RenderTargetView* nullRTV[] = { nullptr };
    ctx->OMSetRenderTargets(1, nullRTV, nullptr);
    ctx->OMSetRenderTargets(1, &oldRTV, oldDSV);
    if (vpCount > 0) ctx->RSSetViewports(vpCount, oldVP);
    if (oldRTV) oldRTV->Release();
    if (oldDSV) oldDSV->Release();

    if (needCopyBack && rtIntermediate) {
        ctx->CopyResource(target, rtIntermediate);
    }

    srv->Release();
    src->Release();
    if (dev) dev->Release();
    // cachedRtv/rtIntermediate/srvIntermediate reused across frames; released in ReleaseBlit()/Stop() or on desc change

    {
        std::lock_guard<std::mutex> lk(impl->frameMutex);
        impl->copiedSeq = seq;
    }
    outDiag = std::string("blit ok | ") + dimInfo + (needCopyBack ? " | via intermediate RT->CopyResource" : " | direct RTV");
    SetDiag(outDiag);
    return true;
}
