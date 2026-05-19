<#
Windows packaging script — run in VS Code terminal
Usage:
  .\package-windows.ps1                  # Default x64, Release
  .\package-windows.ps1 -Arch arm64      # ARM64
  .\package-windows.ps1 -Config Debug    # Debug config

Asset strategy (same as package-debian.sh):
  1. Try v2rayN-core-bin bundle first (pre-structured bin/)
  2. Always download latest geo assets (dat/mmdb/metadb/srs)
  3. If bundle fails, fall back to individual Xray + sing-box from GitHub releases
#>

param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [ValidateSet("Release", "Debug")]
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ── Platform mapping ──
$rid         = if ($Arch -eq "arm64") { "win-arm64" } else { "win-x64" }
$binLabel    = if ($Arch -eq "arm64") { "arm64" } else { "64" }
$publishDir  = Join-Path $PSScriptRoot "v2rayN\v2rayN\bin\$Config\net10.0-windows10.0.19041.0\$rid\publish"
$outputDir   = Join-Path $PSScriptRoot "..\publish"
$outputZip   = Join-Path $outputDir "v2rayN-windows-$binLabel.zip"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  v2rayN Windows Package Script"         -ForegroundColor Cyan
Write-Host "  Arch: $Arch ($rid)  Config: $Config"    -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ═══════════════════════════════════════════════════════════════
# 1. dotnet publish
# ═══════════════════════════════════════════════════════════════
Write-Host "`n[1/5] Publishing v2rayN (RID=$rid)..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\v2rayN\v2rayN\v2rayN.csproj" `
    -c $Config `
    -r $rid `
    -p:PublishSingleFile=false `
    -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "  Publish OK: $publishDir" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# 2. Prepare output directory
# ═══════════════════════════════════════════════════════════════
Write-Host "`n[2/5] Preparing output directory..." -ForegroundColor Yellow
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Force $outputDir | Out-Null
Copy-Item -Path "$publishDir\*" -Destination $outputDir -Recurse -Force
Write-Host "  Copied publish output to $outputDir" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# 3. Download core binaries (bundle first, then fallback)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n[3/5] Downloading core binaries..." -ForegroundColor Yellow

$binDir      = Join-Path $outputDir "bin"
$bundleOk    = $false
$coreUrl     = "https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/v2rayN-windows-$binLabel.zip"
$coreZip     = Join-Path $env:TEMP "v2rayN-core-windows-$binLabel.zip"
$coreDir     = Join-Path $env:TEMP "v2rayN-core-windows-$binLabel"

# 3a. Try bundle first
try {
    Write-Host "  Trying v2rayN-core-bin bundle: $coreUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $coreUrl -OutFile $coreZip -UseBasicParsing

    if (Test-Path $coreDir) { Remove-Item $coreDir -Recurse -Force }
    Expand-Archive -Path $coreZip -DestinationPath $coreDir -Force

    # Bundle structure: v2rayN-windows-64/bin/{xray,sing_box,...}
    $nested = Get-ChildItem $coreDir -Directory | Select-Object -First 1
    if ($nested) {
        $srcBin = Join-Path $nested.FullName "bin"
        if (Test-Path $srcBin) {
            Copy-Item -Path "$srcBin\*" -Destination $binDir -Recurse -Force
        } else {
            Copy-Item -Path "$($nested.FullName)\*" -Destination $binDir -Recurse -Force
        }
        Write-Host "  Bundle extracted successfully" -ForegroundColor Green
        $bundleOk = $true
    }
} catch {
    Write-Host "  Bundle download failed: $_" -ForegroundColor Yellow
} finally {
    Remove-Item $coreZip, $coreDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 3b. Fallback: download Xray + sing-box individually
if (-not $bundleOk) {
    Write-Host "  Falling back to individual core downloads..." -ForegroundColor Yellow

    # ── Xray-core ──
    try {
        Write-Host "  Resolving latest Xray version..."
        $xrayApiUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/latest"
        $xrayVer = (Invoke-RestMethod -Uri $xrayApiUrl -UseBasicParsing).tag_name.TrimStart('v')
        $xrayUrl  = if ($Arch -eq "arm64") {
            "https://github.com/XTLS/Xray-core/releases/download/v$xrayVer/Xray-windows-arm64-v8a.zip"
        } else {
            "https://github.com/XTLS/Xray-core/releases/download/v$xrayVer/Xray-windows-64.zip"
        }
        Write-Host "  Downloading Xray-core v$xrayVer..."
        $xrayZip = Join-Path $env:TEMP "xray.zip"
        $xrayTmp = Join-Path $env:TEMP "xray-extract"
        Invoke-WebRequest -Uri $xrayUrl -OutFile $xrayZip -UseBasicParsing
        if (Test-Path $xrayTmp) { Remove-Item $xrayTmp -Recurse -Force }
        Expand-Archive -Path $xrayZip -DestinationPath $xrayTmp -Force
        $xrayExe = Get-ChildItem $xrayTmp -Recurse -Filter "xray.exe" | Select-Object -First 1
        if ($xrayExe) {
            $xrayDest = Join-Path $binDir "xray"
            New-Item -ItemType Directory -Force $xrayDest | Out-Null
            Copy-Item $xrayExe.FullName $xrayDest -Force
            Write-Host "  Xray-core v$xrayVer OK" -ForegroundColor Green
        }
        Remove-Item $xrayZip, $xrayTmp -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "  WARNING: Xray download failed: $_" -ForegroundColor Yellow
    }

    # ── sing-box ──
    try {
        Write-Host "  Resolving latest sing-box version..."
        $singApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
        $singVer = (Invoke-RestMethod -Uri $singApiUrl -UseBasicParsing).tag_name.TrimStart('v')
        $singUrl  = if ($Arch -eq "arm64") {
            "https://github.com/SagerNet/sing-box/releases/download/v$singVer/sing-box-$singVer-windows-arm64.zip"
        } else {
            "https://github.com/SagerNet/sing-box/releases/download/v$singVer/sing-box-$singVer-windows-amd64.zip"
        }
        Write-Host "  Downloading sing-box v$singVer..."
        $singZip = Join-Path $env:TEMP "singbox.zip"
        $singTmp = Join-Path $env:TEMP "singbox-extract"
        Invoke-WebRequest -Uri $singUrl -OutFile $singZip -UseBasicParsing
        if (Test-Path $singTmp) { Remove-Item $singTmp -Recurse -Force }
        Expand-Archive -Path $singZip -DestinationPath $singTmp -Force
        $singExe = Get-ChildItem $singTmp -Recurse -Filter "sing-box.exe" | Select-Object -First 1
        if ($singExe) {
            $singDest = Join-Path $binDir "sing_box"
            New-Item -ItemType Directory -Force $singDest | Out-Null
            Copy-Item $singExe.FullName $singDest -Force
            Write-Host "  sing-box v$singVer OK" -ForegroundColor Green
        }
        Remove-Item $singZip, $singTmp -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "  WARNING: sing-box download failed: $_" -ForegroundColor Yellow
    }
}

# ── Remove mihomo if bundle included it (v2rayN manages mihomo separately) ──
$mihomoDir = Join-Path $binDir "mihomo"
if (Test-Path $mihomoDir) {
    Remove-Item $mihomoDir -Recurse -Force
    Write-Host "  Removed bundled mihomo (managed separately)" -ForegroundColor Yellow
}

# ═══════════════════════════════════════════════════════════════
# 4. Download latest geo assets (always, to ensure freshness)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n[4/5] Downloading geo assets..." -ForegroundColor Yellow

New-Item -ItemType Directory -Force $binDir | Out-Null
$srssDir = Join-Path $binDir "srss"
New-Item -ItemType Directory -Force $srssDir | Out-Null

$geoAssets = @(
    @{Url="https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";              Dest="$binDir\geosite.dat"},
    @{Url="https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";                Dest="$binDir\geoip.dat"},
    @{Url="https://raw.githubusercontent.com/Loyalsoldier/geoip/release/geoip-only-cn-private.dat";            Dest="$binDir\geoip-only-cn-private.dat"},
    @{Url="https://raw.githubusercontent.com/Loyalsoldier/geoip/release/Country.mmdb";                         Dest="$binDir\Country.mmdb"},
    @{Url="https://github.com/MetaCubeX/meta-rules-dat/releases/latest/download/geoip.metadb";                 Dest="$binDir\geoip.metadb"}
)

$srsFiles = @(
    "geoip-private", "geoip-cn", "geoip-facebook", "geoip-fastly",
    "geoip-google", "geoip-netflix", "geoip-telegram", "geoip-twitter"
)
$srsFilesGeosite = @(
    "geosite-cn", "geosite-gfw", "geosite-google", "geosite-greatfire",
    "geosite-geolocation-cn", "geosite-category-ads-all", "geosite-private"
)

foreach ($asset in $geoAssets) {
    try {
        $name = Split-Path $asset.Dest -Leaf
        Write-Host "  Downloading $name..."
        Invoke-WebRequest -Uri $asset.Url -OutFile $asset.Dest -UseBasicParsing
    } catch {
        Write-Host "  WARNING: Failed to download $name" -ForegroundColor Yellow
    }
}

foreach ($name in $srsFiles) {
    try {
        $url = "https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geoip/$name.srs"
        Write-Host "  Downloading $name.srs..."
        Invoke-WebRequest -Uri $url -OutFile "$srssDir\$name.srs" -UseBasicParsing
    } catch {
        Write-Host "  WARNING: Failed to download $name.srs" -ForegroundColor Yellow
    }
}

foreach ($name in $srsFilesGeosite) {
    try {
        $url = "https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geosite/$name.srs"
        Write-Host "  Downloading $name.srs..."
        Invoke-WebRequest -Uri $url -OutFile "$srssDir\$name.srs" -UseBasicParsing
    } catch {
        Write-Host "  WARNING: Failed to download $name.srs" -ForegroundColor Yellow
    }
}

# ── Unify geo layout: move dat files from bin/xray/ to bin/ if bundle put them there ──
$xrayBinDir = Join-Path $binDir "xray"
if (Test-Path $xrayBinDir) {
    $geoFiles = @("geosite.dat", "geoip.dat", "geoip-only-cn-private.dat", "Country.mmdb", "geoip.metadb")
    foreach ($f in $geoFiles) {
        $src = Join-Path $xrayBinDir $f
        $dst = Join-Path $binDir $f
        if ((Test-Path $src) -and -not (Test-Path $dst)) {
            Move-Item $src $dst -Force
        }
    }
}
Write-Host "  Geo assets OK" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# 5. Package as zip
# ═══════════════════════════════════════════════════════════════
Write-Host "`n[5/5] Creating zip archive..." -ForegroundColor Yellow
if (Test-Path $outputZip) { Remove-Item $outputZip -Force }
Compress-Archive -Path "$outputDir\*" -DestinationPath $outputZip -CompressionLevel Optimal
Write-Host "  Archive: $outputZip" -ForegroundColor Green

# ═══════════════════════════════════════════════════════════════
# Summary
# ═══════════════════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Package complete!"                       -ForegroundColor Cyan
Write-Host "  $outputZip"                              -ForegroundColor Cyan

$publishSize = if (Test-Path $outputDir) {
    "{0:N1} MB" -f ((Get-ChildItem $outputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB)
} else { "N/A" }
Write-Host "  Size: $publishSize" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
