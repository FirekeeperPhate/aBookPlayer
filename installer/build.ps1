# Builds the aBookPlayer installers with Inno Setup.
#   full  : self-contained, includes the .NET runtime (no prerequisites)
#   light : framework-dependent, requires the .NET 10 Desktop Runtime (x64)
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Variants full,light]

param([string[]]$Variants = @('full', 'light'))

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$appDir = Split-Path $installerDir -Parent
$appProject = Join-Path $appDir 'aBookPlayer.csproj'
$script = Join-Path $installerDir 'aBookPlayer.iss'
$outDir = Join-Path $installerDir 'out'

# Inno Setup compiler: from the uninstall registry entry, else the default locations
$iscc = $null
$uninstallKeys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
foreach ($entry in Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Inno Setup*' }) {
    $candidate = Join-Path $entry.InstallLocation 'ISCC.exe'
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    $iscc = @("$env:ProgramFiles\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found' }

[xml]$csproj = [IO.File]::ReadAllText($appProject)
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'Version not found in aBookPlayer.csproj' }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory $outDir | Out-Null

foreach ($variant in $Variants) {
    Write-Host "=== $variant ($version)" -ForegroundColor Cyan
    $publishDir = Join-Path $outDir "publish\$variant"
    $selfContained = if ($variant -eq 'full') { 'true' } else { 'false' }

    dotnet publish $appProject -c Release -r win-x64 --self-contained $selfContained -o $publishDir -p:DebugType=None -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($variant)" }

    # The installer is x64-only: drop the Whisper native libraries for other architectures
    Get-ChildItem (Join-Path $publishDir 'runtimes') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force

    & $iscc /Q "/DVariant=$variant" "/DAppVersion=$version" "/DPublishDir=$publishDir" $script
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($variant)" }

    $suffix = if ($variant -eq 'full') { '' } else { '-light' }
    $setup = Join-Path $outDir "aBookPlayer-$version-x64$suffix-setup.exe"
    Write-Host ("  {0} ({1:N1} MB)" -f (Split-Path $setup -Leaf), ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
}

Remove-Item (Join-Path $outDir 'publish') -Recurse -Force
