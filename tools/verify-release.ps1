# Non-interactive release verification (wharf release chain)
#
# 两种模式（wharf 的日常链与发版后链各用一种）：
#   -Mode latest  （日常门禁）  取**最新** v* tag，验证它的 Release 已发布且资产齐全
#   -Mode head    （发版后人工） 要求 HEAD 恰好落在 v* tag 上，再验证该 Release
#
# 为什么要分两种：发布之后通常还有提交（文档/CI/notes），HEAD 就不再是 tag，
# 严格模式会永远红；日常门禁需要"稳定可绿"，否则会被习惯性忽略。
param(
    [ValidateSet('head', 'latest')]
    [string]$Mode = 'head'
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if ($Mode -eq 'latest') {
    $tag = git describe --tags --abbrev=0 2>$null
    if (-not $tag -or $tag -notmatch '^v\d+\.\d+\.\d+$') { Write-Output "FAIL: no version tag found"; exit 1 }
    Write-Output "latest tag: $tag"
} else {
    $tag = git describe --tags --exact-match HEAD 2>$null
    if (-not $tag -or $tag -notmatch '^v\d+\.\d+\.\d+$') { Write-Output "FAIL: HEAD is not at a version tag"; exit 1 }
    Write-Output "HEAD tag: $tag"
}

$repo = (git remote get-url origin) -replace '.*github.com[/:]', '' -replace '\.git$', ''
$release = gh release view $tag -R $repo --json tagName,assets,isDraft 2>$null | ConvertFrom-Json
if (-not $release -or $release.isDraft) { Write-Output "FAIL: no published release for $tag"; exit 1 }
Write-Output "release: $($release.tagName) published, assets:"
foreach ($a in $release.assets) { Write-Output ("  - " + $a.name + "  (" + [math]::Round($a.size / 1MB, 1) + " MB)") }
if ($release.assets.Count -lt 3) { Write-Output "FAIL: expected zip + exe + SHA256SUMS"; exit 1 }
Write-Output "RELEASE VERIFIED ($Mode)"
