@echo off
chcp 65001 >nul
rem 本脚本位于 scripts\ 下，先回到仓库根目录再操作
cd /d "%~dp0.."

rem ============================================================
rem  无线手写板 注入探针
rem
rem  重要：本文件必须保持 Windows 的 CRLF 行尾（原因见 launch-receiver.cmd）。
rem
rem  用途：绕过平板与 UDP，直接用接收端**同一份注入代码**验证注入是否可用。
rem  它会启动 Windows 画图、注入一条曲线、截图对比前后差异，并打印实测的
rem  压力上界。排查时能干净地把问题切成两半：
rem      探针能画出线  → 问题在平板或网络
rem      探针画不出线  → 问题在注入
rem
rem  需要管理员权限，会短暂接管屏幕（把画图窗口置顶）。
rem ============================================================

set "EXE=PenProbe\bin\Debug\net8.0-windows10.0.19041.0\PenProbe.exe"
if exist "%EXE%" goto run

echo 未找到已编译的探针，正在编译...
dotnet build "PenProbe\PenProbe.csproj" -c Debug --nologo
if errorlevel 1 goto buildfailed

:run
rem 探针自己有 requireAdministrator 清单，直接启动就会自动请求提权
"%EXE%"
echo.
echo 退出码：%ERRORLEVEL%
pause
exit /b 0

:buildfailed
echo.
echo 编译失败。请确认已安装 .NET 8 SDK（在命令行运行 dotnet --version 应有版本号）。
pause
exit /b 1
