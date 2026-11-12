#pragma once
#include "IUnityInterface.h"

struct ID3D11Device;

typedef struct IUnityGraphicsD3D11 IUnityGraphicsD3D11;

struct IUnityGraphicsD3D11 {
    ID3D11Device* (UNITY_INTERFACE_API *GetDevice)();
};

// Unity 5.x+ GUID for IUnityGraphicsD3D11: 0xAAB37EF87A87D748, 0xBF76967F07EFB177
static const UnityInterfaceGUID kUnityGraphicsD3D11GUID = { 0xAAB37EF87A87D748ULL, 0xBF76967F07EFB177ULL };