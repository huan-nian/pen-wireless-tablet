<#
.SYNOPSIS
    用 signing\codesign.pfx 给 Windows 可执行文件做 Authenticode 签名。

.DESCRIPTION
    为什么不用 signtool.exe：
      signtool 属于 Windows SDK，本机没有装；虽然可以从 NuGet 的
      Microsoft.Windows.SDK.BuildTools 包里取，但这个网络下载它非常慢。
      而 PowerShell 自带 Set-AuthenticodeSignature，能力足够（支持指定哈希算法与
      证书），且少一个构建依赖。

    签名后状态说明：
      - 证书未导入「受信任的根」时，Get-AuthenticodeSignature 会返回
        UnknownError 或 NotTrusted —— **这不代表没签上**，
        签名者字段已经能显示出证书主题。
      - 把 codesign.cer 导入「受信任的根证书颁发机构」后即变为 Valid。

.PARAMETER Version
    仅用于日志显示。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\sign-windows.ps1
#>

[CmdletBinding()]
param(
    [string]$Version = '1.0.1'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$pfxPath = Join-Path $repoRoot 'signing\codesign.pfx'
$passPath = Join-Path $repoRoot 'signing\codesign.pass'

if (-not (Test-Path $pfxPath)) {
    Write-Host "未找到 $pfxPath，跳过签名。" -ForegroundColor Yellow
    Write-Host '生成证书：powershell -ExecutionPolicy Bypass -File scripts\make-codesign-cert.ps1' -ForegroundColor Yellow
    exit 0
}

$password = if (Test-Path $passPath) { (Get-Content $passPath -Raw).Trim() } else { '' }

# 用 X509Certificate2 而不是 Get-PfxCertificate：
# 后者的 -Password 参数只在较新的 PowerShell 里才有，PS 5.1 会直接报参数不存在。
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
    $pfxPath, $password,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

if (-not $cert.HasPrivateKey) {
    throw '证书里没有私钥，无法签名。'
}

Write-Host "签名证书：$($cert.Subject)" -ForegroundColor Cyan
Write-Host "指纹    ：$($cert.Thumbprint)"
Write-Host ''

$targets = @(
    (Join-Path $repoRoot "dist\PenWirelessTablet-Setup-$Version.exe"),
    (Join-Path $repoRoot 'dist\PenReceiver-win-x64\PenReceiver.exe'),
    (Join-Path $repoRoot 'dist\PenReceiver-win-x64-lite\PenReceiver.exe')
)

$signed = 0
foreach ($target in $targets) {
    if (-not (Test-Path $target)) {
        Write-Host "跳过（不存在）：$target" -ForegroundColor DarkGray
        continue
    }

    try {
        $result = Set-AuthenticodeSignature -FilePath $target -Certificate $cert -HashAlgorithm SHA256
        $name = Split-Path $target -Leaf
        $dir = Split-Path $target -Parent

        if ($result.SignerCertificate) {
            Write-Host "已签名 $name" -ForegroundColor Green
            $signed++
        }
        else {
            Write-Host "签名未生效 $name：$($result.StatusMessage)" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "签名失败 $(Split-Path $target -Leaf)：$($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
Write-Host "共签名 $signed 个文件。"

if ($signed -gt 0) {
    Write-Host ''
    Write-Host '提醒：自签证书无法消除 SmartScreen 拦截。' -ForegroundColor Yellow
    Write-Host '分发时请一并提供 signing\codesign.cer，' -ForegroundColor Yellow
    Write-Host '用户导入「受信任的根证书颁发机构」后签名才会显示为受信任。' -ForegroundColor Yellow
}
