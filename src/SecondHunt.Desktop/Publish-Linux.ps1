# Builds the Linux test build (no Linux release yet): a folder "Second Hunt" holding SecondHunt (a self-contained .NET
# app, precompiled, for x86-64 Linux), its native libraries (GLFW, miniaudio), the licence and every third-party
# notice, and a SecondHunt-<version>-linux-x64.tar.gz of it. Built on Windows (or Linux) with -p:MphLinux=true:
#   powershell -ExecutionPolicy Bypass -File src\SecondHunt.Desktop\Publish-Linux.ps1
# Run it in WSL (WSLg) or on a Linux PC:  tar xzf SecondHunt-*-linux-x64.tar.gz && chmod +x "Second Hunt/SecondHunt"
#   && "Second Hunt/SecondHunt"   (a Windows tar stores no exec bit)
# The system gives the rest: Mesa (libegl1, libgl1-mesa-dri), PulseAudio or PipeWire, zenity or kdialog (file
# pickers), a font (fonts-dejavu-core), and SDL3 for pads (optional: GLFW's pads without it).
# The folder and archive land in -OutDir (default: the repo's out\ folder, which git ignores). -Public: a public build.
param(
    [string]$Version = "0.0.0-linux-test",
    [string]$OutDir = "",
    [switch]$Public
)
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "SecondHunt.Desktop.csproj"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ($OutDir -eq "") { $OutDir = Join-Path $repo "out" }
$numeric = ($Version -split "-")[0]
$stage = Join-Path $OutDir "linux-x64"
$app = Join-Path $stage "Second Hunt"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $app | Out-Null

dotnet publish $project -c Release -r linux-x64 --self-contained true `
    -p:MphLinux=true -p:MphPublic=$($Public.IsPresent.ToString().ToLowerInvariant()) `
    -p:PublishReadyToRun=true `
    -p:Version=$numeric -p:InformationalVersion=$Version `
    -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $app -Filter *.pdb | Remove-Item

Copy-Item (Join-Path $repo "LICENSE") (Join-Path $app "LICENSE.txt")
Copy-Item (Join-Path $repo "NOTICE-fork.md") (Join-Path $app "NOTICE-fork.txt")
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") (Join-Path $app "THIRD_PARTY_NOTICES.txt")
$licenses = Join-Path $app "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = Join-Path $env:USERPROFILE ".nuget\packages"
$runtime = Get-ChildItem (Join-Path $nuget "microsoft.netcore.app.runtime.linux-x64") | Sort-Object Name | Select-Object -Last 1
Copy-Item (Join-Path $runtime.FullName "LICENSE.TXT") (Join-Path $licenses "dotnet-runtime-LICENSE.txt")
Copy-Item (Join-Path $runtime.FullName "THIRD-PARTY-NOTICES.TXT") (Join-Path $licenses "dotnet-runtime-THIRD-PARTY-NOTICES.txt")
$glfw = Get-ChildItem (Join-Path $nuget "opentk.redist.glfw") | Sort-Object Name | Select-Object -Last 1
Copy-Item (Join-Path $glfw.FullName "COPYING.md") (Join-Path $licenses "glfw-LICENSE.txt")

# Windows 10's own tar (bsdtar), not a Git/MSYS one that reads "C:" as a remote host
$tgz = Join-Path $OutDir "SecondHunt-$Version-linux-x64.tar.gz"
if (Test-Path $tgz) { Remove-Item $tgz }
$tar = if ($IsLinux) { "tar" } else { Join-Path $env:SystemRoot "System32\tar.exe" }
& $tar -czf $tgz -C $stage "Second Hunt"
if ($LASTEXITCODE -ne 0) { throw "tar failed" }
Write-Host "folder:  $app"
Write-Host "archive: $tgz ($([math]::Round((Get-Item $tgz).Length / 1MB, 1)) MB)"
