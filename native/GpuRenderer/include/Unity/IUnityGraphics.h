#pragma once
#include "IUnityInterface.h"

typedef struct IUnityGraphics IUnityGraphics;

struct IUnityGraphics {
    void (UNITY_INTERFACE_API *RegisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    void (UNITY_INTERFACE_API *UnregisterDeviceEventCallback)(IUnityGraphicsDeviceEventCallback callback);
    int  (UNITY_INTERFACE_API *GetRenderer)();
};

extern "C" const UnityInterfaceGUID UnityGraphicsGUID; // not used directly; Unity provides via GetInterface in practice

// Unity 5.x+ GUID for IUnityGraphics: 0x7CBA0A9CA4DDB544, 0x8C5AD4926EB17B11
static const UnityInterfaceGUID kUnityGraphicsGUID = { 0x7CBA0A9CA4DDB544ULL, 0x8C5AD4926EB17B11ULL };