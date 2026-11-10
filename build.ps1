# 从固定版本的 NuGet 包构建 Windows x64 应用，可选运行本地页面测试。
param(
    [string]$PackageDirectory,
    [string]$ModelsDirectory,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cache = Join-Path $root '.cache'
$out = Join-Path $root 'build'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 编译器未找到。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $cache, $out, (Join-Path $out 'x64'), (Join-Path $out 'tessdata') | Out-Null

function Get-Package([string]$Id, [string]$Version, [string]$LocalName) {
    if ($PackageDirectory) { return (Join-Path $PackageDirectory $LocalName) }
    $archive = Join-Path $cache "$Id.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$Id/$Version/$Id.$Version.nupkg" -OutFile "$archive.partial"
        Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    }
    return $archive
}

function Copy-PackageFiles([string]$ArchivePath, [hashtable]$Entries) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entryName in $Entries.Keys) {
            $entry = $archive.GetEntry($entryName)
            if ($null -eq $entry) { throw "NuGet 包缺少文件：$entryName" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $out $Entries[$entryName]), $true)
        }
    } finally { $archive.Dispose() }
}

Copy-PackageFiles (Get-Package 'microsoft.web.webview2' '1.0.2903.40' 'webview2.nupkg') @{
    'lib/net462/Microsoft.Web.WebView2.Core.dll' = 'Microsoft.Web.WebView2.Core.dll'
    'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'Microsoft.Web.WebView2.WinForms.dll'
    'runtimes/win-x64/native/WebView2Loader.dll' = 'WebView2Loader.dll'
    'LICENSE.txt' = 'WebView2-LICENSE.txt'
    'NOTICE.txt' = 'WebView2-NOTICE.txt'
}
Copy-PackageFiles (Get-Package 'tesseract' '5.2.0' 'tesseract.nupkg') @{
    'lib/net47/Tesseract.dll' = 'Tesseract.dll'
    'x64/leptonica-1.82.0.dll' = 'x64\leptonica-1.82.0.dll'
    'x64/tesseract50.dll' = 'x64\tesseract50.dll'
}

foreach ($name in 'chi_sim.traineddata', 'eng.traineddata') {
    $model = if ($ModelsDirectory) { Join-Path $ModelsDirectory $name } else { Join-Path $cache $name }
    if (-not $ModelsDirectory -and -not (Test-Path -LiteralPath $model)) {
        Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$name" -OutFile "$model.partial"
        Move-Item -LiteralPath "$model.partial" -Destination $model -Force
    }
    Copy-Item -LiteralPath $model -Destination (Join-Path $out 'tessdata') -Force
}

$refs = @('System.Windows.Forms.dll','System.Drawing.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Security.dll')
$refs += @('Tesseract.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll') | ForEach-Object { Join-Path $out $_ }
$refs = $refs | ForEach-Object { "/r:$_" }
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs' | ForEach-Object { $_.FullName }
$manifest = Join-Path $root 'src\应用清单.manifest'
$exe = Join-Path $out '学习通桌面助手.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 "/win32manifest:$manifest" "/out:$exe" $refs $sources
if ($LASTEXITCODE -ne 0) { throw "应用编译失败（退出码 $LASTEXITCODE）" }
Write-Output "编译完成：$exe"

if ($Test) {
    $testExe = Join-Path $out 'ReadSmoke.exe'
    & $compiler /nologo /codepage:65001 /target:exe /platform:x64 /main:ReadSmoke "/out:$testExe" $refs $sources (Join-Path $root 'tests\ReadSmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw "测试编译失败（退出码 $LASTEXITCODE）" }
    & $testExe (Join-Path $root 'tests\fixtures')
    if ($LASTEXITCODE -ne 0) { throw "测试失败（退出码 $LASTEXITCODE）" }
}
