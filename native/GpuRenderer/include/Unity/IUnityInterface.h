#pragma once
#include <stdint.h>

// Minimal Unity native plugin interface definitions (Unity 2022.x compatible subset)
// Based on Unity's public PluginAPI headers.

typedef struct UnityInterfaceGUID {
    unsigned long long high;
    unsigned long long low;
} UnityInterfaceGUID;

#define UNITY_INTERFACE_API __stdcall
#define UNITY_INTERFACE_EXPORT __declspec(dllexport)

// Base interface type (opaque in public API)
struct IUnityInterface {};

typedef struct IUnityInterfaces IUnityInterfaces;
struct IUnityInterfaces {
    IUnityInterface* (UNITY_INTERFACE_API *GetInterface)(UnityInterfaceGUID guid);
    void (UNITY_INTERFACE_API *RegisterInterface)(UnityInterfaceGUID guid, IUnityInterface* ptr);
};

// Returns INTERFACE* from IUnityInterfaces::GetInterface (IUnityInterface* base)
#define UNITY_GET_INTERFACE(INTERFACE, interfaces, guid) \
    reinterpret_cast<INTERFACE*>((interfaces)->GetInterface(guid))

// Graphics device events
typedef enum UnityGfxDeviceEventType {
    kUnityGfxDeviceEventInitialize = 0,
    kUnityGfxDeviceEventShutdown = 1,
    kUnityGfxDeviceEventBeforeReset = 2,
    kUnityGfxDeviceEventAfterReset = 3,
} UnityGfxDeviceEventType;

typedef enum UnityGfxRenderer {
    kUnityGfxRendererOpenGL = 0,
    kUnityGfxRendererD3D9 = 1,
    kUnityGfxRendererD3D11 = 2,
    kUnityGfxRendererNull = 4,
    kUnityGfxRendererOpenGLES20 = 8,
    kUnityGfxRendererOpenGLES30 = 11,
    kUnityGfxRendererPS4 = 13,
    kUnityGfxRendererXboxOne = 14,
    kUnityGfxRendererMetal = 16,
    kUnityGfxRendererOpenGLCore = 17,
    kUnityGfxRendererD3D12 = 18,
    kUnityGfxRendererVulkan = 21,
} UnityGfxRenderer;

typedef void (UNITY_INTERFACE_API *IUnityGraphicsDeviceEventCallback)(UnityGfxDeviceEventType eventType);

// Render event function signatures
typedef void (UNITY_INTERFACE_API *UnityRenderingEvent)(int eventId);
typedef void (UNITY_INTERFACE_API *UnityRenderingEventAndData)(int eventId, void* data);

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces);
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload();