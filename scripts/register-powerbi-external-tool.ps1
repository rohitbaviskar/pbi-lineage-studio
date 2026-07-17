# Registers PBI Lineage Studio in Power BI Desktop's External Tools ribbon.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'PBI Lineage Studio.exe'
if (!(Test-Path $exe)) { throw "Build the executable first: $exe" }

Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $icon) { throw 'The executable does not contain an application icon.' }
$bitmap = $icon.ToBitmap()
$stream = New-Object System.IO.MemoryStream
try {
  $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
  $iconData = [Convert]::ToBase64String($stream.ToArray())
}
finally {
  $stream.Dispose()
  $bitmap.Dispose()
  $icon.Dispose()
}

$toolsDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Power BI Desktop\External Tools'
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

$registration = [ordered]@{
  version = '1.0'
  name = 'PBI Lineage Studio'
  description = 'Explore local Power BI semantic model lineage.'
  path = $exe
  arguments = ''
  iconData = $iconData
}

$jsonPath = Join-Path $toolsDir 'pbi-lineage-studio.pbitool.json'
$registration | ConvertTo-Json -Depth 4 | Set-Content -Path $jsonPath -Encoding UTF8
Write-Host "Registered Power BI external tool: $jsonPath"
