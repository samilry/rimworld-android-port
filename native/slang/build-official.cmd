@echo off
rem Builds Slang 2026.19, the shader compiler of the PC's game (its slang-compiler.dll says 2026.19), for Android
rem arm64: first the build-time generators for Windows (Visual Studio's C++ tools and the Windows SDK), then the
rem libraries with NDK r27c (Slang needs C++20; Unity's NDK r23 is older). Sources: downloads\src\slang-2026.19
rem (git clone --branch v2026.19 --recurse-submodules), NDK: downloads\android-ndk-r27c; elsewhere when native/build.sh
rem says so (PORT17_SLANG_SRC, PORT17_SLANG_NDK: host/env.sh's DEPS_DIR and SLANG_NDK, as Windows paths).
rem usage: native\slang\build-official.cmd       (output: downloads\src\slang-2026.19\build-android-arm64-v8a\Release\lib)
setlocal
set ROOT=%~dp0..\..
set SRC=%ROOT%\downloads\src\slang-2026.19
if defined PORT17_SLANG_SRC set "SRC=%PORT17_SLANG_SRC%"
set NDK=%ROOT%\downloads\android-ndk-r27c
if defined PORT17_SLANG_NDK set "NDK=%PORT17_SLANG_NDK%"
rem Visual Studio 2022 with its C++ tools, CMake and Ninja: VS_DIR, else the Community edition where it installs, else
rem the newest installation with the C++ tools that vswhere finds (Professional, Enterprise, the Build Tools).
set "VS=%VS_DIR%"
if not defined VS if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS=C:\Program Files\Microsoft Visual Studio\2022\Community"
if not defined VS for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS echo Visual Studio 2022 with the C++ tools not found: set VS_DIR to its folder & exit 1
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit 1
set PATH=%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;%PATH%
rem cl.exe reads sources in the system's code page (936 here) unless told UTF-8; the warning it gives then is an error.
set CL=/utf-8
cd /d "%SRC%"
if errorlevel 1 exit 1
rem The port's change to Slang (slang-2026.19-android.patch: no posix_spawn before Android 9), applied to the checkout
rem once; the reverse check says whether it already is.
git apply --reverse --check "%~dp0slang-2026.19-android.patch" 2>nul || git apply "%~dp0slang-2026.19-android.patch"
if errorlevel 1 exit 1

if exist generators\bin\slang-bootstrap.exe goto android
echo == generators for Windows
cmake --preset generators -DSLANG_IGNORE_ABORT_MSG=ON
if errorlevel 1 exit 1
cmake --build --preset generators
if errorlevel 1 exit 1
cmake --install build --config Release --prefix generators --component generators
if errorlevel 1 exit 1

:android
echo == Slang for Android arm64
set CL=
set ANDROID_NDK_HOME=%NDK%
rem API 26 as the port's other native libraries (the preset says 31); only the libraries the engine loads are built.
cmake --preset android-arm64 -DSLANG_GENERATORS_PATH=generators/bin -DANDROID_PLATFORM=android-26 -DSLANG_ENABLE_SLANGC=OFF -DSLANG_ENABLE_SLANGI=OFF -DSLANG_ENABLE_SLANGRT=OFF -DSLANG_ENABLE_SLANG_PROXY=OFF -DSLANG_IGNORE_ABORT_MSG=ON
if errorlevel 1 exit 1
rem Only the libraries: the slang command line tool (slang-dispatcher) uses posix_spawn, which API 26 does not have.
cmake --build --preset android-arm64-release --target slang slang-glslang slang-glsl-module
if errorlevel 1 exit 1
echo == done
