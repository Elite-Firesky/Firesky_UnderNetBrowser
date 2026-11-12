@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 (
  echo ERROR: vcvars64.bat not found. Install VS 2022 Build Tools with C++ workload.
  exit /b 1
)
if not exist build mkdir build
cl /nologo /std:c++17 /EHsc /O2 /DUNICODE /D_UNICODE /I. src\VirtualEndpointLoopbackTest.cpp /Fe:build\VirtualEndpointLoopbackTest.exe /link ole32.lib oleaut32.lib avrt.lib mmdevapi.lib uuid.lib
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: build\VirtualEndpointLoopbackTest.exe
endlocal
