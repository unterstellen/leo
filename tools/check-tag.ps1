<#
.SYNOPSIS
  The release workflow's tag check (LEO.md §24.2, §24.4 step 1). Mirrors Leo.Core.Updates.TagRules (unit-tested):
  tags are vX.Y.Z or vX.Y.Z-canary.N; major/minor <= 255, patch <= 65535 (Windows Installer); and X.Y.Z must be
  higher than every earlier tag's, because Windows Installer compares only those three fields.

.EXAMPLE
  .\tools\check-tag.ps1 -Tag v0.2.0
#>
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$pattern = '^v(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?:-([a-z]+)\.(\d{1,5}))?$'

function Get-Core([string]$t) {
    if ($t -notmatch $pattern) { return $null }
    return [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Patch = [int]$Matches[3]; Channel = $Matches[4] }
}

$v = Get-Core $Tag
if (-not $v) { throw "'$Tag' isn't a release tag (vX.Y.Z or vX.Y.Z-canary.N)." }
if ($v.Channel -and $v.Channel -ne 'canary') { throw "'$Tag': the only pre-release channel is canary." }
if ($v.Major -gt 255 -or $v.Minor -gt 255 -or $v.Patch -gt 65535) { throw "'$Tag': Windows Installer allows major and minor up to 255 and patch up to 65535." }

$key = { param($c) ($c.Major * 1e10) + ($c.Minor * 1e5) + $c.Patch }
foreach ($earlier in (git tag -l 'v*') | Where-Object { $_ -ne $Tag }) {
    $e = Get-Core $earlier
    if (-not $e) { continue }
    if ((& $key $v) -le (& $key $e)) { throw "'$Tag': x.y.z must be higher than every earlier tag's ($earlier exists)." }
}

$version = "$($v.Major).$($v.Minor).$($v.Patch)"
$title = if ($v.Major -eq 0) { "LEO $($Tag.TrimStart('v')) (Preview)" } else { "LEO $($Tag.TrimStart('v'))" }
Write-Host "Tag $Tag OK: version $version, title '$title', pre-release: $([bool]$v.Channel)"
if ($env:GITHUB_OUTPUT) {
    "version=$version" | Add-Content $env:GITHUB_OUTPUT
    "title=$title" | Add-Content $env:GITHUB_OUTPUT
    "prerelease=$(if ($v.Channel) { 'true' } else { 'false' })" | Add-Content $env:GITHUB_OUTPUT
}
