@echo off
setlocal enabledelayedexpansion

REM ========================================
REM  v2rayN Windows Packaging Script (.bat)
REM  Asset strategy (same as package-debian.sh):
REM    1. Try v2rayN-core-bin bundle first
REM    2. Always download latest geo assets
REM    3. If bundle fails, fall back to individual Xray + sing-box
REM
REM  Usage:
REM    package-windows.bat                  # Default x64, Release
REM    package-windows.bat arm64            # ARM64
REM    package-windows.bat x64 Debug        # x64, Debug config
REM ========================================

set "ARCH=%~1"
set "CONFIG=%~2"

if "%ARCH%"=="" set "ARCH=x64"
if "%CONFIG%"=="" set "CONFIG=Release"

if not "%ARCH%"=="x64" if not "%ARCH%"=="arm64" (
    echo ERROR: Invalid architecture "%ARCH%". Use x64 or arm64.
    exit /b 1
)
if not "%CONFIG%"=="Release" if not "%CONFIG%"=="Debug" (
    echo ERROR: Invalid configuration "%CONFIG%". Use Release or Debug.
    exit /b 1
)

if "%ARCH%"=="arm64" (set "RID=win-arm64") else (set "RID=win-x64")
if "%ARCH%"=="arm64" (set "BINLABEL=arm64") else (set "BINLABEL=64")

set "SCRIPT_DIR=%~dp0"
set "PUBLISH_DIR=%SCRIPT_DIR%v2rayN\v2rayN\bin\%CONFIG%\net10.0-windows10.0.19041.0\%RID%\publish"
set "OUTPUT_DIR=%SCRIPT_DIR%..\publish"
set "OUTPUT_ZIP=%OUTPUT_DIR%\v2rayN-windows-%BINLABEL%.zip"

echo ========================================
echo   v2rayN Windows Package Script (.bat^)
echo   Arch: %ARCH% (%RID%^)  Config: %CONFIG%
echo ========================================

REM ════════════════════════════════════════════════════════
REM 1. dotnet publish
REM ════════════════════════════════════════════════════════
echo.
echo [1/5] Publishing v2rayN (RID=%RID%^)...
dotnet publish "%SCRIPT_DIR%v2rayN\v2rayN\v2rayN.csproj" -c %CONFIG% -r %RID% -p:PublishSingleFile=false -p:SelfContained=true
if %ERRORLEVEL% neq 0 (
    echo ERROR: dotnet publish failed
    exit /b 1
)
echo   Publish OK: %PUBLISH_DIR%

REM ════════════════════════════════════════════════════════
REM 2. Prepare output directory
REM ════════════════════════════════════════════════════════
echo.
echo [2/5] Preparing output directory...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%" 2>nul
xcopy "%PUBLISH_DIR%\*" "%OUTPUT_DIR%\" /E /Q /Y /H
echo   Copied publish output to %OUTPUT_DIR%

REM ════════════════════════════════════════════════════════
REM 3+4+5. Download assets + package (PowerShell for complex ops)
REM ════════════════════════════════════════════════════════
echo.
echo [3/5] Downloading core binaries...
echo [4/5] Downloading geo assets...
echo [5/5] Creating zip archive...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$ErrorActionPreference='Stop'; ^
    Set-Location '%SCRIPT_DIR%'; ^
    $Arch='%ARCH%'; $binLabel='%BINLABEL%'; ^
    $outputDir='%OUTPUT_DIR%'; $outputZip='%OUTPUT_ZIP%'; ^
    $binDir=Join-Path $outputDir 'bin'; ^
    New-Item -ItemType Directory -Force $binDir | Out-Null; ^
    ^
    $bundleOk=$false; ^
    $coreUrl=\"https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/v2rayN-windows-$binLabel.zip\"; ^
    $coreZip=Join-Path $env:TEMP \"v2rayN-core-bundle.zip\"; ^
    $coreDir=Join-Path $env:TEMP \"v2rayN-core-bundle\"; ^
    ^
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; ^
    ^
    try { ^
        Write-Host '  Trying v2rayN-core-bin bundle...'; ^
        Invoke-WebRequest -Uri $coreUrl -OutFile $coreZip -UseBasicParsing; ^
        if (Test-Path $coreDir) { Remove-Item $coreDir -Recurse -Force }; ^
        Expand-Archive -Path $coreZip -DestinationPath $coreDir -Force; ^
        $nested=Get-ChildItem $coreDir -Directory | Select-Object -First 1; ^
        if ($nested) { ^
            $srcBin=Join-Path $nested.FullName 'bin'; ^
            if (Test-Path $srcBin) { ^
                Copy-Item -Path \"$srcBin\*\" -Destination $binDir -Recurse -Force ^
            } else { ^
                Copy-Item -Path \"$($nested.FullName)\*\" -Destination $binDir -Recurse -Force ^
            }; ^
            Write-Host '  Bundle extracted successfully' -ForegroundColor Green; ^
            $bundleOk=$true ^
        } ^
    } catch { ^
        Write-Host \"  Bundle download failed: $_\" -ForegroundColor Yellow ^
    } finally { ^
        Remove-Item $coreZip,$coreDir -Recurse -Force -ErrorAction SilentlyContinue ^
    }; ^
    ^
    if (-not $bundleOk) { ^
        Write-Host '  Falling back to individual core downloads...' -ForegroundColor Yellow; ^
        try { ^
            Write-Host '  Resolving latest Xray version...'; ^
            $xv=(Invoke-RestMethod -Uri 'https://api.github.com/repos/XTLS/Xray-core/releases/latest' -UseBasicParsing).tag_name.TrimStart('v'); ^
            $xu=if($Arch -eq 'arm64'){\"https://github.com/XTLS/Xray-core/releases/download/v$xv/Xray-windows-arm64-v8a.zip\"}else{\"https://github.com/XTLS/Xray-core/releases/download/v$xv/Xray-windows-64.zip\"}; ^
            Write-Host \"  Downloading Xray-core v$xv...\"; ^
            $xz=Join-Path $env:TEMP 'xray.zip'; $xt=Join-Path $env:TEMP 'xray-tmp'; ^
            Invoke-WebRequest -Uri $xu -OutFile $xz -UseBasicParsing; ^
            if(Test-Path $xt){Remove-Item $xt -Recurse -Force}; ^
            Expand-Archive -Path $xz -DestinationPath $xt -Force; ^
            $xe=Get-ChildItem $xt -Recurse -Filter 'xray.exe'|Select-Object -First 1; ^
            if($xe){New-Item -ItemType Directory -Force (Join-Path $binDir 'xray')|Out-Null;Copy-Item $xe.FullName (Join-Path $binDir 'xray') -Force;Write-Host '  Xray-core OK' -ForegroundColor Green}; ^
            Remove-Item $xz,$xt -Recurse -Force -ErrorAction SilentlyContinue ^
        } catch { Write-Host \"  WARNING: Xray download failed\" -ForegroundColor Yellow }; ^
        try { ^
            Write-Host '  Resolving latest sing-box version...'; ^
            $sv=(Invoke-RestMethod -Uri 'https://api.github.com/repos/SagerNet/sing-box/releases/latest' -UseBasicParsing).tag_name.TrimStart('v'); ^
            $su=if($Arch -eq 'arm64'){\"https://github.com/SagerNet/sing-box/releases/download/v$sv/sing-box-$sv-windows-arm64.zip\"}else{\"https://github.com/SagerNet/sing-box/releases/download/v$sv/sing-box-$sv-windows-amd64.zip\"}; ^
            Write-Host \"  Downloading sing-box v$sv...\"; ^
            $sz=Join-Path $env:TEMP 'singbox.zip'; $st=Join-Path $env:TEMP 'singbox-tmp'; ^
            Invoke-WebRequest -Uri $su -OutFile $sz -UseBasicParsing; ^
            if(Test-Path $st){Remove-Item $st -Recurse -Force}; ^
            Expand-Archive -Path $sz -DestinationPath $st -Force; ^
            $se=Get-ChildItem $st -Recurse -Filter 'sing-box.exe'|Select-Object -First 1; ^
            if($se){New-Item -ItemType Directory -Force (Join-Path $binDir 'sing_box')|Out-Null;Copy-Item $se.FullName (Join-Path $binDir 'sing_box') -Force;Write-Host '  sing-box OK' -ForegroundColor Green}; ^
            Remove-Item $sz,$st -Recurse -Force -ErrorAction SilentlyContinue ^
        } catch { Write-Host '  WARNING: sing-box download failed' -ForegroundColor Yellow } ^
    }; ^
    ^
    $mihomoDir=Join-Path $binDir 'mihomo'; ^
    if(Test-Path $mihomoDir){Remove-Item $mihomoDir -Recurse -Force;Write-Host '  Removed bundled mihomo'}; ^
    ^
    Write-Host '  Downloading geo assets...'; ^
    $srssDir=Join-Path $binDir 'srss'; ^
    New-Item -ItemType Directory -Force $srssDir|Out-Null; ^
    ^
    $geoUrls=@{ ^
        'geosite.dat'='https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat'; ^
        'geoip.dat'='https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat'; ^
        'geoip-only-cn-private.dat'='https://raw.githubusercontent.com/Loyalsoldier/geoip/release/geoip-only-cn-private.dat'; ^
        'Country.mmdb'='https://raw.githubusercontent.com/Loyalsoldier/geoip/release/Country.mmdb'; ^
        'geoip.metadb'='https://github.com/MetaCubeX/meta-rules-dat/releases/latest/download/geoip.metadb' ^
    }; ^
    foreach($k in $geoUrls.Keys){ ^
        try { ^
            Write-Host \"  Downloading $k...\"; ^
            Invoke-WebRequest -Uri $geoUrls[$k] -OutFile (Join-Path $binDir $k) -UseBasicParsing ^
        } catch { Write-Host \"  WARNING: Failed to download $k\" -ForegroundColor Yellow } ^
    }; ^
    ^
    $srsGeoIp=@('geoip-private','geoip-cn','geoip-facebook','geoip-fastly','geoip-google','geoip-netflix','geoip-telegram','geoip-twitter'); ^
    $srsGeoSite=@('geosite-cn','geosite-gfw','geosite-google','geosite-greatfire','geosite-geolocation-cn','geosite-category-ads-all','geosite-private'); ^
    foreach($n in $srsGeoIp){ ^
        try { ^
            Invoke-WebRequest -Uri \"https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geoip/$n.srs\" -OutFile \"$srssDir\$n.srs\" -UseBasicParsing ^
        } catch { Write-Host \"  WARNING: Failed to download $n.srs\" -ForegroundColor Yellow } ^
    }; ^
    foreach($n in $srsGeoSite){ ^
        try { ^
            Invoke-WebRequest -Uri \"https://raw.githubusercontent.com/2dust/sing-box-rules/refs/heads/rule-set-geosite/$n.srs\" -OutFile \"$srssDir\$n.srs\" -UseBasicParsing ^
        } catch { Write-Host \"  WARNING: Failed to download $n.srs\" -ForegroundColor Yellow } ^
    }; ^
    ^
    $xrayBinDir=Join-Path $binDir 'xray'; ^
    if(Test-Path $xrayBinDir){ ^
        foreach($f in @('geosite.dat','geoip.dat','geoip-only-cn-private.dat','Country.mmdb','geoip.metadb')){ ^
            $src=Join-Path $xrayBinDir $f; $dst=Join-Path $binDir $f; ^
            if((Test-Path $src) -and -not (Test-Path $dst)){Move-Item $src $dst -Force} ^
        } ^
    }; ^
    Write-Host '  Geo assets OK' -ForegroundColor Green; ^
    ^
    Write-Host '  Creating zip archive...'; ^
    if(Test-Path $outputZip){Remove-Item $outputZip -Force}; ^
    Compress-Archive -Path \"$outputDir\*\" -DestinationPath $outputZip -CompressionLevel Optimal; ^
    Write-Host \"  Archive: $outputZip\" -ForegroundColor Green; ^
    ^
    $size='{0:N1} MB' -f ((Get-ChildItem $outputDir -Recurse|Measure-Object -Property Length -Sum).Sum/1MB); ^
    Write-Host ''; ^
    Write-Host '========================================' -ForegroundColor Cyan; ^
    Write-Host '  Package complete!' -ForegroundColor Cyan; ^
    Write-Host \"  $outputZip\" -ForegroundColor Cyan; ^
    Write-Host \"  Size: $size\" -ForegroundColor Cyan; ^
    Write-Host '========================================' -ForegroundColor Cyan"

if %ERRORLEVEL% neq 0 (
    echo ERROR: Asset download or packaging failed
    exit /b 1
)

exit /b 0
