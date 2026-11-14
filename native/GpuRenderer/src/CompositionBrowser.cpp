#include "CompositionBrowser.h"

#include <d3dcompiler.h>
#include <dxgi.h>
#include <dxgiformat.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>
#include <wrl.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.UI.Composition.h>

#include <DispatcherQueue.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <audiopolicy.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <tlhelp32.h>
#include <thread>
#include <atomic>
#include <mutex>
#include <vector>
#include <cstring>
#include <vector>
#include <algorithm>
#include <cstdio>
#include <deque>
#include <chrono>
#include <sstream>
#include <cwctype>
#include <string>
#include <fstream>
#include <functional>

#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Callback;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgdx = winrt::Windows::Graphics::DirectX;
namespace d3dwin = winrt::Windows::Graphics::DirectX::Direct3D11;
namespace wuc = winrt::Windows::UI::Composition;

namespace {
    static std::string NarrowUtf8(const std::wstring& w) {
        if (w.empty()) return {};
        int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
        if (len <= 0) return {};
        std::string s(len, '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), len, nullptr, nullptr);
        return s;
    }
    struct CB { float srcRect[4]; float dstSize[2]; float srcTexSize[2]; float flags[4]; };
    static const char* kVS = R"(
    struct VSOut { float4 pos:SV_Position; float2 uv:TEXCOORD0; };
    VSOut main(uint vid:SV_VertexID){ float2 p=(vid==0)?float2(-1,-1):(vid==1)?float2(-1,3):float2(3,-1);
      VSOut o; o.pos=float4(p,0,1); o.uv=float2((p.x+1)*0.5,1-(p.y+1)*0.5); return o; })";
    static const char* kPS = R"(
    Texture2D srcTex:register(t0); SamplerState samp0:register(s0);
    cbuffer CB:register(b0){ float4 srcRect; float2 dstSize; float2 srcTexSize; float4 flags; }
    struct VSOut{float4 pos:SV_Position; float2 uv:TEXCOORD0;};
    float3 toLinear(float3 c){return pow(saturate(c),2.2);} float3 toSRGB(float3 c){return pow(saturate(c),1.0/2.2);} 
    float4 main(VSOut i):SV_Target{ float2 dstPos=i.pos.xy; float srcW=max(1,srcRect.z),srcH=max(1,srcRect.w);
      float scale=min(dstSize.x/srcW,dstSize.y/srcH); float drawW=srcW*scale, drawH=srcH*scale; float offX=(dstSize.x-drawW)*0.5, offY=(dstSize.y-drawH)*0.5;
      if(dstPos.x<offX||dstPos.x>=offX+drawW||dstPos.y<offY||dstPos.y>=offY+drawH) return float4(0,0,0,1);
      float2 local=(dstPos-float2(offX,offY))/float2(drawW,drawH); local.y=1-local.y; float2 srcPx=srcRect.xy+local*float2(srcW,srcH);
      float2 uv=(srcPx+0.5)/max(srcTexSize,float2(1,1)); float4 c=srcTex.Sample(samp0,uv); if(flags.y<-0.5)c.rgb=toLinear(c.rgb); c.a=1; return c; })";
    static DXGI_FORMAT MakeSRV(DXGI_FORMAT f){ if((int)f==87) return (DXGI_FORMAT)87; if((int)f==27) return (DXGI_FORMAT)28; return f; } // B8G8R8A8_TYPELESS->UNORM, R8G8R8A8_TYPELESS->UNORM
    static DXGI_FORMAT MakeRTV(DXGI_FORMAT f){ if((int)f==87) return (DXGI_FORMAT)87; if((int)f==27) return (DXGI_FORMAT)28; return f; }
}

struct CompositionBrowser::Impl {
    struct MouseEvent { int kind=0; int vk=0; unsigned int data=0; int x=0; int y=0; };
    ID3D11Device* unityDevice = nullptr; // not owned
    ID3D11DeviceContext* unityContext = nullptr; // not owned
    int width = 1280, height = 720;
    std::wstring initialUrl = L"about:blank";
    std::wstring userDataFolder; // explicit WebView2 profile path (prevents Schedule I.exe.WebView2 in game root)
    std::wstring additionalBrowserArguments; // optional Chromium/WebView2 switches for environment creation
    std::wstring pendingUrl;
    std::atomic<bool> urlPending{false};
    std::atomic<int> pendingNavAction{0}; // 1=back,2=forward,3=reload
    struct KeyEvent { UINT msg=0; WPARAM wParam=0; LPARAM lParam=0; };
    std::mutex inputMutex;
    std::deque<MouseEvent> mouseQueue;
    std::deque<KeyEvent> keyQueue;
    std::deque<std::wstring> textQueue;
    std::deque<std::wstring> scriptQueue;
    std::atomic<bool> focusRequested{false};
    // Diagnostics (typing/focus/input delivery)
    std::atomic<uint64_t> diagFocusReqSet{0};
    std::atomic<uint64_t> diagFocusApplied{0};
    std::atomic<uint64_t> diagKeysQueued{0};
    std::atomic<uint64_t> diagKeysPosted{0};
    std::atomic<uint64_t> diagTextQueued{0};
    std::atomic<uint64_t> diagTextInjected{0};
    std::atomic<uint64_t> diagScriptQueued{0};
    std::atomic<uint64_t> diagScriptExec{0};
    std::atomic<uint64_t> diagMouseDownQueued{0};
    std::atomic<uint64_t> diagMouseUpQueued{0};
    std::atomic<uint64_t> diagMouseWheelQueued{0};
    std::atomic<uint32_t> diagLastKeyMsg{0};
    std::atomic<uint64_t> diagLastKeyWParam{0};
    std::atomic<uint64_t> diagNativeBlockedRequests{0};
    // Stage 3 PoC diagnostics (read-only observation; no behavior changes)
    std::atomic<int> diagAudioStateKnown{0};      // 1 if ICoreWebView2_8 audio/mute APIs available
    std::atomic<int> diagDocumentPlayingAudio{0}; // 1 if WebView2 reports document playing audio
    std::atomic<int> diagIsMuted{0};              // 1 if WebView2 core is muted
    std::string extDiag; // extension install/runtime status summary
    std::mutex extDiagMutex;
    std::string initTimingSummary; // preserved for diagnostics (not overwritten by frame blit diag)
    std::atomic<bool> capturePaused{false};

    // Adaptive Rendering (visual-only): capture/copy throttling and visual standby (keeps WebView2 session/audio alive).
    std::atomic<int> adaptiveCaptureIntervalMs{0}; // <=0 = uncapped/full
    std::atomic<bool> adaptiveVisualStandby{false};
    std::atomic<uint64_t> lastFrameAcceptMs{0};
    std::atomic<uint64_t> lastCopyMs{0};

    // Zoom (applied on browser thread)
    std::atomic<bool> zoomPending{false};
    std::atomic<int> zoomFactorMilli{1000}; // 1.0 = 1000
    std::atomic<int> currentZoomFactorMilli{1000};

    // Stage 4.1 startup timeline markers (ms since Start thread baseline; -1 = not observed)
    std::atomic<uint64_t> startTickMs{0};
    std::atomic<int64_t> tEnvReqMs{-1};
    std::atomic<int64_t> tEnvCbMs{-1};
    std::atomic<int64_t> tCtrlReqMs{-1};
    std::atomic<int64_t> tCtrlCbMs{-1};
    std::atomic<int64_t> tVisualDoneMs{-1};
    std::atomic<int64_t> tCaptureStartMs{-1};
    std::atomic<int64_t> tNavReqMs{-1};
    std::atomic<int64_t> tNavStartingMs{-1};
    std::atomic<int64_t> tContentLoadingMs{-1};
    std::atomic<int64_t> tDomContentLoadedMs{-1};
    std::atomic<int64_t> tNavCompletedMs{-1};
    std::atomic<int64_t> tNavCompletedStatus{-1};
    std::atomic<int64_t> tFirstFrameArrivedMs{-1};
    std::atomic<int64_t> tFirstFrameStoredMs{-1};
    std::atomic<int64_t> tFirstUnityBlitMs{-1};
    std::atomic<int64_t> tExtGetReqMs{-1};
    std::atomic<int64_t> tExtGetCbMs{-1};
    std::atomic<int64_t> tExtInstallPassDoneMs{-1};
    std::atomic<int64_t> tInitTotalMs{-1};

    // Stage 3 Gate A PoC (experimental, dev-only): WASAPI process-loopback capture attempt/diagnostics.
    // Controlled via SetSpatialAudioPoc(); must not affect render/input/lifecycle when disabled.
    std::atomic<bool> spatialPocEnabled{false};
    std::atomic<uint32_t> spatialTargetPid{0};
    std::atomic<uint32_t> spatialBrowserPid{0};
    std::atomic<int> spatialCaptureStatus{0}; // 0=idle/disabled,1=starting,2=capturing,<0 error
    std::atomic<uint64_t> spatialPackets{0};
    std::atomic<uint64_t> spatialFrames{0};
    std::atomic<uint64_t> spatialBytes{0};
    std::atomic<uint64_t> spatialUnderruns{0};
    std::atomic<uint64_t> spatialErrors{0};
    std::atomic<uint32_t> spatialSampleRate{0};
    std::atomic<uint32_t> spatialChannels{0};
    std::atomic<uint64_t> spatialReadyTicks{0};
    std::atomic<bool> spatialMuteApplied{false};
    std::mutex spatialDiagMutex;
    std::string spatialDiag = "spatial: disabled";
    // PCM ring buffer for Unity spatial playback (float32 interleaved). Preallocated on capture start.
    std::mutex spatialPcmMutex;
    std::vector<float> spatialPcmRing;
    size_t spatialPcmWrite = 0;
    size_t spatialPcmRead = 0;
    size_t spatialPcmCount = 0; // frames queued (not samples) or samples? store as frames
    uint32_t spatialPcmRate = 0;
    uint32_t spatialPcmCh = 0;
    mutable std::mutex stateMutex;
    std::wstring currentUrl;
    std::atomic<bool> isFullscreen{false};

    std::thread thread;
    std::atomic<bool> stop{false};
    std::atomic<bool> ready{false};
    HANDLE readyEvent = nullptr;
    std::string startError;

    HWND hostHwnd = nullptr;
    winrt::Windows::System::DispatcherQueueController dq{ nullptr };
    wuc::Compositor compositor{ nullptr };
    wuc::ContainerVisual rootVisual{ nullptr };
    wuc::ContainerVisual webVisual{ nullptr };

    ComPtr<ICoreWebView2Environment> env;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2CompositionController> compController;
    ComPtr<ICoreWebView2> core;

    d3dwin::IDirect3DDevice winrtDevice{ nullptr };
    wgc::GraphicsCaptureItem item{ nullptr };
    wgc::Direct3D11CaptureFramePool pool{ nullptr };
    wgc::GraphicsCaptureSession session{ nullptr };

    std::mutex frameMutex;
    ID3D11Texture2D* latest = nullptr; // AddRef'd
    UINT cW=0,cH=0,tW=0,tH=0; DXGI_FORMAT fmt=DXGI_FORMAT_UNKNOWN; uint64_t seq=0,copied=0;
    uint64_t lastFullDiagBuildMs = 0; // throttle expensive per-frame diag string rebuilds

    // Distance-based audio attenuation (best-effort, WebView2 process-tree session volume)
    std::atomic<bool> distanceAudioEnabled{false};
    std::atomic<int> distanceAudioVolumeMilli{1000}; // 0..1000
    uint64_t lastDistanceAudioApplyMs = 0;
    int lastDistanceAudioAppliedMilli = -1;
    bool distanceAudioNeedsRestore = false;

    // blit
    ID3D11VertexShader* vs=nullptr; ID3D11PixelShader* ps=nullptr; ID3D11SamplerState* samp=nullptr; ID3D11Buffer* cb=nullptr; ID3D11BlendState* blend=nullptr; ID3D11RasterizerState* rs=nullptr; bool blitReady=false;

    // cached per-frame blit resources (recreated only when desc/size/format change)
    ID3D11Texture2D* cachedTarget = nullptr;
    DXGI_FORMAT cachedTargetFmt = DXGI_FORMAT_UNKNOWN;
    UINT cachedTargetW = 0, cachedTargetH = 0;
    UINT cachedTargetBind = 0;
    ID3D11RenderTargetView* cachedRtv = nullptr;
    ID3D11Texture2D* cachedRtIntermediate = nullptr;
    bool cachedNeedCopyBack = false;

    DXGI_FORMAT cachedSrcFmt = DXGI_FORMAT_UNKNOWN;
    UINT cachedSrcW = 0, cachedSrcH = 0, cachedSrcBind = 0;
    ID3D11Texture2D* cachedSrvIntermediate = nullptr;

    ~Impl(){ /* resources released in Shutdown path */ }
};

static LRESULT CALLBACK HostProc(HWND h, UINT m, WPARAM w, LPARAM l){ return DefWindowProcW(h,m,w,l); }

void CompositionBrowser::SetDiag(const std::string& s){ std::lock_guard<std::mutex> lk(m_diagMutex); m_diag=s; }
void CompositionBrowser::SetError(const std::string& s){ std::lock_guard<std::mutex> lk(m_diagMutex); m_diag=s; m_status.store(-1); }

namespace {
    // Minimal Gate A PoC: WASAPI process-loopback capture attempt for a target PID.
    // Experimental/dev-only; best-effort diagnostics. Runs on a dedicated thread when enabled.
    struct SpatialPocCaptureState {
        std::atomic<bool> stop{false};
        std::thread thread;
    };
    static std::mutex g_spatialPocGlobalMutex;
    static SpatialPocCaptureState* g_spatialPoc = nullptr;

    static void SetSpatialDiag(CompositionBrowser::Impl* impl, const char* msg) {
        if (!impl) return;
        std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
        impl->spatialDiag = msg ? msg : "";
    }
    static void SetSpatialDiagStr(CompositionBrowser::Impl* impl, const std::string& msg) {
        if (!impl) return;
        std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
        impl->spatialDiag = msg;
    }

    class ActivateAudioInterfaceCompletionHandler : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, Microsoft::WRL::FtmBase, IActivateAudioInterfaceCompletionHandler> {
    public:
        HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
            HRESULT hrActivate = E_UNEXPECTED;
            IUnknown* punk = nullptr;
            HRESULT hrGet = operation ? operation->GetActivateResult(&hrActivate, &punk) : E_INVALIDARG;
            m_hrGetResult = hrGet;
            m_hrActivate = hrActivate;
            m_hr = SUCCEEDED(hrGet) ? hrActivate : hrGet; // legacy combined (prefer separate fields)
            if (punk) { m_unk = punk; punk->Release(); }
            if (m_event) SetEvent(m_event);
            return S_OK;
        }
        HANDLE m_event = nullptr;
        HRESULT m_hr = E_FAIL;           // combined (legacy)
        HRESULT m_hrGetResult = E_FAIL;  // HRESULT from GetActivateResult call
        HRESULT m_hrActivate = E_FAIL;   // activation HRESULT from GetActivateResult out param
        Microsoft::WRL::ComPtr<IUnknown> m_unk;
    };

    static void SpatialPocCaptureThread(CompositionBrowser::Impl* impl, uint32_t targetPid) {
        if (!impl) return;
        impl->spatialPackets.store(0, std::memory_order_relaxed);
        impl->spatialFrames.store(0, std::memory_order_relaxed);
        impl->spatialBytes.store(0, std::memory_order_relaxed);
        impl->spatialUnderruns.store(0, std::memory_order_relaxed);
        impl->spatialErrors.store(0, std::memory_order_relaxed);
        impl->spatialSampleRate.store(0, std::memory_order_relaxed);
        impl->spatialChannels.store(0, std::memory_order_relaxed);

        auto fail = [&](const std::string& msg) {
            impl->spatialCaptureStatus.store(-1, std::memory_order_relaxed);
            impl->spatialErrors.fetch_add(1, std::memory_order_relaxed);
            SetSpatialDiagStr(impl, "spatial: " + msg);
        };

        // Resolve target PID: explicit targetPid wins; otherwise wait for browser PID resolved by browser init thread.
        if (targetPid == 0) {
            SetSpatialDiag(impl, "spatial: resolving browser pid");
            for (int i = 0; i < 200 && targetPid == 0; ++i) { // ~20s max wait
                if (impl->stop.load(std::memory_order_relaxed) || !impl->spatialPocEnabled.load(std::memory_order_relaxed)) {
                    SetSpatialDiag(impl, "spatial: resolve cancelled");
                    impl->spatialCaptureStatus.store(0, std::memory_order_relaxed);
                    return;
                }
                uint32_t bpid = impl->spatialBrowserPid.load(std::memory_order_relaxed);
                if (bpid != 0) {
                    targetPid = bpid;
                    impl->spatialTargetPid.store(targetPid, std::memory_order_relaxed);
                    break;
                }
                Sleep(100);
            }
        }

        if (targetPid == 0) { fail("no target pid (browser pid unresolved)"); return; }

        HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        bool comInit = SUCCEEDED(hrCo) || hrCo == RPC_E_CHANGED_MODE;
        if (FAILED(hrCo) && hrCo != RPC_E_CHANGED_MODE) { fail("CoInitializeEx failed"); return; }

        impl->spatialCaptureStatus.store(1, std::memory_order_relaxed);

        auto getDescendants = [](uint32_t rootPid) -> std::vector<uint32_t> {
            std::vector<uint32_t> all;
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) return all;
            struct Node { uint32_t pid; uint32_t ppid; };
            std::vector<Node> procs;
            PROCESSENTRY32W pe{}; pe.dwSize = sizeof(pe);
            if (Process32FirstW(snap, &pe)) {
                do { procs.push_back({ pe.th32ProcessID, pe.th32ParentProcessID }); } while (Process32NextW(snap, &pe));
            }
            CloseHandle(snap);
            std::vector<uint32_t> q; q.push_back(rootPid);
            for (size_t i = 0; i < q.size(); ++i) {
                uint32_t cur = q[i];
                for (auto& n : procs) {
                    if (n.ppid == cur && n.pid != 0) {
                        // avoid cycles/dupes
                        bool seen = (n.pid == rootPid);
                        for (auto v : all) if (v == n.pid) { seen = true; break; }
                        if (!seen) { all.push_back(n.pid); q.push_back(n.pid); }
                    }
                }
            }
            return all;
        };

        std::vector<uint32_t> candidates;
        candidates.push_back(targetPid);
        {
            auto desc = getDescendants(targetPid);
            // Try browser pid first (tree), then individual descendants (often audio is in a utility/renderer process).
            for (auto d : desc) {
                bool dup = false; for (auto c : candidates) if (c == d) { dup = true; break; }
                if (!dup) candidates.push_back(d);
            }
        }
        // Cap attempts to avoid long stalls.
        if (candidates.size() > 24) candidates.resize(24);

        Microsoft::WRL::ComPtr<IAudioClient> audioClient;
        uint32_t activatedPid = 0;
        HRESULT lastHr = E_FAIL;
        std::string lastErr;

        auto tryActivatePid = [&](uint32_t pid, PROCESS_LOOPBACK_MODE mode, const char* modeTag) -> bool {
            AUDIOCLIENT_ACTIVATION_PARAMS* actParams = (AUDIOCLIENT_ACTIVATION_PARAMS*)CoTaskMemAlloc(sizeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            if (!actParams) { lastErr = "CoTaskMemAlloc"; return false; }
            memset(actParams, 0, sizeof(AUDIOCLIENT_ACTIVATION_PARAMS));
            actParams->ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            actParams->ProcessLoopbackParams.TargetProcessId = pid;
            actParams->ProcessLoopbackParams.ProcessLoopbackMode = mode;

            PROPVARIANT pv{}; PropVariantInit(&pv);
            pv.vt = VT_BLOB; pv.blob.cbSize = sizeof(AUDIOCLIENT_ACTIVATION_PARAMS); pv.blob.pBlobData = reinterpret_cast<BYTE*>(actParams);

            auto handler = Microsoft::WRL::Make<ActivateAudioInterfaceCompletionHandler>();
            if (!handler) {
                CoTaskMemFree(actParams); lastErr = "handler alloc"; return false;
            }
            handler->m_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!handler->m_event) { CoTaskMemFree(actParams); lastErr = "CreateEvent"; return false; }

            Microsoft::WRL::ComPtr<IActivateAudioInterfaceAsyncOperation> asyncOp;
            HRESULT hrA = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv, handler.Get(), &asyncOp);
            if (FAILED(hrA)) {
                lastHr = hrA;
                char buf[220]; sprintf_s(buf, "ActivateAudioInterfaceAsync hr=0x%08X pid=%u %s", (unsigned)hrA, (unsigned)pid, modeTag);
                lastErr = buf;
                CoTaskMemFree(actParams);
                CloseHandle(handler->m_event);
                return false;
            }

            DWORD waited = 0;
            while (waited < 8000) {
                if (impl->stop.load(std::memory_order_relaxed) || !impl->spatialPocEnabled.load(std::memory_order_relaxed)) {
                    CoTaskMemFree(actParams); CloseHandle(handler->m_event);
                    lastErr = "cancelled"; return false;
                }
                DWORD wr = WaitForSingleObject(handler->m_event, 100);
                if (wr == WAIT_OBJECT_0) break;
                waited += 100;
            }
            if (WaitForSingleObject(handler->m_event, 0) != WAIT_OBJECT_0) {
                CoTaskMemFree(actParams); CloseHandle(handler->m_event);
                lastHr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
                char buf[160]; sprintf_s(buf, "activation timeout pid=%u %s", (unsigned)pid, modeTag);
                lastErr = buf; return false;
            }
            CloseHandle(handler->m_event);
            CoTaskMemFree(actParams);

            if (FAILED(handler->m_hrGetResult) || FAILED(handler->m_hrActivate) || !handler->m_unk) {
                lastHr = FAILED(handler->m_hrGetResult) ? handler->m_hrGetResult : handler->m_hrActivate;
                char buf[280]; sprintf_s(buf, "activation complete: GetActivateResult=0x%08X activate=0x%08X pid=%u %s",
                    (unsigned)handler->m_hrGetResult, (unsigned)handler->m_hrActivate, (unsigned)pid, modeTag);
                lastErr = buf; return false;
            }
            Microsoft::WRL::ComPtr<IAudioClient> ac;
            HRESULT hrQ = handler->m_unk.As(&ac);
            if (FAILED(hrQ) || !ac) {
                lastHr = hrQ; char buf[160]; sprintf_s(buf, "QI IAudioClient failed pid=%u", (unsigned)pid); lastErr = buf; return false;
            }
            audioClient = ac; activatedPid = pid; return true;
        };

        for (size_t i = 0; i < candidates.size() && !audioClient; ++i) {
            uint32_t pid = candidates[i];
            // First candidate (browser/root) try include-tree; descendants try include-tree then target-only.
            PROCESS_LOOPBACK_MODE modes[2] = { PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE, PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE };
            const char* tags[2] = { "tree", "exclude" };
            int modeCount = (i == 0) ? 1 : 2;
            // For descendants, prefer include-tree as well? "exclude target but include children" not useful for leaf; try include-tree only.
            if (i != 0) { modes[0] = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE; tags[0] = "tree"; modeCount = 1; }
            for (int m = 0; m < modeCount && !audioClient; ++m) {
                if (impl->stop.load(std::memory_order_relaxed) || !impl->spatialPocEnabled.load(std::memory_order_relaxed)) {
                    SetSpatialDiag(impl, "spatial: activation cancelled");
                    impl->spatialCaptureStatus.store(0, std::memory_order_relaxed);
                    CoUninitialize(); return;
                }
                {
                    char buf[220]; sprintf_s(buf, "spatial: activating pid=%u (%u/%u) %s", (unsigned)pid, (unsigned)(i + 1), (unsigned)candidates.size(), tags[m]);
                    SetSpatialDiag(impl, buf);
                }
                if (tryActivatePid(pid, modes[m], tags[m])) break;
            }
        }

        if (!audioClient) {
            char buf[260];
            sprintf_s(buf, "%s (tried %u pids, root=%u)", lastErr.empty() ? "activation failed" : lastErr.c_str(), (unsigned)candidates.size(), (unsigned)targetPid);
            fail(buf);
            CoUninitialize();
            return;
        }

        HRESULT hr = S_OK;
        targetPid = activatedPid; // for diag below
        impl->spatialTargetPid.store(activatedPid, std::memory_order_relaxed);

        WAVEFORMATEX* mixFmt = nullptr;
        hr = audioClient->GetMixFormat(&mixFmt);
        if (FAILED(hr) || !mixFmt) {
            char buf[160]; sprintf_s(buf, "GetMixFormat hr=0x%08X", (unsigned)hr);
            fail(buf);
            CoUninitialize();
            return;
        }

        // Initialize loopback capture (polling, no event callback handle required).
        REFERENCE_TIME hnsBuffer = 200000; // 20ms
        hr = audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
            hnsBuffer, 0, mixFmt, nullptr);
        if (FAILED(hr)) {
            hr = audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, hnsBuffer, 0, mixFmt, nullptr);
        }
        if (FAILED(hr)) {
            char buf[160]; sprintf_s(buf, "IAudioClient::Initialize hr=0x%08X", (unsigned)hr);
            fail(buf);
            CoTaskMemFree(mixFmt);
            CoUninitialize();
            return;
        }

        UINT32 bufferFrames = 0;
        audioClient->GetBufferSize(&bufferFrames);
        Microsoft::WRL::ComPtr<IAudioCaptureClient> captureClient;
        hr = audioClient->GetService(__uuidof(IAudioCaptureClient), &captureClient);
        if (FAILED(hr) || !captureClient) {
            char buf[160]; sprintf_s(buf, "GetService(IAudioCaptureClient) hr=0x%08X", (unsigned)hr);
            fail(buf);
            CoTaskMemFree(mixFmt);
            CoUninitialize();
            return;
        }

        hr = audioClient->Start();
        if (FAILED(hr)) {
            char buf[160]; sprintf_s(buf, "IAudioClient::Start hr=0x%08X", (unsigned)hr);
            fail(buf);
            CoTaskMemFree(mixFmt);
            CoUninitialize();
            return;
        }

        // Determine format for Unity output (float32 interleaved)
        bool isFloat = false, isPcmInt = false, isExt = false;
        WORD bits = mixFmt->wBitsPerSample;
        WORD tag = mixFmt->wFormatTag;
        WORD ch = mixFmt->nChannels ? mixFmt->nChannels : 2;
        DWORD sr = mixFmt->nSamplesPerSec ? mixFmt->nSamplesPerSec : 48000;
        WAVEFORMATEXTENSIBLE* ext = (WAVEFORMATEXTENSIBLE*)mixFmt;
        if (tag == WAVE_FORMAT_IEEE_FLOAT && bits == 32) isFloat = true;
        else if (tag == WAVE_FORMAT_PCM && (bits == 16 || bits == 32)) isPcmInt = true;
        else if (tag == WAVE_FORMAT_EXTENSIBLE && mixFmt->cbSize >= (sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX))) {
            isExt = true;
            if (IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) && bits == 32) isFloat = true;
            else if (IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) && (bits == 16 || bits == 32)) isPcmInt = true;
        }
        if (!isFloat && !isPcmInt) { isFloat = true; bits = 32; } // best-effort assumption with AUTOCONVERTPCM

        {
            std::lock_guard<std::mutex> lk(impl->spatialPcmMutex);
            impl->spatialPcmRate = sr;
            impl->spatialPcmCh = ch;
            size_t capFrames = (size_t)sr * 2; // ~2s ring
            if (capFrames < 4096) capFrames = 4096;
            impl->spatialPcmRing.assign(capFrames * (size_t)ch, 0.0f);
            impl->spatialPcmWrite = 0; impl->spatialPcmRead = 0; impl->spatialPcmCount = 0;
        }
        impl->spatialSampleRate.store(sr, std::memory_order_relaxed);
        impl->spatialChannels.store(ch, std::memory_order_relaxed);
        impl->spatialCaptureStatus.store(2, std::memory_order_relaxed);
        {
            char buf[240];
            sprintf_s(buf, "spatial: capturing pid=%u %uHz %uch %ubit tag=%u%s", (unsigned)targetPid, (unsigned)sr, (unsigned)ch, (unsigned)bits, (unsigned)tag, isExt ? " ext" : "");
            SetSpatialDiag(impl, buf);
        }

        while (!impl->stop.load(std::memory_order_relaxed) && impl->spatialPocEnabled.load(std::memory_order_relaxed)) {
            UINT32 packetFrames = 0;
            hr = captureClient->GetNextPacketSize(&packetFrames);
            if (FAILED(hr)) { impl->spatialErrors.fetch_add(1, std::memory_order_relaxed); Sleep(5); continue; }
            if (packetFrames == 0) { impl->spatialUnderruns.fetch_add(1, std::memory_order_relaxed); Sleep(2); continue; }

            while (packetFrames != 0) {
                BYTE* data = nullptr;
                UINT32 framesAvailable = 0;
                DWORD flags = 0;
                hr = captureClient->GetBuffer(&data, &framesAvailable, &flags, nullptr, nullptr);
                if (FAILED(hr)) { impl->spatialErrors.fetch_add(1, std::memory_order_relaxed); break; }
                if (framesAvailable > 0) {
                    impl->spatialPackets.fetch_add(1, std::memory_order_relaxed);
                    impl->spatialFrames.fetch_add((uint64_t)framesAvailable, std::memory_order_relaxed);
                    impl->spatialBytes.fetch_add((uint64_t)framesAvailable * (uint64_t)mixFmt->nBlockAlign, std::memory_order_relaxed);

                    if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) && data) {
                        std::lock_guard<std::mutex> lk(impl->spatialPcmMutex);
                        auto& ring = impl->spatialPcmRing;
                        if (!ring.empty() && impl->spatialPcmCh > 0) {
                            size_t capFramesRing = ring.size() / (size_t)impl->spatialPcmCh;
                            for (UINT32 f = 0; f < framesAvailable; ++f) {
                                // Overwrite-oldest ring: if full, drop oldest frame
                                if (impl->spatialPcmCount >= capFramesRing) {
                                    impl->spatialPcmRead = (impl->spatialPcmRead + 1) % capFramesRing;
                                    impl->spatialPcmCount--;
                                }
                                size_t wFrame = impl->spatialPcmWrite;
                                for (UINT32 c = 0; c < impl->spatialPcmCh; ++c) {
                                    float sample = 0.0f;
                                    size_t si = (size_t)f * (size_t)ch + (size_t)(c < ch ? c : (ch - 1));
                                    if (isFloat && bits == 32) {
                                        const float* src = reinterpret_cast<const float*>(data);
                                        sample = src[si];
                                    } else if (isPcmInt && bits == 16) {
                                        const int16_t* src = reinterpret_cast<const int16_t*>(data);
                                        sample = (float)src[si] / 32768.0f;
                                    } else if (isPcmInt && bits == 32) {
                                        const int32_t* src = reinterpret_cast<const int32_t*>(data);
                                        sample = (float)((double)src[si] / 2147483648.0);
                                    } else {
                                        // fallback treat as float32
                                        const float* src = reinterpret_cast<const float*>(data);
                                        sample = src[si];
                                    }
                                    if (sample > 1.0f) sample = 1.0f; else if (sample < -1.0f) sample = -1.0f;
                                    ring[wFrame * (size_t)impl->spatialPcmCh + c] = sample;
                                }
                                impl->spatialPcmWrite = (wFrame + 1) % capFramesRing;
                                impl->spatialPcmCount++;
                            }
                        }
                    } else if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                        // write silence frames to keep timing reasonable
                        std::lock_guard<std::mutex> lk(impl->spatialPcmMutex);
                        auto& ring = impl->spatialPcmRing;
                        if (!ring.empty() && impl->spatialPcmCh > 0) {
                            size_t capFramesRing = ring.size() / (size_t)impl->spatialPcmCh;
                            for (UINT32 f = 0; f < framesAvailable; ++f) {
                                if (impl->spatialPcmCount >= capFramesRing) {
                                    impl->spatialPcmRead = (impl->spatialPcmRead + 1) % capFramesRing;
                                    impl->spatialPcmCount--;
                                }
                                size_t wFrame = impl->spatialPcmWrite;
                                for (UINT32 c = 0; c < impl->spatialPcmCh; ++c) ring[wFrame * (size_t)impl->spatialPcmCh + c] = 0.0f;
                                impl->spatialPcmWrite = (wFrame + 1) % capFramesRing;
                                impl->spatialPcmCount++;
                            }
                        }
                    }
                } else if (framesAvailable == 0) {
                    impl->spatialUnderruns.fetch_add(1, std::memory_order_relaxed);
                }
                captureClient->ReleaseBuffer(framesAvailable);
                hr = captureClient->GetNextPacketSize(&packetFrames);
                if (FAILED(hr)) { impl->spatialErrors.fetch_add(1, std::memory_order_relaxed); packetFrames = 0; }
            }
        }

        audioClient->Stop();
        CoTaskMemFree(mixFmt);
        impl->spatialCaptureStatus.store(impl->spatialPocEnabled.load(std::memory_order_relaxed) ? 1 : 0, std::memory_order_relaxed);
        SetSpatialDiag(impl, "spatial: capture stopped");
        CoUninitialize();
    }

    static void StartOrUpdateSpatialPocCapture(CompositionBrowser::Impl* impl) {
        if (!impl) return;
        bool enabled = impl->spatialPocEnabled.load(std::memory_order_relaxed);
        uint32_t pid = impl->spatialTargetPid.load(std::memory_order_relaxed);
        if (pid == 0) pid = impl->spatialBrowserPid.load(std::memory_order_relaxed);
        std::lock_guard<std::mutex> g(g_spatialPocGlobalMutex);
        if (!enabled || impl->stop.load(std::memory_order_relaxed)) {
            if (g_spatialPoc) {
                g_spatialPoc->stop.store(true, std::memory_order_relaxed);
            }
            return;
        }
        if (pid == 0) return; // wait for resolved browser PID
        int st = impl->spatialCaptureStatus.load(std::memory_order_relaxed);
        // If a capture thread is actively running (not stopped), leave it alone unless it failed.
        if (g_spatialPoc && g_spatialPoc->thread.joinable() && !g_spatialPoc->stop.load(std::memory_order_relaxed)) {
            if (st >= 0) return;
        }
        if (g_spatialPoc) {
            try { g_spatialPoc->stop.store(true, std::memory_order_relaxed); if (g_spatialPoc->thread.joinable()) g_spatialPoc->thread.join(); } catch (...) {}
            delete g_spatialPoc; g_spatialPoc = nullptr;
        }
        impl->spatialTargetPid.store(pid, std::memory_order_relaxed);
        if (st >= 0) impl->spatialCaptureStatus.store(1, std::memory_order_relaxed);
        g_spatialPoc = new SpatialPocCaptureState();
        g_spatialPoc->thread = std::thread([impl, pid]() {
            try {
                SpatialPocCaptureThread(impl, pid);
            } catch (...) {
                if (impl) {
                    impl->spatialCaptureStatus.store(-1, std::memory_order_relaxed);
                    impl->spatialErrors.fetch_add(1, std::memory_order_relaxed);
                    std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
                    if (impl->spatialDiag.rfind("spatial:", 0) != 0 || impl->spatialDiag.find("failed") == std::string::npos)
                        impl->spatialDiag = "spatial: capture thread exception";
                }
            }
            // If thread exits without setting terminal/success state, mark as failed so retry can proceed.
            if (impl) {
                int st = impl->spatialCaptureStatus.load(std::memory_order_relaxed);
                if (st == 1) {
                    impl->spatialCaptureStatus.store(-1, std::memory_order_relaxed);
                    impl->spatialErrors.fetch_add(1, std::memory_order_relaxed);
                    std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
                    if (impl->spatialDiag == "spatial: enabled (pid=" + std::to_string(pid) + ", capture pending)" ||
                        impl->spatialDiag == "spatial: enabled (waiting for browser pid, capture pending)" ||
                        impl->spatialDiag.find("retrying") != std::string::npos || impl->spatialDiag.empty()) {
                        impl->spatialDiag = "spatial: capture ended without activation (check logs/runtime support)";
                    }
                }
            }
        });
    }
}

static std::vector<uint32_t> CollectProcessTreePids(uint32_t rootPid);
static void ApplyDistanceAudioVolumeToProcessTree(CompositionBrowser::Impl* impl, bool enabled, float volume01);

bool CompositionBrowser::Start(ID3D11Device* device, ID3D11DeviceContext* context, int width, int height, const std::wstring& initialUrl, const std::wstring& userDataFolder, const std::wstring& additionalBrowserArguments, std::string& outError) {
    Shutdown();
    outError.clear();
    if(!device||!context){ outError="Unity D3D11 device/context unavailable"; SetError(outError); return false; }
    m_impl = new Impl();
    m_impl->unityDevice = device; m_impl->unityContext = context;
    m_impl->width = (std::max)(1,width); m_impl->height=(std::max)(1,height); m_impl->initialUrl = initialUrl.empty()?L"about:blank":initialUrl;
    m_impl->userDataFolder = userDataFolder;
    m_impl->additionalBrowserArguments = additionalBrowserArguments;
    m_impl->readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    // Create WinRT D3D device wrapper from Unity device (fast; do on calling thread before async browser startup).
    auto t0 = std::chrono::steady_clock::now();
    try {
        winrt::com_ptr<IDXGIDevice> dxgi; if(FAILED(device->QueryInterface(IID_PPV_ARGS(dxgi.put())))) { outError="QueryInterface IDXGIDevice failed"; SetError(outError); delete m_impl; m_impl=nullptr; return false; }
        winrt::com_ptr<::IInspectable> insp; if(FAILED(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), insp.put()))) { outError="CreateDirect3D11DeviceFromDXGIDevice failed"; SetError(outError); delete m_impl; m_impl=nullptr; return false; }
        m_impl->winrtDevice = insp.as<d3dwin::IDirect3DDevice>();
    } catch(...) { outError="WinRT D3D device wrapper failed"; SetError(outError); delete m_impl; m_impl=nullptr; return false; }
    auto tWrapMs = (int)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();

    m_status.store(2); // starting (async)
    SetDiag("Composition browser starting (async)");

    auto* impl = m_impl;
    impl->thread = std::thread([this, impl, t0, tWrapMs]() {
        using clock = std::chrono::steady_clock;
        auto stageMs = [&](const char* name, clock::time_point s, clock::time_point e, std::string& into){
            char b[96]; sprintf_s(b, "%s=%dms", name, (int)std::chrono::duration_cast<std::chrono::milliseconds>(e-s).count());
            if(!into.empty()) into += ", ";
            into += b;
        };
        std::string timings;
        {
            char b[64]; sprintf_s(b, "d3dWrap=%dms", tWrapMs); timings = b;
        }
        auto tThread = clock::now();
        impl->startTickMs.store(GetTickCount64(), std::memory_order_relaxed);
        auto markMs = [impl]() -> int64_t {
            uint64_t s = impl->startTickMs.load(std::memory_order_relaxed);
            if (s == 0) return -1;
            return (int64_t)(GetTickCount64() - s);
        };

        try { winrt::init_apartment(winrt::apartment_type::single_threaded); } catch(...) {}

        auto tSta = clock::now(); stageMs("sta", tThread, tSta, timings); SetDiag("Starting: STA ready | " + timings);

        auto tDq0 = clock::now();
        DispatcherQueueOptions opt{ sizeof(DispatcherQueueOptions), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA };
        ABI::Windows::System::IDispatcherQueueController* dqRaw = nullptr;
        if (SUCCEEDED(CreateDispatcherQueueController(opt, &dqRaw)) && dqRaw) {
            winrt::attach_abi(impl->dq, dqRaw);
        }
        auto tDq1 = clock::now(); stageMs("dq", tDq0, tDq1, timings);

        auto tHwnd0 = clock::now();
        WNDCLASSW wc{}; wc.lpfnWndProc=HostProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"UN_CompHost"; RegisterClassW(&wc);
        // Offscreen host (no visible ordinary browser window) but focusable for composition keyboard routing.
        impl->hostHwnd = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, L"UN_CompHost", WS_POPUP,
            -32000, -32000, impl->width, impl->height, nullptr, nullptr, wc.hInstance, nullptr);
        ShowWindow(impl->hostHwnd, SW_SHOWNA);
        auto tHwnd1 = clock::now(); stageMs("hwnd", tHwnd0, tHwnd1, timings);
        SetDiag("Starting: host window created | " + timings);

        // Ensure explicit WebView2 user data/profile folder exists (prevents default <exe>.WebView2 in game root)
        if (!impl->userDataFolder.empty()) {
            try { CreateDirectoryW(impl->userDataFolder.c_str(), nullptr); } catch (...) {}
        }
        const wchar_t* userDataArg = impl->userDataFolder.empty() ? nullptr : impl->userDataFolder.c_str();

        // Environment options (WebView2 SDK 1.0.2903.40): enable browser extensions for runtime/profile extension APIs.
        Microsoft::WRL::ComPtr<CoreWebView2EnvironmentOptions> envOptions;
        Microsoft::WRL::ComPtr<ICoreWebView2EnvironmentOptions> envOptionsBase;
        try {
            envOptions = Microsoft::WRL::Make<CoreWebView2EnvironmentOptions>();
            if (envOptions) {
                try { envOptions->put_AreBrowserExtensionsEnabled(TRUE); } catch (...) {}
                try {
                    if (!impl->additionalBrowserArguments.empty()) {
                        envOptions->put_AdditionalBrowserArguments(impl->additionalBrowserArguments.c_str());
                    }
                } catch (...) {}
                envOptions.As(&envOptionsBase);
            }
        } catch (...) {}

        auto tEnvReq = clock::now();
        impl->tEnvReqMs.store(markMs(), std::memory_order_relaxed);
        CreateCoreWebView2EnvironmentWithOptions(nullptr, userDataArg, envOptionsBase.Get(),
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([this,impl, timings, tEnvReq, tThread](HRESULT hr, ICoreWebView2Environment* env)->HRESULT{
                using clock = std::chrono::steady_clock;
                auto stageMs = [&](const char* name, clock::time_point s, clock::time_point e, std::string& into){
                    char b[96]; sprintf_s(b, "%s=%dms", name, (int)std::chrono::duration_cast<std::chrono::milliseconds>(e-s).count());
                    if(!into.empty()) into += ", ";
                    into += b;
                };
                std::string t = timings;
                auto tEnvCb = clock::now(); stageMs("env", tEnvReq, tEnvCb, t);
                impl->tEnvCbMs.store((int64_t)std::chrono::duration_cast<std::chrono::milliseconds>(tEnvCb - tThread).count(), std::memory_order_relaxed);
                SetDiag("Starting: environment callback | " + t);

                if(FAILED(hr)||!env){ impl->startError="CreateCoreWebView2Environment failed"; m_running.store(false); m_status.store(-1); SetDiag("Start failed: env | " + t); SetEvent(impl->readyEvent); return hr; }
                impl->env=env; ComPtr<ICoreWebView2Environment3> env3; if(FAILED(env->QueryInterface(IID_PPV_ARGS(&env3)))){ impl->startError="Env3 missing"; m_running.store(false); m_status.store(-1); SetDiag("Start failed: Env3 | " + t); SetEvent(impl->readyEvent); return E_NOINTERFACE; }

                auto tCtrlReq = clock::now();
                impl->tCtrlReqMs.store((int64_t)std::chrono::duration_cast<std::chrono::milliseconds>(tCtrlReq - tThread).count(), std::memory_order_relaxed);
                env3->CreateCoreWebView2CompositionController(impl->hostHwnd,
                    Callback<ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler>([this,impl,t,tCtrlReq,tThread](HRESULT hr, ICoreWebView2CompositionController* cc)->HRESULT{
                        using clock = std::chrono::steady_clock;
                        auto stageMs = [&](const char* name, clock::time_point s, clock::time_point e, std::string& into){
                            char b[96]; sprintf_s(b, "%s=%dms", name, (int)std::chrono::duration_cast<std::chrono::milliseconds>(e-s).count());
                            if(!into.empty()) into += ", ";
                            into += b;
                        };
                        std::string tt = t;
                        auto tCtrlCb = clock::now(); stageMs("compCtrl", tCtrlReq, tCtrlCb, tt);
                        impl->tCtrlCbMs.store((int64_t)std::chrono::duration_cast<std::chrono::milliseconds>(tCtrlCb - tThread).count(), std::memory_order_relaxed);
                        SetDiag("Starting: composition controller | " + tt);

                        if(FAILED(hr)||!cc){ impl->startError="CreateCompositionController failed"; m_running.store(false); m_status.store(-1); SetDiag("Start failed: compCtrl | " + tt); SetEvent(impl->readyEvent); return hr; }
                        impl->compController=cc; cc->QueryInterface(IID_PPV_ARGS(&impl->controller)); if(impl->controller) impl->controller->get_CoreWebView2(&impl->core);
                        // Resolve browser PID once core is ready (used for spatial PoC and as last-resort shutdown cleanup).
                        try {
                            if (impl->core) {
                                UINT32 bpid = 0;
                                if (SUCCEEDED(impl->core->get_BrowserProcessId(&bpid)) && bpid != 0) {
                                    impl->spatialBrowserPid.store((uint32_t)bpid, std::memory_order_relaxed);
                                    if (impl->spatialTargetPid.load(std::memory_order_relaxed) == 0) {
                                        impl->spatialTargetPid.store((uint32_t)bpid, std::memory_order_relaxed);
                                    }
                                }
                                // Spatial PoC activation state/diag (capture worker auto-start deferred).
                                if (impl->spatialPocEnabled.load(std::memory_order_relaxed)) {
                                    std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
                                    uint32_t pid = impl->spatialTargetPid.load(std::memory_order_relaxed);
                                    char buf[128];
                                    if (pid != 0) sprintf_s(buf, "spatial: enabled (pid=%u, capture deferred)", (unsigned)pid);
                                    else sprintf_s(buf, "spatial: enabled (browser pid unresolved, capture deferred)");
                                    impl->spatialDiag = buf;
                                    impl->spatialCaptureStatus.store(1, std::memory_order_relaxed);
                                }
                            }
                        } catch (...) {}
                        if (impl->core) {
                            ComPtr<ICoreWebView2Settings> settings;
                            if (SUCCEEDED(impl->core->get_Settings(&settings)) && settings) {
                                settings->put_IsStatusBarEnabled(FALSE);
                                settings->put_AreDefaultContextMenusEnabled(TRUE);
                                settings->put_IsZoomControlEnabled(FALSE);
                                settings->put_IsBuiltInErrorPageEnabled(TRUE);
                                try { settings->put_AreDevToolsEnabled(FALSE); } catch (...) {}
                                try {
                                    ComPtr<ICoreWebView2Settings5> s5; if (SUCCEEDED(settings.As(&s5))) { s5->put_AreBrowserAcceleratorKeysEnabled(TRUE); } // keep normal keys unless problematic; default acceptable
                                } catch (...) {}
                                try {
                                    ComPtr<ICoreWebView2Settings2> s2; if (SUCCEEDED(settings.As(&s2))) { /* keep defaults */ }
                                } catch (...) {}
                            }

                            // Popup/new-window suppression (prevents ad/popups from spawning visible windows).
                            impl->core->add_NewWindowRequested(
                                Callback<ICoreWebView2NewWindowRequestedEventHandler>([](ICoreWebView2* sender, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
                                    try {
                                        if (args) {
                                            args->put_Handled(TRUE);
                                            // Best-effort: do not open external/new window. (Could navigate current page instead, but keep conservative.)
                                        }
                                    } catch (...) {}
                                    return S_OK;
                                }).Get(), nullptr);

                            // Security hardening (Stage 2): centralized navigation policy (main + frame)
                            try {
                                auto toLowerW = [](std::wstring s) {
                                    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return (wchar_t)towlower(c); });
                                    return s;
                                };
                                auto trimSlashW = [](std::wstring s) {
                                    while (!s.empty() && (s.back() == L'\\' || s.back() == L'/')) s.pop_back();
                                    return s;
                                };
                                auto isSafeAppId = [](const std::wstring& id) -> bool {
                                    if (id.empty() || id.size() > 64) return false;
                                    for (wchar_t c : id) {
                                        bool ok = (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9') || c == L'-' || c == L'_';
                                        if (!ok) return false;
                                    }
                                    return true;
                                };
                                auto pathToFileUri = [](std::wstring p) -> std::wstring {
                                    for (auto& c : p) if (c == L'\\') c = L'/';
                                    // Minimal percent-encoding for common path characters.
                                    std::wstring out; out.reserve(p.size() + 16);
                                    for (wchar_t c : p) {
                                        if (c == L' ') out += L"%20";
                                        else if (c == L'#') out += L"%23";
                                        else if (c == L'%') out += L"%25";
                                        else out += c;
                                    }
                                    return L"file:///" + out;
                                };
                                auto resolveUnderNetRouteToFileUri = [impl, toLowerW, trimSlashW, isSafeAppId, pathToFileUri](const std::wstring& uri) -> std::wstring {
                                    try {
                                        if (uri.empty() || impl->userDataFolder.empty()) return L"";
                                        std::wstring lower = toLowerW(uri);
                                        bool isHome = (lower == L"undernet:home" || lower == L"about:home" || lower == L"home:home" || lower == L"home:homepage");
                                        std::wstring appId;
                                        if (!isHome) {
                                            if (lower.rfind(L"home:", 0) == 0) appId = uri.substr(5);
                                            else if (lower.rfind(L"undernet:app/", 0) == 0) appId = uri.substr(13);
                                            else if (lower.rfind(L"undernet:", 0) == 0) {
                                                std::wstring rest = uri.substr(9);
                                                std::wstring restLower = toLowerW(rest);
                                                if (restLower == L"home" || restLower == L"homepage") isHome = true;
                                                else appId = rest;
                                            } else {
                                                return L"";
                                            }
                                        }
                                        // impl->userDataFolder is ...\UserData\UnderNetBrowser\WebView2Composition (composition profile)
                                        std::wstring udRoot = trimSlashW(impl->userDataFolder);
                                        size_t sep = udRoot.find_last_of(L"\\/");
                                        if (sep == std::wstring::npos) return L"";
                                        udRoot = udRoot.substr(0, sep); // ...\UserData\UnderNetBrowser
                                        if (isHome) {
                                            std::wstring p = udRoot + L"\\root\\Homepage\\index.html";
                                            if (GetFileAttributesW(p.c_str()) == INVALID_FILE_ATTRIBUTES) return L"";
                                            return pathToFileUri(p);
                                        }
                                        // normalize app id
                                        while (!appId.empty() && (appId.front() == L'/' || appId.front() == L'\\')) appId.erase(appId.begin());
                                        while (!appId.empty() && (appId.back() == L'/' || appId.back() == L'\\')) appId.pop_back();
                                        size_t q = appId.find_first_of(L"?#");
                                        if (q != std::wstring::npos) appId = appId.substr(0, q);
                                        if (!isSafeAppId(appId)) return L"";
                                        std::wstring lowerId = toLowerW(appId);
                                        if (lowerId == L"home" || lowerId == L"homepage") {
                                            std::wstring p = udRoot + L"\\root\\Homepage\\index.html";
                                            if (GetFileAttributesW(p.c_str()) == INVALID_FILE_ATTRIBUTES) return L"";
                                            return pathToFileUri(p);
                                        }
                                        std::wstring p = udRoot + L"\\root\\Apps\\" + appId + L"\\index.html";
                                        if (GetFileAttributesW(p.c_str()) == INVALID_FILE_ATTRIBUTES) return L"";
                                        return pathToFileUri(p);
                                    } catch (...) {}
                                    return L"";
                                };
                                auto isAllowedUri = [impl, toLowerW](const std::wstring& uri) -> bool {
                                    try {
                                        std::wstring lower = toLowerW(uri);
                                        if (lower.empty()) return false;
                                        if (lower.rfind(L"javascript:", 0) == 0 || lower.rfind(L"vbscript:", 0) == 0 ||
                                            lower.rfind(L"shell:", 0) == 0 || lower.rfind(L"ms-appx:", 0) == 0 ||
                                            lower.rfind(L"ms-appinstaller:", 0) == 0 || lower.rfind(L"ms-settings:", 0) == 0 ||
                                            lower.rfind(L"ms-cxh:", 0) == 0) {
                                            return false;
                                        }
                                        // Custom local app/home routes are resolved and redirected in NavigationStarting.
                                        if (lower.rfind(L"home:", 0) == 0 || lower.rfind(L"undernet:", 0) == 0) return false;
                                        if (lower.rfind(L"file://", 0) == 0) {
                                            // Allow only verified internal UnderNetBrowser pages/routes under our UserData root.
                                            std::string luri = NarrowUtf8(uri);
                                            std::string ll = luri;
                                            for (char& c : ll) if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                                            if (!impl->userDataFolder.empty()) {
                                                std::string ud = NarrowUtf8(impl->userDataFolder);
                                                for (char& c : ud) if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                                                for (char& c : ud) if (c == '\\') c = '/';
                                                // file:/// URIs may use forward slashes; do a tolerant containment check.
                                                if (!ud.empty() && ll.find(ud) != std::string::npos) return true;
                                            }
                                            if (ll.find("undernetbrowser") != std::string::npos || ll.find("/home/home.html") != std::string::npos || ll.find("/root/homepage/index.html") != std::string::npos || ll.find("/root/apps/") != std::string::npos) return true;
                                            return false;
                                        }
                                        if (lower.rfind(L"https://", 0) == 0 || lower.rfind(L"http://", 0) == 0) return true;
                                        if (lower.rfind(L"about:", 0) == 0 || lower.rfind(L"data:", 0) == 0 || lower.rfind(L"blob:", 0) == 0) return true;
                                        // Unknown/custom URI schemes blocked by default.
                                        if (lower.find(L"://") != std::wstring::npos || lower.find(L':') != std::wstring::npos) return false;
                                        return true; // relative/internal navigations
                                    } catch (...) {}
                                    return false;
                                };
                                impl->core->add_NavigationStarting(
                                    Callback<ICoreWebView2NavigationStartingEventHandler>([impl, isAllowedUri, resolveUnderNetRouteToFileUri](ICoreWebView2* sender, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                                        try {
                                            if (args) {
                                                LPWSTR uriW = nullptr;
                                                if (SUCCEEDED(args->get_Uri(&uriW)) && uriW) {
                                                    std::wstring uri(uriW);
                                                    CoTaskMemFree(uriW);
                                                    std::wstring resolved = resolveUnderNetRouteToFileUri(uri);
                                                    if (!resolved.empty() && impl->core) {
                                                        args->put_Cancel(TRUE);
                                                        try { impl->core->Navigate(resolved.c_str()); } catch (...) {}
                                                        return S_OK;
                                                    }
                                                }
                                            }
                                        } catch (...) {}
                                        try {
                                            int64_t expected = -1;
                                            uint64_t s = impl->startTickMs.load(std::memory_order_relaxed);
                                            int64_t v = (s == 0) ? -1 : (int64_t)(GetTickCount64() - s);
                                            impl->tNavStartingMs.compare_exchange_strong(expected, v, std::memory_order_relaxed);
                                        } catch (...) {}
                                        try {
                                            if (!args) return S_OK;
                                            LPWSTR uriW = nullptr;
                                            if (FAILED(args->get_Uri(&uriW)) || !uriW) return S_OK;
                                            std::wstring uri(uriW);
                                            CoTaskMemFree(uriW);
                                            if (!isAllowedUri(uri)) {
                                                args->put_Cancel(TRUE);
                                            }
                                        } catch (...) {}
                                        return S_OK;
                                    }).Get(), nullptr);
                                impl->core->add_FrameNavigationStarting(
                                    Callback<ICoreWebView2NavigationStartingEventHandler>([impl, isAllowedUri](ICoreWebView2* sender, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                                        try {
                                            if (!args) return S_OK;
                                            LPWSTR uriW = nullptr;
                                            if (FAILED(args->get_Uri(&uriW)) || !uriW) return S_OK;
                                            std::wstring uri(uriW);
                                            CoTaskMemFree(uriW);
                                            if (!isAllowedUri(uri)) {
                                                args->put_Cancel(TRUE);
                                            }
                                        } catch (...) {}
                                        return S_OK;
                                    }).Get(), nullptr);
                            } catch (...) {}

                            // Stage 4.1: navigation/content/readiness timeline markers (best-effort, async).
                            try {
                                impl->core->add_ContentLoading(
                                    Callback<ICoreWebView2ContentLoadingEventHandler>([impl](ICoreWebView2* /*sender*/, ICoreWebView2ContentLoadingEventArgs* /*args*/) -> HRESULT {
                                        try { int64_t e=-1; uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); impl->tContentLoadingMs.compare_exchange_strong(e, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); } catch(...) {}
                                        return S_OK;
                                    }).Get(), nullptr);
                            } catch (...) {}
                            // DOMContentLoaded requires newer WebView2 interface; omitted to keep compatibility.
                            try {
                                impl->core->add_NavigationCompleted(
                                    Callback<ICoreWebView2NavigationCompletedEventHandler>([impl](ICoreWebView2* /*sender*/, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
                                        try {
                                            int64_t e=-1; uint64_t s=impl->startTickMs.load(std::memory_order_relaxed);
                                            impl->tNavCompletedMs.compare_exchange_strong(e, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed);
                                            if (args) { BOOL ok = FALSE; args->get_IsSuccess(&ok); COREWEBVIEW2_WEB_ERROR_STATUS st = COREWEBVIEW2_WEB_ERROR_STATUS_UNKNOWN; try { args->get_WebErrorStatus(&st); } catch(...) {} impl->tNavCompletedStatus.store(ok ? 1 : -(int64_t)st, std::memory_order_relaxed); }
                                        } catch(...) {}
                                        // Removed: prior best-effort playback auto-resume guard (after user pause/input) caused unwanted behavior and did not resolve the original issue.
                                        return S_OK;
                                    }).Get(), nullptr);
                            } catch (...) {}

                            // Security hardening: download protection (cancel all downloads)
                            try {
                                ComPtr<ICoreWebView2_4> core4;
                                if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core4))) && core4) {
                                    core4->add_DownloadStarting(
                                        Callback<ICoreWebView2DownloadStartingEventHandler>([impl](ICoreWebView2* sender, ICoreWebView2DownloadStartingEventArgs* args) -> HRESULT {
                                            try {
                                                if (!args) return S_OK;
                                                args->put_Cancel(TRUE);
                                                args->put_Handled(TRUE);
                                            } catch (...) {}
                                            return S_OK;
                                        }).Get(), nullptr);
                                }
                            } catch (...) {}

                            // Security hardening: external protocol protection
                            try {
                                ComPtr<ICoreWebView2_18> core18;
                                if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core18))) && core18) {
                                    core18->add_LaunchingExternalUriScheme(
                                        Callback<ICoreWebView2LaunchingExternalUriSchemeEventHandler>([impl](ICoreWebView2* sender, ICoreWebView2LaunchingExternalUriSchemeEventArgs* args) -> HRESULT {
                                            try {
                                                if (!args) return S_OK;
                                                args->put_Cancel(TRUE);
                                            } catch (...) {}
                                            return S_OK;
                                        }).Get(), nullptr);
                                }
                            } catch (...) {}

                            // Security hardening: permissions - default deny (camera/microphone/geolocation/notifications/clipboard and all other explicit requests)
                            try {
                                impl->core->add_PermissionRequested(
                                    Callback<ICoreWebView2PermissionRequestedEventHandler>([impl](ICoreWebView2* sender, ICoreWebView2PermissionRequestedEventArgs* args) -> HRESULT {
                                        try {
                                            if (!args) return S_OK;
                                            args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
                                            // Best-effort: prevent saving denied permission decisions in profile when supported
                                            try { ComPtr<ICoreWebView2PermissionRequestedEventArgs3> a3; if (SUCCEEDED(args->QueryInterface(IID_PPV_ARGS(&a3))) && a3) a3->put_SavesInProfile(FALSE); } catch (...) {}
                                        } catch (...) {}
                                        return S_OK;
                                    }).Get(), nullptr);
                            } catch (...) {}

                            // Stage 3 PoC diagnostics (read-only): observe WebView2 audio/mute state when available (ICoreWebView2_8).
                            try {
                                ComPtr<ICoreWebView2_8> core8;
                                if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core8))) && core8) {
                                    impl->diagAudioStateKnown.store(1, std::memory_order_relaxed);
                                    BOOL playing = FALSE, muted = FALSE;
                                    try { if (SUCCEEDED(core8->get_IsDocumentPlayingAudio(&playing))) impl->diagDocumentPlayingAudio.store(playing ? 1 : 0, std::memory_order_relaxed); } catch (...) {}
                                    try { if (SUCCEEDED(core8->get_IsMuted(&muted))) impl->diagIsMuted.store(muted ? 1 : 0, std::memory_order_relaxed); } catch (...) {}
                                    try {
                                        core8->add_IsDocumentPlayingAudioChanged(
                                            Callback<ICoreWebView2IsDocumentPlayingAudioChangedEventHandler>([impl](ICoreWebView2* sender, IUnknown* args) -> HRESULT {
                                                try {
                                                    ComPtr<ICoreWebView2_8> c8;
                                                    if (sender && SUCCEEDED(sender->QueryInterface(IID_PPV_ARGS(&c8))) && c8) {
                                                        BOOL p = FALSE;
                                                        if (SUCCEEDED(c8->get_IsDocumentPlayingAudio(&p))) impl->diagDocumentPlayingAudio.store(p ? 1 : 0, std::memory_order_relaxed);
                                                    }
                                                } catch (...) {}
                                                return S_OK;
                                            }).Get(), nullptr);
                                    } catch (...) {}
                                    try {
                                        core8->add_IsMutedChanged(
                                            Callback<ICoreWebView2IsMutedChangedEventHandler>([impl](ICoreWebView2* sender, IUnknown* args) -> HRESULT {
                                                try {
                                                    ComPtr<ICoreWebView2_8> c8;
                                                    if (sender && SUCCEEDED(sender->QueryInterface(IID_PPV_ARGS(&c8))) && c8) {
                                                        BOOL m = FALSE;
                                                        if (SUCCEEDED(c8->get_IsMuted(&m))) impl->diagIsMuted.store(m ? 1 : 0, std::memory_order_relaxed);
                                                    }
                                                } catch (...) {}
                                                return S_OK;
                                            }).Get(), nullptr);
                                    } catch (...) {}
                                }
                            } catch (...) {}

                            // Lightweight built-in request blocking (best-effort, conservative host hints; avoids breaking YouTube core playback).
                            try {
                                impl->core->AddWebResourceRequestedFilter(L"*", COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);
                                impl->core->add_WebResourceRequested(
                                    Callback<ICoreWebView2WebResourceRequestedEventHandler>([impl](ICoreWebView2* sender, ICoreWebView2WebResourceRequestedEventArgs* args) -> HRESULT {
                                        try {
                                            if (!args || !impl || !impl->env) return S_OK;
                                            ComPtr<ICoreWebView2WebResourceRequest> req;
                                            if (FAILED(args->get_Request(&req)) || !req) return S_OK;
                                            LPWSTR uriW = nullptr;
                                            if (FAILED(req->get_Uri(&uriW)) || !uriW) return S_OK;
                                            std::wstring uri(uriW);
                                            CoTaskMemFree(uriW);
                                            std::wstring lower = uri;
                                            std::transform(lower.begin(), lower.end(), lower.begin(), [](wchar_t c){ return (wchar_t)towlower(c); });

                                            auto contains = [&](const wchar_t* s) { return lower.find(s) != std::wstring::npos; };
                                            bool blocked = false;

                                            // Conservative third-party ad/tracker/popup domains and generic ad endpoints.
                                            // Intentionally excludes core YouTube/googlevideo/gstatic/ytimg domains required for playback/UI.
                                            static const wchar_t* kBlockHints[] = {
                                                L"doubleclick.net", L"googlesyndication.com", L"googleadservices.com", L"adservice.google",
                                                L"pagead2.googlesyndication.com", L"tpc.googlesyndication.com", L"securepubads.g.doubleclick.net",
                                                L"adnxs.com", L"adsrvr.org", L"taboola.com", L"outbrain.com", L"criteo.com", L"betweendigital.com",
                                                L"popads.net", L"popcash.net", L"propellerads.com", L"adsterra.com", L"imasdk.googleapis.com",
                                                L"/ads?", L"/adserver", L"/popup", L"/pagead/", L"&ad_type=", L"=adunit", L"/ptracking"
                                            };
                                            for (auto* h : kBlockHints) { if (contains(h)) { blocked = true; break; } }

                                            // Safer YouTube ad telemetry/endpoints (best-effort; avoid blocking core playback/media manifests).
                                            if (!blocked && contains(L"youtube.com")) {
                                                if (contains(L"/api/stats/ads") || contains(L"/pagead/") || contains(L"/pcs/activeview") ||
                                                    contains(L"get_midroll_info") || contains(L"ad_break") || contains(L"adformat=") ||
                                                    contains(L"&oad") || contains(L"/ptracking") || contains(L"/api/stats/qoe?") && contains(L"ad")) {
                                                    blocked = true;
                                                }
                                            }

                                            if (blocked) {
                                                try { impl->diagNativeBlockedRequests.fetch_add(1, std::memory_order_relaxed); } catch (...) {}
                                                // Native blocker (custom fallback). Extension-level blocks, if active, may occur inside WebView2
                                                // before/without this handler providing a response.
                                                ComPtr<ICoreWebView2WebResourceResponse> resp;
                                                if (SUCCEEDED(impl->env->CreateWebResourceResponse(nullptr, 204, L"Blocked", L"Content-Type: text/plain", &resp)) && resp) {
                                                    args->put_Response(resp.Get());
                                                }
                                            }
                                        } catch (...) {}
                                        return S_OK;
                                    }).Get(), nullptr);
                            } catch (...) {} // older runtimes/API issues: non-fatal

                            // Basic cosmetic cleanup for common consent/promo overlays (best-effort; does not remove in-video ads).
                            try {
                                // Cosmetic hiding + lightweight in-player ad mitigation (best-effort). YouTube ad delivery changes frequently;
                                // this aims to reduce visible ads/overlays and auto-skip when possible without breaking core playback.
                                const wchar_t* script = L"(function(){try{if(window.__unCompAdblockInit)return;window.__unCompAdblockInit=true;"
                                    L"function addCss(){try{if(document.getElementById('un-comp-cosmetic'))return;var st=document.createElement('style');st.id='un-comp-cosmetic';st.textContent='"
                                    L"ytd-consent-bump-v2-lightbox,tp-yt-iron-overlay-backdrop,yt-mealbar-promo-renderer,ytd-enforcement-message-view-model,"
                                    L"#masthead-ad,ytd-ad-slot-renderer,ytd-display-ad-renderer,ytd-promoted-sparkles-web-renderer,ytd-promoted-sparkles-text-search-renderer,"
                                    L"ytd-banner-promo-renderer,ytd-statement-banner-renderer,ytd-in-feed-ad-layout-renderer,ytd-promoted-video-renderer,ytd-compact-promoted-video-renderer,"
                                    L"ytd-rich-item-renderer:has(ytd-ad-slot-renderer),ytd-video-renderer:has(ytd-ad-slot-renderer),ytd-reel-video-renderer:has(ytd-ad-slot-renderer),"
                                    L".ytp-ad-module,.ytp-ad-overlay-container,.ytp-ad-image-overlay,.ytp-ad-player-overlay-instream-info,.ytp-ad-message-container,"
                                    L"ytd-player-legacy-desktop-watch-ads-renderer,ytd-action-companion-ad-renderer,ytd-companion-slot-renderer,#player-ads,#panels ytd-engagement-panel-section-list-renderer[target-id*=\"ad\"]"
                                    L"{display:none!important}';(document.head||document.documentElement).appendChild(st);}catch(_){}}"
                                    L"function clickIf(el){try{if(!el)return false;var r=el.getBoundingClientRect(); if(r && r.width===0 && r.height===0){} el.click(); return true;}catch(_){return false;}}"
                                    L"function trySkipAds(){try{"
                                    L"var sels=['.ytp-ad-skip-button','.ytp-ad-skip-button-modern','.ytp-skip-ad-button','button.ytp-ad-skip-button-modern','.ytp-ad-overlay-close-button'];"
                                    L"for(var i=0;i<sels.length;i++){var nodes=document.querySelectorAll(sels[i]); for(var j=0;j<nodes.length;j++){clickIf(nodes[j]);}}"
                                    L"var btns=document.querySelectorAll('button,[role=\"button\"]'); for(var k=0;k<btns.length;k++){var b=btns[k]; var t=((b.innerText||b.textContent||'')+' '+(b.getAttribute('aria-label')||'')).toLowerCase(); if(!t)continue; if(t.indexOf('skip ad')>=0||t.indexOf('skip ads')>=0||t.indexOf('skip')>=0&&t.indexOf('ad')>=0){clickIf(b);} }"
                                    L"var player=document.querySelector('.html5-video-player'); var adShowing=player && (player.classList.contains('ad-showing')||player.classList.contains('ad-interrupting'));"
                                    L"if(adShowing){var v=document.querySelector('video.html5-main-video,video'); if(v){try{if(isFinite(v.duration)&&v.duration>0&&v.currentTime<v.duration-0.25){v.currentTime=Math.max(v.currentTime,v.duration-0.25);} }catch(_){ } try{v.muted=true;}catch(_){ } try{v.playbackRate=16;}catch(_){ } }}"
                                    L"else{var v2=document.querySelector('video.html5-main-video,video'); if(v2 && v2.playbackRate>2){try{v2.playbackRate=1;}catch(_){}}}"
                                    L"}catch(_){}}"
                                    L"addCss(); trySkipAds();"
                                    L"var mo=null; try{mo=new MutationObserver(function(){addCss(); trySkipAds();}); mo.observe(document.documentElement||document,{childList:true,subtree:true,attributes:true});}catch(_){ }"
                                    L"try{setInterval(function(){addCss(); trySkipAds();}, 1000);}catch(_){ }"
                                    L"try{document.addEventListener('yt-navigate-finish',function(){addCss(); trySkipAds();},true);}catch(_){ }"
                                    L"}catch(e){}})();";
                                impl->core->AddScriptToExecuteOnDocumentCreated(script, nullptr);

                                // Track HTML fullscreen (e.g. YouTube player fullscreen) so managed UI can hide chrome/expand.
                                try {
                                    BOOL fs = FALSE;
                                    if (SUCCEEDED(impl->core->get_ContainsFullScreenElement(&fs)))
                                        impl->isFullscreen.store(fs ? true : false, std::memory_order_relaxed);
                                    impl->core->add_ContainsFullScreenElementChanged(
                                        Callback<ICoreWebView2ContainsFullScreenElementChangedEventHandler>(
                                            [impl](ICoreWebView2* sender, IUnknown*) -> HRESULT {
                                                try {
                                                    BOOL fsNow = FALSE;
                                                    if (sender && SUCCEEDED(sender->get_ContainsFullScreenElement(&fsNow)))
                                                        impl->isFullscreen.store(fsNow ? true : false, std::memory_order_relaxed);
                                                } catch(...) {}
                                                return S_OK;
                                            }).Get(), nullptr);
                                } catch(...) {}
                            } catch (...) {}
                        }

                        // Browser extension support (WebView2 SDK 1.0.2903.40, ICoreWebView2_13::get_Profile + ICoreWebView2Profile7).
                        // Looks for unpacked extensions under: <userDataFolder>\Extensions\<extensionFolder>\manifest.json
                        // Installs asynchronously (non-blocking). Keeps custom/native blocker as fallback.
                        try {
                            auto logExt = [this, impl](const std::string& s) {
                                try {
                                    if (impl) {
                                        std::lock_guard<std::mutex> lk(impl->extDiagMutex);
                                        if (!impl->extDiag.empty()) impl->extDiag += "; ";
                                        impl->extDiag += s;
                                    }
                                } catch (...) {}
                            };
                            ComPtr<ICoreWebView2_13> core13;
                            ComPtr<ICoreWebView2Profile> profile;
                            ComPtr<ICoreWebView2Profile7> profile7;
                            if (impl->core && SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core13))) && core13) {
                                core13->get_Profile(&profile);
                            }
                            if (profile) {
                                profile->QueryInterface(IID_PPV_ARGS(&profile7));
                            }
                            if (profile7) {
                                wchar_t profilePathBuf[2048] = {};
                                LPWSTR profilePathW = nullptr;
                                try {
                                    if (SUCCEEDED(profile->get_ProfilePath(&profilePathW)) && profilePathW) {
                                        wcsncpy_s(profilePathBuf, profilePathW, _TRUNCATE);
                                        CoTaskMemFree(profilePathW); profilePathW = nullptr;
                                    }
                                } catch (...) { if (profilePathW) CoTaskMemFree(profilePathW); }

                                struct ExtCtx {
                                    std::wstring folderPath;
                                    std::wstring folderName;
                                    std::wstring markerPath;
                                    std::wstring rootPath;
                                };

                                auto trimSlash = [](std::wstring s) {
                                    while (!s.empty() && (s.back() == L'\\' || s.back() == L'/')) s.pop_back();
                                    return s;
                                };
                                auto ensureExtReadme = [](const std::wstring& root) {
                                    try {
                                        std::wstring readme = root + L"\\README.txt";
                                        DWORD attrs = GetFileAttributesW(readme.c_str());
                                        if (attrs != INVALID_FILE_ATTRIBUTES) return;
                                        std::ofstream f(readme.c_str(), std::ios::out | std::ios::trunc);
                                        if (f.is_open()) {
                                            f << "Place unpacked WebView2 browser extensions here.\r\n";
                                            f << "Each extension must be in its own folder containing manifest.json.\r\n";
                                            f << "Example: <this folder>\\uBlockOriginLite\\manifest.json\r\n";
                                            f << "Do not put extension files inside the WebView2 profile directory (EBWebView).\r\n";
                                            f << "Extensions are installed on browser startup when supported by the installed WebView2 runtime.\r\n";
                                            f.close();
                                        }
                                    } catch (...) {}
                                };

                                std::vector<std::wstring> extRoots;
                                if (!impl->userDataFolder.empty()) {
                                    std::wstring ud = trimSlash(impl->userDataFolder);
                                    extRoots.push_back(ud + L"\\Extensions"); // e.g. ...\UnderNetBrowser\WebView2Composition\Extensions
                                    // Also support parent-level Extensions for convenience: ...\UnderNetBrowser\Extensions
                                    size_t sep = ud.find_last_of(L"\\/");
                                    if (sep != std::wstring::npos) {
                                        extRoots.push_back(ud.substr(0, sep) + L"\\Extensions");
                                    }
                                }

                                // Create extension roots automatically and enumerate unpacked candidates.
                                std::vector<ExtCtx> candidates;
                                for (const auto& extRoot : extRoots) {
                                    if (extRoot.empty()) continue;
                                    CreateDirectoryW(extRoot.c_str(), nullptr);
                                    ensureExtReadme(extRoot);

                                    std::wstring searchPath = extRoot + L"\\*";
                                    WIN32_FIND_DATAW fd{};
                                    HANDLE hFind = FindFirstFileW(searchPath.c_str(), &fd);
                                    if (hFind == INVALID_HANDLE_VALUE) continue;
                                    do {
                                        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
                                        std::wstring name = fd.cFileName;
                                        if (name == L"." || name == L".." || name == L".state") continue;
                                        std::wstring folder = extRoot + L"\\" + name;
                                        std::wstring manifest = folder + L"\\manifest.json";
                                        DWORD attrs = GetFileAttributesW(manifest.c_str());
                                        if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_DIRECTORY)) continue;

                                        std::wstring stateDir = extRoot + L"\\.state";
                                        CreateDirectoryW(stateDir.c_str(), nullptr);
                                        std::wstring marker = stateDir + L"\\" + name + L".installed";
                                        ExtCtx ctx; ctx.folderPath = folder; ctx.folderName = name; ctx.markerPath = marker; ctx.rootPath = extRoot;
                                        candidates.push_back(ctx);
                                    } while (FindNextFileW(hFind, &fd));
                                    FindClose(hFind);
                                }

                                std::wstring extRootLog;
                                for (size_t i = 0; i < extRoots.size(); ++i) { if (i) extRootLog += L" | "; extRootLog += extRoots[i]; }

                                // Log environment/profile info
                                {
                                    char info[1024];
                                    std::string rootsA = NarrowUtf8(extRootLog);
                                    std::string profA = NarrowUtf8(std::wstring(profilePathBuf));
                                    sprintf_s(info, "profile7 ready, candidates=%u, extensionsRoots='%s', profilePath='%s'",
                                        (unsigned)candidates.size(), rootsA.c_str(), profA.c_str());
                                    logExt(info);
                                }

                                // First get currently installed extensions, then install missing candidates.
                                // Keep COM profile interface alive across async callbacks.
                                ComPtr<ICoreWebView2Profile7> profile7Keep = profile7;
                                auto installCandidates = [this, impl, profile7Keep, candidates, logExt](ICoreWebView2BrowserExtensionList* existingList) mutable {
                                    try {
                                        std::vector<std::wstring> existingIds;
                                        UINT count = 0;
                                        if (existingList) existingList->get_Count(&count);
                                        for (UINT i = 0; i < count; i++) {
                                            ComPtr<ICoreWebView2BrowserExtension> ext;
                                            if (SUCCEEDED(existingList->GetValueAtIndex(i, &ext)) && ext) {
                                                LPWSTR id = nullptr, name = nullptr;
                                                BOOL enabled = FALSE;
                                                try { ext->get_Id(&id); } catch (...) {}
                                                try { ext->get_Name(&name); } catch (...) {}
                                                try { ext->get_IsEnabled(&enabled); } catch (...) {}
                                                std::wstring idW = id ? id : L"";
                                                std::wstring nameW = name ? name : L"";
                                                if (id) { existingIds.push_back(idW); CoTaskMemFree(id); }
                                                {
                                                    char b[512];
                                                    std::string ida = NarrowUtf8(idW);
                                                    std::string na = NarrowUtf8(nameW);
                                                    sprintf_s(b, "existing extension id='%s' name='%s' enabled=%d", ida.c_str(), na.c_str(), enabled ? 1 : 0);
                                                    logExt(b);
                                                }
                                                if (name) CoTaskMemFree(name);
                                            }
                                        }

                                        if (candidates.empty()) {
                                            logExt("no unpacked extension candidates found (place folders with manifest.json under Extensions/)");
                                            return;
                                        }

                                        // Helper: install one candidate if not marked as installed
                                        struct InstallerState { size_t index = 0; std::vector<ExtCtx> list; std::vector<std::wstring> existing; ComPtr<ICoreWebView2Profile7> profile; };
                                        auto state = std::make_shared<InstallerState>();
                                        state->index = 0; state->list = candidates; state->existing = existingIds; state->profile = profile7Keep;

                                        auto installNext = std::make_shared<std::function<void()>>();
                                        *installNext = [this, state, logExt, installNext]() mutable {
                                            if (!state || !state->profile) return;
                                            while (state->index < state->list.size()) {
                                                auto c = state->list[state->index++];
                                                // Skip if marker exists (already installed from this folder path previously)
                                                DWORD mattr = GetFileAttributesW(c.markerPath.c_str());
                                                if (mattr != INVALID_FILE_ATTRIBUTES) {
                                                    char b[256]; std::string fn = NarrowUtf8(c.folderName);
                                                    sprintf_s(b, "skip install (marker exists) folder='%s'", fn.c_str());
                                                    logExt(b);
                                                    continue;
                                                }

                                                char bstart[256]; std::string fn = NarrowUtf8(c.folderName);
                                                sprintf_s(bstart, "installing extension folder='%s'...", fn.c_str());
                                                logExt(bstart);

                                                auto handlerHolder = std::make_shared<ComPtr<ICoreWebView2ProfileAddBrowserExtensionCompletedHandler>>();
                                                *handlerHolder = Callback<ICoreWebView2ProfileAddBrowserExtensionCompletedHandler>(
                                                    [this, c, logExt, installNext, handlerHolder](HRESULT errorCode, ICoreWebView2BrowserExtension* result) -> HRESULT {
                                                        try {
                                                            if (SUCCEEDED(errorCode) && result) {
                                                                LPWSTR id = nullptr, name = nullptr; BOOL enabled = FALSE;
                                                                try { result->get_Id(&id); } catch (...) {}
                                                                try { result->get_Name(&name); } catch (...) {}
                                                                try { result->get_IsEnabled(&enabled); } catch (...) {}
                                                                std::wstring idW = id ? id : L"";
                                                                std::wstring nameW = name ? name : L"";
                                                                // Write marker to avoid repeated reinstalls from same folder path.
                                                                try {
                                                                    std::wofstream mf(c.markerPath.c_str(), std::ios::out | std::ios::trunc);
                                                                    if (mf.is_open()) { mf << L"id=" << idW << L"\nname=" << nameW << L"\npath=" << c.folderPath << L"\n"; mf.close(); }
                                                                } catch (...) {}
                                                                char b[640];
                                                                std::string ida = NarrowUtf8(idW);
                                                                std::string na = NarrowUtf8(nameW);
                                                                std::string pa = NarrowUtf8(c.folderPath);
                                                                sprintf_s(b, "installed extension ok hr=0x%08X id='%s' name='%s' enabled=%d path='%s'",
                                                                    (unsigned)errorCode, ida.c_str(), na.c_str(), enabled ? 1 : 0, pa.c_str());
                                                                logExt(b);
                                                                if (id) CoTaskMemFree(id);
                                                                if (name) CoTaskMemFree(name);
                                                            } else {
                                                                char b[512]; std::string pa = NarrowUtf8(c.folderPath);
                                                                sprintf_s(b, "install extension failed hr=0x%08X path='%s' (runtime may not support extensions or manifest invalid)", (unsigned)errorCode, pa.c_str());
                                                                logExt(b);
                                                            }
                                                        } catch (...) {}
                                                        // Continue with next candidate asynchronously
                                                        try { if (installNext && *installNext) (*installNext)(); } catch (...) {}
                                                        try { handlerHolder->Reset(); } catch (...) {}
                                                        return S_OK;
                                                    });
                                                HRESULT hrAdd = state->profile->AddBrowserExtension(c.folderPath.c_str(), handlerHolder->Get());
                                                if (FAILED(hrAdd)) {
                                                    char b[512]; std::string pa = NarrowUtf8(c.folderPath);
                                                    sprintf_s(b, "AddBrowserExtension call failed immediately hr=0x%08X path='%s'", (unsigned)hrAdd, pa.c_str());
                                                    logExt(b);
                                                    continue; // try next in loop
                                                }
                                                return; // async completion will continue chain
                                            }
                                            logExt("extension install pass complete");
                                        };

                                        (*installNext)();
                                        try { if (impl) { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); int64_t e=-1; impl->tExtInstallPassDoneMs.compare_exchange_strong(e, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); } } catch(...) {}
                                    } catch (...) {
                                        logExt("extension enumeration/install threw exception");
                                    }
                                };

                                if (impl) { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); impl->tExtGetReqMs.store(s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); }
                                auto getHandlerHolder = std::make_shared<ComPtr<ICoreWebView2ProfileGetBrowserExtensionsCompletedHandler>>();
                                *getHandlerHolder = Callback<ICoreWebView2ProfileGetBrowserExtensionsCompletedHandler>(
                                    [this, impl, installCandidates, logExt, getHandlerHolder](HRESULT errorCode, ICoreWebView2BrowserExtensionList* result) mutable -> HRESULT {
                                        try { if (impl) { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); int64_t e=-1; impl->tExtGetCbMs.compare_exchange_strong(e, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); } } catch(...) {}
                                        try {
                                            char b[128]; sprintf_s(b, "GetBrowserExtensions hr=0x%08X", (unsigned)errorCode);
                                            logExt(b);
                                        } catch (...) {}
                                        try { installCandidates(SUCCEEDED(errorCode) ? result : nullptr); } catch (...) { logExt("installCandidates failed"); }
                                        try { getHandlerHolder->Reset(); } catch (...) {}
                                        return S_OK;
                                    });
                                HRESULT hrGet = profile7->GetBrowserExtensions(getHandlerHolder->Get());
                                if (FAILED(hrGet)) {
                                    char b[128]; sprintf_s(b, "GetBrowserExtensions call failed hr=0x%08X (extension APIs may be unsupported by runtime)", (unsigned)hrGet);
                                    logExt(b);
                                }
                            } else {
                                // Extensions not supported on this runtime/profile interface
                                try { SetDiag(GetDiag() + " | ext: ICoreWebView2Profile7 unavailable (runtime/interface unsupported)"); } catch (...) {}
                            }
                        } catch (...) {
                            try { SetDiag(GetDiag() + " | ext: setup exception (non-fatal)"); } catch (...) {}
                        }

                        auto tVis0 = clock::now();
                        impl->compositor = wuc::Compositor();
                        impl->rootVisual = impl->compositor.CreateContainerVisual();
                        impl->webVisual = impl->compositor.CreateContainerVisual();
                        impl->rootVisual.Children().InsertAtTop(impl->webVisual);
                        impl->rootVisual.Size({(float)impl->width,(float)impl->height});
                        impl->webVisual.RelativeSizeAdjustment({1.f,1.f});
                        impl->compController->put_RootVisualTarget(winrt::get_unknown(impl->webVisual));
                        if(impl->controller){ RECT b{0,0,impl->width,impl->height}; impl->controller->put_Bounds(b); impl->controller->put_IsVisible(TRUE); }
                        auto tVis1 = clock::now(); stageMs("visual", tVis0, tVis1, tt);
                        { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); impl->tVisualDoneMs.store(s?(int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); }

                        auto tCap0 = clock::now();
                        try {
                            impl->item = wgc::GraphicsCaptureItem::CreateFromVisual(impl->rootVisual);
                            if(impl->item){ auto sz=impl->item.Size(); impl->pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(impl->winrtDevice, wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, sz);
                              impl->session = impl->pool.CreateCaptureSession(impl->item); try{impl->session.IsCursorCaptureEnabled(false);}catch(...){}
                              impl->pool.FrameArrived([impl](auto const& sender, auto const&){
                                try{
                                    auto frame=sender.TryGetNextFrame(); if(!frame) return;
                                    if (impl->capturePaused.load(std::memory_order_relaxed) || impl->adaptiveVisualStandby.load(std::memory_order_relaxed)) return;
                                    int intervalMs = impl->adaptiveCaptureIntervalMs.load(std::memory_order_relaxed);
                                    if (intervalMs > 0) {
                                        uint64_t now = GetTickCount64();
                                        uint64_t last = impl->lastFrameAcceptMs.load(std::memory_order_relaxed);
                                        if (last != 0 && now >= last && (now - last) < (uint64_t)intervalMs) return;
                                        impl->lastFrameAcceptMs.store(now, std::memory_order_relaxed);
                                    }
                                    int64_t exp=-1; uint64_t s=impl->startTickMs.load(std::memory_order_relaxed);
                                    impl->tFirstFrameArrivedMs.compare_exchange_strong(exp, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed);
                                    auto cs=frame.ContentSize(); auto surface=frame.Surface();
                                    auto access=surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                                    winrt::com_ptr<ID3D11Texture2D> tex; if(FAILED(access->GetInterface(IID_PPV_ARGS(tex.put())))) return;
                                    D3D11_TEXTURE2D_DESC d{}; tex->GetDesc(&d); ID3D11Texture2D* raw=tex.detach();
                                    { std::lock_guard<std::mutex> lk(impl->frameMutex); if(impl->latest) impl->latest->Release(); impl->latest=raw; impl->cW=(UINT)cs.Width; impl->cH=(UINT)cs.Height; impl->tW=d.Width; impl->tH=d.Height; impl->fmt=d.Format; impl->seq++; }
                                    int64_t exp2=-1; s=impl->startTickMs.load(std::memory_order_relaxed);
                                    impl->tFirstFrameStoredMs.compare_exchange_strong(exp2, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed);
                                }catch(...){}
                              });
                              impl->session.StartCapture();
                              { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); impl->tCaptureStartMs.store(s?(int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); }
                            }
                        } catch(...) { /* capture init failure reported later */ }
                        auto tCap1 = clock::now(); stageMs("capture", tCap0, tCap1, tt);

                        auto tNav0 = clock::now();
                        if(impl->core && !impl->initialUrl.empty()) impl->core->Navigate(impl->initialUrl.c_str());
                        auto tNav1 = clock::now(); stageMs("navReq", tNav0, tNav1, tt);
                        { uint64_t s=impl->startTickMs.load(std::memory_order_relaxed); impl->tNavReqMs.store(s?(int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed); }

                        auto totalMs = (int)std::chrono::duration_cast<std::chrono::milliseconds>(tNav1 - tThread).count();
                        impl->tInitTotalMs.store((int64_t)totalMs, std::memory_order_relaxed);
                        char totalBuf[64]; sprintf_s(totalBuf, ", total=%dms", totalMs);
                        tt += totalBuf;

                        impl->initTimingSummary = tt;
                        impl->ready.store(true);
                        m_running.store(true);
                        m_status.store(1);
                        SetDiag("Composition browser running (async init complete) | " + tt);
                        SetEvent(impl->readyEvent);
                        return S_OK;
                    }).Get());
                return S_OK;
            }).Get());

        MSG msg{};
        int urlPollTick = 0;
        while(!impl->stop.load()){
            while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){ TranslateMessage(&msg); DispatchMessageW(&msg); }
            auto runPreNavCleanup = [&](const wchar_t* reason) {
                (void)reason;
                if (!impl || !impl->core) return;
                // Best-effort cleanup before leaving heavy pages (WebGPU/canvas/media/RAF loops can otherwise appear visually stuck).
                // Avoid blanking/destroying DOM here: it can leave a black frame if navigation doesn't immediately present new content.
                try {
                    const wchar_t* cleanupScript =
                        L"(function(){try{"
                        L"try{var ms=document.querySelectorAll('video,audio');for(var i=0;i<ms.length;i++){try{var m=ms[i];m.pause();m.muted=true;try{m.srcObject=null;}catch(e){}try{m.removeAttribute('src');}catch(e){}try{m.load();}catch(e){}}catch(e){}}}catch(e){}"
                        L"try{window.requestAnimationFrame=function(){return 0};window.cancelAnimationFrame=function(){};}catch(e){}"
                        L"try{var cs=document.querySelectorAll('canvas');for(var i=0;i<cs.length;i++){try{var c=cs[i];try{var gl=c.getContext&& (c.getContext('webgl2')||c.getContext('webgl')||c.getContext('experimental-webgl')); if(gl&&gl.getExtension){var ext=gl.getExtension('WEBGL_lose_context'); if(ext&&ext.loseContext) ext.loseContext();}}catch(e){}}catch(e){}}}catch(e){}"
                        L"}catch(e){}})();";
                    impl->core->ExecuteScript(cleanupScript, nullptr);
                } catch (...) {}
                // Do not call Stop() here; Navigate/GoBack/GoForward/Reload will replace the current page.
            };

            if(impl->urlPending.exchange(false) && impl->core){
                std::wstring u; u = impl->pendingUrl;
                if(!u.empty()) {
                    runPreNavCleanup(L"navigate");
                    impl->core->Navigate(u.c_str());
                }
            }
            if (impl->core) {
                int nav = impl->pendingNavAction.exchange(0);
                if (nav == 1) {
                    BOOL can=FALSE; if (SUCCEEDED(impl->core->get_CanGoBack(&can)) && can) { runPreNavCleanup(L"back"); impl->core->GoBack(); }
                }
                else if (nav == 2) {
                    BOOL can=FALSE; if (SUCCEEDED(impl->core->get_CanGoForward(&can)) && can) { runPreNavCleanup(L"forward"); impl->core->GoForward(); }
                }
                else if (nav == 3) { runPreNavCleanup(L"reload"); impl->core->Reload(); }
            }
            if(impl->core && (++urlPollTick >= 10)){
                urlPollTick = 0;
                LPWSTR src = nullptr;
                if (SUCCEEDED(impl->core->get_Source(&src)) && src) {
                    std::lock_guard<std::mutex> lk(impl->stateMutex);
                    impl->currentUrl = src;
                    CoTaskMemFree(src);
                } else if (src) { CoTaskMemFree(src); }
            }
            if (impl->controller) {
                try {
                    double z = 1.0;
                    if (SUCCEEDED(impl->controller->get_ZoomFactor(&z))) {
                        int zm = (int)(z * 1000.0 + (z >= 0 ? 0.5 : -0.5));
                        if (zm < 100) zm = 100; if (zm > 5000) zm = 5000;
                        impl->currentZoomFactorMilli.store(zm, std::memory_order_relaxed);
                    }
                } catch (...) {}
                if (impl->zoomPending.exchange(false, std::memory_order_relaxed)) {
                    try {
                        int zm = impl->zoomFactorMilli.load(std::memory_order_relaxed);
                        if (zm < 250) zm = 250; if (zm > 5000) zm = 5000;
                        impl->zoomFactorMilli.store(zm, std::memory_order_relaxed);
                        double zf = (double)zm / 1000.0;
                        impl->controller->put_ZoomFactor(zf);
                        impl->currentZoomFactorMilli.store(zm, std::memory_order_relaxed);
                    } catch (...) {}
                }
            }
            if(impl->compController){
                // Stage 3 PoC: delayed capture start after stable init/frames; best-effort mute to avoid double audio when capturing.
                try {
                    if (impl->spatialPocEnabled.load(std::memory_order_relaxed) && !impl->stop.load(std::memory_order_relaxed)) {
                        uint32_t bpid = impl->spatialBrowserPid.load(std::memory_order_relaxed);
                        if (bpid != 0 && impl->spatialTargetPid.load(std::memory_order_relaxed) == 0)
                            impl->spatialTargetPid.store(bpid, std::memory_order_relaxed);
                        bool hasFrame = false;
                        { std::lock_guard<std::mutex> lk(impl->frameMutex); hasFrame = (impl->copied > 0 || impl->seq > 0); }
                        if (hasFrame || impl->ready.load(std::memory_order_relaxed)) impl->spatialReadyTicks.fetch_add(1, std::memory_order_relaxed);
                        int st = impl->spatialCaptureStatus.load(std::memory_order_relaxed);
                        uint64_t ticks = impl->spatialReadyTicks.load(std::memory_order_relaxed);
                        static std::atomic<uint64_t> s_lastSpatialRetryTick{0};
                        static std::atomic<uint64_t> s_lastSpatialStartTick{0};
                        // Start once after ~1.5-2s of readiness; retry periodically on failure.
                        if (ticks > 1500 && st != 2) {
                            bool shouldStart = false;
                            if (st == 0) {
                                shouldStart = true;
                            } else if (st == 1) {
                                // Avoid spamming start calls while capture thread is starting/attempting.
                                uint64_t lastS = s_lastSpatialStartTick.load(std::memory_order_relaxed);
                                if (lastS == 0 || (ticks - lastS > 20000)) { // watchdog restart if stuck >20s
                                    shouldStart = true;
                                }
                            } else if (st < 0) {
                                uint64_t last = s_lastSpatialRetryTick.load(std::memory_order_relaxed);
                                // ~10s retry interval (loop sleeps ~1ms)
                                if (last == 0 || (ticks - last > 10000)) {
                                    shouldStart = true;
                                    s_lastSpatialRetryTick.store(ticks, std::memory_order_relaxed);
                                }
                            }
                            if (shouldStart) {
                                if (st < 0) {
                                    // Preserve last failure diag for visibility; append retry note.
                                    std::string prev;
                                    {
                                        std::lock_guard<std::mutex> lk(impl->spatialDiagMutex);
                                        prev = impl->spatialDiag;
                                        if (prev.size() > 120) prev.resize(120);
                                        impl->spatialDiag = "spatial: retrying (prev: " + prev + ")";
                                    }
                                    impl->spatialCaptureStatus.store(1, std::memory_order_relaxed);
                                    // keep error counters cumulative for diagnosis
                                }
                                s_lastSpatialStartTick.store(ticks, std::memory_order_relaxed);
                                StartOrUpdateSpatialPocCapture(impl);
                            }
                        }
                        // If capture becomes active, mute WebView2 core output to prevent duplicate endpoint audio (PoC behavior).
                        if (impl->spatialCaptureStatus.load(std::memory_order_relaxed) == 2 && impl->core) {
                            try {
                                ComPtr<ICoreWebView2_8> core8;
                                if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core8))) && core8) {
                                    BOOL muted = FALSE;
                                    if (SUCCEEDED(core8->get_IsMuted(&muted)) && !muted) core8->put_IsMuted(TRUE);
                                    impl->spatialMuteApplied.store(true, std::memory_order_relaxed);
                                }
                            } catch (...) {}
                        }
                    } else if (!impl->spatialPocEnabled.load(std::memory_order_relaxed) && impl->spatialMuteApplied.load(std::memory_order_relaxed) && impl->core) {
                        try {
                            ComPtr<ICoreWebView2_8> core8;
                            if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core8))) && core8) core8->put_IsMuted(FALSE);
                        } catch (...) {}
                        impl->spatialMuteApplied.store(false, std::memory_order_relaxed);
                    }
                } catch (...) {}

                // Focus: request WebView controller focus and best-effort focus the host HWND on its own thread.
                // IMPORTANT: do not spam MoveFocus on every key batch/keystroke; repeated programmatic focus moves can
                // blur/deselect the currently focused DOM element (e.g. YouTube search input). Ensure focus on explicit
                // interactions (mouse click/wheel/managed RequestFocus) and then deliver queued input.
                if (impl->focusRequested.exchange(false)) {
                    impl->diagFocusApplied.fetch_add(1, std::memory_order_relaxed);
                    try { if (impl->controller) impl->controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC); } catch(...) {}
                    try { if (impl->hostHwnd) SetFocus(impl->hostHwnd); } catch(...) {}
                }
                std::deque<Impl::MouseEvent> mevs;
                {
                    std::lock_guard<std::mutex> lk(impl->inputMutex);
                    if(!impl->mouseQueue.empty()) mevs.swap(impl->mouseQueue);
                }
                for(const auto& e : mevs){
                    POINT pt{ e.x, e.y };
                    try {
                        impl->compController->SendMouseInput((COREWEBVIEW2_MOUSE_EVENT_KIND)e.kind,
                            (COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS)e.vk, (UINT32)e.data, pt);
                    } catch(...) {}
                }
                std::deque<Impl::KeyEvent> kevs;
                {
                    std::lock_guard<std::mutex> lk(impl->inputMutex);
                    if(!impl->keyQueue.empty()) kevs.swap(impl->keyQueue);
                }
                if (!kevs.empty() && impl->hostHwnd) {
                    // Do not force SetFocus/SetActiveWindow per key batch: it can disrupt WebView2 composition/DOM focus.
                    // Host/controller focus is applied on explicit focus requests (click/wheel/RequestFocus).
                    for (auto k : kevs) {
                        try {
                            // Build basic lParam (scan code / key transition flags) for KEYDOWN/KEYUP if missing.
                            if ((k.msg == WM_KEYDOWN || k.msg == WM_SYSKEYDOWN || k.msg == WM_KEYUP || k.msg == WM_SYSKEYUP) && k.lParam == 0) {
                                UINT scan = MapVirtualKeyW((UINT)k.wParam, MAPVK_VK_TO_VSC);
                                LPARAM lp = 1; // repeat count
                                lp |= (LPARAM)(scan & 0xFF) << 16;
                                if (k.msg == WM_KEYUP || k.msg == WM_SYSKEYUP) {
                                    lp |= (LPARAM)1 << 30; // previous key state
                                    lp |= (LPARAM)1 << 31; // transition state (release)
                                }
                                k.lParam = lp;
                            }
                            PostMessageW(impl->hostHwnd, k.msg, k.wParam, k.lParam);
                            impl->diagKeysPosted.fetch_add(1, std::memory_order_relaxed);
                        } catch(...) {}
                    }
                }

                // Text/script injection fallback: insert text or run scripts in page context via ExecuteScript.
                // Used when WM message delivery doesn't reach web inputs in composition mode.
                if (impl->core) {
                    std::deque<std::wstring> texts;
                    std::deque<std::wstring> scripts;
                    {
                        std::lock_guard<std::mutex> lk(impl->inputMutex);
                        if (!impl->textQueue.empty()) texts.swap(impl->textQueue);
                        if (!impl->scriptQueue.empty()) scripts.swap(impl->scriptQueue);
                    }
                    if (!texts.empty()) {
                        std::wstring combined;
                        for (auto& t : texts) combined += t;
                        if (!combined.empty()) {
                            auto escapeForJs = [](const std::wstring& s) -> std::wstring {
                                std::wstring o; o.reserve(s.size() + 8);
                                for (wchar_t c : s) {
                                    switch (c) {
                                        case L'\\': o += L"\\\\\\\\"; break;
                                        case L'\'': o += L"\\\\'"; break;
                                        case L'"':  o += L"\\\\\""; break;
                                        case L'\n': o += L"\\\\n"; break;
                                        case L'\r': o += L"\\\\r"; break;
                                        case L'\t': o += L"\\\\t"; break;
                                        default:
                                            if (c < 0x20) { wchar_t buf[8]; swprintf_s(buf, L"\\\\u%04x", (unsigned)c); o += buf; }
                                            else o += c;
                                            break;
                                    }
                                }
                                return o;
                            };
                            std::wstring esc = escapeForJs(combined);
                            std::wstring script = L"(function(){try{var t='";
                            script += esc;
                            script += L"';var e=document.activeElement; if(!e) return;"
                                      L"function fire(el,ev,init){try{el.dispatchEvent(new ev(''+init.type,init));}catch(_){}}"
                                      L"function fireInput(el,data){try{el.dispatchEvent(new InputEvent('beforeinput',{bubbles:true,cancelable:true,inputType:'insertText',data:data}));}catch(_){ }"
                                      L"try{el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:data}));}catch(_){try{el.dispatchEvent(new Event('input',{bubbles:true}));}catch(__){}}}"
                                      L"if(e.isContentEditable){try{document.execCommand('insertText',false,t);}catch(_){try{e.textContent=(e.textContent||'')+t;}catch(__){}} fireInput(e,t); return;}"
                                      L"var tag=(e.tagName||'').toUpperCase(); if(tag==='INPUT'||tag==='TEXTAREA'){var v=(e.value==null?'':(''+e.value)); var s=(e.selectionStart==null?v.length:e.selectionStart); var n=(e.selectionEnd==null?s:e.selectionEnd);"
                                      L"try{if(typeof e.setRangeText==='function'){e.setRangeText(t,s,n,'end');}else{e.value=v.slice(0,s)+t+v.slice(n); var p=s+t.length; e.selectionStart=p; e.selectionEnd=p;}}catch(_){try{e.value=v+t;}catch(__){}}"
                                      L"fireInput(e,t); try{e.dispatchEvent(new Event('change',{bubbles:true}));}catch(_){ } }"
                                      L"}catch(_){}})();";
                            try {
                                impl->core->ExecuteScript(script.c_str(), nullptr);
                                impl->diagTextInjected.fetch_add(1, std::memory_order_relaxed);
                            } catch (...) {}
                        }
                    }
                    if (!scripts.empty()) {
                        for (auto& sc : scripts) {
                            if (sc.empty()) continue;
                            try {
                                impl->core->ExecuteScript(sc.c_str(), nullptr);
                                impl->diagScriptExec.fetch_add(1, std::memory_order_relaxed);
                            } catch (...) {}
                        }
                    }
                }
            } else {
                std::lock_guard<std::mutex> lk(impl->inputMutex);
                impl->mouseQueue.clear();
            }

            // Distance-based WebView2 process audio attenuation (best-effort, throttled).
            try {
                uint64_t nowMs = GetTickCount64();
                bool enabled = impl->distanceAudioEnabled.load(std::memory_order_relaxed);
                int milli = impl->distanceAudioVolumeMilli.load(std::memory_order_relaxed);
                if (milli < 0) milli = 0; if (milli > 1000) milli = 1000;
                bool needApply = false;
                if (enabled) {
                    if (impl->lastDistanceAudioAppliedMilli != milli || (nowMs - impl->lastDistanceAudioApplyMs) >= 250) needApply = true;
                } else if (impl->distanceAudioNeedsRestore && (nowMs - impl->lastDistanceAudioApplyMs) >= 250) {
                    needApply = true;
                }
                if (needApply) {
                    ApplyDistanceAudioVolumeToProcessTree(impl, enabled, (float)milli / 1000.0f);
                    impl->lastDistanceAudioApplyMs = nowMs;
                    if (enabled) {
                        impl->lastDistanceAudioAppliedMilli = milli;
                        impl->distanceAudioNeedsRestore = true;
                    } else {
                        impl->lastDistanceAudioAppliedMilli = 1000;
                        impl->distanceAudioNeedsRestore = false;
                    }
                }
            } catch (...) {}

            // Wait for messages or a short timeout to reduce CPU wakeups while keeping input/nav responsive.
            MsgWaitForMultipleObjectsEx(0, nullptr, 8, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        }

        // Best-effort: restore process audio volume if distance attenuation modified it.
        try {
            ApplyDistanceAudioVolumeToProcessTree(impl, false, 1.0f);
            impl->distanceAudioEnabled.store(false, std::memory_order_relaxed);
            impl->distanceAudioVolumeMilli.store(1000, std::memory_order_relaxed);
            impl->distanceAudioNeedsRestore = false;
            impl->lastDistanceAudioAppliedMilli = 1000;
        } catch (...) {}

        // Best-effort stop playback/content before teardown (hardened to avoid lingering WebView2 processes).
        auto PumpMessagesFor = [](DWORD totalMs) {
            const ULONGLONG end = GetTickCount64() + (ULONGLONG)totalMs;
            do {
                MSG msg{};
                while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                    if (msg.message == WM_QUIT) break;
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                DWORD remaining = 0;
                ULONGLONG now = GetTickCount64();
                if (now < end) {
                    ULONGLONG rem64 = end - now;
                    remaining = rem64 > 50 ? 50 : (DWORD)rem64;
                }
                MsgWaitForMultipleObjectsEx(0, nullptr, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            } while (GetTickCount64() < end);
        };

        try {
            if (impl->core) {
                // Stop active page work/media as aggressively as possible before closing WebView2.
                const wchar_t* stopScript =
                    L"(function(){try{var v=document.querySelectorAll('video,audio');for(var i=0;i<v.length;i++){try{v[i].pause();v[i].muted=true;v[i].removeAttribute('src');v[i].load();}catch(e){}}}catch(e){}"
                    L"try{if(window.__unStopMediaInt){clearInterval(window.__unStopMediaInt);}}catch(e){}"
                    L"try{window.__unStopMediaInt=setInterval(function(){try{var v=document.querySelectorAll('video,audio');for(var i=0;i<v.length;i++){try{v[i].pause();v[i].muted=true;}catch(e){}}}catch(e){}},100);}catch(e){}"
                    L"try{var AC=window.AudioContext||window.webkitAudioContext;if(AC&&AC.prototype&&AC.prototype.close&&!AC.__unPatched){var _c=AC.prototype.close;AC.prototype.close=function(){try{return _c.apply(this,arguments);}catch(e){}};AC.__unPatched=1;}}catch(e){}"
                    L"try{if(document.exitPointerLock){document.exitPointerLock();}}catch(e){}"
                    L"try{if(document.fullscreenElement&&document.exitFullscreen){document.exitFullscreen();}}catch(e){}"
                    L"try{window.stop&&window.stop();}catch(e){}})();";
                try { impl->core->ExecuteScript(stopScript, nullptr); } catch (...) {}
                try {
                    ComPtr<ICoreWebView2_8> core8;
                    if (SUCCEEDED(impl->core->QueryInterface(IID_PPV_ARGS(&core8))) && core8) {
                        try { core8->put_IsMuted(TRUE); } catch (...) {}
                    }
                } catch (...) {}
            }
        } catch (...) {}

        // Allow script/mute requests to be processed, then navigate away and stop.
        PumpMessagesFor(100);
        try { if (impl->core) impl->core->Navigate(L"about:blank"); } catch (...) {}
        try { if (impl->core) impl->core->Stop(); } catch (...) {}
        PumpMessagesFor(150);

        try { if (impl->session) impl->session.Close(); } catch (...) {}
        try { impl->session = nullptr; } catch (...) {}
        try { if (impl->pool) impl->pool.Close(); } catch (...) {}
        try { impl->pool = nullptr; } catch (...) {}
        PumpMessagesFor(50);

        try { if (impl->controller) impl->controller->put_IsVisible(FALSE); } catch (...) {}
        try { if (impl->controller) impl->controller->Close(); } catch (...) {}
        PumpMessagesFor(250);

        // Release WebView2/WinRT objects on the browser STA thread.
        try { impl->core.Reset(); } catch (...) {}
        try { impl->compController.Reset(); } catch (...) {}
        try { impl->controller.Reset(); } catch (...) {}
        PumpMessagesFor(150);
        try { impl->env.Reset(); } catch (...) {}
        PumpMessagesFor(100);
        try { impl->webVisual = nullptr; } catch (...) {}
        try { impl->rootVisual = nullptr; } catch (...) {}
        try { impl->compositor = nullptr; } catch (...) {}
        try { impl->item = nullptr; } catch (...) {}
        try { impl->winrtDevice = nullptr; } catch (...) {}

        if (impl->hostHwnd) { DestroyWindow(impl->hostHwnd); impl->hostHwnd = nullptr; }

        // Final pump to let WebView2 process teardown/completion messages drain.
        PumpMessagesFor(150);

        // Last resort: if WebView2 browser process for this session is still alive, terminate its process tree.
        // This prevents orphaned msedgewebview2.exe processes in cases where graceful shutdown doesn't complete.
        try {
            uint32_t browserPid = impl->spatialBrowserPid.load(std::memory_order_relaxed);
            if (browserPid == 0) browserPid = impl->spatialTargetPid.load(std::memory_order_relaxed);
            if (browserPid != 0) {
                auto processExists = [](DWORD pid) -> bool {
                    if (pid == 0) return false;
                    HANDLE h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
                    if (!h) return GetLastError() != ERROR_INVALID_PARAMETER;
                    DWORD wr = WaitForSingleObject(h, 0);
                    CloseHandle(h);
                    return wr == WAIT_TIMEOUT;
                };
                auto collectTree = [](DWORD rootPid) -> std::vector<DWORD> {
                    std::vector<DWORD> out;
                    if (rootPid == 0) return out;
                    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                    if (snap == INVALID_HANDLE_VALUE) { out.push_back(rootPid); return out; }
                    std::vector<std::pair<DWORD, DWORD>> procs; // pid,parent
                    PROCESSENTRY32W pe{};
                    pe.dwSize = sizeof(pe);
                    if (Process32FirstW(snap, &pe)) {
                        do {
                            procs.push_back({ pe.th32ProcessID, pe.th32ParentProcessID });
                        } while (Process32NextW(snap, &pe));
                    }
                    CloseHandle(snap);
                    std::vector<DWORD> q; q.push_back(rootPid);
                    for (size_t i = 0; i < q.size(); ++i) {
                        DWORD cur = q[i];
                        bool seen = false;
                        for (DWORD v : out) { if (v == cur) { seen = true; break; } }
                        if (seen) continue;
                        out.push_back(cur);
                        for (auto& pp : procs) {
                            if (pp.second == cur) q.push_back(pp.first);
                        }
                    }
                    return out;
                };

                // Give graceful shutdown a brief moment to finish before forcing termination.
                if (processExists(browserPid)) {
                    PumpMessagesFor(150);
                }
                if (processExists(browserPid)) {
                    auto tree = collectTree(browserPid);
                    // Terminate children first, then root browser process.
                    for (auto it = tree.rbegin(); it != tree.rend(); ++it) {
                        DWORD pid = *it;
                        if (pid == 0 || pid == GetCurrentProcessId()) continue;
                        HANDLE h = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
                        if (h) {
                            TerminateProcess(h, 0);
                            WaitForSingleObject(h, 200);
                            CloseHandle(h);
                        }
                    }
                }
            }
        } catch (...) {}

        // Release dispatcher queue controller reference; do not force ShutdownQueueAsync from teardown path.
        try { impl->dq = nullptr; } catch (...) {}
        PumpMessagesFor(100);
    });

    // Async init: do not block Unity main thread. Thread will set status ready(1)/error(<0) and diag with per-stage timings.
    // outError remains empty on accepted start; callers should poll status/diag for progress or failure.
    m_running.store(true);
    return true;
}

void CompositionBrowser::Navigate(const std::wstring& url){ if(!m_impl) return; m_impl->pendingUrl=url; m_impl->urlPending.store(true); }
void CompositionBrowser::GoBack(){ if(!m_impl) return; m_impl->pendingNavAction.store(1); }
void CompositionBrowser::GoForward(){ if(!m_impl) return; m_impl->pendingNavAction.store(2); }
void CompositionBrowser::Reload(){ if(!m_impl) return; m_impl->pendingNavAction.store(3); }
void CompositionBrowser::SetBounds(int w,int h){ if(!m_impl) return; /* minimal: TODO post resize to browser thread */ (void)w;(void)h; }

void CompositionBrowser::SetCapturePaused(bool paused) {
    if (!m_impl) return;
    m_impl->capturePaused.store(paused, std::memory_order_relaxed);
}

void CompositionBrowser::SetAdaptiveVisualState(int captureIntervalMs, bool visualStandby) {
    if (!m_impl) return;
    if (captureIntervalMs < 0) captureIntervalMs = 0;
    m_impl->adaptiveCaptureIntervalMs.store(captureIntervalMs, std::memory_order_relaxed);
    m_impl->adaptiveVisualStandby.store(visualStandby, std::memory_order_relaxed);
    // Do not modify external capturePaused here; FrameArrived/CopyLatestTo honor both flags.
}

int CompositionBrowser::GetAdaptiveCaptureIntervalMs() const {
    if (!m_impl) return 0;
    return m_impl->adaptiveCaptureIntervalMs.load(std::memory_order_relaxed);
}

bool CompositionBrowser::IsAdaptiveVisualStandby() const {
    if (!m_impl) return false;
    return m_impl->adaptiveVisualStandby.load(std::memory_order_relaxed);
}

void CompositionBrowser::Shutdown(){
    Impl* impl = m_impl;
    if(!impl) { m_running.store(false); m_status.store(0); return; }

    // Stop Stage 3 spatial PoC capture (if running) before tearing down browser/impl.
    try {
        std::thread toJoin;
        {
            std::lock_guard<std::mutex> g(g_spatialPocGlobalMutex);
            if (g_spatialPoc) {
                g_spatialPoc->stop.store(true, std::memory_order_relaxed);
                if (g_spatialPoc->thread.joinable()) toJoin = std::move(g_spatialPoc->thread);
                delete g_spatialPoc;
                g_spatialPoc = nullptr;
            }
        }
        if (toJoin.joinable()) { try { toJoin.join(); } catch (...) {} }
    } catch (...) {}

    // Detach early so re-entrant/concurrent callers see stopped state and we avoid double teardown.
    m_impl = nullptr;
    m_running.store(false);
    m_status.store(0);

    try { impl->stop.store(true); } catch (...) {}
    try { if (impl->readyEvent) SetEvent(impl->readyEvent); } catch (...) {}

    if (impl->thread.joinable()) {
        try { impl->thread.join(); } catch (...) {}
    }

    { std::lock_guard<std::mutex> lk(impl->frameMutex); if(impl->latest){ impl->latest->Release(); impl->latest=nullptr; } }
    // release blit resources
    if(impl->cachedRtv) { impl->cachedRtv->Release(); impl->cachedRtv=nullptr; }
    if(impl->cachedRtIntermediate) { impl->cachedRtIntermediate->Release(); impl->cachedRtIntermediate=nullptr; }
    if(impl->cachedSrvIntermediate) { impl->cachedSrvIntermediate->Release(); impl->cachedSrvIntermediate=nullptr; }
    if(impl->blend) { impl->blend->Release(); impl->blend=nullptr; }
    if(impl->rs) { impl->rs->Release(); impl->rs=nullptr; }
    if(impl->cb) { impl->cb->Release(); impl->cb=nullptr; }
    if(impl->samp) { impl->samp->Release(); impl->samp=nullptr; }
    if(impl->ps) { impl->ps->Release(); impl->ps=nullptr; }
    if(impl->vs) { impl->vs->Release(); impl->vs=nullptr; }
    if(impl->readyEvent) { CloseHandle(impl->readyEvent); impl->readyEvent=nullptr; }

    delete impl;
    SetDiag("Composition browser stopped");
}

bool CompositionBrowser::CopyLatestTo(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, std::string& outDiag){
    outDiag.clear();
    if(!ctx||!target||!m_impl){ outDiag="inactive"; return false; }
    if (m_impl->adaptiveVisualStandby.load(std::memory_order_relaxed) || m_impl->capturePaused.load(std::memory_order_relaxed)) {
        outDiag = "visual standby";
        return false;
    }
    int intervalMs = m_impl->adaptiveCaptureIntervalMs.load(std::memory_order_relaxed);
    if (intervalMs > 0) {
        uint64_t now = GetTickCount64();
        uint64_t last = m_impl->lastCopyMs.load(std::memory_order_relaxed);
        if (last != 0 && now >= last && (now - last) < (uint64_t)intervalMs) {
            outDiag = "throttled";
            return false;
        }
    }
    ID3D11Texture2D* src=nullptr; UINT cW=0,cH=0,tW=0,tH=0; DXGI_FORMAT fmt=DXGI_FORMAT_UNKNOWN; uint64_t seq=0;
    { std::lock_guard<std::mutex> lk(m_impl->frameMutex); if(!m_impl->latest){ outDiag="no frame yet"; return false; } if(m_impl->copied==m_impl->seq){ outDiag="no new frame"; return false; } src=m_impl->latest; src->AddRef(); cW=m_impl->cW;cH=m_impl->cH;tW=m_impl->tW;tH=m_impl->tH;fmt=m_impl->fmt;seq=m_impl->seq; }
    D3D11_TEXTURE2D_DESC dstDesc{}; target->GetDesc(&dstDesc); D3D11_TEXTURE2D_DESC srcDesc{}; src->GetDesc(&srcDesc);
    ID3D11Device* dev=nullptr; target->GetDevice(&dev);
    if(!m_impl->blitReady){
        winrt::com_ptr<ID3DBlob> vsb,psb; HRESULT hr = D3DCompile(kVS, strlen(kVS), "vs", nullptr,nullptr,"main","vs_4_0",0,0,vsb.put(),nullptr);
        if(SUCCEEDED(hr)) hr=D3DCompile(kPS, strlen(kPS), "ps", nullptr,nullptr,"main","ps_4_0",0,0,psb.put(),nullptr);
        if(SUCCEEDED(hr)) hr=dev->CreateVertexShader(vsb->GetBufferPointer(),vsb->GetBufferSize(),nullptr,&m_impl->vs);
        if(SUCCEEDED(hr)) hr=dev->CreatePixelShader(psb->GetBufferPointer(),psb->GetBufferSize(),nullptr,&m_impl->ps);
        D3D11_SAMPLER_DESC sd{}; sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD=D3D11_FLOAT32_MAX; if(SUCCEEDED(hr)) hr=dev->CreateSamplerState(&sd,&m_impl->samp);
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth=sizeof(CB); bd.Usage=D3D11_USAGE_DYNAMIC; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE; if(SUCCEEDED(hr)) hr=dev->CreateBuffer(&bd,nullptr,&m_impl->cb);
        D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE; if(SUCCEEDED(hr)) hr=dev->CreateRasterizerState(&rd,&m_impl->rs);
        D3D11_BLEND_DESC bld{}; bld.RenderTarget[0].RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL; if(SUCCEEDED(hr)) hr=dev->CreateBlendState(&bld,&m_impl->blend);
        m_impl->blitReady = SUCCEEDED(hr) && m_impl->vs && m_impl->ps && m_impl->samp && m_impl->cb && m_impl->rs && m_impl->blend;
        if(!m_impl->blitReady){ outDiag="comp blit init failed"; src->Release(); if(dev) dev->Release(); return false; }
    }
    // Cache intermediate SRV-copy texture when source texture is not shader-readable
    ID3D11Texture2D* srvTex = src;
    if ((srcDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0) {
        bool needNewInter = (!m_impl->cachedSrvIntermediate ||
            m_impl->cachedSrcFmt != srcDesc.Format || m_impl->cachedSrcW != srcDesc.Width || m_impl->cachedSrcH != srcDesc.Height || m_impl->cachedSrcBind != srcDesc.BindFlags);
        if (needNewInter) {
            if (m_impl->cachedSrvIntermediate) { m_impl->cachedSrvIntermediate->Release(); m_impl->cachedSrvIntermediate = nullptr; }
            D3D11_TEXTURE2D_DESC tmp = srcDesc;
            tmp.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            tmp.Usage = D3D11_USAGE_DEFAULT;
            tmp.CPUAccessFlags = 0;
            tmp.MiscFlags = 0;
            tmp.MipLevels = 1; tmp.ArraySize = 1;
            if (FAILED(dev->CreateTexture2D(&tmp, nullptr, &m_impl->cachedSrvIntermediate)) || !m_impl->cachedSrvIntermediate) {
                outDiag = "srv intermediate fail"; src->Release(); if (dev) dev->Release(); return false;
            }
            m_impl->cachedSrcFmt = srcDesc.Format; m_impl->cachedSrcW = srcDesc.Width; m_impl->cachedSrcH = srcDesc.Height; m_impl->cachedSrcBind = srcDesc.BindFlags;
        }
        ctx->CopyResource(m_impl->cachedSrvIntermediate, src);
        srvTex = m_impl->cachedSrvIntermediate;
    }

    // SRV: source/intermediate texture object may change per frame, so create per call (release after draw)
    D3D11_SHADER_RESOURCE_VIEW_DESC srvd{}; srvd.Format = MakeSRV(srcDesc.Format); srvd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; srvd.Texture2D.MipLevels = 1;
    ID3D11ShaderResourceView* srv = nullptr; HRESULT hr = dev->CreateShaderResourceView(srvTex, &srvd, &srv);
    if (FAILED(hr)) { srvd.Format = srcDesc.Format; hr = dev->CreateShaderResourceView(srvTex, &srvd, &srv); }
    if (FAILED(hr) || !srv) { outDiag = "srv fail"; src->Release(); if (dev) dev->Release(); return false; }

    // Cache RTV/intermediate RT for stable destination target
    bool needNewRtv = (!m_impl->cachedRtv || m_impl->cachedTarget != target || m_impl->cachedTargetFmt != dstDesc.Format ||
        m_impl->cachedTargetW != dstDesc.Width || m_impl->cachedTargetH != dstDesc.Height || m_impl->cachedTargetBind != dstDesc.BindFlags);
    if (needNewRtv) {
        if (m_impl->cachedRtv) { m_impl->cachedRtv->Release(); m_impl->cachedRtv = nullptr; }
        if (m_impl->cachedRtIntermediate) { m_impl->cachedRtIntermediate->Release(); m_impl->cachedRtIntermediate = nullptr; }
        m_impl->cachedNeedCopyBack = false;
        m_impl->cachedTarget = target;
        m_impl->cachedTargetFmt = dstDesc.Format; m_impl->cachedTargetW = dstDesc.Width; m_impl->cachedTargetH = dstDesc.Height; m_impl->cachedTargetBind = dstDesc.BindFlags;

        ID3D11Texture2D* rtTex = target;
        if ((dstDesc.BindFlags & D3D11_BIND_RENDER_TARGET) == 0) {
            D3D11_TEXTURE2D_DESC rt = dstDesc; rt.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE; rt.Usage = D3D11_USAGE_DEFAULT; rt.CPUAccessFlags = 0; rt.MiscFlags = 0; rt.MipLevels = 1; rt.ArraySize = 1;
            if (FAILED(dev->CreateTexture2D(&rt, nullptr, &m_impl->cachedRtIntermediate)) || !m_impl->cachedRtIntermediate) {
                outDiag = "rt intermediate fail"; srv->Release(); src->Release(); if (dev) dev->Release(); return false;
            }
            rtTex = m_impl->cachedRtIntermediate;
            m_impl->cachedNeedCopyBack = true;
        }
        D3D11_RENDER_TARGET_VIEW_DESC rtvd{}; rtvd.Format = MakeRTV(dstDesc.Format); rtvd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        hr = dev->CreateRenderTargetView(rtTex, &rtvd, &m_impl->cachedRtv);
        if (FAILED(hr)) { rtvd.Format = dstDesc.Format; hr = dev->CreateRenderTargetView(rtTex, &rtvd, &m_impl->cachedRtv); }
        if (FAILED(hr) || !m_impl->cachedRtv) {
            outDiag = "rtv fail"; srv->Release(); if (m_impl->cachedRtIntermediate) { m_impl->cachedRtIntermediate->Release(); m_impl->cachedRtIntermediate = nullptr; } src->Release(); if (dev) dev->Release(); return false;
        }
    }
    ID3D11RenderTargetView* rtv = m_impl->cachedRtv;
    bool needCopyBack = m_impl->cachedNeedCopyBack;
    ID3D11Texture2D* rtInter = m_impl->cachedRtIntermediate;
    float srcW=(cW>0&&cW<=srcDesc.Width)?(float)cW:(float)srcDesc.Width; float srcH=(cH>0&&cH<=srcDesc.Height)?(float)cH:(float)srcDesc.Height;
    bool dstSRGB=((int)dstDesc.Format==29||(int)dstDesc.Format==91);
    D3D11_MAPPED_SUBRESOURCE map{}; if(SUCCEEDED(ctx->Map(m_impl->cb,0,D3D11_MAP_WRITE_DISCARD,0,&map))){
        auto* c=(CB*)map.pData; c->srcRect[0]=0;c->srcRect[1]=0;c->srcRect[2]=srcW;c->srcRect[3]=srcH; c->dstSize[0]=(float)dstDesc.Width;c->dstSize[1]=(float)dstDesc.Height; c->srcTexSize[0]=(float)srcDesc.Width;c->srcTexSize[1]=(float)srcDesc.Height;
        c->flags[0]=0;
        // WebView2/WGC frames are sRGB-encoded. Decode to linear for correct results in linear-space pipelines.
        // For sRGB RTVs hardware re-encodes on store; for UNORM RTVs Unity/engine sampling expects linear values.
        c->flags[1]=-1.f;
        c->flags[2]=dstSRGB?1.f:0.f; // diagnostic: dst treated as sRGB-capable RTV format
        c->flags[3]=0; ctx->Unmap(m_impl->cb,0);
    } 
    ID3D11RenderTargetView* oldRTV=nullptr; ID3D11DepthStencilView* oldDSV=nullptr; ctx->OMGetRenderTargets(1,&oldRTV,&oldDSV);
    FLOAT clear[4]={0,0,0,1}; ctx->ClearRenderTargetView(rtv,clear); ID3D11RenderTargetView* rtvs[]={rtv}; ctx->OMSetRenderTargets(1,rtvs,nullptr);
    D3D11_VIEWPORT vp{}; vp.Width=(FLOAT)dstDesc.Width; vp.Height=(FLOAT)dstDesc.Height; vp.MaxDepth=1; ctx->RSSetViewports(1,&vp);
    ctx->IASetInputLayout(nullptr); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(m_impl->vs,nullptr,0); ctx->PSSetShader(m_impl->ps,nullptr,0);
    ID3D11SamplerState* ss[]={m_impl->samp}; ctx->PSSetSamplers(0,1,ss); ID3D11ShaderResourceView* srvs[]={srv}; ctx->PSSetShaderResources(0,1,srvs); ID3D11Buffer* cbs[]={m_impl->cb}; ctx->PSSetConstantBuffers(0,1,cbs);
    float bf[4]={0,0,0,0}; ctx->OMSetBlendState(m_impl->blend,bf,0xFFFFFFFF); ctx->RSSetState(m_impl->rs); ctx->Draw(3,0);
    ID3D11ShaderResourceView* n0[]={nullptr}; ctx->PSSetShaderResources(0,1,n0); ID3D11RenderTargetView* n1[]={nullptr}; ctx->OMSetRenderTargets(1,n1,nullptr);
    ctx->OMSetRenderTargets(1,&oldRTV,oldDSV); if(oldRTV) oldRTV->Release(); if(oldDSV) oldDSV->Release();
    if(needCopyBack && rtInter) ctx->CopyResource(target, rtInter);
    srv->Release();
    src->Release();
    if(dev) dev->Release();
    // cachedRtv/rtInter/srvInter are reused across frames; released in Shutdown() or on desc change
    { std::lock_guard<std::mutex> lk(m_impl->frameMutex); m_impl->copied=seq; }
    m_impl->lastCopyMs.store(GetTickCount64(), std::memory_order_relaxed);
    {
        // Only record first blit when a real WebView2 frame has arrived
        if (m_impl->seq > 0 || m_impl->tFirstFrameArrivedMs.load(std::memory_order_relaxed) >= 0) {
            int64_t e=-1; uint64_t s=m_impl->startTickMs.load(std::memory_order_relaxed);
            m_impl->tFirstUnityBlitMs.compare_exchange_strong(e, s? (int64_t)(GetTickCount64()-s):-1, std::memory_order_relaxed);
        }
    }
    // Throttle expensive diag string formatting/storage (runs on render thread after every copied frame).
    // Keep a lightweight "comp blit ok" diag between refreshes so managed first-frame detection remains reliable.
    uint64_t nowDiagMs = GetTickCount64();
    bool buildFullDiag = (m_impl->lastFullDiagBuildMs == 0) || (nowDiagMs - m_impl->lastFullDiagBuildMs >= 250);
    if (!buildFullDiag) {
        outDiag = "comp blit ok";
        SetDiag(outDiag);
    } else {
        m_impl->lastFullDiagBuildMs = nowDiagMs;
        char info[1024]; sprintf_s(info, "comp blit ok | content=%ux%u tex=%ux%u srcFmt=%d dstFmt=%d dst=%ux%u | in: focusReq=%llu focusApp=%llu keyQ=%llu keyPost=%llu textQ=%llu textInj=%llu scriptQ=%llu scriptExec=%llu md=%llu mu=%llu mw=%llu lastKey=%u/%llu nativeBlocked=%llu | audio: known=%d playing=%d muted=%d",
            cW,cH,srcDesc.Width,srcDesc.Height,(int)srcDesc.Format,(int)dstDesc.Format,dstDesc.Width,dstDesc.Height,
            (unsigned long long)m_impl->diagFocusReqSet.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagFocusApplied.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagKeysQueued.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagKeysPosted.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagTextQueued.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagTextInjected.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagScriptQueued.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagScriptExec.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagMouseDownQueued.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagMouseUpQueued.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagMouseWheelQueued.load(std::memory_order_relaxed),
            (unsigned)m_impl->diagLastKeyMsg.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagLastKeyWParam.load(std::memory_order_relaxed),
            (unsigned long long)m_impl->diagNativeBlockedRequests.load(std::memory_order_relaxed),
            m_impl->diagAudioStateKnown.load(std::memory_order_relaxed),
            m_impl->diagDocumentPlayingAudio.load(std::memory_order_relaxed),
            m_impl->diagIsMuted.load(std::memory_order_relaxed));
        std::string spatial;
        {
            std::lock_guard<std::mutex> lk(m_impl->spatialDiagMutex);
            spatial = m_impl->spatialDiag;
        }
        if (!spatial.empty()) {
            // Append compact spatial PoC diagnostics (Gate A instrumentation). No behavior change.
            char sfx[320];
            sprintf_s(sfx, " | %s st=%d pid=%u pk=%llu fr=%llu by=%llu und=%llu err=%llu fmt=%u/%uch",
                spatial.c_str(),
                m_impl->spatialCaptureStatus.load(std::memory_order_relaxed),
                (unsigned)m_impl->spatialTargetPid.load(std::memory_order_relaxed),
                (unsigned long long)m_impl->spatialPackets.load(std::memory_order_relaxed),
                (unsigned long long)m_impl->spatialFrames.load(std::memory_order_relaxed),
                (unsigned long long)m_impl->spatialBytes.load(std::memory_order_relaxed),
                (unsigned long long)m_impl->spatialUnderruns.load(std::memory_order_relaxed),
                (unsigned long long)m_impl->spatialErrors.load(std::memory_order_relaxed),
                (unsigned)m_impl->spatialSampleRate.load(std::memory_order_relaxed),
                (unsigned)m_impl->spatialChannels.load(std::memory_order_relaxed));
            strncat_s(info, sfx, _TRUNCATE);
        }
        std::string full = info;
        if (m_impl) {
            char st[440];
            sprintf_s(st, " | startup: envReq=%lld envCb=%lld ctrlReq=%lld ctrlCb=%lld visual=%lld capStart=%lld navReq=%lld navStart=%lld content=%lld dom=%lld navDone=%lld navSt=%lld firstFrame=%lld firstStore=%lld firstBlit=%lld extGetReq=%lld extGetCb=%lld extPass=%lld initTotal=%lld",
                (long long)m_impl->tEnvReqMs.load(std::memory_order_relaxed),
                (long long)m_impl->tEnvCbMs.load(std::memory_order_relaxed),
                (long long)m_impl->tCtrlReqMs.load(std::memory_order_relaxed),
                (long long)m_impl->tCtrlCbMs.load(std::memory_order_relaxed),
                (long long)m_impl->tVisualDoneMs.load(std::memory_order_relaxed),
                (long long)m_impl->tCaptureStartMs.load(std::memory_order_relaxed),
                (long long)m_impl->tNavReqMs.load(std::memory_order_relaxed),
                (long long)m_impl->tNavStartingMs.load(std::memory_order_relaxed),
                (long long)m_impl->tContentLoadingMs.load(std::memory_order_relaxed),
                (long long)m_impl->tDomContentLoadedMs.load(std::memory_order_relaxed),
                (long long)m_impl->tNavCompletedMs.load(std::memory_order_relaxed),
                (long long)m_impl->tNavCompletedStatus.load(std::memory_order_relaxed),
                (long long)m_impl->tFirstFrameArrivedMs.load(std::memory_order_relaxed),
                (long long)m_impl->tFirstFrameStoredMs.load(std::memory_order_relaxed),
                (long long)m_impl->tFirstUnityBlitMs.load(std::memory_order_relaxed),
                (long long)m_impl->tExtGetReqMs.load(std::memory_order_relaxed),
                (long long)m_impl->tExtGetCbMs.load(std::memory_order_relaxed),
                (long long)m_impl->tExtInstallPassDoneMs.load(std::memory_order_relaxed),
                (long long)m_impl->tInitTotalMs.load(std::memory_order_relaxed));
            full += st;
        }
        if (m_impl && !m_impl->initTimingSummary.empty()) { full += " | init: "; full += m_impl->initTimingSummary; }
        if (m_impl) {
            std::string ext;
            try { std::lock_guard<std::mutex> lk(m_impl->extDiagMutex); ext = m_impl->extDiag; } catch (...) {}
            if (!ext.empty()) { full += " | ext: "; full += ext; }
        }
        outDiag = full; SetDiag(outDiag);
    }
    return true;
}

void CompositionBrowser::GetContentSize(UINT& w, UINT& h) const { w=0;h=0; if(!m_impl) return; std::lock_guard<std::mutex> lk(m_impl->frameMutex); w=m_impl->cW?m_impl->cW:m_impl->tW; h=m_impl->cH?m_impl->cH:m_impl->tH; }

std::wstring CompositionBrowser::GetCurrentUrl() const {
    if(!m_impl) return L"";
    std::lock_guard<std::mutex> lk(m_impl->stateMutex);
    return m_impl->currentUrl;
}

bool CompositionBrowser::IsFullscreen() const {
    if(!m_impl) return false;
    return m_impl->isFullscreen.load(std::memory_order_relaxed);
}

void CompositionBrowser::SendMouseMove(int x,int y,bool leftButton,bool rightButton){
    if(!m_impl) return;
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    Impl::MouseEvent e{};
    e.kind = COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE;
    uint32_t vk = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;
    if (leftButton) vk |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON;
    if (rightButton) vk |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON;
    e.vk = (COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS)vk;
    e.data = 0; e.x = x; e.y = y;
    // Coalesce consecutive moves to reduce input latency/flooding.
    if (!m_impl->mouseQueue.empty() && m_impl->mouseQueue.back().kind == COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE) {
        m_impl->mouseQueue.back() = e;
    } else {
        m_impl->mouseQueue.push_back(e);
    }
}
void CompositionBrowser::SendMouseDown(int x,int y,bool rightButton){
    if(!m_impl) return;
    // Do not force host/controller focus on every click: repeated SetFocus/MoveFocus(PROGRAMMATIC)
    // can dismiss web popups/menus (e.g. YouTube settings/quality). Managed side requests focus on
    // explicit focus transitions when needed.
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    // Position first, then deliver button-down.
    uint32_t moveVk = rightButton ? COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON : COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;
    Impl::MouseEvent m{}; m.kind = COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE; m.vk = (COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS)moveVk; m.data=0; m.x=x; m.y=y; m_impl->mouseQueue.push_back(m);
    Impl::MouseEvent e{};
    e.kind = rightButton ? COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOWN : COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOWN;
    e.vk = (COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS)(rightButton ? COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON : COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON);
    e.data=0; e.x=x; e.y=y; m_impl->mouseQueue.push_back(e);
    m_impl->diagMouseDownQueued.fetch_add(1, std::memory_order_relaxed);
}
void CompositionBrowser::SendMouseUp(int x,int y,bool rightButton){
    if(!m_impl) return;
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    // Position first, then deliver button-up (button no longer pressed).
    Impl::MouseEvent m{}; m.kind = COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE; m.vk = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE; m.data=0; m.x=x; m.y=y; m_impl->mouseQueue.push_back(m);
    Impl::MouseEvent e{};
    e.kind = rightButton ? COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_UP : COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_UP;
    e.vk = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;
    e.data=0; e.x=x; e.y=y; m_impl->mouseQueue.push_back(e);
    m_impl->diagMouseUpQueued.fetch_add(1, std::memory_order_relaxed);
}
void CompositionBrowser::SendMouseWheel(int x,int y,int delta){
    if(!m_impl) return;
    // Avoid forcing MoveFocus/SetFocus on every wheel event (can disrupt web menus/popups). Managed side
    // requests focus on initial interaction transitions when needed.
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    // Ensure pointer position/hit target is current before wheel delivery.
    Impl::MouseEvent m{}; m.kind = COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE; m.vk = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE; m.data = 0; m.x = x; m.y = y;
    if (!m_impl->mouseQueue.empty() && m_impl->mouseQueue.back().kind == COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE) m_impl->mouseQueue.back() = m;
    else m_impl->mouseQueue.push_back(m);
    Impl::MouseEvent e{}; e.kind = COREWEBVIEW2_MOUSE_EVENT_KIND_WHEEL; e.vk = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE; e.data = (unsigned int)delta; e.x=x; e.y=y; m_impl->mouseQueue.push_back(e);
    m_impl->diagMouseWheelQueued.fetch_add(1, std::memory_order_relaxed);
}

void CompositionBrowser::SendKey(uint32_t msg, uint64_t wParam, uint64_t lParam){
    if(!m_impl) return;
    // Do not force focusRequested/MoveFocus on every key event: that can blur the active DOM element while typing.
    // Managed side requests focus on explicit web interactions (click/wheel/focus transitions).
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    Impl::KeyEvent k{}; k.msg = (UINT)msg; k.wParam = (WPARAM)wParam; k.lParam = (LPARAM)lParam;
    m_impl->keyQueue.push_back(k);
    m_impl->diagKeysQueued.fetch_add(1, std::memory_order_relaxed);
    m_impl->diagLastKeyMsg.store((uint32_t)msg, std::memory_order_relaxed);
    m_impl->diagLastKeyWParam.store((uint64_t)wParam, std::memory_order_relaxed);
}

void CompositionBrowser::SendText(const std::wstring& text){
    if(!m_impl || text.empty()) return;
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    m_impl->textQueue.push_back(text);
    m_impl->diagTextQueued.fetch_add(1, std::memory_order_relaxed);
}

void CompositionBrowser::ExecuteScript(const std::wstring& script){
    if(!m_impl || script.empty()) return;
    std::lock_guard<std::mutex> lk(m_impl->inputMutex);
    m_impl->scriptQueue.push_back(script);
    m_impl->diagScriptQueued.fetch_add(1, std::memory_order_relaxed);
}

void CompositionBrowser::RequestFocus(){
    if(!m_impl) return;
    m_impl->diagFocusReqSet.fetch_add(1, std::memory_order_relaxed);
    m_impl->focusRequested.store(true);
}

void CompositionBrowser::SetZoomFactor(double zoomFactor) {
    if (!m_impl) return;
    if (!(zoomFactor > 0.0)) zoomFactor = 1.0;
    if (zoomFactor < 0.25) zoomFactor = 0.25;
    if (zoomFactor > 5.0) zoomFactor = 5.0;
    int zm = (int)(zoomFactor * 1000.0 + 0.5);
    if (zm < 250) zm = 250;
    if (zm > 5000) zm = 5000;
    m_impl->zoomFactorMilli.store(zm, std::memory_order_relaxed);
    m_impl->zoomPending.store(true, std::memory_order_relaxed);
}

double CompositionBrowser::GetZoomFactor() const {
    if (!m_impl) return 1.0;
    int zm = m_impl->currentZoomFactorMilli.load(std::memory_order_relaxed);
    if (zm <= 0) zm = 1000;
    return (double)zm / 1000.0;
}

static std::vector<uint32_t> CollectProcessTreePids(uint32_t rootPid)
{
    std::vector<uint32_t> result;
    if (rootPid == 0) return result;
    result.push_back(rootPid);

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return result;

    struct Node { uint32_t pid; uint32_t ppid; };
    std::vector<Node> procs;
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe)) {
        do {
            procs.push_back({ pe.th32ProcessID, pe.th32ParentProcessID });
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);

    std::vector<uint32_t> queue;
    queue.push_back(rootPid);
    for (size_t qi = 0; qi < queue.size(); ++qi) {
        uint32_t cur = queue[qi];
        for (const auto& n : procs) {
            if (n.ppid != cur || n.pid == 0) continue;
            bool seen = false;
            for (auto v : result) { if (v == n.pid) { seen = true; break; } }
            if (!seen) {
                result.push_back(n.pid);
                queue.push_back(n.pid);
            }
        }
    }
    return result;
}

static void ApplyDistanceAudioVolumeToProcessTree(CompositionBrowser::Impl* impl, bool enabled, float volume01)
{
    if (!impl) return;
    if (volume01 < 0.0f) volume01 = 0.0f;
    if (volume01 > 1.0f) volume01 = 1.0f;

    uint32_t browserPid = impl->spatialBrowserPid.load(std::memory_order_relaxed);
    if (browserPid == 0) {
        // Fallback: target pid field may be used in some contexts; prefer browser pid for this feature.
        browserPid = impl->spatialTargetPid.load(std::memory_order_relaxed);
    }
    if (browserPid == 0) return;

    auto pids = CollectProcessTreePids(browserPid);
    if (pids.empty()) return;

    auto pidInTree = [&pids](DWORD pid) -> bool {
        for (auto p : pids) { if (p == pid) return true; }
        return false;
    };

    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (FAILED(hr) || !enumerator) return;

    ComPtr<IMMDevice> device;
    hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    if (FAILED(hr) || !device) return;

    ComPtr<IAudioSessionManager2> sessionManager;
    hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, (void**)sessionManager.GetAddressOf());
    if (FAILED(hr) || !sessionManager) return;

    ComPtr<IAudioSessionEnumerator> sessionEnum;
    hr = sessionManager->GetSessionEnumerator(&sessionEnum);
    if (FAILED(hr) || !sessionEnum) return;

    int count = 0;
    if (FAILED(sessionEnum->GetCount(&count)) || count <= 0) return;

    const float targetVol = enabled ? volume01 : 1.0f;
    for (int i = 0; i < count; ++i) {
        ComPtr<IAudioSessionControl> sessionControl;
        if (FAILED(sessionEnum->GetSession(i, &sessionControl)) || !sessionControl) continue;

        ComPtr<IAudioSessionControl2> sessionControl2;
        if (FAILED(sessionControl.As(&sessionControl2)) || !sessionControl2) continue;

        DWORD sessionPid = 0;
        if (FAILED(sessionControl2->GetProcessId(&sessionPid))) continue;
        if (sessionPid == 0 || !pidInTree(sessionPid)) continue;

        ComPtr<ISimpleAudioVolume> simpleVol;
        if (FAILED(sessionControl.As(&simpleVol)) || !simpleVol) continue;

        float current = 1.0f;
        if (SUCCEEDED(simpleVol->GetMasterVolume(&current))) {
            if (fabsf(current - targetVol) > 0.01f) {
                try { simpleVol->SetMasterVolume(targetVol, nullptr); } catch (...) {}
            }
        } else {
            try { simpleVol->SetMasterVolume(targetVol, nullptr); } catch (...) {}
        }
    }
}

void CompositionBrowser::SetDistanceAudioVolume(bool enabled, float volume01)
{
    if (volume01 < 0.0f) volume01 = 0.0f;
    if (volume01 > 1.0f) volume01 = 1.0f;
    int milli = (int)(volume01 * 1000.0f + 0.5f);
    if (milli < 0) milli = 0;
    if (milli > 1000) milli = 1000;

    if (!m_impl) {
        // No impl yet; nothing to apply now. Managed side should re-send when active.
        return;
    }

    m_impl->distanceAudioEnabled.store(enabled, std::memory_order_relaxed);
    m_impl->distanceAudioVolumeMilli.store(milli, std::memory_order_relaxed);
    if (enabled) m_impl->distanceAudioNeedsRestore = true;

    // Apply promptly on caller thread best-effort (managed calls are fine; COM calls are lightweight and this is test feature).
    // Also applied periodically from browser thread loop for ongoing attenuation/restore.
    try { ApplyDistanceAudioVolumeToProcessTree(m_impl, enabled, (float)milli / 1000.0f); } catch (...) {}
    m_impl->lastDistanceAudioApplyMs = GetTickCount64();
    m_impl->lastDistanceAudioAppliedMilli = enabled ? milli : 1000;
}

