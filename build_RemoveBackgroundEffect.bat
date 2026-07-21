@echo off
title Build - RemoveBackgroundEffect
setlocal enabledelayedexpansion

cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Khong tim thay .NET SDK ^(lenh dotnet^).
    echo         Cai .NET 9 SDK tai: https://dotnet.microsoft.com/download
    pause & exit /b 1
)
echo [OK]  Tim thay .NET SDK.

set "PDN_DIR="
for %%D in ("%ProgramFiles%\paint.net" "%ProgramFiles(x86)%\paint.net") do (
    if exist "%%~D\PaintDotNet.Effects.dll" set "PDN_DIR=%%~D"
)
if "%PDN_DIR%"=="" (
    echo [ERROR] Khong tim thay Paint.NET voi PaintDotNet.Effects.dll.
    pause & exit /b 1
)
echo [OK]  Paint.NET: %PDN_DIR%

echo [INFO] Bien dich RemoveBackgroundEffect.vbproj -^> bin\RemoveBackgroundEffect.dll
dotnet build "RemoveBackgroundEffect.vbproj" -c Release -p:PdnDir="%PDN_DIR%" -o bin
if errorlevel 1 (
    echo [ERROR] Bien dich that bai. Xem loi phia tren.
    pause & exit /b 1
)
echo [OK]  Da tao bin\RemoveBackgroundEffect.dll

set /p DEPLOY="Copy RemoveBackgroundEffect.dll vao %PDN_DIR%\Effects\ khong? (y/n): "
if /i "%DEPLOY%"=="y" (
    copy /Y "bin\RemoveBackgroundEffect.dll" "%PDN_DIR%\Effects\" >nul
    echo [OK]  Da copy vao %PDN_DIR%\Effects\
)

echo [INFO] Xong. Khoi dong lai Paint.NET de thay hieu ung moi.
pause
endlocal
