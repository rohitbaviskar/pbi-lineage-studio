# Updates the single version source and verifies a local release build.
param(
  [Parameter(Mandatory = $true)]
  [string]$Version
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+(?:\.\d+){2,}$') {
  throw "Version must look like 0.2.4, 0.2.4.1, or 0.2.4.1.1. Found: $Version"
}

$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root 'VERSION'
$output = Join-Path $root 'PBI Lineage Studio.exe'
$buildScript = Join-Path $PSScriptRoot 'build.ps1'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllText($versionFile, "$Version`r`n", $utf8NoBom)
Write-Host "Updated VERSION to $Version"

& $buildScript
if ($LASTEXITCODE -ne 0) { throw 'The release build failed.' }

$versionParts = @($Version.Split('.'))
$fileVersionParts = @('0', '0', '0', '0')
for ($index = 0; $index -lt [Math]::Min(4, $versionParts.Length); $index++) {
  $fileVersionParts[$index] = [string]([int]$versionParts[$index])
}
$expectedFileVersion = $fileVersionParts -join '.'
$versionInfo = (Get-Item $output).VersionInfo
$actualFileVersion = $versionInfo.FileVersion
if ($actualFileVersion -ne $expectedFileVersion) {
  throw "Built file version is $actualFileVersion; expected $expectedFileVersion."
}
$actualProductVersion = $versionInfo.ProductVersion
if ($actualProductVersion -ne $Version) {
  throw "Built application version is $actualProductVersion; expected $Version."
}

Write-Host "Verified application version $actualProductVersion (Windows file version $actualFileVersion)"
Write-Host ''
Write-Host 'When you are ready to publish, commit all intended release changes and then run:'
Write-Host '  git push origin main'
Write-Host "  git tag v$Version"
Write-Host "  git push origin v$Version"
