<#
.SYNOPSIS
  Dev helper: install / uninstall / restart the LEO service from the local Debug build.

.DESCRIPTION
  DEV ONLY. Registers the service binary straight from bin\, a user-writable folder, which a release
  must never do (the MSI installs to Program Files). Self-elevates via UAC.

.EXAMPLE
  .\tools\dev-service.ps1 install
  .\tools\dev-service.ps1 uninstall
  .\tools\dev-service.ps1 restart
#>
param(
    [Parameter(Mandatory)][ValidateSet('install', 'uninstall', 'restart')] [string] $Action,
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\LEO.Service\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\LEO.Service.exe"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", $Action, '-Configuration', $Configuration)
    return
}

if (-not (Test-Path $exe)) { throw "Build first: dotnet build LEO.slnx ($exe not found)" }

switch ($Action) {
    'install'   { & $exe install --start }
    'uninstall' { & $exe uninstall }
    'restart'   { Restart-Service -Name LEO -Force; Get-Service LEO }
}
Write-Host "Done. Press Enter to close."; [void][Console]::ReadLine()
