// Standalone PoC scaffold: WebView2 CompositionController + WinRT Composition + WGC CreateFromVisual -> D3D11 texture.
// Objective: continuously updating browser pixels delivered to D3D11 without CPU readback and without a visible ordinary browser window.
// STATUS: implementation attempt for build/test by user; NOT verified in this environment.
//
// Proof strategy:
// - Hidden parent HWND for CompositionController (not a visible browser window).
// - Separate visible proof window displays captured D3D11 frames copied to its swapchain (demonstrates GPU texture consumption).
// - WebView2 renders into a WinRT Composition visual (RootVisualTarget); WGC captures from that visual (CreateFromVisual).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <DispatcherQueue.h>
#include <wrl.h>
#include <string>
#include <fstream>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <dispatcherqueue.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.UI.Composition.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <WebView2.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "ole32.lib")

using namespace Microsoft::WRL;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgdx = winrt::Windows::Graphics::DirectX;
namespace d3d = winrt::Windows::Graphics::DirectX::Direct3D11;
namespace comp = winrt::Windows::UI::Composition;

struct AppState {
    HWND proofHwnd = nullptr;   // visible window showing captured frames (proof display)
    HWND hostHwnd = nullptr;    // hidden parent for CompositionController
    int width = 1280, height = 720;

    ComPtr<ID3D11Device> d3dDevice;
    ComPtr<ID3D11DeviceContext> d3dCtx;
    ComPtr<IDXGISwapChain1> swapChain;

    ABI::Windows::System::IDispatcherQueueController* dq{ nullptr };
    comp::Compositor compositor{ nullptr };
    comp::ContainerVisual rootVisual{ nullptr };
    comp::ContainerVisual webVisual{ nullptr };

    ComPtr<ICoreWebView2Environment> env;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2CompositionController> compController;
    ComPtr<ICoreWebView2> webview;

    d3d::IDirect3DDevice winrtDevice{ nullptr };
    wgc::GraphicsCaptureItem captureItem{ nullptr };
    wgc::Direct3D11CaptureFramePool framePool{ nullptr };
    wgc::GraphicsCaptureSession session{ nullptr };

    uint64_t frameCount = 0;
    uint64_t copyCount = 0;
    bool captureStarted = false;
};

static AppState g;

static void Log(const wchar_t* msg) {
    OutputDebugStringW(msg);
    OutputDebugStringW(L"\n");
    try {
        std::wofstream f(L"CompositionPoC.log", std::ios::app);
        if (f.is_open()) f << msg << L"\n";
    } catch (...) {}
}

static LRESULT CALLBACK ProofWndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}
static LRESULT CALLBACK HostWndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

static bool InitD3D() {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION,
        g.d3dDevice.GetAddressOf(), &fl, g.d3dCtx.GetAddressOf());
    if (FAILED(hr)) return false;

    ComPtr<IDXGIDevice> dxgiDev; g.d3dDevice.As(&dxgiDev);
    ComPtr<IDXGIAdapter> adapter; dxgiDev->GetAdapter(&adapter);
    ComPtr<IDXGIFactory2> factory; adapter->GetParent(IID_PPV_ARGS(&factory));

    DXGI_SWAP_CHAIN_DESC1 scd{};
    scd.Width = g.width; scd.Height = g.height;
    scd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.BufferCount = 2;
    scd.SampleDesc.Count = 1;
    scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    hr = factory->CreateSwapChainForHwnd(g.d3dDevice.Get(), g.proofHwnd, &scd, nullptr, nullptr, &g.swapChain);
    if (FAILED(hr)) return false;

    winrt::com_ptr<::IInspectable> inspectable;
    hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDev.Get(), inspectable.put());
    if (FAILED(hr)) return false;
    g.winrtDevice = inspectable.as<d3d::IDirect3DDevice>();
    return true;
}

static bool StartCaptureFromVisual() {
    try {
        if (!g.rootVisual || !g.winrtDevice) { Log(L"[PoC] StartCaptureFromVisual: missing rootVisual or winrtDevice"); return false; }
        // WinRT API (projection available in SDK headers): capture from composition visual.
        g.captureItem = wgc::GraphicsCaptureItem::CreateFromVisual(g.rootVisual);
        if (!g.captureItem) { Log(L"[PoC] CreateFromVisual returned null item"); return false; }
        auto sz = g.captureItem.Size();
        wchar_t buf[256]; wsprintfW(buf, L"[PoC] CreateFromVisual item size=%dx%d", sz.Width, sz.Height); Log(buf);
        g.framePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            g.winrtDevice, wgdx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, sz);
        g.session = g.framePool.CreateCaptureSession(g.captureItem);
        try { g.session.IsCursorCaptureEnabled(false); } catch (...) {}
        g.session.StartCapture();
        g.captureStarted = true;
        Log(L"[PoC] WGC CreateFromVisual capture started (hidden host HWND).");
        return true;
    } catch (winrt::hresult_error const& e) {
        wchar_t buf[512]; wsprintfW(buf, L"[PoC] StartCaptureFromVisual failed hr=0x%08X msg=%s", (unsigned)e.code(), e.message().c_str());
        Log(buf);
        return false;
    } catch (...) {
        Log(L"[PoC] StartCaptureFromVisual failed: unknown exception");
        return false;
    }
}

static void TickCaptureAndPresent() {
    if (!g.captureStarted || !g.framePool) return;
    try {
        auto frame = g.framePool.TryGetNextFrame();
        if (!frame) return;
        g.frameCount++;
        auto surface = frame.Surface();
        auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> src;
        if (FAILED(access->GetInterface(IID_PPV_ARGS(&src)))) return;
        ComPtr<ID3D11Texture2D> back;
        if (FAILED(g.swapChain->GetBuffer(0, IID_PPV_ARGS(&back)))) return;

        D3D11_TEXTURE2D_DESC sd{}, bd{}; src->GetDesc(&sd); back->GetDesc(&bd);
        if (sd.Width == bd.Width && sd.Height == bd.Height) {
            g.d3dCtx->CopyResource(back.Get(), src.Get());
        } else {
            // Size mismatch: copy what we can (center/top-left) for proof; real integration should scale/blit.
            D3D11_BOX box{}; box.left = 0; box.top = 0; box.front = 0;
            box.right = sd.Width < bd.Width ? sd.Width : bd.Width;
            box.bottom = sd.Height < bd.Height ? sd.Height : bd.Height;
            box.back = 1;
            g.d3dCtx->CopySubresourceRegion(back.Get(), 0, 0, 0, 0, src.Get(), 0, &box);
        }
        g.swapChain->Present(1, 0);
        g.copyCount++;
        if (g.copyCount == 1 || (g.copyCount % 120) == 0) {
            wchar_t buf[256];
            swprintf_s(buf, L"[PoC] frames=%I64u copies=%I64u src=%ux%u dst=%ux%u", (unsigned long long)g.frameCount, (unsigned long long)g.copyCount, sd.Width, sd.Height, bd.Width, bd.Height);
            Log(buf);
        }
    } catch (...) {
        // ignore transient frame errors
    }
}

static void InitWebView2() {
    CreateCoreWebView2EnvironmentWithOptions(nullptr, nullptr, nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
            [](HRESULT hr, ICoreWebView2Environment* env) -> HRESULT {
                if (FAILED(hr) || !env) return hr;
                g.env = env;
                ComPtr<ICoreWebView2Environment3> env3;
                if (FAILED(env->QueryInterface(IID_PPV_ARGS(&env3)))) {
                    Log(L"[PoC] ICoreWebView2Environment3 (CompositionController) not available.");
                    return E_NOINTERFACE;
                }
                return env3->CreateCoreWebView2CompositionController(g.hostHwnd,
                    Callback<ICoreWebView2CreateCoreWebView2CompositionControllerCompletedHandler>(
                        [](HRESULT hr, ICoreWebView2CompositionController* compCtrl) -> HRESULT {
                            if (FAILED(hr) || !compCtrl) return hr;
                            g.compController = compCtrl;
                            compCtrl->QueryInterface(IID_PPV_ARGS(&g.controller));
                            if (g.controller) g.controller->get_CoreWebView2(&g.webview);

                            // WinRT composition visual tree for WebView2 output
                            g.compositor = comp::Compositor();
                            g.rootVisual = g.compositor.CreateContainerVisual();
                            g.webVisual = g.compositor.CreateContainerVisual();
                            g.rootVisual.Children().InsertAtTop(g.webVisual);
                            g.rootVisual.Size({ (float)g.width, (float)g.height });
                            g.webVisual.RelativeSizeAdjustment({ 1.0f, 1.0f });

                            // Attach WebView2 visual to our composition visual
                            auto targetAbi = winrt::get_unknown(g.webVisual);
                            g.compController->put_RootVisualTarget(targetAbi);

                            if (g.controller) {
                                RECT bounds{ 0, 0, g.width, g.height };
                                g.controller->put_Bounds(bounds);
                                g.controller->put_IsVisible(TRUE);
                            }

                            // Start WGC capture from the root composition visual (contains WebView visual child)
                            if (!StartCaptureFromVisual()) {
                                Log(L"[PoC] Capture start failed (CreateFromVisual/runtime support/window state may be incompatible).");
                            }

                            const wchar_t* html = L"data:text/html;charset=utf-8,"
                                L"%3C!doctype%20html%3E%3Chtml%3E%3Cbody%20style%3D'margin%3A0%3Bbackground%3A%23111%3Bcolor%3A%230f8%3Bfont%3A48px%20Segoe%20UI'%3E"
                                L"%3Cdiv%20id%3Dt%3E%3C%2Fdiv%3E%3Cdiv%20style%3D'font%3A22px%20Segoe%20UI%3Bcolor%3A%23ccc'%3ECompositionController%20CreateFromVisual%20PoC%3C%2Fdiv%3E"
                                L"%3Cscript%3EsetInterval(()%3D%3E%7Bt.textContent%3Dnew%20Date().toISOString()%3Bdocument.body.style.background%3D%60hsl(%24%7B(Date.now()%2F20)%25360%7D%2040%25%2012%25)%60%7D%2C16)%3C%2Fscript%3E"
                                L"%3C%2Fbody%3E%3C%2Fhtml%3E";
                            if (g.webview) g.webview->Navigate(html);
                            Log(L"[PoC] WebView2 CompositionController initialized and navigated.");
                            return S_OK;
                        }).Get());
            }).Get());
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR, int) {
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    // Dispatcher queue required for WinRT composition APIs on this thread
    DispatcherQueueOptions opt{ sizeof(DispatcherQueueOptions), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA };
    if (FAILED(CreateDispatcherQueueController(opt, &g.dq))) {
        g.dq = nullptr;
    }

    WNDCLASSW wc1{}; wc1.lpfnWndProc = ProofWndProc; wc1.hInstance = hInst; wc1.lpszClassName = L"UnderNetCompPoC_Proof"; RegisterClassW(&wc1);
    WNDCLASSW wc2{}; wc2.lpfnWndProc = HostWndProc;  wc2.hInstance = hInst; wc2.lpszClassName = L"UnderNetCompPoC_Host";  RegisterClassW(&wc2);

    g.proofHwnd = CreateWindowExW(0, wc1.lpszClassName, L"UnderNet Composition PoC - captured D3D11 output (proof)",
        WS_OVERLAPPEDWINDOW, 80, 80, g.width, g.height, nullptr, nullptr, hInst, nullptr);
    // Hidden host HWND: not a visible ordinary browser window
    g.hostHwnd = CreateWindowExW(0, wc2.lpszClassName, L"UnderNet Composition Host (hidden)",
        WS_OVERLAPPEDWINDOW, -32000, -32000, g.width, g.height, nullptr, nullptr, hInst, nullptr);

    ShowWindow(g.proofHwnd, SW_SHOW);
    ShowWindow(g.hostHwnd, SW_HIDE); // key feasibility condition to validate with CreateFromVisual

    if (!InitD3D()) { MessageBoxW(nullptr, L"D3D11 init failed", L"PoC", MB_ICONERROR); return 1; }
    InitWebView2();

    MSG msg{};
    bool running = true;
    while (running) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) { running = false; break; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        TickCaptureAndPresent();
        Sleep(1);
    }

    try { if (g.session) g.session.Close(); } catch (...) {}
    try { if (g.framePool) g.framePool.Close(); } catch (...) {}
    if (g.controller) g.controller->Close();
    if (g.dq) { /* DispatcherQueueController shutdown not strictly required for process exit */ }
    return 0;
}
