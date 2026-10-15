<#
.SYNOPSIS
    生成用于 Windows 程序（Authenticode）签名的自签代码签名证书。

.DESCRIPTION
    先说清楚这个脚本能做什么、不能做什么，避免误解：

    ✅ 能做的：
       - 消除「未知发布者」提示，文件属性里会显示发布者名称；
       - 让「数字签名」选项卡显示签名有效（前提是证书被导入到“受信任的根”）。

    ❌ 不能做的：
       - **无法消除 SmartScreen 的「Windows 已保护你的电脑」拦截。**
         那个拦截基于证书的声誉（是否由受信任 CA 签发、有多少下载量），
         自签证书没有声誉，所以首次运行仍会被拦。只有向 CA 购买代码签名证书
         （DigiCert / Sectigo 等，年费几百到上千元）才能真正消除。

    ⚠️ 分发时的额外代价：
       用户必须先把导出的证书导入「受信任的根证书颁发机构」，签名才会被认。
       否则签名依然会显示为「不受信任」。这一点务必在发布说明里写清楚。

.PARAMETER Password
    PFX 文件的保护口令。省略时脚本会生成一个随机口令并写入 signing\codesign.pass。

.PARAMETER Subject
    证书主题。默认与 Android 端保持一致，便于用户核对是同一个发布者。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-codesign-cert.ps1
#>

[CmdletBinding()]
param(
    [string]$Password,
    [string]$Subject = 'CN=Huan Nian, O=pen-wireless-tablet, OU=Windows, C=CN'
)

$ErrorActionPreference = 'Stop'

# 仓库根目录：本脚本在 scripts\ 下
$repoRoot = Split-Path -Parent $PSScriptRoot
$signingDir = Join-Path $repoRoot 'signing'
$pfxPath = Join-Path $signingDir 'codesign.pfx'
$cerPath = Join-Path $signingDir 'codesign.cer'
$passPath = Join-Path $signingDir 'codesign.pass'

if (-not (Test-Path $signingDir)) {
    New-Item -ItemType Directory -Path $signingDir -Force | Out-Null
}

# 已经存在就不覆盖：换证书会让“已验证发布者”的用户重新变回未知，得不偿失
if (Test-Path $pfxPath) {
    Write-Host "已存在 $pfxPath，未做改动。" -ForegroundColor Yellow
    Write-Host "如需重新生成，请先手动删除该文件与 codesign.cer。" -ForegroundColor Yellow
    exit 0
}

if (-not $Password) {
    # 用随机口令而不是固定值：PFX 里含私钥，口令公开等于私钥公开
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $Password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'A'
}

Write-Host '正在生成自签代码签名证书…' -ForegroundColor Cyan

# 有效期 10 年。代码签名证书到期后已签名的程序会逐渐失效，
# 取一个足够长的期限，避免用户过几年发现签名过期。
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

Write-Host "  证书指纹: $($cert.Thumbprint)"

# 导出私钥（PFX，给 signtool 签名用）与公钥（CER，给用户导入信任用）
$securePass = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePass | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

# 口令落盘到 gitignore 覆盖的 signing\ 目录
Set-Content -Path $passPath -Value $Password -Encoding ASCII -NoNewline

# 把证书加入本机“受信任的根”与“受信任的发布者”，
# 这样本机双击自己打的包不会再看到「未知发布者」，方便自测。
# 注意：这只影响本机，别人的电脑上仍需手动导入 codesign.cer。
try {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    Write-Host '  已导入本机「受信任的根」与「受信任的发布者」（仅本机生效）' -ForegroundColor Green
}
catch {
    Write-Host "  未能导入本机受信任存储（需要管理员权限）：$($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host '  不影响签名生成，但本机也会显示「未知发布者」。' -ForegroundColor Yellow
}

# 从当前用户存储里移除，避免证书库里越积越多
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '完成：' -ForegroundColor Cyan
Write-Host "  $pfxPath    （含私钥，签名用，已在 .gitignore 中排除）"
Write-Host "  $cerPath    （公钥，**需要和安装包一起发布**，用户导入后才能识别发布者）"
Write-Host "  $passPath   （PFX 口令）"
Write-Host ''
Write-Host '提醒：' -ForegroundColor Yellow
Write-Host '  1. 自签证书**无法消除 SmartScreen 拦截**，用户首次运行仍需点「仍要运行」。'
Write-Host '  2. 请在发布说明里附上 codesign.cer，并写明导入方法，否则签名显示为「不受信任」。'
Write-Host '  3. 这两个文件都不要提交进仓库。'
