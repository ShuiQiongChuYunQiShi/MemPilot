# Human-gated release publisher. NOT wired into CI — tag push publishes immediately
# (see .github/workflows/release.yml), so this script is the approval gate.
param(
  [Parameter(Mandatory = $true)]
  [string]$Version   # e.g. v0.2.0 (with the leading v)
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if ($Version -notmatch '^v\d+\.\d+\.\d+$') { Write-Error "Version must look like v0.2.0"; exit 1 }
if (-not (Test-Path (Join-Path $PSScriptRoot '..', 'wharf.project.json'))) { Write-Error "run from the repo"; exit 1 }

$dirty = git status --porcelain
if ($dirty) { Write-Error "working tree is dirty — commit first (uncommitted evidence is not deployable)"; exit 1 }

Write-Host "Running test gate..."
dotnet test tests/MemoryTool.Tests/MemoryTool.Tests.csproj -c Release -v q
if ($LASTEXITCODE -ne 0) { Write-Error "tests failed"; exit 1 }

Write-Host ""
Write-Host "About to tag $Version at HEAD and push (main + tag)."
Write-Host "Tag push triggers .github/workflows/release.yml which publishes:"
Write-Host "  portable zip + self-contained exe + SHA256SUMS to GitHub Releases."
$confirm = Read-Host "Confirm release $Version? (yes/no)"
if ($confirm -ne 'yes') { Write-Output "aborted"; exit 1 }

git tag -f $Version
git push origin $Version
git push
Write-Output "release workflow triggered — monitor: gh run watch"
