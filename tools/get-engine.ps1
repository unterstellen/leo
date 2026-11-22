<#
.SYNOPSIS
  Fetches the pinned split-tunneling engine (mihomo, GPL-3.0) for development builds.

.DESCRIPTION
  Downloads the exact release below from github.com/MetaCubeX/mihomo, refuses it unless the SHA-256 matches,
  and extracts it to artifacts\engine\mihomo.exe (git-ignored). tools\dev-service.ps1 deploy copies it next to
  the service. Release builds compile mihomo from source in CI instead (LEO.md §17).

.PARAMETER FromZip
  Use an already-downloaded release zip instead of downloading (still checked against the SHA-256).
#>
param([string]$FromZip)
$ErrorActionPreference = 'Stop'
$Version = 'v1.19.34'
$Asset = "mihomo-windows-amd64-compatible-$Version.zip"
$Sha256 = 'CD6738D4648CC1A5F48049A1343B697AB510889D648F045C5C780FC1AB443A79'

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'artifacts\engine'
$exe = Join-Path $outDir 'mihomo.exe'
$stamp = Join-Path $outDir 'version.txt'

if ((Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq "$Version $Sha256")) {
    Write-Host "Engine $Version already present: $exe"
    return
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) $Asset
if ($FromZip) {
    Copy-Item $FromZip $zip -Force
} else {
    $url = "https://github.com/MetaCubeX/mihomo/releases/download/$Version/$Asset"
    Write-Host "Downloading $url"
    $ProgressPreference = 'SilentlyContinue' # makes Invoke-WebRequest fast in Windows PowerShell
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item $zip -Force
    throw "SHA-256 mismatch for $Asset (got $hash, expected $Sha256). Not using it."
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) "leo-engine-$([guid]::NewGuid().ToString('N'))"
Expand-Archive $zip -DestinationPath $tmp -Force
$found = Get-ChildItem $tmp -Filter 'mihomo*.exe' | Select-Object -First 1
if (-not $found) { throw "No mihomo executable inside $Asset" }
Copy-Item $found.FullName $exe -Force
Set-Content $stamp "$Version $Sha256"
Remove-Item $tmp -Recurse -Force
Remove-Item $zip -Force
Write-Host "Engine $Version ready: $exe"
