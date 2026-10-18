# Picks the game files a served match needs (gamedata.txt) out of the owner's own extract, for deploy.ps1 -GameData.
#   .\gamedata.ps1 -Extract <...\files\AMHE1> -Out <folder>      then: .\deploy.ps1 -Server root@... -GameData <folder>
# Only the listed files are copied (no cart code, sound, movies or menus); <folder> is a scratch copy, never in the repo.
param(
    [Parameter(Mandatory = $true)][string]$Extract,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$names = Get-Content (Join-Path $here 'gamedata.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
$missing = 0
foreach ($name in $names) {
    $from = Join-Path $Extract $name
    if (-not (Test-Path -LiteralPath $from)) { Write-Warning "not in the extract: $name"; $missing++; continue }
    $to = Join-Path $Out $name
    New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath $from $to
}
if ($missing) { throw "$missing listed file(s) missing: is -Extract the files\AMHE1 (or AMHE0) folder?" }
$mb = [math]::Round(((Get-ChildItem -Recurse -File $Out | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "$($names.Count) files, $mb MB in $Out"
