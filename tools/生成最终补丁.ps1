param(
    [string]$GameRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Split-Path -Parent $projectRoot }
$managedDir = Join-Path $GameRoot 'Erratum_Data\Managed'
$pluginProject = Join-Path $projectRoot 'plugin-src\ErratumChinesePatch\ErratumChinesePatch.csproj'
$pluginOutput = Join-Path $projectRoot 'plugin-src\ErratumChinesePatch\bin\Release\netstandard2.1\ErratumChinesePatch.dll'
$pluginTarget = Join-Path $projectRoot 'payload\BepInEx\plugins\ErratumChinesePatch\ErratumChinesePatch.dll'
$fontTarget = Join-Path $projectRoot 'payload\BepInEx\plugins\ErratumChinesePatch\fonts\SourceHanSansSC-Regular.otf'
$fontLicenseTarget = Join-Path $projectRoot 'payload\BepInEx\plugins\ErratumChinesePatch\fonts\LICENSE.SourceHanSans.txt'
$dist = Join-Path $projectRoot 'dist'
$zip = Join-Path $dist 'Erratum-简体中文补丁.zip'

if (-not (Test-Path -LiteralPath (Join-Path $managedDir 'UnityEngine.dll'))) {
    throw "找不到游戏程序集目录：$managedDir。请用 -GameRoot 指定 Erratum 游戏根目录。"
}
if (-not (Test-Path -LiteralPath $fontTarget) -or -not (Test-Path -LiteralPath $fontLicenseTarget)) {
    throw 'payload 中缺少 Source Han Sans SC 字体或其许可证。'
}

& (Join-Path $projectRoot 'tools\编译本地化.ps1')

dotnet build $pluginProject -c Release --nologo -p:ErratumManagedDir="$managedDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pluginTarget) | Out-Null
Copy-Item -LiteralPath $pluginOutput -Destination $pluginTarget -Force
New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $projectRoot 'payload\*') -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Format-List
