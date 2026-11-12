#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>
#include <chrono>
#include <thread>
#include <atomic>
#include <csignal>
#include <cmath>
#include <iostream>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "avrt.lib")
#pragma comment(lib, "uuid.lib")

using Microsoft::WRL::ComPtr;

static std::atomic<bool> g_stop{false};
static BOOL WINAPI ConsoleCtrl(DWORD) { g_stop.store(true); return TRUE; }

struct EndpointInfo {
    std::wstring id;
    std::wstring name;
    std::wstring iface;
    EDataFlow flow = eRender;
    DWORD state = 0;
};

static std::string Narrow(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return {};
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}
static std::wstring Widen(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), n);
    return w;
}
static bool ContainsInsensitive(const std::wstring& hay, const std::wstring& needle) {
    if (needle.empty()) return true;
    auto H = hay; auto N = needle;
    std::transform(H.begin(), H.end(), H.begin(), ::towlower);
    std::transform(N.begin(), N.end(), N.begin(), ::towlower);
    return H.find(N) != std::wstring::npos;
}

static HRESULT ListEndpoints(IMMDeviceEnumerator* en, EDataFlow flow, std::vector<EndpointInfo>& out) {
    ComPtr<IMMDeviceCollection> col;
    HRESULT hr = en->EnumAudioEndpoints(flow, DEVICE_STATEMASK_ALL, &col);
    if (FAILED(hr)) return hr;
    UINT count = 0; col->GetCount(&count);
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> dev;
        if (FAILED(col->Item(i, &dev)) || !dev) continue;
        EndpointInfo ei{}; ei.flow = flow;
        dev->GetState(&ei.state);
        LPWSTR id = nullptr; if (SUCCEEDED(dev->GetId(&id)) && id) { ei.id = id; CoTaskMemFree(id); }
        ComPtr<IPropertyStore> props;
        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &props)) && props) {
            PROPVARIANT pv; PropVariantInit(&pv);
            if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal) ei.name = pv.pwszVal;
            PropVariantClear(&pv);
            PropVariantInit(&pv);
            if (SUCCEEDED(props->GetValue(PKEY_DeviceInterface_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal) ei.iface = pv.pwszVal;
            PropVariantClear(&pv);
        }
        out.push_back(std::move(ei));
    }
    return S_OK;
}

static void PrintEndpoints(const std::vector<EndpointInfo>& eps) {
    auto stateStr = [](DWORD s)->const char*{ switch(s){case DEVICE_STATE_ACTIVE:return "ACTIVE";case DEVICE_STATE_DISABLED:return "DISABLED";case DEVICE_STATE_NOTPRESENT:return "NOTPRESENT";case DEVICE_STATE_UNPLUGGED:return "UNPLUGGED";default:return "UNKNOWN";} };
    for (auto& e : eps) {
        std::printf("[%s] %-10s | %s\n", e.flow==eRender?"RENDER ":"CAPTURE", stateStr(e.state), Narrow(e.name).c_str());
        if (!e.iface.empty() && e.iface != e.name) std::printf("           iface: %s\n", Narrow(e.iface).c_str());
        std::printf("           id: %s\n", Narrow(e.id).c_str());
    }
}

struct CaptureStats {
    uint64_t packets = 0;
    uint64_t frames = 0;
    uint64_t bytes = 0;
    uint64_t silentFrames = 0;
    uint64_t discontinuities = 0;
    double peak = 0.0;
    double sumSq = 0.0;
    uint64_t sumSqFrames = 0;
};

static void AnalyzeFloat(const float* data, UINT32 frames, UINT32 ch, CaptureStats& st, bool silentFlag) {
    if (!data || frames == 0 || ch == 0) return;
    st.frames += frames;
    st.bytes += (uint64_t)frames * ch * sizeof(float);
    st.packets += 1;
    if (silentFlag) { st.silentFrames += frames; return; }
    double localPeak = 0.0, ss = 0.0;
    uint64_t n = (uint64_t)frames * ch;
    for (uint64_t i = 0; i < n; ++i) {
        double v = (double)data[i];
        double a = std::fabs(v);
        if (a > localPeak) localPeak = a;
        ss += v * v;
    }
    if (localPeak > st.peak) st.peak = localPeak;
    st.sumSq += ss;
    st.sumSqFrames += frames;
    // consider very low energy as silence-ish for reporting
    if (localPeak < 1e-7) st.silentFrames += frames;
}

static void AnalyzePCM16(const int16_t* data, UINT32 frames, UINT32 ch, CaptureStats& st, bool silentFlag) {
    if (!data || frames == 0 || ch == 0) return;
    st.frames += frames;
    st.bytes += (uint64_t)frames * ch * sizeof(int16_t);
    st.packets += 1;
    if (silentFlag) { st.silentFrames += frames; return; }
    double localPeak = 0.0, ss = 0.0;
    uint64_t n = (uint64_t)frames * ch;
    for (uint64_t i = 0; i < n; ++i) {
        double v = (double)data[i] / 32768.0;
        double a = std::fabs(v);
        if (a > localPeak) localPeak = a;
        ss += v * v;
    }
    if (localPeak > st.peak) st.peak = localPeak;
    st.sumSq += ss;
    st.sumSqFrames += frames;
    if (localPeak < 1e-6) st.silentFrames += frames;
}

static bool IsFloatFormat(const WAVEFORMATEX* wf) {
    if (!wf) return false;
    if (wf->wFormatTag == WAVE_FORMAT_IEEE_FLOAT && wf->wBitsPerSample == 32) return true;
    if (wf->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wf->cbSize >= 22) {
        auto* wfe = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wf);
        return IsEqualGUID(wfe->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }
    return false;
}
static bool IsPCM16Format(const WAVEFORMATEX* wf) {
    if (!wf) return false;
    if (wf->wFormatTag == WAVE_FORMAT_PCM && wf->wBitsPerSample == 16) return true;
    if (wf->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wf->cbSize >= 22) {
        auto* wfe = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wf);
        return IsEqualGUID(wfe->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) && wf->wBitsPerSample == 16;
    }
    return false;
}

int wmain(int argc, wchar_t** argv) {
    SetConsoleCtrlHandler(ConsoleCtrl, TRUE);
    bool doList = false;
    bool doCapture = false;
    std::wstring deviceNeedle = L"CABLE Input";
    int seconds = 60;

    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        if (a == L"--list" || a == L"-l") doList = true;
        else if (a == L"--capture" || a == L"-c") doCapture = true;
        else if ((a == L"--device" || a == L"-d") && i + 1 < argc) deviceNeedle = argv[++i];
        else if ((a == L"--seconds" || a == L"-s") && i + 1 < argc) seconds = _wtoi(argv[++i]);
        else if (a == L"--help" || a == L"-h" || a == L"/?") {
            std::printf("Usage:\n  --list\n  --capture [--device \"CABLE Input\"] [--seconds 60]\n");
            return 0;
        }
    }
    if (!doList && !doCapture) { doList = true; doCapture = true; }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) { std::printf("ERROR: CoInitializeEx failed hr=0x%08X\n", (unsigned)hr); return 2; }

    ComPtr<IMMDeviceEnumerator> en;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en));
    if (FAILED(hr) || !en) { std::printf("ERROR: MMDeviceEnumerator hr=0x%08X\n", (unsigned)hr); CoUninitialize(); return 2; }

    std::vector<EndpointInfo> renders, captures;
    ListEndpoints(en.Get(), eRender, renders);
    ListEndpoints(en.Get(), eCapture, captures);

    if (doList) {
        std::printf("=== RENDER ENDPOINTS ===\n"); PrintEndpoints(renders);
        std::printf("\n=== CAPTURE ENDPOINTS ===\n"); PrintEndpoints(captures);
        std::printf("\n");
    }

    if (!doCapture) { CoUninitialize(); return 0; }

    // Find target render endpoint (virtual device audio is rendered to its playback/input endpoint, e.g. "CABLE Input")
    ComPtr<IMMDevice> target;
    EndpointInfo chosen{};
    for (auto& e : renders) {
        if (e.state == DEVICE_STATE_ACTIVE && (ContainsInsensitive(e.name, deviceNeedle) || ContainsInsensitive(e.iface, deviceNeedle) || ContainsInsensitive(e.id, deviceNeedle))) {
            chosen = e; en->GetDevice(e.id.c_str(), &target); break;
        }
    }
    if (!target) {
        std::printf("ERROR: could not find ACTIVE render endpoint matching \"%s\".\n", Narrow(deviceNeedle).c_str());
        std::printf("Hint: install a virtual audio device (e.g. VB-CABLE) or pass --device \"<name substring>\". Use --list to see endpoints.\n");
        CoUninitialize();
        return 3;
    }

    std::printf("=== CAPTURE TARGET (render endpoint loopback) ===\n");
    std::printf("Name: %s\n", Narrow(chosen.name).c_str());
    std::printf("Iface: %s\n", Narrow(chosen.iface).c_str());
    std::printf("ID: %s\n", Narrow(chosen.id).c_str());
    std::printf("State: ACTIVE\n\n");

    ComPtr<IAudioClient> client;
    hr = target->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client);
    if (FAILED(hr) || !client) { std::printf("ERROR: Activate(IAudioClient) hr=0x%08X\n", (unsigned)hr); CoUninitialize(); return 4; }

    WAVEFORMATEX* mix = nullptr;
    hr = client->GetMixFormat(&mix);
    if (FAILED(hr) || !mix) { std::printf("ERROR: GetMixFormat hr=0x%08X\n", (unsigned)hr); CoUninitialize(); return 4; }

    bool isFloat = IsFloatFormat(mix);
    bool isPcm16 = IsPCM16Format(mix);
    std::printf("MixFormat: tag=%u bits=%u channels=%u sampleRate=%u blockAlign=%u avgBytes=%u float=%d pcm16=%d\n",
        (unsigned)mix->wFormatTag, (unsigned)mix->wBitsPerSample, (unsigned)mix->nChannels, (unsigned)mix->nSamplesPerSec,
        (unsigned)mix->nBlockAlign, (unsigned)mix->nAvgBytesPerSec, (int)isFloat, (int)isPcm16);

    REFERENCE_TIME hns = 200000; // 20ms
    DWORD flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
    hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, hns, 0, mix, nullptr);
    if (FAILED(hr)) {
        std::printf("ERROR: IAudioClient::Initialize(loopback) hr=0x%08X\n", (unsigned)hr);
        CoTaskMemFree(mix); CoUninitialize(); return 5;
    }

    UINT32 bufFrames = 0; client->GetBufferSize(&bufFrames);
    std::printf("BufferFrames: %u (engine buffer)\n", (unsigned)bufFrames);

    ComPtr<IAudioCaptureClient> cap;
    hr = client->GetService(__uuidof(IAudioCaptureClient), &cap);
    if (FAILED(hr) || !cap) { std::printf("ERROR: GetService(IAudioCaptureClient) hr=0x%08X\n", (unsigned)hr); CoTaskMemFree(mix); CoUninitialize(); return 5; }

    hr = client->Start();
    if (FAILED(hr)) { std::printf("ERROR: IAudioClient::Start hr=0x%08X\n", (unsigned)hr); CoTaskMemFree(mix); CoUninitialize(); return 5; }

    std::printf("\nCapturing loopback from target endpoint. Press Ctrl+C to stop.\n");
    std::printf("Target seconds: %s\n\n", seconds <= 0 ? "unlimited" : std::to_string(seconds).c_str());

    auto tStart = std::chrono::steady_clock::now();
    auto tLastReport = tStart;
    auto tNextSecond = tStart + std::chrono::seconds(1);
    CaptureStats total{}, window{};

    while (!g_stop.load()) {
        if (seconds > 0) {
            auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - tStart).count();
            if (elapsed >= seconds) break;
        }
        UINT32 packetFrames = 0;
        hr = cap->GetNextPacketSize(&packetFrames);
        if (FAILED(hr)) { std::printf("WARN: GetNextPacketSize hr=0x%08X\n", (unsigned)hr); std::this_thread::sleep_for(std::chrono::milliseconds(5)); continue; }
        while (packetFrames != 0 && !g_stop.load()) {
            BYTE* data = nullptr; UINT32 framesAvail = 0; DWORD flagsPkt = 0;
            hr = cap->GetBuffer(&data, &framesAvail, &flagsPkt, nullptr, nullptr);
            if (FAILED(hr)) { std::printf("WARN: GetBuffer hr=0x%08X\n", (unsigned)hr); break; }
            bool silent = (flagsPkt & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            if (framesAvail > 0) {
                if (isFloat) { AnalyzeFloat(reinterpret_cast<const float*>(data), framesAvail, mix->nChannels, total, silent); AnalyzeFloat(reinterpret_cast<const float*>(data), framesAvail, mix->nChannels, window, silent); }
                else if (isPcm16) { AnalyzePCM16(reinterpret_cast<const int16_t*>(data), framesAvail, mix->nChannels, total, silent); AnalyzePCM16(reinterpret_cast<const int16_t*>(data), framesAvail, mix->nChannels, window, silent); }
                else {
                    // generic byte count
                    total.frames += framesAvail; total.bytes += (uint64_t)framesAvail * mix->nBlockAlign; total.packets += 1;
                    window.frames += framesAvail; window.bytes += (uint64_t)framesAvail * mix->nBlockAlign; window.packets += 1;
                    if (silent) { total.silentFrames += framesAvail; window.silentFrames += framesAvail; }
                }
                if (flagsPkt & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) { total.discontinuities++; window.discontinuities++; }
            }
            cap->ReleaseBuffer(framesAvail);
            hr = cap->GetNextPacketSize(&packetFrames);
            if (FAILED(hr)) { std::printf("WARN: GetNextPacketSize(2) hr=0x%08X\n", (unsigned)hr); packetFrames = 0; }
        }

        auto now = std::chrono::steady_clock::now();
        if (now >= tNextSecond) {
            double rms = 0.0;
            if (window.sumSqFrames > 0) rms = std::sqrt(window.sumSq / (double)(window.sumSqFrames * mix->nChannels));
            bool activity = (window.frames > 0) && ((window.frames > window.silentFrames) || window.peak > 1e-5 || rms > 1e-5);
            std::printf("[%5.0fs] packets=%llu frames=%llu bytes=%llu silentFrames=%llu peak=%.6f rms=%.6f disc=%llu => %s\n",
                std::chrono::duration<double>(now - tStart).count(),
                (unsigned long long)window.packets, (unsigned long long)window.frames, (unsigned long long)window.bytes,
                (unsigned long long)window.silentFrames, window.peak, rms, (unsigned long long)window.discontinuities,
                activity ? "AUDIO ACTIVITY" : "silence/no-activity");
            window = CaptureStats{};
            tNextSecond += std::chrono::seconds(1);
            if (now > tNextSecond) tNextSecond = now + std::chrono::seconds(1);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }

    client->Stop();
    double rmsT = 0.0;
    if (total.sumSqFrames > 0) rmsT = std::sqrt(total.sumSq / (double)(total.sumSqFrames * mix->nChannels));
    bool totalActivity = (total.frames > 0) && ((total.frames > total.silentFrames) || total.peak > 1e-5 || rmsT > 1e-5);
    std::printf("\n=== SUMMARY ===\n");
    std::printf("Endpoint: %s\n", Narrow(chosen.name).c_str());
    std::printf("Format: %u Hz, %u ch, %u bits, tag=%u\n", (unsigned)mix->nSamplesPerSec, (unsigned)mix->nChannels, (unsigned)mix->wBitsPerSample, (unsigned)mix->wFormatTag);
    std::printf("Totals: packets=%llu frames=%llu bytes=%llu silentFrames=%llu discontinuities=%llu\n",
        (unsigned long long)total.packets, (unsigned long long)total.frames, (unsigned long long)total.bytes,
        (unsigned long long)total.silentFrames, (unsigned long long)total.discontinuities);
    std::printf("Peak=%.6f RMS=%.6f => %s\n", total.peak, rmsT, totalActivity ? "AUDIO ACTIVITY DETECTED" : "SILENCE / NO ACTIVITY DETECTED");

    CoTaskMemFree(mix);
    CoUninitialize();
    return 0;
}
