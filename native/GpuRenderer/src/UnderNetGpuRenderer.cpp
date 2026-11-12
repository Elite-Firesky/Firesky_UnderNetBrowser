#include "UnderNetGpuRenderer.h"
#include "WgcCapture.h"
#include "CompositionBrowser.h"
#include "Unity/IUnityInterface.h"
#include "Unity/IUnityGraphics.h"
#include "Unity/IUnityGraphicsD3D11.h"

#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <atomic>
#include <mutex>
#include <string>
#include <vector>
#include <cstring>
#include <cmath>
#include <algorithm>

using Microsoft::WRL::ComPtr;

namespace {
    std::mutex g_mutex;
    std::atomic<int32_t> g_status{ UN_GPU_OK };
    std::string g_lastError;
    UnGpuLogCallback g_logCb = nullptr;

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_graphics = nullptr;
    ID3D11Device* g_device = nullptr;           // not owned
    ID3D11DeviceContext* g_context = nullptr;   // owned (AddRef'd)
    std::atomic<int32_t> g_pluginLoaded{ 0 };
    std::atomic<int32_t> g_renderer{ -1 };

    // Phase 3: animated test texture (created on Unity's D3D11 device, render thread)
    ComPtr<ID3D11Texture2D> g_testTexture;
    int32_t g_testW = 0;
    int32_t g_testH = 0;
    uint32_t g_frameCounter = 0;
    std::vector<uint32_t> g_testPixels; // RGBA8

    // Alternative path: Unity-owned Texture2D target (pointer from Texture2D.GetNativeTexturePtr)
    void* g_targetPtrPending = nullptr; // main-thread provided; consumed on render thread
    int32_t g_targetPendingW = 0;
    int32_t g_targetPendingH = 0;
    ID3D11Texture2D* g_targetTexture = nullptr; // not owned (Unity owns)
    ComPtr<ID3D11Texture2D> g_targetSrcTexture; // native-owned animation source on same device
    int32_t g_targetW = 0;
    int32_t g_targetH = 0;
    uint32_t g_targetFrameCounter = 0;
    std::vector<uint32_t> g_targetPixels; // RGBA8 staging for UpdateSubresource into src
    std::atomic<int32_t> g_targetReady{ 0 };

    // Phase 4A: WGC capture PoC (captures HWND into Unity-owned target texture via GPU copy on render thread)
    WgcCapture g_wgc;
    std::atomic<void*> g_wgcPendingHwnd{ nullptr };
    std::atomic<int32_t> g_wgcWantStart{ 0 };
    std::atomic<int32_t> g_wgcWantStop{ 0 };
    std::string g_wgcLastDiag;

    // Native CompositionController browser (WebView2 visual hosting + WGC CreateFromVisual capture)
    CompositionBrowser g_comp;
    std::wstring g_compPendingUrl = L"about:blank";
    std::wstring g_compPendingUserData = L"";
    int32_t g_compPendingW = 1280;
    int32_t g_compPendingH = 720;
    std::atomic<int32_t> g_compWantStart{ 0 };
    std::atomic<int32_t> g_compWantStop{ 0 };
    std::string g_compLastDiag;

    void SetError(int32_t code, const std::string& msg) {
        g_status.store(code);
        g_lastError = msg;
        if (g_logCb) g_logCb(code == UN_GPU_OK ? 0 : 2, msg.c_str());
    }

    void Log(int32_t level, const std::string& msg) {
        if (g_logCb) g_logCb(level, msg.c_str());
    }

    void ReleaseTestTexture() {
        g_testTexture.Reset();
        g_testPixels.clear();
        g_testW = 0;
        g_testH = 0;
        g_frameCounter = 0;
    }

    void ReleaseTargetResources(bool clearPending) {
        // Never release Unity-owned texture. Release only native-owned resources/context refs acquired via target path.
        g_targetReady.store(0);
        g_targetSrcTexture.Reset();
        g_targetPixels.clear();
        g_targetTexture = nullptr;
        g_targetW = 0;
        g_targetH = 0;
        g_targetFrameCounter = 0;
        if (clearPending) {
            g_targetPtrPending = nullptr;
            g_targetPendingW = 0;
            g_targetPendingH = 0;
        }
        // If device/context came only from target path (no Unity plugin interfaces), keep them for reuse until cleared/reset.
    }

    void ReleaseDeviceResources() {
        g_wgc.Stop();
        g_comp.Shutdown();
        ReleaseTestTexture();
        ReleaseTargetResources(true);
        if (g_context) { g_context->Release(); g_context = nullptr; }
        g_device = nullptr;
    }

    bool EnsureTestTexture(int32_t w, int32_t h) {
        if (!g_device || !g_context) {
            SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "No Unity D3D11 device/context available");
            return false;
        }
        if (g_testTexture && g_testW == w && g_testH == h) return true;

        ReleaseTestTexture();

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = (UINT)w;
        desc.Height = (UINT)h;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; // Unity TextureFormat.RGBA32
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;

        HRESULT hr = g_device->CreateTexture2D(&desc, nullptr, g_testTexture.GetAddressOf());
        if (FAILED(hr) || !g_testTexture) {
            char buf[128]; sprintf_s(buf, "CreateTexture2D failed hr=0x%08X", (unsigned)hr);
            SetError(UN_GPU_ERR_CREATE_TEXTURE, buf);
            return false;
        }

        g_testW = w; g_testH = h;
        g_testPixels.assign((size_t)w * (size_t)h, 0xFF000000u);
        g_status.store(UN_GPU_OK);
        g_lastError.clear();
        Log(0, "UnderNetGpuRenderer: test texture created");
        return true;
    }

    void UpdateTestTextureFrame() {
        if (!g_testTexture || !g_context || g_testW <= 0 || g_testH <= 0) return;
        // Simple animated pattern (native CPU fill + GPU upload) to prove render-thread external texture updates.
        // No CPU readback, no JPEG/managed image pipeline.
        const uint32_t t = g_frameCounter++;
        for (int y = 0; y < g_testH; ++y) {
            for (int x = 0; x < g_testW; ++x) {
                uint8_t r = (uint8_t)((x * 255) / (g_testW - 1));
                uint8_t g = (uint8_t)((y * 255) / (g_testH - 1));
                uint8_t b = (uint8_t)((t * 3 + (x ^ y)) & 0xFF);
                // moving bar
                int bar = (int)((t * 4) % (uint32_t)g_testW);
                if (std::abs(x - bar) < 8) { r = 255; g = 255; b = 255; }
                // border
                if (x < 4 || y < 4 || x >= g_testW - 4 || y >= g_testH - 4) { r = 124; g = 255; b = 107; }
                g_testPixels[(size_t)y * g_testW + x] = (0xFFu << 24) | ((uint32_t)b << 16) | ((uint32_t)g << 8) | r; // RGBA8 in memory -> DXGI_FORMAT_R8G8B8A8_UNORM
            }
        }
        g_context->UpdateSubresource(g_testTexture.Get(), 0, nullptr, g_testPixels.data(), (UINT)(g_testW * 4), 0);
    }

    bool ResolveTextureFromPtr(void* nativePtr, ID3D11Texture2D** outTex) {
        if (!nativePtr || !outTex) return false;
        *outTex = nullptr;
        IUnknown* unk = reinterpret_cast<IUnknown*>(nativePtr);
        // Unity D3D11 GetNativeTexturePtr may return ID3D11Texture2D* or ID3D11ShaderResourceView* depending on version/context.
        if (SUCCEEDED(unk->QueryInterface(__uuidof(ID3D11Texture2D), (void**)outTex)) && *outTex) {
            return true;
        }
        ID3D11ShaderResourceView* srv = nullptr;
        if (SUCCEEDED(unk->QueryInterface(__uuidof(ID3D11ShaderResourceView), (void**)&srv)) && srv) {
            ID3D11Resource* res = nullptr;
            srv->GetResource(&res);
            srv->Release();
            if (res) {
                if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)outTex)) && *outTex) {
                    res->Release();
                    return true;
                }
                res->Release();
            }
        }
        // Fallback: assume raw texture pointer (best-effort; validated by GetDesc/device checks)
        *outTex = reinterpret_cast<ID3D11Texture2D*>(nativePtr);
        (*outTex)->AddRef();
        return true;
    }

    void FillTargetPattern(std::vector<uint32_t>& pixels, int32_t w, int32_t h, uint32_t t) {
        if (w <= 0 || h <= 0) return;
        pixels.resize((size_t)w * (size_t)h);
        for (int y = 0; y < h; ++y) {
            for (int x = 0; x < w; ++x) {
                uint8_t r = (uint8_t)((x * 255) / (w > 1 ? (w - 1) : 1));
                uint8_t g = (uint8_t)((y * 255) / (h > 1 ? (h - 1) : 1));
                uint8_t b = (uint8_t)((t * 5 + (x * 3) + (y * 7)) & 0xFF);
                int barX = (int)((t * 6) % (uint32_t)(w > 0 ? w : 1));
                int barY = (int)((t * 4) % (uint32_t)(h > 0 ? h : 1));
                if (std::abs(x - barX) < 10) { r = 255; g = 80; b = 80; }
                if (std::abs(y - barY) < 10) { r = 80; g = 160; b = 255; }
                if (((x / 32 + y / 32 + (int)(t / 8)) % 2) == 0) { b = (uint8_t)std::min(255, b + 40); }
                if (x < 6 || y < 6 || x >= w - 6 || y >= h - 6) { r = 124; g = 255; b = 107; }
                pixels[(size_t)y * w + x] = (0xFFu << 24) | ((uint32_t)b << 16) | ((uint32_t)g << 8) | r;
            }
        }
    }

    bool EnsureTargetReadyOnRenderThread() {
        if (g_targetReady.load() && g_targetTexture && g_device && g_context && g_targetSrcTexture) return true;
        void* ptr = g_targetPtrPending;
        int32_t w = g_targetPendingW;
        int32_t h = g_targetPendingH;
        if (!ptr || w <= 0 || h <= 0) {
            // no pending target
            return g_targetReady.load() != 0;
        }

        // Reset previous target state (but keep pending values)
        g_targetReady.store(0);
        g_targetSrcTexture.Reset();
        g_targetPixels.clear();
        if (g_targetTexture) { g_targetTexture->Release(); g_targetTexture = nullptr; }

        ID3D11Texture2D* tex = nullptr;
        if (!ResolveTextureFromPtr(ptr, &tex) || !tex) {
            SetError(UN_GPU_ERR_INVALID, "Failed to resolve Unity texture pointer as ID3D11Texture2D/SRV");
            return false;
        }

        D3D11_TEXTURE2D_DESC desc{};
        tex->GetDesc(&desc);
        if (desc.Width == 0 || desc.Height == 0) {
            tex->Release();
            SetError(UN_GPU_ERR_INVALID, "Unity texture has invalid dimensions");
            return false;
        }

        ID3D11Device* dev = nullptr;
        tex->GetDevice(&dev);
        if (!dev) {
            tex->Release();
            SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "Failed to get D3D11 device from Unity texture");
            return false;
        }

        ID3D11DeviceContext* ctx = nullptr;
        dev->GetImmediateContext(&ctx);
        if (!ctx) {
            dev->Release();
            tex->Release();
            SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "Failed to get D3D11 immediate context from Unity texture device");
            return false;
        }

        // Replace any previous device/context refs (safe: we AddRef'd)
        if (g_context) g_context->Release();
        g_context = ctx; // takes ref from GetImmediateContext
        g_device = dev;  // not AddRef'd separately; GetDevice returns pointer we should Release? actually GetDevice AddRefs in D3D - but we'll store raw; better keep ComPtr-like ref via AddRef/Release. Simpler: store and Release in cleanup.
        dev->AddRef();
        g_device = dev;
        // we have two refs on dev now (GetDevice + AddRef) and ctx; balance by releasing our extra dev and keeping g_device managed manually
        dev->Release(); // g_device still valid (AddRef above)

        // Create native-owned source texture on same device for animated pattern, then GPU copy into Unity texture
        D3D11_TEXTURE2D_DESC srcDesc = desc;
        srcDesc.MipLevels = 1;
        srcDesc.ArraySize = 1;
        srcDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; // requested/expected
        srcDesc.SampleDesc.Count = 1;
        srcDesc.Usage = D3D11_USAGE_DEFAULT;
        srcDesc.BindFlags = 0;
        srcDesc.CPUAccessFlags = 0;
        srcDesc.MiscFlags = 0;

        ComPtr<ID3D11Texture2D> src;
        HRESULT hr = dev->CreateTexture2D(&srcDesc, nullptr, src.GetAddressOf());
        if (FAILED(hr) || !src) {
            tex->Release();
            char buf[128]; sprintf_s(buf, "CreateTexture2D (target src) failed hr=0x%08X", (unsigned)hr);
            SetError(UN_GPU_ERR_CREATE_TEXTURE, buf);
            return false;
        }

        g_targetTexture = tex; // Unity-owned, we hold a ref while using (released on clear/reset)
        g_targetSrcTexture = src;
        g_targetW = (int32_t)desc.Width;
        g_targetH = (int32_t)desc.Height;
        g_targetFrameCounter = 0;
        g_targetPixels.clear();
        g_targetReady.store(1);
        g_status.store(UN_GPU_OK);
        {
            char msg[256];
            sprintf_s(msg, "Target texture ready (%dx%d fmt=%u) device acquired from Unity-owned texture", g_targetW, g_targetH, (unsigned)desc.Format);
            g_lastError = msg;
            Log(0, std::string("UnderNetGpuRenderer: ") + msg);
        }
        return true;
    }

    void UpdateTargetFrameOnRenderThread() {
        if (!EnsureTargetReadyOnRenderThread()) return;
        if (!g_targetTexture || !g_targetSrcTexture || !g_context) return;

        const uint32_t t = g_targetFrameCounter++;
        FillTargetPattern(g_targetPixels, g_targetW, g_targetH, t);
        g_context->UpdateSubresource(g_targetSrcTexture.Get(), 0, nullptr, g_targetPixels.data(), (UINT)(g_targetW * 4), 0);

        // GPU-to-GPU copy into Unity-owned texture (same device). No CPU readback.
        D3D11_TEXTURE2D_DESC dstDesc{}, srcDesc{};
        g_targetTexture->GetDesc(&dstDesc);
        g_targetSrcTexture->GetDesc(&srcDesc);
        if (dstDesc.Width == srcDesc.Width && dstDesc.Height == srcDesc.Height) {
            g_context->CopyResource(g_targetTexture, g_targetSrcTexture.Get());
        } else {
            D3D11_BOX box{};
            box.left = 0; box.top = 0; box.front = 0;
            box.right = (std::min)(dstDesc.Width, srcDesc.Width);
            box.bottom = (std::min)(dstDesc.Height, srcDesc.Height);
            box.back = 1;
            g_context->CopySubresourceRegion(g_targetTexture, 0, 0, 0, 0, g_targetSrcTexture.Get(), 0, &box);
        }
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType) {
        std::lock_guard<std::mutex> lock(g_mutex);
        switch (eventType) {
        case kUnityGfxDeviceEventInitialize:
        case kUnityGfxDeviceEventAfterReset: {
            ReleaseDeviceResources();
            if (g_unityInterfaces) {
                auto* d3d = UNITY_GET_INTERFACE(IUnityGraphicsD3D11, g_unityInterfaces, kUnityGraphicsD3D11GUID);
                if (d3d && d3d->GetDevice) {
                    g_device = d3d->GetDevice();
                    if (g_device) {
                        g_device->GetImmediateContext(&g_context);
                        SetError(UN_GPU_OK, "Unity D3D11 device acquired");
                        Log(0, "UnderNetGpuRenderer: Unity D3D11 device/context ready");
                    } else {
                        SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "IUnityGraphicsD3D11 returned null device");
                    }
                } else {
                    // Not D3D11 or interface unavailable
                    SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "IUnityGraphicsD3D11 interface unavailable (non-D3D11 or not provided)");
                }
            }
            break;
        }
        case kUnityGfxDeviceEventShutdown:
        case kUnityGfxDeviceEventBeforeReset:
            ReleaseDeviceResources();
            Log(0, "UnderNetGpuRenderer: device resources released");
            break;
        default:
            break;
        }
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
        std::lock_guard<std::mutex> lock(g_mutex);
        switch (eventId) {
        case UN_GPU_EVENT_CREATE_TEST_TEXTURE:
            EnsureTestTexture(512, 512);
            if (g_testTexture) UpdateTestTextureFrame();
            break;
        case UN_GPU_EVENT_UPDATE_TEST_TEXTURE:
            if (!g_testTexture) EnsureTestTexture(512, 512);
            UpdateTestTextureFrame();
            break;
        case UN_GPU_EVENT_DESTROY_TEST_TEXTURE:
            ReleaseTestTexture();
            Log(0, "UnderNetGpuRenderer: test texture destroyed");
            break;
        case UN_GPU_EVENT_TARGET_INIT_UPDATE:
        case UN_GPU_EVENT_TARGET_UPDATE:
            UpdateTargetFrameOnRenderThread();
            break;
        case UN_GPU_EVENT_TARGET_RELEASE:
            g_wgc.Stop();
            g_comp.Shutdown();
            if (g_targetTexture) { g_targetTexture->Release(); g_targetTexture = nullptr; }
            ReleaseTargetResources(false);
            Log(0, "UnderNetGpuRenderer: target resources released (Unity texture untouched)");
            break;
        case UN_GPU_EVENT_WGC_START: {
            g_wgcWantStart.store(0);
            // Ensure target/device are ready (render thread). WGC uses Unity D3D11 device from target.
            if (!EnsureTargetReadyOnRenderThread() || !g_device || !g_context) {
                SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "WGC start: target/device not ready (set Unity-owned texture first)");
                g_wgcLastDiag = g_lastError;
                break;
            }
            HWND hwnd = (HWND)g_wgcPendingHwnd.load();
            std::string err;
            if (g_wgc.Start(hwnd, g_device, g_context, err)) {
                g_wgcLastDiag = "WGC started";
                g_status.store(UN_GPU_OK);
                g_lastError = "WGC started";
                Log(0, "UnderNetGpuRenderer: WGC capture started");
            } else {
                g_wgcLastDiag = err;
                SetError(UN_GPU_ERR_INVALID, "WGC start failed: " + err);
                Log(2, std::string("UnderNetGpuRenderer: WGC start failed: ") + err);
            }
            break;
        }
        case UN_GPU_EVENT_WGC_COPY_LATEST: {
            if (g_wgcWantStop.exchange(0)) { g_wgc.Stop(); g_wgcLastDiag = "WGC stopped"; }
            if (g_wgcWantStart.exchange(0)) {
                // try start inline if requested
                if (EnsureTargetReadyOnRenderThread() && g_device && g_context) {
                    HWND hwnd = (HWND)g_wgcPendingHwnd.load();
                    std::string err;
                    if (g_wgc.Start(hwnd, g_device, g_context, err)) { g_wgcLastDiag = "WGC started"; Log(0, "UnderNetGpuRenderer: WGC started (deferred)"); }
                    else { g_wgcLastDiag = err; SetError(UN_GPU_ERR_INVALID, "WGC start failed: " + err); }
                }
            }
            if (!g_wgc.IsActive()) break;
            if (!EnsureTargetReadyOnRenderThread() || !g_context || !g_targetTexture) break;
            std::string diag;
            if (g_wgc.CopyLatestTo(g_context, g_targetTexture, diag)) {
                // copied new frame
            }
            if (!diag.empty()) g_wgcLastDiag = diag;
            break;
        }
        case UN_GPU_EVENT_WGC_STOP:
            g_wgcWantStop.store(0);
            g_wgc.Stop();
            g_wgcLastDiag = "WGC stopped";
            Log(0, "UnderNetGpuRenderer: WGC capture stopped (event)");
            break;
        case UN_GPU_EVENT_COMP_START: {
            g_compWantStart.store(0);
            g_wgc.Stop(); // avoid competing capture paths
            if (!EnsureTargetReadyOnRenderThread() || !g_device || !g_context) {
                SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "Comp start: target/device not ready (set Unity-owned texture first)");
                g_compLastDiag = g_lastError;
                break;
            }
            std::string err;
            if (g_comp.Start(g_device, g_context, g_compPendingW, g_compPendingH, g_compPendingUrl, g_compPendingUserData, err)) {
                g_compLastDiag = g_comp.GetDiag();
                g_status.store(UN_GPU_OK);
                g_lastError.clear();
                Log(0, std::string("UnderNetGpuRenderer: composition browser start accepted (async). ") + g_compLastDiag);
            } else {
                g_compLastDiag = err.empty() ? g_comp.GetDiag() : err;
                SetError(UN_GPU_ERR_INVALID, "Composition browser start failed: " + g_compLastDiag);
                Log(2, std::string("UnderNetGpuRenderer: composition browser start failed: ") + g_compLastDiag);
            }
            break;
        }
        case UN_GPU_EVENT_COMP_COPY_LATEST: {
            if (g_compWantStop.exchange(0)) { g_comp.Shutdown(); g_compLastDiag = "Composition browser stopped"; }
            if (g_compWantStart.exchange(0)) {
                g_wgc.Stop();
                if (EnsureTargetReadyOnRenderThread() && g_device && g_context) {
                    std::string err;
                    if (g_comp.Start(g_device, g_context, g_compPendingW, g_compPendingH, g_compPendingUrl, g_compPendingUserData, err)) {
                        g_compLastDiag = g_comp.GetDiag();
                        Log(0, std::string("UnderNetGpuRenderer: composition browser start accepted (deferred/async). ") + g_compLastDiag);
                    } else {
                        g_compLastDiag = err.empty() ? g_comp.GetDiag() : err;
                        SetError(UN_GPU_ERR_INVALID, "Composition browser start failed: " + g_compLastDiag);
                    }
                }
            }
            if (!g_comp.IsRunning()) break;
            if (!EnsureTargetReadyOnRenderThread() || !g_context || !g_targetTexture) break;
            {
                std::string diag;
                if (g_comp.CopyLatestTo(g_context, g_targetTexture, diag)) {
                    // copied
                }
                if (!diag.empty()) g_compLastDiag = diag;
            }
            break;
        }
        case UN_GPU_EVENT_COMP_STOP:
            g_compWantStop.store(0);
            g_comp.Shutdown();
            g_compLastDiag = "Composition browser stopped";
            Log(0, "UnderNetGpuRenderer: composition browser stopped (event)");
            break;
        default:
            break;
        }
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_pluginLoaded.store(1);
    g_unityInterfaces = unityInterfaces;
    g_graphics = UNITY_GET_INTERFACE(IUnityGraphics, unityInterfaces, kUnityGraphicsGUID);
    if (g_graphics && g_graphics->RegisterDeviceEventCallback) {
        g_renderer.store(g_graphics->GetRenderer ? g_graphics->GetRenderer() : -1);
        g_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        // If device already initialized, Unity may have fired event; also try to query now via callback logic by simulating init
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    } else {
        SetError(UN_GPU_ERR_NO_D3D11_DEVICE, "UnityPluginLoad: IUnityGraphics unavailable");
    }
    Log(0, "UnderNetGpuRenderer: UnityPluginLoad");
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_graphics && g_graphics->UnregisterDeviceEventCallback) {
        g_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }
    ReleaseDeviceResources();
    g_graphics = nullptr;
    g_unityInterfaces = nullptr;
    Log(0, "UnderNetGpuRenderer: UnityPluginUnload");
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API GetRenderEventFunc() {
    return OnRenderEvent;
}

// C exports for managed P/Invoke
UN_EXPORT void __stdcall UnGpu_SetLogCallback(UnGpuLogCallback cb) { g_logCb = cb; }
UN_EXPORT int32_t __stdcall UnGpu_GetStatus() { return g_status.load(); }
UN_EXPORT void __stdcall UnGpu_GetLastError(char* buffer, int32_t bufferSize) {
    if (!buffer || bufferSize <= 0) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    strncpy_s(buffer, bufferSize, g_lastError.c_str(), _TRUNCATE);
}
UN_EXPORT int32_t __stdcall UnGpu_IsUnityDeviceReady() { return (g_device && g_context) ? 1 : 0; }
UN_EXPORT int32_t __stdcall UnGpu_IsUnityPluginLoaded() { return g_pluginLoaded.load(); }
UN_EXPORT int32_t __stdcall UnGpu_GetRenderer() { return g_renderer.load(); }
UN_EXPORT void* __stdcall UnGpu_GetTestTexturePtr() { std::lock_guard<std::mutex> lock(g_mutex); return g_testTexture.Get(); }
UN_EXPORT int32_t __stdcall UnGpu_GetTestTextureWidth() { return g_testW; }
UN_EXPORT int32_t __stdcall UnGpu_GetTestTextureHeight() { return g_testH; }

UN_EXPORT int32_t __stdcall UnGpu_SetTargetTexture(void* nativeTexturePtr, int32_t width, int32_t height) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_wgc.Stop();
    g_targetPtrPending = nativeTexturePtr;
    g_targetPendingW = width;
    g_targetPendingH = height;
    // reset readiness so render thread revalidates (e.g. resize/new texture)
    g_targetReady.store(0);
    if (g_targetTexture) { g_targetTexture->Release(); g_targetTexture = nullptr; }
    g_targetSrcTexture.Reset();
    g_targetPixels.clear();
    g_status.store(UN_GPU_OK);
    g_lastError = "Target texture pointer set (pending render-thread validation)";
    return (nativeTexturePtr && width > 0 && height > 0) ? 1 : 0;
}

UN_EXPORT int32_t __stdcall UnGpu_SetWgcCaptureHwnd(void* hwnd) {
    g_wgcPendingHwnd.store(hwnd);
    g_wgcWantStart.store(hwnd ? 1 : 0);
    return hwnd ? 1 : 0;
}

UN_EXPORT int32_t __stdcall UnGpu_GetWgcStatus() { return g_wgc.GetStatusCode(); }

UN_EXPORT void __stdcall UnGpu_GetWgcDiag(char* buffer, int32_t bufferSize) {
    if (!buffer || bufferSize <= 0) return;
    std::string d = g_wgcLastDiag;
    if (d.empty()) d = g_wgc.GetDiag();
    strncpy_s(buffer, bufferSize, d.c_str(), _TRUNCATE);
}

UN_EXPORT void __stdcall UnGpu_GetWgcFrameSize(int32_t* outWidth, int32_t* outHeight) {
    UINT w=0,h=0;
    g_wgc.GetLatestFrameSize(w,h);
    if (outWidth) *outWidth = (int32_t)w;
    if (outHeight) *outHeight = (int32_t)h;
}

UN_EXPORT int32_t __stdcall UnGpu_CompSetParams(const wchar_t* initialUrl, int32_t width, int32_t height, const wchar_t* userDataFolder) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_compPendingUrl = initialUrl ? initialUrl : L"about:blank";
    g_compPendingUserData = userDataFolder ? userDataFolder : L"";
    g_compPendingW = width > 0 ? width : 1280;
    g_compPendingH = height > 0 ? height : 720;
    g_compWantStart.store(1);
    return 1;
}

UN_EXPORT int32_t __stdcall UnGpu_CompNavigate(const wchar_t* url) {
    if (!url) return 0;
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.Navigate(url);
    return 1;
}

UN_EXPORT int32_t __stdcall UnGpu_CompGetStatus() { std::lock_guard<std::mutex> lock(g_mutex); return g_comp.GetStatusCode(); }

UN_EXPORT void __stdcall UnGpu_CompGetDiag(char* buffer, int32_t bufferSize) {
    if (!buffer || bufferSize <= 0) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    std::string d = g_compLastDiag;
    if (d.empty()) d = g_comp.GetDiag();
    strncpy_s(buffer, bufferSize, d.c_str(), _TRUNCATE);
}

UN_EXPORT void __stdcall UnGpu_CompGetFrameSize(int32_t* outWidth, int32_t* outHeight) {
    UINT w=0,h=0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_comp.GetContentSize(w,h);
    }
    if (outWidth) *outWidth = (int32_t)w;
    if (outHeight) *outHeight = (int32_t)h;
}

UN_EXPORT void __stdcall UnGpu_CompGoBack() { std::lock_guard<std::mutex> lock(g_mutex); g_comp.GoBack(); }
UN_EXPORT void __stdcall UnGpu_CompGoForward() { std::lock_guard<std::mutex> lock(g_mutex); g_comp.GoForward(); }
UN_EXPORT void __stdcall UnGpu_CompReload() { std::lock_guard<std::mutex> lock(g_mutex); g_comp.Reload(); }

UN_EXPORT void __stdcall UnGpu_CompSendMouse(int32_t kind, int32_t vk, uint32_t data, int32_t x, int32_t y) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (kind == 1) g_comp.SendMouseDown(x, y, (vk & 0x2) != 0);
    else if (kind == 2) g_comp.SendMouseUp(x, y, (vk & 0x2) != 0);
    else if (kind == 3) g_comp.SendMouseWheel(x, y, (int32_t)data);
    else if (kind == 4) g_comp.SendMouseDown(x, y, true);
    else if (kind == 5) g_comp.SendMouseUp(x, y, true);
    else g_comp.SendMouseMove(x, y, (vk & 0x1) != 0, (vk & 0x2) != 0);
}

UN_EXPORT void __stdcall UnGpu_CompGetCurrentUrl(wchar_t* buffer, int32_t bufferChars) {
    if (!buffer || bufferChars <= 0) return;
    buffer[0] = L'\0';
    std::wstring url;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        url = g_comp.GetCurrentUrl();
    }
    if (url.empty()) return;
    wcsncpy_s(buffer, (size_t)bufferChars, url.c_str(), _TRUNCATE);
}

UN_EXPORT int32_t __stdcall UnGpu_CompIsFullscreen() {
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_comp.IsFullscreen() ? 1 : 0;
}

UN_EXPORT void __stdcall UnGpu_CompShutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_compWantStart.store(0);
    g_compWantStop.store(0);
    g_comp.Shutdown();
    g_compLastDiag = "Composition browser stopped (api)";
    Log(0, "UnderNetGpuRenderer: composition browser stopped (api)");
}

UN_EXPORT void __stdcall UnGpu_CompSetCapturePaused(int32_t paused) {
    std::lock_guard<std::mutex> lock(g_mutex);
    try { g_comp.SetCapturePaused(paused != 0); } catch (...) {}
}

UN_EXPORT void __stdcall UnGpu_CompSendKey(uint32_t msg, uint64_t wParam, uint64_t lParam) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.SendKey(msg, wParam, lParam);
}

UN_EXPORT void __stdcall UnGpu_CompSendText(const wchar_t* text) {
    if (!text) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.SendText(std::wstring(text));
}

UN_EXPORT void __stdcall UnGpu_CompExecuteScript(const wchar_t* script) {
    if (!script) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.ExecuteScript(std::wstring(script));
}

UN_EXPORT void __stdcall UnGpu_CompRequestFocus() {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.RequestFocus();
}

UN_EXPORT void __stdcall UnGpu_CompSetSpatialAudioPoc(int32_t enabled, uint32_t targetPid) {
    std::lock_guard<std::mutex> lock(g_mutex);
    char msg[160];
    sprintf_s(msg, "SpatialPoC native boundary: UnGpu_CompSetSpatialAudioPoc enabled=%d targetPid=%u", (int)enabled, (unsigned)targetPid);
    Log(0, std::string("UnderNetGpuRenderer: ") + msg);
    g_comp.SetSpatialAudioPoc(enabled != 0, targetPid);
}

UN_EXPORT void __stdcall UnGpu_CompGetSpatialAudioFormat(uint32_t* sampleRate, uint32_t* channels) {
    uint32_t sr = 0, ch = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_comp.GetSpatialAudioFormat(sr, ch);
    }
    if (sampleRate) *sampleRate = sr;
    if (channels) *channels = ch;
}

UN_EXPORT uint32_t __stdcall UnGpu_CompReadSpatialAudioSamples(float* dstInterleaved, uint32_t maxFrames) {
    if (!dstInterleaved || maxFrames == 0) return 0;
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_comp.ReadSpatialAudioSamples(dstInterleaved, maxFrames);
}

UN_EXPORT int32_t __stdcall UnGpu_CompIsSpatialCaptureActive() {
    std::lock_guard<std::mutex> lock(g_mutex);
    return g_comp.IsSpatialCaptureActive() ? 1 : 0;
}

UN_EXPORT void __stdcall UnGpu_ClearTargetTexture() {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_comp.Shutdown();
    if (g_targetTexture) { g_targetTexture->Release(); g_targetTexture = nullptr; }
    ReleaseTargetResources(true);
    g_status.store(UN_GPU_OK);
    g_lastError = "Target texture cleared";
}

UN_EXPORT int32_t __stdcall UnGpu_IsTargetReady() { return g_targetReady.load(); }

UN_EXPORT void* __stdcall UnGpu_GetRenderEventFuncPtr() { return (void*)OnRenderEvent; }