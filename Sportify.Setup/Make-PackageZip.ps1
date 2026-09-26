<#
.SYNOPSIS
    Packs the built installer into the ZIP that is carried to another computer: Sportify-<version>-Revit2025.zip with the installer, its checksum, the license, README-FIRST.txt (the
    steps of a test) and Remove-Developer-Sportify.ps1 (takes the developer side of Sportify out of Revit, into a backup folder).

    Run it after the installer is built (and signed, if it is): BuildDistribution.ps1 does; on its own it packs whatever dist\Sportify-Setup-<version>-Revit2025.exe is there.
#>
[CmdletBinding()]
param([string]$OutputDir)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
if (-not $OutputDir) { $OutputDir = Join-Path $repo "dist" }
$version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()
$exe = Join-Path $OutputDir "Sportify-Setup-$version-Revit2025.exe"
if (-not (Test-Path $exe)) { throw "The installer is not built: $exe (run Build-Installer.ps1 first)." }

$stage = Join-Path $OutputDir "package-staging"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage
(Get-FileHash (Join-Path $stage (Split-Path $exe -Leaf)) -Algorithm SHA256).Hash.ToLower() + "  " + (Split-Path $exe -Leaf) | Set-Content (Join-Path $stage ((Split-Path $exe -Leaf) + ".sha256")) -Encoding ascii
Copy-Item (Join-Path $here "LICENSE_AGREEMENT.txt") $stage
Copy-Item (Join-Path $here "package\Remove-Developer-Sportify.ps1") $stage
(Get-Content (Join-Path $here "package\README-FIRST.txt") -Raw -Encoding UTF8).Replace("{{VERSION}}", $version) | Set-Content (Join-Path $stage "README-FIRST.txt") -Encoding UTF8

$zip = Join-Path $OutputDir "Sportify-$version-Revit2025.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Write-Host ("{0}  {1:N0} MB" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
$zip
