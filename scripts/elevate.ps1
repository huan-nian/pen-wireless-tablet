# 无线手写板：提权并启动接收端
#
# 从 launch-receiver.cmd 调用，也可以单独运行：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\elevate.ps1
#
# 为什么用 PowerShell 而不用纯批处理做提权：失败原因会完整写进日志，
# 而不是窗口一闪就没了、什么都看不到。
#
# 本文件必须保存为「带 BOM 的 UTF-8」：Windows PowerShell 5.1 对无 BOM 的 .ps1
# 会按 GBK 解析，中文全部变成乱码。

param(
    # 仓库根目录。批处理会显式传入，直接运行时由脚本位置推断。
    [string]$Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = Split-Path -Parent $PSScriptRoot
}

$log = Join-Path $env:TEMP 'pen-receiver-launch.log'

function Write-Log([string]$message) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
    Write-Host $message
}

"===== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') =====" | Set-Content -LiteralPath $log -Encoding UTF8

try {
    Write-Log "项目目录：$Root"

    $binRoot = Join-Path $Root 'PenReceiver\bin\Debug'
    $exe = Join-Path $binRoot 'net8.0-windows10.0.19041.0\PenReceiver.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Log "默认路径没有找到 exe，改为在 bin\Debug 下搜索"
        $found = Get-ChildItem -LiteralPath $binRoot -Recurse -Filter 'PenReceiver.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $exe = $found.FullName }
    }

    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Log "没有已编译的程序，请先运行：dotnet build PenReceiver\PenReceiver.csproj"
        exit 1
    }

    # 尽力清理一下普通权限能杀掉的旧实例。提权实例杀不掉是正常的——程序内部有
    # 单实例锁，重复启动只会提示「已在运行」，不会再堆积进程。
    Get-Process -Name 'PenReceiver' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
            Write-Log "已结束旧实例 PID $($_.Id)"
        }
        catch {
            Write-Log "旧实例 PID $($_.Id) 权限较高，跳过（程序内的单实例锁会处理）"
        }
    }

    # 应用自身的输出（含崩溃堆栈）重定向到文件。WinForms 程序正常时不输出任何内容，
    # 一旦启动即崩溃，异常文本就会落到这里，比「窗口闪一下就没了」好排查得多。
    $appOut = Join-Path $env:TEMP 'pen-receiver-app.out.log'
    $appErr = Join-Path $env:TEMP 'pen-receiver-app.err.log'

    # 注意：PowerShell 不允许 -Verb RunAs 与 -RedirectStandardOutput 同时使用
    # （两者属于互斥的参数集）。所以这里用 cmd 做一层包装，由 cmd 完成重定向。
    $inner = 'cmd /c ""' + $exe + '" > "' + $appOut + '" 2> "' + $appErr + '""'
    Write-Log "启动：$exe"
    Write-Log "应用输出重定向到：$appErr"

    try {
        Start-Process -FilePath 'cmd.exe' -ArgumentList $inner -Verb RunAs -ErrorAction Stop
    }
    catch [System.ComponentModel.Win32Exception] {
        # UAC 被拒绝时 Win32Exception 的提示就是「操作已被用户取消」
        Write-Log "提权被取消：你在 UAC 窗口点了「否」。"
        Write-Log "接收端必须提权才能注入笔输入，请重新运行并在 UAC 窗口点「是」。"
        exit 1
    }

    # 给 UAC 确认与进程启动留时间
    Start-Sleep -Seconds 6

    $running = Get-Process -Name 'PenReceiver' -ErrorAction SilentlyContinue
    if ($running) {
        $withWindow = $running | Where-Object { $_.MainWindowHandle -ne 0 }
        if ($withWindow) {
            Write-Log "成功：接收端窗口已打开（PID $($withWindow[0].Id)）"
        }
        else {
            Write-Log "接收端进程在运行（PID $($running[0].Id)），但暂时没有可见窗口。"
            Write-Log "若确认没看到窗口，检查任务栏；也双击屏幕右下角的托盘图标。"
        }
        exit 0
    }

    Write-Log "失败：没有检测到运行中的接收端。"
    Write-Log "最可能的原因：① 在 UAC 窗口点了「否」；② 程序启动即崩溃。"
    foreach ($f in @($appOut, $appErr)) {
        if ((Test-Path -LiteralPath $f) -and (Get-Item -LiteralPath $f).Length -gt 0) {
            Write-Log "----- $f 内容 -----"
            Get-Content -LiteralPath $f -Tail 40 | ForEach-Object { Write-Log $_ }
        }
    }
    exit 1
}
catch {
    Write-Log "异常：$($_.Exception.Message)"
    Write-Log $_.ScriptStackTrace
    exit 1
}
