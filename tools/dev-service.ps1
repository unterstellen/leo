<#
.SYNOPSIS
  Dev helper for the LEO service: build + deploy to Program Files, uninstall, restart, or tail logs.

.DESCRIPTION
  deploy     Builds LEO.Service (Debug), then — elevated, one UAC prompt — stops the service, copies the build to
             C:\Program Files\UNTERSTELLEN\LEO\Service, (re)registers it there and starts it.
             Running from Program Files (admin-only writable) instead of bin\ keeps SYSTEM off user-writable
             paths and lets you rebuild while the service runs. The MSI (M8) replaces this.
  uninstall  Stops (LEO hands metrics back to Windows) and removes the service. Leaves files and config.
  restart    Restarts the service.
  logs       Shows the last 40 lines of today's service log.

.EXAMPLE
  .\tools\dev-service.ps1 deploy
#>
param(
    [Parameter(Mandatory)][ValidateSet('deploy', 'uninstall', 'restart', 'logs')] [string] $Action,
    [string] $Configuration = 'Debug',
    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$buildDir = Join-Path $root "src\LEO.Service\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$installDir = Join-Path $env:ProgramFiles 'UNTERSTELLEN\LEO\Service'
$exe = Join-Path $installDir 'LEO.Service.exe'

if ($Action -eq 'logs') {
    $log = Get-ChildItem "$env:ProgramData\UNTERSTELLEN\LEO\logs\service-*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
    Get-Content $log.FullName -Tail 40
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Action -eq 'deploy') {
        # Build as the normal user so bin\ and obj\ stay user-owned.
        dotnet build (Join-Path $root 'src\LEO.Service\LEO.Service.csproj') -c $Configuration -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", $Action, '-Configuration', $Configuration, '-Elevated')
    if ($p.ExitCode -ne 0) { throw "Elevated step failed (exit $($p.ExitCode)). See $env:TEMP\leo-dev-service.log" }
    Get-Content "$env:TEMP\leo-dev-service.log" -ErrorAction SilentlyContinue
    return
}

Start-Transcript -Path "$env:TEMP\leo-dev-service.log" -Force | Out-Null
try {
    $svc = Get-Service LEO -ErrorAction SilentlyContinue
    switch ($Action) {
        'deploy' {
            if (-not (Test-Path (Join-Path $buildDir 'LEO.Service.exe'))) { throw "No build at $buildDir" }
            if ($svc -and $svc.Status -ne 'Stopped') {
                Stop-Service LEO -Force
                (Get-Service LEO).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
            }
            New-Item -ItemType Directory -Force $installDir | Out-Null
            robocopy $buildDir $installDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy $LASTEXITCODE)" }
            & $exe install --start
            if ($LASTEXITCODE -ne 0) { throw "Service install failed ($LASTEXITCODE)" }
            Write-Host "Deployed to $installDir"
        }
        'uninstall' {
            if (Test-Path $exe) { & $exe uninstall }
            elseif ($svc) { sc.exe stop LEO | Out-Null; sc.exe delete LEO | Out-Null }
        }
        'restart' { Restart-Service LEO -Force }
    }
    Get-Service LEO -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
