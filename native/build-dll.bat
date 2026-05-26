@echo off
REM Build RecastBuilderDll native DLL
REM Requires Visual Studio (any version) with C++ workload installed

echo Building RecastBuilderDll...
cd /d "%~dp0RecastBuilderDll"

REM Use vswhere to locate any installed Visual Studio with C++ tools
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo Error: vswhere.exe not found. Please install Visual Studio with C++ workload.
    exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.VisualCpp.Tools.HostX64.TargetX64 -find VC\Auxiliary\Build\vcvarsall.bat`) do (
    set "VCVARSALL=%%i"
)

if not defined VCVARSALL (
    echo Error: No Visual Studio installation found with C++ workload.
    echo Please install the "Desktop development with C++" workload in Visual Studio.
    exit /b 1
)

call "%VCVARSALL%" x64

msbuild RecastBuilderDll.vcxproj /p:Configuration=Release /p:Platform=x64 /t:Build /v:m
if errorlevel 1 exit /b 1

echo.
echo Build complete: RecastBuilderDll\bin\Release\RecastBuilderDll.dll