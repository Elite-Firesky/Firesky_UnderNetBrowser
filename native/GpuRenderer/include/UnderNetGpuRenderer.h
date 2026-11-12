#pragma once
#include <stdint.h>

#if defined(_WIN32)
  #define UN_EXPORT extern "C" __declspec(dllexport)
#else
  #define UN_EXPORT extern "C"
#endif

// Status codes
enum UnGpuStatus : int32_t {
    UN_GPU_OK = 0,
    UN_GPU_ERR_NOT_INITIALIZED = -1,
    UN_GPU_ERR_NO_D3D11_DEVICE = -2,
    UN_GPU_ERR_ALREADY_EXISTS = -3,
    UN_GPU_ERR_CREATE_TEXTURE = -4,
    UN_GPU_ERR_INVALID = -5,
};

// Simple diagnostics (UTF-8)
typedef void (__stdcall *UnGpuLogCallback)(int32_t level, const char* message);

UN_EXPORT void __stdcall UnGpu_SetLogCallback(UnGpuLogCallback cb);
UN_EXPORT int32_t __stdcall UnGpu_GetStatus();
UN_EXPORT void __stdcall UnGpu_GetLastError(char* buffer, int32_t bufferSize);
UN_EXPORT int32_t __stdcall UnGpu_IsUnityDeviceReady(); // 1 if Unity D3D11 device captured
UN_EXPORT int32_t __stdcall UnGpu_IsUnityPluginLoaded();  // 1 if UnityPluginLoad was called
UN_EXPORT int32_t __stdcall UnGpu_GetRenderer();          // UnityGfxRenderer value or -1
UN_EXPORT void* __stdcall UnGpu_GetTestTexturePtr();
UN_EXPORT int32_t __stdcall UnGpu_GetTestTextureWidth();
UN_EXPORT int32_t __stdcall UnGpu_GetTestTextureHeight();

// Request operations executed on Unity render thread via GL.IssuePluginEvent(eventId)
// Event IDs (legacy Unity-plugin-owned texture path)
#define UN_GPU_EVENT_CREATE_TEST_TEXTURE  1
#define UN_GPU_EVENT_UPDATE_TEST_TEXTURE  2
#define UN_GPU_EVENT_DESTROY_TEST_TEXTURE 3
// Event IDs (Unity-owned Texture2D target path: pointer provided via GetNativeTexturePtr)
#define UN_GPU_EVENT_TARGET_INIT_UPDATE   20  // validate target, acquire device/context, create src, render+copy
#define UN_GPU_EVENT_TARGET_UPDATE        21  // render+copy frame
#define UN_GPU_EVENT_TARGET_RELEASE       22  // release native-owned resources (never Unity texture)
// WGC capture (Phase 4A PoC) - uses Unity-owned target texture/device acquired from it
#define UN_GPU_EVENT_WGC_START            30  // init/start WGC capture for pending HWND (requires target ready)
#define UN_GPU_EVENT_WGC_COPY_LATEST      31  // copy latest captured frame into Unity-owned target texture
#define UN_GPU_EVENT_WGC_STOP             32  // stop capture and release WGC resources
// CompositionController browser (native-hosted WebView2 + CreateFromVisual WGC capture into Unity-owned target texture)
#define UN_GPU_EVENT_COMP_START           40  // start native composition browser (uses pending URL/size)
#define UN_GPU_EVENT_COMP_COPY_LATEST     41  // copy latest captured frame into Unity-owned target texture
#define UN_GPU_EVENT_COMP_STOP            42  // stop composition browser and release resources
#define UN_GPU_EVENT_PUMP                 10  // reserved for future capture pump

// Unity-owned target texture (managed passes Texture2D.GetNativeTexturePtr on main thread;
// native validates/uses it only on render thread callbacks)
UN_EXPORT int32_t __stdcall UnGpu_SetTargetTexture(void* nativeTexturePtr, int32_t width, int32_t height);
UN_EXPORT void __stdcall UnGpu_ClearTargetTexture();
UN_EXPORT int32_t __stdcall UnGpu_IsTargetReady(); // 1 if target validated and device/context ready for copies

// WGC test capture (Phase 4A): capture a visible HWND into the Unity-owned target texture.
// hwnd passed as integer pointer value from managed (FindWindow/GetForegroundWindow etc.).
UN_EXPORT int32_t __stdcall UnGpu_SetWgcCaptureHwnd(void* hwnd);
UN_EXPORT int32_t __stdcall UnGpu_GetWgcStatus(); // 0=stopped, 1=starting/active, negative=error
UN_EXPORT void __stdcall UnGpu_GetWgcDiag(char* buffer, int32_t bufferSize);
UN_EXPORT void __stdcall UnGpu_GetWgcFrameSize(int32_t* outWidth, int32_t* outHeight);

// Native composition browser (WebView2 CompositionController + visual capture). No visible ordinary browser window.
// Start is performed on render thread (event COMP_START) after target texture/device is ready.
UN_EXPORT int32_t __stdcall UnGpu_CompSetParams(const wchar_t* initialUrl, int32_t width, int32_t height, const wchar_t* userDataFolder);
UN_EXPORT int32_t __stdcall UnGpu_CompNavigate(const wchar_t* url);
UN_EXPORT void __stdcall UnGpu_CompGoBack();
UN_EXPORT void __stdcall UnGpu_CompGoForward();
UN_EXPORT void __stdcall UnGpu_CompReload();
UN_EXPORT int32_t __stdcall UnGpu_CompGetStatus(); // 0=stopped, 1=running, negative=error
UN_EXPORT void __stdcall UnGpu_CompGetDiag(char* buffer, int32_t bufferSize);
UN_EXPORT void __stdcall UnGpu_CompGetFrameSize(int32_t* outWidth, int32_t* outHeight);
UN_EXPORT void __stdcall UnGpu_CompSendMouse(int32_t kind, int32_t vk, uint32_t data, int32_t x, int32_t y); // kind: 0 move,1 down,2 up,3 wheel
UN_EXPORT void __stdcall UnGpu_CompGetCurrentUrl(wchar_t* buffer, int32_t bufferChars);
UN_EXPORT int32_t __stdcall UnGpu_CompIsFullscreen(); // 1=fullscreen, 0=not, -1=unavailable
UN_EXPORT void __stdcall UnGpu_CompShutdown();
UN_EXPORT void __stdcall UnGpu_CompSetCapturePaused(int32_t paused);
UN_EXPORT void __stdcall UnGpu_CompSendKey(uint32_t msg, uint64_t wParam, uint64_t lParam);
UN_EXPORT void __stdcall UnGpu_CompSendText(const wchar_t* text);
UN_EXPORT void __stdcall UnGpu_CompExecuteScript(const wchar_t* script);
UN_EXPORT void __stdcall UnGpu_CompRequestFocus();

// Stage 3 Gate A PoC (dev-only): configure experimental WASAPI process-loopback capture diagnostics.
// targetPid: WebView2 process PID to target (e.g. audio.mojom.AudioService). 0 disables target selection.
UN_EXPORT void __stdcall UnGpu_CompSetSpatialAudioPoc(int32_t enabled, uint32_t targetPid);
UN_EXPORT void __stdcall UnGpu_CompGetSpatialAudioFormat(uint32_t* sampleRate, uint32_t* channels);
UN_EXPORT uint32_t __stdcall UnGpu_CompReadSpatialAudioSamples(float* dstInterleaved, uint32_t maxFrames);
UN_EXPORT int32_t __stdcall UnGpu_CompIsSpatialCaptureActive();

// Unity plugin exports are defined in .cpp (UnityPluginLoad/Unload, GetRenderEventFunc)
UN_EXPORT void* __stdcall UnGpu_GetRenderEventFuncPtr();