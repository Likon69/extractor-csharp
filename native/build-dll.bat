@echo off
REM Build RecastBuilderDll native DLL
REM Requires Visual Studio 2022 Developer Command Prompt or MSVC installed

echo Building RecastBuilderDll...
cd /d "%~dp0native\RecastBuilderDll"

if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" (
    call "%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
) else if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvarsall.bat" (
    call "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
) else (
    echo Error: Visual Studio 2022 not found
    echo Please install Visual Studio 2022 with C++ workload
    exit /b 1
)

msbuild RecastBuilderDll.vcxproj /p:Configuration=Release /p:Platform=x64 /t:Build /v:m
if errorlevel 1 exit /b 1

echo.
echo Build complete: native\RecastBuilderDll\bin\Release\RecastBuilderDll.dll