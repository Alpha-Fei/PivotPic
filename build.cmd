@echo off
chcp 65001 >nul
setlocal

rem 使用用户目录下的 .NET 8 SDK（本机未安装全局 SDK 时也能构建）
set DOTNET_ROOT=%USERPROFILE%\.dotnet
set PATH=%DOTNET_ROOT%;%PATH%

echo ============================================================
echo   正画 PivotPic - 一键出包
echo   版本号按 version-iteration.txt 自动递增
echo   产物：dist\PivotPic.exe        绿色版
echo         dist\正画-Setup-*.exe    安装包
echo ============================================================
echo.

if not exist "%~dp0src\PivotPic\PivotPic.csproj" (
    echo [错误] 找不到项目文件 src\PivotPic\PivotPic.csproj
    goto :fail
)

rem 关闭正在运行的实例，避免文件占用导致复制失败
taskkill /F /IM PivotPic.exe >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
if errorlevel 1 goto :fail

echo.
echo ============================================================
echo   完成
echo ============================================================
echo   dist\PivotPic.exe        绿色版，双击即用
echo   dist\正画-Setup-*.exe    安装包，可选桌面快捷方式与开机自启
echo ============================================================
goto :done

:fail
echo.
echo [失败] 出包未完成，请查看上方错误信息。
:done
echo.
pause
endlocal
