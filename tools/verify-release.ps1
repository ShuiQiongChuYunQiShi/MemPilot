# Non-interactive release verification (wharf release chain):
# 1) HEAD carries a v* tag  2) that tag has a published GitHub release  3) assets listed
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$tag = git describe --tags --exact-match HEAD 2>$null
if (-not $tag -or $tag -notmatch '^v\d+\.\d+\.\d+$') { Write-Output "FAIL: HEAD is not at a version tag"; exit 1 }
Write-Output "HEAD tag: $tag"

$repo = (git remote get-url origin) -replace '.*github.com[/:]', '' -replace '\.git$', ''
$release = gh release view $tag -R $repo --json tagName,assets,isDraft 2>$null | ConvertFrom-Json
if (-not $release -or $release.isDraft) { Write-Output "FAIL: no published release for $tag"; exit 1 }
Write-Output "release: $($release.tagName) published, assets:"
foreach ($a in $release.assets) { Write-Output ("  - " + $a.name + "  (" + [math]::Round($a.size / 1MB, 1) + " MB)") }
if ($release.assets.Count -lt 3) { Write-Output "FAIL: expected zip + exe + SHA256SUMS"; exit 1 }
Write-Output "RELEASE VERIFIED"
