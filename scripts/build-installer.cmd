@echo off
chcp 65001 >nul
rem 本脚本位于 scripts\ 下，先回到仓库根目录再操作
cd /d "%~dp0.."

rem ============================================================
rem  构建 Windows 安装包（setup.exe）
rem
rem  用法：scripts\build-installer.cmd [版本号]
rem        scripts\build-installer.cmd 1.0.1
rem
rem  产出：
rem    dist\PenWirelessTablet-Setup-<版本>.exe   单个安装包（内置自包含接收端）
rem    dist\PenReceiver-win-x64\                 免安装的绿色版（安装包内的同一份程序）
rem    dist\SHA256SUMS.txt                       校验值，防下载损坏
rem
rem  安装包的核心特点：**安装时不需要联网，也不需要预装 .NET**。
rem  载荷是 self-contained 发布产物（已内含 .NET 8），压缩后嵌进安装器。
rem
rem  三种签名互不相同，别混：
rem    1. Android APK  —— keystore 签名，见 README「正式签名」
rem    2. Windows 程序 —— Authenticode 签名，本脚本调用 scripts\sign-windows.ps1
rem    3. 校验文件     —— SHA256SUMS.txt
rem
rem  重要：本文件必须保持 Windows 的 CRLF 行尾（原因见 launch-receiver.cmd）。
rem ============================================================

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.0.1"

echo === 构建安装包 %VERSION% ===
echo.

rem ---------- 1. 发布自包含接收端（作为安装器载荷） ----------
echo [1/5] 发布接收端（自包含单文件，内含 .NET 8）...
if exist "dist\PenReceiver-win-x64" rmdir /s /q "dist\PenReceiver-win-x64"
dotnet publish "PenReceiver\PenReceiver.csproj" ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:Version=%VERSION% ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o "dist\PenReceiver-win-x64"
if errorlevel 1 goto failed

rem ---------- 2. 压缩载荷并嵌进安装器 ----------
echo.
echo [2/5] 压缩安装载荷...
if exist "PenInstaller\payload.zip" del /f /q "PenInstaller\payload.zip"
powershell -NoProfile -Command ^
  "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory('%CD%\dist\PenReceiver-win-x64', '%CD%\PenInstaller\payload.zip', [System.IO.Compression.CompressionLevel]::Optimal, $false)"
if errorlevel 1 goto failed
for %%F in ("PenInstaller\payload.zip") do echo   载荷 %%~zF 字节

rem ---------- 3. 编译安装器为单文件 ----------
echo.
echo [3/5] 编译安装器（单文件 exe）...
if exist "PenInstaller\publish" rmdir /s /q "PenInstaller\publish"
dotnet publish "PenInstaller\PenInstaller.csproj" ^
  -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:Version=%VERSION% ^
  -o "PenInstaller\publish"
if errorlevel 1 goto failed

if not exist "dist" mkdir "dist"
copy /y "PenInstaller\publish\PenWirelessTablet-Setup.exe" "dist\PenWirelessTablet-Setup-%VERSION%.exe" >nul
if errorlevel 1 goto failed

rem ---------- 4. Authenticode 签名 ----------
echo.
echo [4/5] 代码签名...
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\sign-windows.ps1" -Version %VERSION%
if errorlevel 1 echo   签名阶段有问题，但安装包已生成，继续打包。

rem ---------- 5. 校验文件 ----------
echo.
echo [5/5] 生成校验文件...
powershell -NoProfile -Command ^
  "$f='dist\PenWirelessTablet-Setup-%VERSION%.exe'; $h=(Get-FileHash $f -Algorithm SHA256).Hash; \"$h  $(Split-Path $f -Leaf)\" | Set-Content 'dist\SHA256SUMS.txt' -Encoding ASCII"
echo   dist\SHA256SUMS.txt
if exist "signing\codesign.cer" (
  copy /y "signing\codesign.cer" "dist\codesign.cer" >nul
  echo   dist\codesign.cer  （发布时一并提供，用户导入后才显示发布者）
)

echo.
echo === 完成 ===
if exist "dist\PenWirelessTablet-Setup-%VERSION%.exe" for %%F in ("dist\PenWirelessTablet-Setup-%VERSION%.exe") do echo   %%~nxF  %%~zF 字节
exit /b 0

:failed
echo.
echo 构建失败，请检查上面的错误输出。
exit /b 1
