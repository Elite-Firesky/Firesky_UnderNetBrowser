#pragma once
#include <d3d11.h>
#include <windows.h>
#include <atomic>
#include <mutex>
#include <string>

// Minimal Phase 4A WGC capture helper.
// - Uses Unity's D3D11 device (obtained from Unity-owned texture path).
// - Captures an HWND into a frame pool and keeps the latest frame texture.
// - Unity render thread copies latest frame into Unity-owned target texture (GPU copy).
//
// Notes:
// - WGC commonly provides BGRA8 frames. Format/size mismatches are handled best-effort.
// - Frame callback runs on WGC's thread; latest frame access is synchronized.
// - This is a PoC for milestone A/B before WebView2 integration.

class WgcCapture {
public:
    WgcCapture() = default;
    ~WgcCapture() { Stop(); }

    // Call on Unity render thread (has valid D3D11 device/context). Requires WinRT init success.
    bool Start(HWND hwnd, ID3D11Device* unityDevice, ID3D11DeviceContext* unityContext, std::string& outError);
    void Stop();
    bool IsActive() const { return m_active.load(); }

    // Call on Unity render thread: copies latest captured frame into target texture if available.
    // Returns true if a copy was attempted/performed this call.
    bool CopyLatestTo(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, std::string& outDiag);

    int GetStatusCode() const { return m_status.load(); }
    std::string GetDiag() const { std::lock_guard<std::mutex> lk(m_diagMutex); return m_diag; }
    void GetLatestFrameSize(UINT& w, UINT& h) const;

private:
    void SetDiag(const std::string& s);
    void SetError(const std::string& s);

    std::atomic<bool> m_active{ false };
    std::atomic<int> m_status{ 0 }; // 0 stopped, 1 active, <0 error
    mutable std::mutex m_diagMutex;
    std::string m_diag;

    // WGC/WinRT objects are kept opaque as void* + implementation in .cpp to avoid winrt headers leaking everywhere.
    struct Impl;
    Impl* m_impl = nullptr;
};
