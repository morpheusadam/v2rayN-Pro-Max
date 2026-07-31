<#
Builds the two release archives.

The first release shipped without a bin folder and claimed the app would fetch the cores
on first run. It does not, so every ping and speed test failed and Auto Mode had nothing
to work with. This script exists so the cores are part of the archive by construction
rather than by remembering to copy them.

    pwsh -File tools\package-release.ps1

Windows cores are taken from a known-good local bin folder; Linux cores are downloaded
from the upstream projects, because the Windows executables are useless there. The geo
rule files are platform independent and are shared between the two.
#>

[CmdletBinding()]
param(
    [string]$Dotnet = "C:\Program Files\dotnet\dotnet.exe",
    [string]$Project = "$PSScriptRoot\..\v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj",
    [string]$OutRoot = "$PSScriptRoot\..\..\publish",
    # Windows cores and the geo files are copied from here.
    [string]$WindowsBin = "$PSScriptRoot\..\..\bin",
    [switch]$SkipLinux
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if (-not (Test-Path $Dotnet)) { throw "dotnet not found at $Dotnet" }
if (-not (Test-Path $WindowsBin)) { throw "windows bin folder not found at $WindowsBin" }

$OutRoot = (Resolve-Path $OutRoot).Path
$WindowsBin = (Resolve-Path $WindowsBin).Path
$work = Join-Path $OutRoot "_cores"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Files the cores read at runtime. Same bytes on every platform, so they are copied out of
# the Windows bin rather than downloaded twice.
$geoFiles = @("geoip.dat", "geosite.dat", "Country.mmdb", "geoip.metadb", "geoip-only-cn-private.dat")

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Get-LatestTag([string]$repo) {
    (Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ "User-Agent" = "v2rayN-Pro-Max-packager" }).tag_name
}

function Save-Url([string]$url, [string]$path) {
    Write-Host "    $url"
    Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
}

# ---------------------------------------------------------------- publish ----

function Invoke-Publish([string]$rid, [string]$outDir) {
    Write-Step "publish $rid"
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    & $Dotnet publish $Project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }

    # DebugType=none covers our own assemblies but not the Skia/HarfBuzz native packages,
    # which drop their pdbs beside the executable.
    Get-ChildItem $outDir -Filter "*.pdb" -File | Remove-Item -Force
}

# ------------------------------------------------------------- linux cores ----

function New-LinuxBin([string]$binDir) {
    Write-Step "fetch linux cores"
    if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $binDir "xray") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $binDir "sing_box") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $binDir "mihomo") | Out-Null

    # Xray — a plain zip with the binary at the root.
    $tag = Get-LatestTag "XTLS/Xray-core"
    $zip = Join-Path $work "xray-linux-64.zip"
    Save-Url "https://github.com/XTLS/Xray-core/releases/download/$tag/Xray-linux-64.zip" $zip
    $tmp = Join-Path $work "xray"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    Copy-Item (Join-Path $tmp "xray") (Join-Path $binDir "xray\xray") -Force

    # sing-box — tar.gz with a versioned top-level folder.
    $tag = Get-LatestTag "SagerNet/sing-box"
    $ver = $tag.TrimStart("v")
    $tgz = Join-Path $work "sing-box-linux-amd64.tar.gz"
    Save-Url "https://github.com/SagerNet/sing-box/releases/download/$tag/sing-box-$ver-linux-amd64.tar.gz" $tgz
    $tmp = Join-Path $work "sing-box"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    tar -xzf $tgz -C $tmp
    if ($LASTEXITCODE -ne 0) { throw "extracting sing-box failed" }
    $singbox = Get-ChildItem $tmp -Recurse -File -Filter "sing-box" | Select-Object -First 1
    if (-not $singbox) { throw "sing-box binary not found in the archive" }
    Copy-Item $singbox.FullName (Join-Path $binDir "sing_box\sing-box") -Force

    # mihomo — a bare gzip stream, not a tarball.
    $tag = Get-LatestTag "MetaCubeX/mihomo"
    $gz = Join-Path $work "mihomo-linux-amd64.gz"
    Save-Url "https://github.com/MetaCubeX/mihomo/releases/download/$tag/mihomo-linux-amd64-v1-$tag.gz" $gz
    Expand-Gzip $gz (Join-Path $binDir "mihomo\mihomo")

    foreach ($f in $geoFiles) {
        $src = Join-Path $WindowsBin $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $binDir $f) -Force }
    }
    $srss = Join-Path $WindowsBin "srss"
    if (Test-Path $srss) { Copy-Item $srss (Join-Path $binDir "srss") -Recurse -Force }
}

function Expand-Gzip([string]$source, [string]$target) {
    $in = [System.IO.File]::OpenRead($source)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $out = [System.IO.File]::Create($target)
            try { $gz.CopyTo($out) } finally { $out.Dispose() }
        } finally { $gz.Dispose() }
    } finally { $in.Dispose() }
}

# ----------------------------------------------------------------- readme ----

function Write-Readme([string]$dir, [string]$platform, [string]$runLine) {
    $text = @"
v2rayN Pro Max - $platform
============================

1. Extract this whole folder, keeping "bin" next to the program
2. $runLine

The proxy cores (Xray, sing-box, mihomo) and the geo rule files are already in the "bin"
folder, so nothing has to be downloaded before the first test. Newer cores can be fetched
later from Help -> Check Update.

Getting started with Auto Mode
------------------------------
1. Click the gear icon beside the orange "automode" button
2. Paste your subscription links, or click "Import file" and choose sub.example.txt
3. Close the settings window and press "automode"

A run takes three to five minutes. When it finishes, the group "Auto Mode" holds the
fastest servers it could verify, sorted by measured download speed.

Notes
-----
- Application self-update is disabled on purpose, so this build keeps its features and
  styling. The proxy cores still update normally from their own projects.
- sub.example.txt contains 76 public subscription links to get started. They are not
  operated or verified by this project.

https://github.com/morpheusadam/v2rayN-Pro-Max
"@
    Set-Content -Path (Join-Path $dir "README.txt") -Value $text -Encoding UTF8
}

# ------------------------------------------------------------------ verify ----

function Test-Payload([string]$dir, [string]$exe, [string[]]$cores) {
    $missing = @()
    if (-not (Test-Path (Join-Path $dir $exe))) { $missing += $exe }
    foreach ($c in $cores) {
        if (-not (Test-Path (Join-Path $dir $c))) { $missing += $c }
    }
    foreach ($f in $geoFiles) {
        if (-not (Test-Path (Join-Path $dir "bin\$f"))) { $missing += "bin/$f" }
    }
    if ($missing.Count -gt 0) { throw "package $dir is incomplete: $($missing -join ', ')" }
    Write-Host "    payload ok" -ForegroundColor Green
}

# -------------------------------------------------------------------- run ----

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$sample = Join-Path $repoRoot "sub.example.txt"

# Windows
$winDir = Join-Path $OutRoot "win-x64"
Invoke-Publish "win-x64" $winDir
Write-Step "assemble windows payload"
Copy-Item $WindowsBin (Join-Path $winDir "bin") -Recurse -Force
if (Test-Path $sample) { Copy-Item $sample $winDir -Force }
Write-Readme $winDir "Windows x64" "Run v2rayN.exe"
Test-Payload $winDir "v2rayN.exe" @("bin\xray\xray.exe", "bin\sing_box\sing-box.exe", "bin\mihomo\mihomo.exe")

$winZip = Join-Path $OutRoot "v2rayN-Pro-Max-windows-64.zip"
if (Test-Path $winZip) { Remove-Item $winZip -Force }
Write-Step "zip windows"
Compress-Archive -Path (Join-Path $winDir "*") -DestinationPath $winZip -CompressionLevel Optimal

# Linux
if (-not $SkipLinux) {
    $linDir = Join-Path $OutRoot "linux-x64"
    Invoke-Publish "linux-x64" $linDir
    Write-Step "assemble linux payload"
    New-LinuxBin (Join-Path $linDir "bin")
    if (Test-Path $sample) { Copy-Item $sample $linDir -Force }
    Write-Readme $linDir "Linux x64" "chmod +x ./v2rayN ./bin/xray/xray ./bin/sing_box/sing-box ./bin/mihomo/mihomo  then run ./v2rayN"
    Test-Payload $linDir "v2rayN" @("bin/xray/xray", "bin/sing_box/sing-box", "bin/mihomo/mihomo")

    $linZip = Join-Path $OutRoot "v2rayN-Pro-Max-linux-64.zip"
    if (Test-Path $linZip) { Remove-Item $linZip -Force }
    Write-Step "zip linux"
    Compress-Archive -Path (Join-Path $linDir "*") -DestinationPath $linZip -CompressionLevel Optimal
}

Write-Step "done"
Get-ChildItem (Join-Path $OutRoot "v2rayN-Pro-Max-*.zip") |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
