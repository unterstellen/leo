<#
.SYNOPSIS
  Builds the split-tunneling engine (mihomo, GPL-3.0) from source for release builds (LEO.md §17, §24.4 step 2).

.DESCRIPTION
  Used by .github\workflows\release.yml; developers keep using tools\get-engine.ps1 (the pinned upstream binary).
  1. Clones github.com/MetaCubeX/mihomo at the pinned tag (shallow) into artifacts\engine-src.
  2. Builds the equivalent of upstream's "windows-amd64-compatible" asset: GOOS=windows GOARCH=amd64 GOAMD64=v1,
     build tag with_gvisor, -trimpath, stripped, version stamped like upstream's release workflow.
  3. Writes artifacts\engine\mihomo.exe (+ version.txt with `mihomo -v` and the SHA-256) so the engine tests
     (`mihomo -t`) and tools\build-release.ps1 use it.
  4. Packs the corresponding source (git archive of the tag + vendored Go modules) as
     artifacts\release\mihomo-<tag>-source.tar.gz: LEO distributes a GPL binary, so the source goes next to it.

  Needs Go (the release workflow installs the version below with actions/setup-go) and git.
  Downloads the mihomo source and its Go modules; nothing is installed.

  CHECK WHEN BUMPING THE ENGINE: compare Go version, tags and flags with upstream's
  .github/workflows/build.yml at the new tag, and keep $Tag in step with tools\get-engine.ps1.
#>
param(
    [string]$Tag = 'v1.19.34',
    # Use a checkout the caller already made (the release workflow checks out the tag first to read go.mod).
    [switch]$UseExistingSource
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'artifacts\engine-src'
$outDir = Join-Path $root 'artifacts\engine'
$releaseDir = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Force $outDir, $releaseDir | Out-Null

if ($UseExistingSource) {
    if (-not (Test-Path (Join-Path $src 'go.mod'))) { throw "No mihomo checkout at $src." }
} else {
    if (Test-Path $src) { Remove-Item -Recurse -Force $src }
    git clone --depth 1 --branch $Tag https://github.com/MetaCubeX/mihomo.git $src
    if ($LASTEXITCODE -ne 0) { throw "Cloning mihomo $Tag failed." }
}
$commit = (git -C $src rev-parse HEAD).Trim()

$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$env:GOAMD64 = 'v1'          # "compatible": runs on every x64 CPU
$env:CGO_ENABLED = '0'
$buildTime = (git -C $src log -1 --format=%cI).Trim() # reproducible: the tag's commit time, not "now"
$ldflags = "-X `"github.com/metacubex/mihomo/constant.Version=$Tag`" -X `"github.com/metacubex/mihomo/constant.BuildTime=$buildTime`" -w -s -buildid="
$exe = Join-Path $outDir 'mihomo.exe'
Push-Location $src
try {
    go build -tags with_gvisor -trimpath -ldflags $ldflags -o $exe
    if ($LASTEXITCODE -ne 0) { throw 'go build failed.' }

    # Corresponding source: the tag plus every Go module it was built with.
    go mod vendor
    if ($LASTEXITCODE -ne 0) { throw 'go mod vendor failed.' }
} finally {
    Pop-Location
}

$archive = Join-Path $releaseDir "mihomo-$Tag-source.tar.gz"
# tar ships with Windows 10/11 and GitHub's runners.
tar -czf $archive -C (Split-Path $src) --exclude='.git' (Split-Path $src -Leaf)
if ($LASTEXITCODE -ne 0) { throw 'Packing the source failed.' }

$version = (& $exe -v | Select-Object -First 1)
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
"$Tag $sha" | Set-Content (Join-Path $outDir 'version.txt') -Encoding ascii
"built from source: $Tag ($commit)`n$version`nSHA-256 $sha" | Set-Content (Join-Path $outDir 'build-info.txt') -Encoding utf8
Write-Host "Engine: $version"
Write-Host "  commit $commit, SHA-256 $sha"
Write-Host "  source: $archive"
