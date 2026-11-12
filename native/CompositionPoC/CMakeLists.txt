cmake_minimum_required(VERSION 3.20)
project(UnderNetWebView2CompositionPoC LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(UnderNetWebView2CompositionPoC WIN32 src/main.cpp)

# WebView2 native headers/lib: prefer NuGet package installed on this machine (adjust as needed).
set(WEBVIEW2_NUGET_ROOT "C:/Users/Administrator/.nuget/packages/microsoft.web.webview2/1.0.2903.40/build/native" CACHE PATH "Path to Microsoft.Web.WebView2 native build folder")
if(EXISTS "${WEBVIEW2_NUGET_ROOT}/include/WebView2.h")
    target_include_directories(UnderNetWebView2CompositionPoC PRIVATE "${WEBVIEW2_NUGET_ROOT}/include")
else()
    message(WARNING "WebView2 headers not found at ${WEBVIEW2_NUGET_ROOT}/include. Set WEBVIEW2_NUGET_ROOT.")
endif()

# WebView2Loader static lib (x64) if present in NuGet package; fallback to loader DLL linkage not handled here.
if(EXISTS "${WEBVIEW2_NUGET_ROOT}/x64/WebView2LoaderStatic.lib")
    target_link_libraries(UnderNetWebView2CompositionPoC PRIVATE "${WEBVIEW2_NUGET_ROOT}/x64/WebView2LoaderStatic.lib")
elseif(EXISTS "${WEBVIEW2_NUGET_ROOT}/x64/WebView2Loader.dll.lib")
    target_link_libraries(UnderNetWebView2CompositionPoC PRIVATE "${WEBVIEW2_NUGET_ROOT}/x64/WebView2Loader.dll.lib")
else()
    message(WARNING "WebView2 loader lib not found under ${WEBVIEW2_NUGET_ROOT}/x64. Link manually if needed.")
endif()

target_link_libraries(UnderNetWebView2CompositionPoC PRIVATE
    windowsapp d3d11 dxgi dcomp ole32 oleaut32 user32 gdi32 runtimeobject
)

target_compile_definitions(UnderNetWebView2CompositionPoC PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
