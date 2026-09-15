param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\CursorSync\CursorSync.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$installerDir = Join-Path $root "artifacts\installer"
$iss = Join-Path $root "installer\CursorSync.iss"

function Find-Iscc {
    $candidates = @(
        Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe",
        Join-Path ${env:ProgramFiles} "Inno Setup 6\ISCC.exe"
    )
    $pf86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($pf86) {
        $candidates += Join-Path $pf86 "Inno Setup 6\ISCC.exe"
        $candidates += Join-Path $pf86 "Inno Setup 5\ISCC.exe"
    }
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

Write-Host "Publishing CursorSync $Version ($Runtime, self-contained)..."
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishReadyToRun=true `
    -p:SatelliteResourceLanguages=en`;de `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Get-ChildItem $publishDir -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

$iscc = Find-Iscc
if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 and re-run this script."
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "Building installer..."
& $iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed."
}

$setup = Get-ChildItem $installerDir -Filter "CursorSync-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Installer created: $($setup.FullName)"
Write-Host "Size: $([math]::Round($setup.Length / 1MB, 1)) MB"
