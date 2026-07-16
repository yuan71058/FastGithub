@echo off
setlocal

set SOLUTION=%~dp0
set OUTPUT=%SOLUTION%publish

echo ============================================
echo   FastGithub Build Script
echo ============================================

:: 清理编译目录
echo.
echo [0/4] Cleaning build directories...
for /d %%i in ("%SOLUTION%*") do (
    if exist "%%i\bin" rd /S /Q "%%i\bin"
    if exist "%%i\obj" rd /S /Q "%%i\obj"
)

:: 清理输出目录
if exist "%OUTPUT%" rd /S /Q "%OUTPUT%"
mkdir "%OUTPUT%"

:: 编译解决方案 (Release)
echo.
echo [1/4] Building solution...
dotnet build "%SOLUTION%FastGithub.sln" -c Release
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

:: 复制后端 (net7.0 win-x64)
echo.
echo [2/4] Copying backend...
xcopy /E /Y /Q "%SOLUTION%FastGithub\bin\Release\net7.0\win-x64\*" "%OUTPUT%\" >nul

:: 复制前端 (net45 win-x64)
echo [3/4] Copying UI...
xcopy /E /Y /Q "%SOLUTION%FastGithub.UI\bin\Release\net45\win-x64\*" "%OUTPUT%\" >nul

:: 复制配置文件
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
