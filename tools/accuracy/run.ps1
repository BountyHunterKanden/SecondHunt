# Runs one BizHawk route headlessly-ish (the EmuHawk window opens and closes by itself). See README.md.
# Usage: tools\accuracy\run.ps1 -Route tools\accuracy\routes\to_landing.lua [-Timeout 300]
param(
    [Parameter(Mandatory = $true)][string]$Route,
    [int]$Timeout = 300,
    [string]$EmuDir = "$env:USERPROFILE\Downloads\mph-emu",
    [string]$Rom = "$env:USERPROFILE\Downloads\mph-emu\roms\AMHE1.nds"
)
$bh = Get-ChildItem -Directory "$EmuDir" -Filter "BizHawk-*" | Sort-Object Name | Select-Object -Last 1
if (-not $bh) { throw "No BizHawk-* folder in $EmuDir" }
New-Item -ItemType Directory -Force "$EmuDir\out\shots", "$EmuDir\states" | Out-Null
$env:MPH_EMU_DIR = $EmuDir -replace '\\', '/'
$env:MPH_ROUTE = (Resolve-Path $Route).Path -replace '\\', '/'
$driver = Join-Path $PSScriptRoot "driver.lua"
$p = Start-Process -FilePath "$($bh.FullName)\EmuHawk.exe" -ArgumentList "--lua=`"$driver`"", "`"$Rom`"" -WorkingDirectory $bh.FullName -PassThru
$deadline = (Get-Date).AddSeconds($Timeout)
while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; "TIMEOUT: stopped EmuHawk $($p.Id)" }
Get-Content "$EmuDir\out\driver_log.txt" -ErrorAction SilentlyContinue
