@echo off
chcp 65001 >nul
rem 本脚本位于 scripts\ 下，先回到仓库根目录再操作
cd /d "%~dp0.."

rem ============================================================
rem  无线手写板 完整发布打包
rem
rem  用法：scripts\package-release.cmd [版本号]
rem        scripts\package-release.cmd 1.0.1
rem
rem  产出 dist\ 目录，内容可直接作为 GitHub Release 的附件：
rem    PenWirelessTablet-Setup-<版本>.exe   ★ 推荐给普通用户：双击安装
rem    PenReceiver-<版本>-win-x64.zip        免安装绿色版（自包含，无需 .NET）
rem    PenReceiver-<版本>-win-x64-lite.zip   框架依赖版，需预装 .NET 8
rem    PenClient-<版本>.apk                  平板端，正式签名版
rem    codesign.cer                          自签证书公钥，用户导入后能识别发布者
rem    SHA256SUMS.txt                        校验值
rem
rem  前置条件：
rem    signing\pen-release.keystore 与 signing\keystore.properties 必须存在，
rem    否则脚本会明确报错中止，不会拿 debug 签名凑合。
rem    生成方式见 README「正式签名」一节。
rem
rem  重要：本文件必须保持 Windows 的 CRLF 行尾（原因见 launch-receiver.cmd）。
rem ============================================================

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.0.1"

echo === 打包版本 %VERSION% ===
echo.

rem ---------- 前置检查：Android 签名材料 ----------
if not exist "signing\pen-release.keystore" goto nosigning
if not exist "signing\keystore.properties" goto nosigning

if not exist "dist" mkdir "dist"

rem ---------- 1. Windows 安装包（含自包含程序与 Authenticode 签名） ----------
echo [1/4] 构建 Windows 安装包...
call "scripts\build-installer.cmd" %VERSION%
if errorlevel 1 goto failed

rem ---------- 2. 绿色版（框架依赖，体积小） ----------
echo.
echo [2/4] 发布绿色版（框架依赖，需 .NET 8）...
if exist "dist\PenReceiver-win-x64-lite" rmdir /s /q "dist\PenReceiver-win-x64-lite"
dotnet publish "PenReceiver\PenReceiver.csproj" ^
  -c Release -r win-x64 --self-contained false ^
  -p:Version=%VERSION% ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o "dist\PenReceiver-win-x64-lite"
if errorlevel 1 goto failed

echo.
echo [3/4] 组装压缩包并补签名...
call :zip "dist\PenReceiver-win-x64"      "dist\PenReceiver-%VERSION%-win-x64.zip"
call :zip "dist\PenReceiver-win-x64-lite" "dist\PenReceiver-%VERSION%-win-x64-lite.zip"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\sign-windows.ps1" -Version %VERSION%

rem ---------- 4. Android 正式签名 APK ----------
echo.
echo [4/4] 构建正式签名 APK...
pushd PenClient
call gradlew.bat :app:assembleRelease
if errorlevel 1 (
  popd
  goto failed
)
popd

set "RELAPK=PenClient\app\build\outputs\apk\release\app-release.apk"
if exist "%RELAPK%" (
  copy /y "%RELAPK%" "dist\PenClient-%VERSION%.apk" >nul
) else (
  echo   错误：没有找到 release APK
  goto failed
)

rem ---------- 汇总校验值 ----------
echo.
echo 生成校验文件...
powershell -NoProfile -Command ^
  "$v='%VERSION%'; $files=@(\"PenWirelessTablet-Setup-$v.exe\",\"PenReceiver-$v-win-x64.zip\",\"PenReceiver-$v-win-x64-lite.zip\",\"PenClient-$v.apk\"); $lines=@(); foreach($n in $files){ $p=Join-Path 'dist' $n; if(Test-Path $p){ $h=(Get-FileHash $p -Algorithm SHA256).Hash; $lines += \"$h  $n\" } }; $lines | Set-Content 'dist\SHA256SUMS.txt' -Encoding ASCII; $lines | ForEach-Object { \"  $_\" }"

echo.
echo === 完成，产物在 dist\ ===
for %%F in ("dist\PenWirelessTablet-Setup-%VERSION%.exe" "dist\PenReceiver-%VERSION%-win-x64.zip" "dist\PenReceiver-%VERSION%-win-x64-lite.zip" "dist\PenClient-%VERSION%.apk") do (
  if exist "%%~F" for %%S in ("%%~F") do echo   %%~nxS  %%~zS 字节
)
echo.
echo 下一步：把 dist\ 里的文件作为附件上传到 GitHub Release。
exit /b 0

rem ---- 子过程：压缩一个目录（先删旧包，避免把上次的产物混进去）----
:zip
if exist "%~1" (
  if exist "%~2" del /f /q "%~2"
  powershell -NoProfile -Command "Compress-Archive -Path '%~1\*' -DestinationPath '%~2' -Force"
  echo   已生成 %~nx2
) else (
  echo   警告：目录 %~1 不存在，跳过
)
exit /b 0

:nosigning
echo 错误：找不到 Android 签名材料，已中止。
echo.
echo   需要以下两个文件：
echo     signing\pen-release.keystore
echo     signing\keystore.properties
echo.
echo   密钥库是发布者身份，不进仓库，请按 README「正式签名」一节生成。
echo   这里刻意不拿 debug 签名凑合——那会产出一个「看起来是正式版、
echo   装上去却是调试签名」的包，以后无法用正式密钥升级。
exit /b 1

:failed
echo.
echo 打包失败，请检查上面的错误输出。
exit /b 1
