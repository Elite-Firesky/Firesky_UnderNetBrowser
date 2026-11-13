#pragma once
#include <windows.h>
#include <d3d11.h>
#include <string>
#include <mutex>
#include <atomic>

// Hosts WebView2 via CompositionController (RootVisualTarget + WinRT composition visual),
// captures frames with WGC CreateFromVisual, and provides GPU copy into a D3D11 target texture.
// Designed for integration with Unity render-thread texture updates (no CPU readback).
class CompositionBrowser {
public:
    CompositionBrowser() = default;
    ~CompositionBrowser() { Shutdown(); }

    // Called from render thread with valid Unity D3D11 device/context. Starts internal STA thread for WebView2/WinRT.
    // userDataFolder: absolute path for WebView2 user data/profile (must be inside mod/user data; avoids default exe-adjacent profile).
    // additionalBrowserArguments: optional Chromium/WebView2 switches for environment creation (testing/advanced).
    bool Start(ID3D11Device* device, ID3D11DeviceContext* context, int width, int height, const std::wstring& initialUrl, const std::wstring& userDataFolder, const std::wstring& additionalBrowserArguments, std::string& outError);
    // Thread-safe request; actual navigation occurs on browser thread.
    void Navigate(const std::wstring& url);
    void GoBack();
    void GoForward();
    void Reload();
    void SetBounds(int width, int height);
    void Shutdown();

    // Pause/resume WGC capture to reduce CPU/GPU/background work while keeping WebView2 session alive (warm reopen).
    void SetCapturePaused(bool paused);

    // Render thread: copy latest captured frame into target texture. Returns true if a new frame was copied.
    bool CopyLatestTo(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, std::string& outDiag);

    bool IsRunning() const { return m_running.load(); }
    int GetStatusCode() const { return m_status.load(); } // 0 init/stopped, 1 running, <0 error
    std::string GetDiag() const { std::lock_guard<std::mutex> lk(m_diagMutex); return m_diag; }
    std::wstring GetCurrentUrl() const;
    bool IsFullscreen() const;
    void GetContentSize(UINT& w, UINT& h) const;

    // Input forwarding (thread-safe; queued and processed on browser thread)
    void SendMouseMove(int x, int y, bool leftButton, bool rightButton = false);
    void SendMouseDown(int x, int y, bool rightButton = false);
    void SendMouseUp(int x, int y, bool rightButton = false);
    void SendMouseWheel(int x, int y, int delta);
    void SendKey(uint32_t msg, uint64_t wParam, uint64_t lParam);
    // Injects text into the currently focused DOM element via script (fallback for composition keyboard delivery issues).
    void SendText(const std::wstring& text);
    // Queues JavaScript for execution in the page (fallback for key/action delivery issues in composition hosting).
    void ExecuteScript(const std::wstring& script);
    void RequestFocus();

    // Stage 3 Gate A PoC (dev-only, experimental): WASAPI process-loopback capture diagnostics/control.
    // No effect on rendering/input/lifecycle behavior when disabled.
    void SetSpatialAudioPoc(bool enabled, uint32_t targetPid);
    // Spatial audio playback support (PoC): format/read captured PCM (float32 interleaved) for Unity streaming AudioSource.
    void GetSpatialAudioFormat(uint32_t& sampleRate, uint32_t& channels) const;
    uint32_t ReadSpatialAudioSamples(float* dstInterleaved, uint32_t maxFrames);
    bool IsSpatialCaptureActive() const;

    // Best-effort: attenuate default-render audio sessions belonging to this WebView2 browser process tree.
    // volume01 is clamped to [0,1]. When disabled, restores affected sessions to 1.0.
    void SetDistanceAudioVolume(bool enabled, float volume01);

    struct Impl;
private:
    Impl* m_impl = nullptr;
    std::atomic<bool> m_running{ false };
    std::atomic<int> m_status{ 0 };
    mutable std::mutex m_diagMutex;
    std::string m_diag;
    // Spatial PoC config can be set before impl/Start; keep pending and apply when impl is ready.
    std::atomic<bool> m_spatialPocPendingEnabled{ false };
    std::atomic<uint32_t> m_spatialPocPendingTargetPid{ 0 };
    std::atomic<bool> m_spatialPocPendingSet{ false };

    void SetDiag(const std::string& s);
    void SetError(const std::string& s);
};
