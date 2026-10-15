<#
.SYNOPSIS
    校验 exe 里嵌入的应用清单（RT_MANIFEST）是否有效。

.DESCRIPTION
    这个脚本的存在源于一次真实的发布事故：
    安装器在 app.manifest 里手写了 DPI 节点
        <application xmlns="urn:schemas-microsoft-com:asm.v3">
          <windowsSettings><dpiAwareness>PerMonitorV2</dpiAwareness></windowsSettings>
        </application>
    结果程序一启动就报「应用程序无法启动，因为应用程序的并行配置不正确」。
    更糟的是构建、签名、打包全部成功，产物体积也正常——
    **只有真的去双击运行才会暴露**，而打包脚本当时并不运行程序。

    所以这里把两个能自动做的检查固化下来：
      1) 清单能被 XML 解析器解析；
      2) 清单里不含 dpiAwareness —— DPI 必须由 csproj 的 ApplicationHighDpiMode 控制，
         两处都写会产生冲突的激活上下文。

    为什么用 XML 解析 + 元素检查，而不是“启动程序看它会不会报错”：
    安装器要求提权，自动化环境点不了 UAC；而 SxS 错误发生在提权之前，
    所以只要清单本身正常，就不会有 SxS 事件。反过来，用事件日志判断需要
    等到下次真机双击，太晚了。

.PARAMETER Path
    要校验的 exe。可以给多个。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\verify-manifest.ps1 -Path dist\PenWirelessTablet-Setup-1.0.1.exe
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

# ---- 读取 RT_MANIFEST 需要的 Win32 调用 ----
if (-not ('PenManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PenManifestReader
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr res);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr res);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    private const uint LoadLibraryAsDataFile = 0x2;
    private static readonly IntPtr RtManifest = new IntPtr(24);

    /// <summary>返回 PE 文件里嵌入的 RT_MANIFEST 文本；读不到返回 null。</summary>
    public static string Read(string path)
    {
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LoadLibraryAsDataFile);
        if (module == IntPtr.Zero) return null;

        try
        {
            IntPtr res = FindResource(module, new IntPtr(1), RtManifest);
            if (res == IntPtr.Zero) return null;

            uint size = SizeofResource(module, res);
            IntPtr data = LockResource(LoadResource(module, res));
            if (data == IntPtr.Zero || size == 0) return null;

            byte[] buffer = new byte[size];
            Marshal.Copy(data, buffer, 0, (int)size);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
'@ -Language CSharp
}

$failed = 0

foreach ($file in $Path) {
    Write-Host "校验清单：$file" -ForegroundColor Cyan

    if (-not (Test-Path $file)) {
        Write-Host "  ✗ 文件不存在" -ForegroundColor Red
        $failed++
        continue
    }

    $manifest = [PenManifestReader]::Read((Resolve-Path $file).Path)
    if (-not $manifest) {
        Write-Host "  ✗ 读不到 RT_MANIFEST 资源" -ForegroundColor Red
        $failed++
        continue
    }

    $ok = $true

    # 1) XML 必须能解析 —— 直接对应 Windows 报的 "Invalid Xml syntax"
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.LoadXml($manifest)
        Write-Host '  ✓ XML 语法有效' -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ XML 解析失败：$($_.Exception.Message)" -ForegroundColor Red
        $ok = $false
    }

    # 判定元素是否存在前必须先剥掉注释，否则会被“说明文字里提到该元素”误伤。
    # 这个坑我踩过一次：检查脚本一开始直接用正则匹配整个清单文本，
    # 结果注释里那句「不要手写 <dpiAwareness>」把自己给举报了。
    $stripped = [regex]::Replace(
        $manifest, '<!--.*?-->', '',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)

    # 2) 不能含 dpiAwareness —— 这是那次事故的直接原因
    if ($stripped -match '<dpiAwareness') {
        Write-Host '  ✗ 清单里含 <dpiAwareness> 元素，会与 SDK 生成的节点冲突并导致 SxS 失败' -ForegroundColor Red
        Write-Host '    请改 csproj 的 <ApplicationHighDpiMode>，不要写进 app.manifest' -ForegroundColor Yellow
        $ok = $false
    }
    else {
        Write-Host '  ✓ 未含 <dpiAwareness> 元素（DPI 由 csproj 控制）' -ForegroundColor Green
    }

    # 3) 如果要求提权，必须有 requestedExecutionLevel
    if ($stripped -match 'requireAdministrator' -and $stripped -notmatch '<requestedExecutionLevel') {
        Write-Host '  ✗ 提到提权但没有 requestedExecutionLevel 节点' -ForegroundColor Red
        $ok = $false
    }

    if (-not $ok) { $failed++ }
    Write-Host ''
}

if ($failed -gt 0) {
    Write-Host "$failed 个文件的清单校验未通过。" -ForegroundColor Red
    exit 1
}

Write-Host '全部清单校验通过。' -ForegroundColor Green
exit 0
