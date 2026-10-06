# Builds the Windows release zip: SecondHunt-<version>-windows-x64.zip with one folder "Second Hunt" holding
# SecondHunt.exe (a self-contained .NET app, precompiled), the two native libraries (GLFW, miniaudio), the README, the
# licence and every third-party notice. Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File src\SecondHunt.Desktop\Publish-Windows.ps1 -Version 0.1.0-beta
# The zip and its SHA-256 land in -OutDir (default: the repo's out\ folder, which git ignores).
param(
    [string]$Version = "0.1.0-beta",
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "SecondHunt.Desktop.csproj"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ($OutDir -eq "") { $OutDir = Join-Path $repo "out" }
$numeric = ($Version -split "-")[0]                     # 0.1.0-beta -> 0.1.0 (the exe's file version)
$stage = Join-Path ([IO.Path]::GetTempPath()) "SecondHunt-publish-$Version"
$app = Join-Path $stage "Second Hunt"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $app | Out-Null
New-Item -ItemType Directory -Force $OutDir | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:MphPublic=true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:Version=$numeric -p:InformationalVersion=$Version `
    -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $app -Filter *.pdb | Remove-Item

# the player's README, the app's licence and notices, and the .NET runtime's own (the exe carries the runtime)
Copy-Item (Join-Path $PSScriptRoot "dist\README.txt") (Join-Path $app "README.txt")
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $app "LICENSE.txt")
Copy-Item (Join-Path $repo "NOTICE-fork.md") (Join-Path $app "NOTICE-fork.txt")
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") (Join-Path $app "THIRD_PARTY_NOTICES.txt")
$licenses = Join-Path $app "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = Join-Path $env:USERPROFILE ".nuget\packages"
$runtime = Get-ChildItem (Join-Path $nuget "microsoft.netcore.app.runtime.win-x64") | Sort-Object Name | Select-Object -Last 1
$desktop = Get-ChildItem (Join-Path $nuget "microsoft.windowsdesktop.app.runtime.win-x64") | Sort-Object Name | Select-Object -Last 1
Copy-Item (Join-Path $runtime.FullName "LICENSE.TXT") (Join-Path $licenses "dotnet-runtime-LICENSE.txt")
Copy-Item (Join-Path $runtime.FullName "THIRD-PARTY-NOTICES.TXT") (Join-Path $licenses "dotnet-runtime-THIRD-PARTY-NOTICES.txt")
Copy-Item (Join-Path $desktop.FullName "LICENSE") (Join-Path $licenses "dotnet-windowsdesktop-LICENSE.txt")
$glfw = Get-ChildItem (Join-Path $nuget "opentk.redist.glfw") | Sort-Object Name | Select-Object -Last 1
Copy-Item (Join-Path $glfw.FullName "COPYING.md") (Join-Path $licenses "glfw-LICENSE.txt")

$zip = Join-Path $OutDir "SecondHunt-$Version-windows-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
# entries written one by one with "/" separators: Windows PowerShell 5's Compress-Archive writes "\" (against the zip
# spec), which some unzip tools turn into file names instead of folders
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $root = (Get-Item $app).FullName.TrimEnd("\")
    Get-ChildItem -Recurse -File $app | ForEach-Object {
        $entry = "Second Hunt/" + $_.FullName.Substring($root.Length + 1).Replace("\", "/")
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"
Get-ChildItem -Recurse $app | Select-Object FullName, Length | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "zip:     $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "sha256:  $hash"
