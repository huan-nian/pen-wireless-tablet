@echo off
chcp 65001 >nul
rem 本脚本位于 scripts\ 下，先回到仓库根目录再操作
cd /d "%~dp0.."

rem ============================================================
rem  无线手写板 接收端启动器
rem
rem  重要：本文件必须保持 Windows 的 CRLF 行尾。
rem  一旦被编辑器改成 LF 行尾，cmd.exe 会解析错乱并立刻退出
rem  （现象就是双击后窗口一闪而过、什么都不显示）。
rem
rem  为什么要提权：Windows 的笔输入注入接口（InputInjector）
rem  只在提权后的进程里可用。已提权时会跳过，不会重复弹 UAC。
rem
rem  启动后会自动开始监听（可在 %APPDATA%\PenReceiver\settings.ini 里关掉）。
rem ============================================================

rem --- 未提权则走提权分支；提权与启动的详细日志写在 %TEMP% 下 ---
net session >nul 2>&1
if errorlevel 1 goto elevate

rem --- 优先运行已编译好的程序，没有就现场编译 ---
set "EXE=PenReceiver\bin\Debug\net8.0-windows10.0.19041.0\PenReceiver.exe"
if exist "%EXE%" goto run

echo 未找到已编译的接收端，正在编译...
dotnet build "PenReceiver\PenReceiver.csproj" -c Debug --nologo
if errorlevel 1 goto buildfailed

:run
echo 启动接收端（管理员权限）...
start "" "%EXE%"
exit /b 0

:elevate
echo 正在请求管理员权限，请在 UAC 窗口点「是」...
echo 日志文件：%TEMP%\pen-receiver-launch.log
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0elevate.ps1" -Root "%CD%"
if errorlevel 1 (
  echo.
  echo 提权或启动失败。详情见日志：%TEMP%\pen-receiver-launch.log
  pause
)
exit /b 0

:buildfailed
echo.
echo 编译失败。请确认已安装 .NET 8 SDK（在命令行运行 dotnet --version 应有版本号）。
pause
exit /b 1
