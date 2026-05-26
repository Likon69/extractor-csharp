@echo off
REM Full build script for MaNGOS Unified Extractor

setlocal

echo ========================================
echo   MaNGOS Extractor - Full Build
echo ========================================
echo.

REM Check .NET SDK
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo Error: .NET SDK not found
    echo Please install .NET 10 SDK from https://dotnet.microsoft.com/download
    exit /b 1
)

echo [1/4] Restoring NuGet packages...
dotnet restore MaNGOS.Extractor.sln
if errorlevel 1 exit /b 1

echo.
echo [2/4] Building C# solution...
dotnet build MaNGOS.Extractor.sln -c Release --no-restore
if errorlevel 1 exit /b 1

echo.
echo [3/4] Building native DLL (RecastBuilderDll)...
if exist "native\RecastBuilderDll\bin\Release\RecastBuilderDll.dll" (
    echo    RecastBuilderDll.dll already exists, skipping...
) else (
    call native\build-dll.bat
    if errorlevel 1 (
        echo    Warning: C++ build failed (MSVC may not be installed)
    )
)

echo.
echo [4/4] Running unit tests...
dotnet test MaNGOS.Extractor.sln --no-build -c Release --verbosity minimal

echo.
echo ========================================
echo   Build Complete
echo ========================================
echo.
echo Output files:
echo   - src\bin\Release\net10.0-windows\MaNGOS.Extractor.dll
if exist "native\RecastBuilderDll\bin\Release\RecastBuilderDll.dll" (
    echo   - native\RecastBuilderDll\bin\Release\RecastBuilderDll.dll
)
echo.
echo To run: dotnet run --project src\MaNGOS.Extractor.csproj

endlocal