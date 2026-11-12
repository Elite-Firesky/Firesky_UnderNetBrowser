cmake_minimum_required(VERSION 3.20)
project(UnderNetGpuRenderer LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# WebView2 native package (for CompositionController hosting mode)
set(WEBVIEW2_NUGET_ROOT "C:/Users/Administrator/.nuget/packages/microsoft.web.webview2/1.0.2903.40/build/native" CACHE PATH "Path to Microsoft.Web.WebView2 native build folder")

add_library(UnderNetGpuRenderer SHARED
    src/UnderNetGpuRenderer.cpp
    src/WgcCapture.cpp
    src/CompositionBrowser.cpp
)

target_include_directories(UnderNetGpuRenderer PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

if(EXISTS "${WEBVIEW2_NUGET_ROOT}/include/WebView2.h")
    target_include_directories(UnderNetGpuRenderer PRIVATE "${WEBVIEW2_NUGET_ROOT}/include")
else()
    message(WARNING "WebView2 headers not found at ${WEBVIEW2_NUGET_ROOT}/include. Set WEBVIEW2_NUGET_ROOT.")
endif()

target_compile_definitions(UnderNetGpuRenderer PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    UNICODE
    _UNICODE
)

target_link_libraries(UnderNetGpuRenderer PRIVATE
    d3d11
    dxgi
    d3dcompiler
    windowsapp
    runtimeobject
    ole32
    oleaut32
    user32
    mmdevapi
    avrt
)

# WebView2 loader lib
if(EXISTS "${WEBVIEW2_NUGET_ROOT}/x64/WebView2LoaderStatic.lib")
    target_link_libraries(UnderNetGpuRenderer PRIVATE "${WEBVIEW2_NUGET_ROOT}/x64/WebView2LoaderStatic.lib")
elseif(EXISTS "${WEBVIEW2_NUGET_ROOT}/x64/WebView2Loader.dll.lib")
    target_link_libraries(UnderNetGpuRenderer PRIVATE "${WEBVIEW2_NUGET_ROOT}/x64/WebView2Loader.dll.lib")
else()
    message(WARNING "WebView2 loader lib not found under ${WEBVIEW2_NUGET_ROOT}/x64. Link manually if needed.")
endif()

if(MSVC)
    target_compile_options(UnderNetGpuRenderer PRIVATE /W4 /EHsc)
    # Static runtime for easier deployment (adjust if needed)
    set_property(TARGET UnderNetGpuRenderer PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

set_target_properties(UnderNetGpuRenderer PROPERTIES
    OUTPUT_NAME "UnderNetGpuRenderer"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)