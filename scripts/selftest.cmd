@echo off
chcp 65001 >nul
rem 本脚本位于 scripts\ 下，先回到仓库根目录再操作
cd /d "%~dp0.."

rem ============================================================
rem  无线手写板 自检
rem
rem  重要：本文件必须保持 Windows 的 CRLF 行尾（原因见 launch-receiver.cmd）。
rem
rem  验证协议解析、坐标映射、压力缩放、长按状态机、快捷命令映射、
rem  UDP 收发与探测回复。不注入任何输入，因此不需要管理员权限，
rem  也不会影响正在使用的鼠标键盘。全部通过时退出码为 0。
rem ============================================================

set "EXE=PenReceiver.SelfTest\bin\Debug\net8.0-windows10.0.19041.0\PenSelfTest.exe"
if exist "%EXE%" goto run

echo 未找到已编译的自检程序，正在编译...
dotnet build "PenReceiver.SelfTest\PenReceiver.SelfTest.csproj" -c Debug --nologo
if errorlevel 1 goto buildfailed

:run
"%EXE%"
echo.
echo 退出码：%ERRORLEVEL%（0 表示全部通过）
pause
exit /b 0

:buildfailed
echo.
echo 编译失败。请确认已安装 .NET 8 SDK（在命令行运行 dotnet --version 应有版本号）。
pause
exit /b 1
