@echo off
setlocal

set SOLUTION=%~dp0
set OUTPUT=%SOLUTION%publish

echo ============================================
echo   FastGithub Build Script
echo ============================================

:: 清理输出目录
if exist "%OUTPUT%" rd /S /Q "%OUTPUT%"
mkdir "%OUTPUT%"

:: 编译解决方案 (Release)
echo.
echo [1/3] Building solution...
dotnet build "%SOLUTION%FastGithub.sln" -c Release
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

:: 复制后端 (net7.0 win-x64)
echo.
echo [2/3] Copying backend...
xcopy /E /Y /Q "%SOLUTION%FastGithub\bin\Release\net7.0\win-x64\*" "%OUTPUT%\" >nul

:: 复制前端 (net45 win-x64)
echo [3/3] Copying UI...
xcopy /E /Y /Q "%SOLUTION%FastGithub.UI\bin\Release\net45\win-x64\*" "%OUTPUT%\" >nul

:: 复制配置文件
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
