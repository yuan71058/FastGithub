@echo off
setlocal

set SOLUTION=%~dp0
set OUTPUT=%SOLUTION%publish

echo ============================================
echo   FastGithub Build Script
echo ============================================

:: Clean build directories
echo.
echo [0/4] Cleaning build directories...
for /d %%i in ("%SOLUTION%*") do (
    if exist "%%i\bin" rd /S /Q "%%i\bin"
    if exist "%%i\obj" rd /S /Q "%%i\obj"
)

:: Clean output directory
if exist "%OUTPUT%" rd /S /Q "%OUTPUT%"
mkdir "%OUTPUT%"

:: Publish backend as single file
echo.
echo [1/4] Publishing backend...
dotnet publish "%SOLUTION%FastGithub\FastGithub.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "%OUTPUT%"
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

:: Build UI (Costura.Fody merges dlls into exe)
echo.
echo [2/4] Building UI...
dotnet build "%SOLUTION%FastGithub.UI\FastGithub.UI.csproj" -c Release
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

:: Copy UI single file
echo.
echo [3/4] Copying UI...
copy /Y "%SOLUTION%FastGithub.UI\bin\Release\net45\win-x64\FastGithub.UI.exe" "%OUTPUT%\FastGithub.UI.exe" >nul
copy /Y "%SOLUTION%FastGithub.UI\bin\Release\net45\win-x64\FastGithub.UI.exe.config" "%OUTPUT%\FastGithub.UI.exe.config" >nul

:: Copy config files
echo [4/4] Copying config...
if exist "%SOLUTION%FastGithub\appsettings" (
    xcopy /E /Y /Q "%SOLUTION%FastGithub\appsettings\*" "%OUTPUT%\appsettings\" >nul
)

echo.
echo ============================================
echo   Output: %OUTPUT%
echo ============================================
echo.
dir /B "%OUTPUT%\*.exe" 2>nul
echo.
