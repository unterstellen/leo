<#
.SYNOPSIS
  Builds a local, UNSIGNED LEO release payload and MSI for testing (LEO.md §24.1, §24.2, §24.9).

.DESCRIPTION
  1. Publishes LEO.App and LEO.Service self-contained (win-x64, ReadyToRun, English only) into separate folders.
  2. Merges them into one install folder (artifacts\release\payload). The build FAILS if both publishes produce the
     same file name with different content (a version clash); identical shared files (the .NET runtime) are kept once.
  3. Adds engine\mihomo.exe (the pinned dev engine from tools\get-engine.ps1; release CI builds it from source) and
     LICENSE.
  4. Lists every PE file with its Authenticode status: what the release workflow would have to sign.
  5. Builds artifacts\release\LEO-<x.y.z>-x64.msi with WiX and writes SHA256SUMS.txt.

  Never signs anything: release builds are signed only in GitHub Actions (LEO.md §24.3). Smart App Control blocks
  unsigned builds, so test the MSI on a PC (or VM) where it's off.
  Doesn't install anything on this PC.

.PARAMETER Version
  x.y.z for the MSI and the binaries. Default: the <Version> in Directory.Build.props.

.PARAMETER SkipMsi
  Only build and check the payload (the release workflow signs it next).

.PARAMETER MsiOnly
  Only build the MSI from an existing artifactselease\payload (the release workflow, after signing the payload).
#>
param(
    [string]$Version,
    [switch]$SkipMsi,
    [switch]$MsiOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\release'
$appOut = Join-Path $out 'publish-app'
$svcOut = Join-Path $out 'publish-service'
$payload = Join-Path $out 'payload'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Version')." }
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($parts[0] -gt 255 -or $parts[1] -gt 255 -or $parts[2] -gt 65535) {
    throw "Windows Installer allows major <= 255, minor <= 255, build <= 65535 (LEO.md §24.2)."
}

if (-not $MsiOnly) { Write-Host "LEO ${Version}: unsigned test build" -ForegroundColor Yellow }

if (-not $MsiOnly) {
# ── 1. Publish ───────────────────────────────────────────────────────────────
Remove-Item -Recurse -Force $appOut, $svcOut, $payload -ErrorAction SilentlyContinue
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", '-p:PublishReadyToRun=true', '-nologo')
dotnet publish (Join-Path $root 'src\LEO.Service\LEO.Service.csproj') @common -o $svcOut
if ($LASTEXITCODE -ne 0) { throw 'Publishing LEO.Service failed.' }
dotnet publish (Join-Path $root 'src\LEO.App\LEO.App.csproj') @common -o $appOut
if ($LASTEXITCODE -ne 0) { throw 'Publishing LEO.App failed.' }

# ── 2. Merge into one install folder, refusing version clashes ───────────────
New-Item -ItemType Directory -Force $payload | Out-Null
robocopy $appOut $payload /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the app payload failed.' }
$clashes = @()
Get-ChildItem $svcOut -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($svcOut.Length).TrimStart('\')
    $dest = Join-Path $payload $rel
    if (Test-Path $dest) {
        if ((Get-FileHash $dest -Algorithm SHA256).Hash -ne (Get-FileHash $_.FullName -Algorithm SHA256).Hash) { $clashes += $rel }
    } else {
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }
}
if ($clashes.Count -gt 0) {
    $clashes | ForEach-Object { Write-Host "  clash: $_" -ForegroundColor Red }
    throw "App and service publish the same file with different content ($($clashes.Count)). Align the package versions, or fall back to two folders (LEO.md §24.1)."
}

# ── 3. Engine + licence ──────────────────────────────────────────────────────
$engine = Join-Path $root 'artifacts\engine\mihomo.exe'
if (-not (Test-Path $engine)) { throw "No engine at $engine. Run tools\get-engine.ps1 first." }
New-Item -ItemType Directory -Force (Join-Path $payload 'engine') | Out-Null
Copy-Item $engine (Join-Path $payload 'engine\mihomo.exe')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $payload 'LICENSE.txt')
Get-ChildItem $payload -Recurse -Filter *.pdb | Remove-Item

# ── 4. PE files and their signatures ─────────────────────────────────────────
$pe = Get-ChildItem $payload -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll', '.sys' }
$status = $pe | ForEach-Object {
    $sig = Get-AuthenticodeSignature $_.FullName
    [pscustomobject]@{
        File   = $_.FullName.Substring($payload.Length).TrimStart('\')
        Status = $sig.Status
        Signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.GetNameInfo('SimpleName', $false) } else { '' }
    }
}
$unsigned = @($status | Where-Object { $_.Status -ne 'Valid' })
$status | Sort-Object File | Format-Table -AutoSize | Out-String -Width 200 | Set-Content (Join-Path $out 'payload-signatures.txt') -Encoding utf8
$size = ($pe | Measure-Object Length -Sum).Sum
$all = Get-ChildItem $payload -Recurse -File
Write-Host ("Payload: {0} files, {1:N1} MB; {2} PE files, {3} not signed (a release signs LEO's own and unsigned third-party ones, §24.3)" -f `
    $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB), $pe.Count, $unsigned.Count)
$unsigned | ForEach-Object { Write-Host "  unsigned: $($_.File)" }

if ($SkipMsi) { return }
}

# ── 5. MSI ───────────────────────────────────────────────────────────────────
$msiOut = Join-Path $out 'msi'
dotnet build (Join-Path $root 'installer\LEO.Installer.wixproj') -c Release -nologo "-p:ProductVersion=$Version" "-p:PayloadDir=$payload\" -o $msiOut
if ($LASTEXITCODE -ne 0) { throw 'Building the MSI failed.' }
$msi = Join-Path $out "LEO-$Version-x64.msi"
Copy-Item (Join-Path $msiOut "LEO-$Version-x64.msi") $msi -Force
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  LEO-$Version-x64.msi" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Write-Host ("MSI: {0} ({1:N1} MB), SHA-256 {2}" -f $msi, ((Get-Item $msi).Length / 1MB), $hash) -ForegroundColor Green
if (-not $MsiOnly) {
    Write-Host 'Unsigned test build: Smart App Control blocks it. Install only on a test PC or VM, never as a release.' -ForegroundColor Yellow
}
