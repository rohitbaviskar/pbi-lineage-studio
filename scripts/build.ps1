# Builds the standalone PBI Lineage Studio Windows executable.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'native\PbiLineageStudio.cs'
$versionFile = Join-Path $root 'VERSION'
$releaseNotes = Join-Path $root 'RELEASE_NOTES.md'
$out = Join-Path $root 'PBI Lineage Studio.exe'
if (!(Test-Path $source)) { throw "Missing native source: $source" }
if (!(Test-Path $versionFile)) { throw "Missing version file: $versionFile" }
if (!(Test-Path $releaseNotes)) { throw "Missing release notes: $releaseNotes" }

$version = (Get-Content $versionFile -Raw).Trim()
if ($version -notmatch '^\d+(?:\.\d+){2,}$') {
  throw "VERSION must contain at least three numeric parts, such as 0.2.4 or 0.2.4.1.1. Found: $version"
}
$versionParts = @($version.Split('.'))
$fileVersionParts = @('0', '0', '0', '0')
for ($index = 0; $index -lt [Math]::Min(4, $versionParts.Length); $index++) {
  $component = [int]$versionParts[$index]
  if ($component -gt 65534) { throw "The first four VERSION components must be between 0 and 65534." }
  $fileVersionParts[$index] = [string]$component
}
$assemblyVersion = $fileVersionParts -join '.'

function New-AppIcon([string]$path) {
  Add-Type -AssemblyName System.Drawing

  $size = 64
  $bitmap = New-Object System.Drawing.Bitmap($size, $size)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $tileBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(79, 70, 229))
  $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(199, 210, 254), [Math]::Max(2, $size / 18))
  $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(2, $size / 20))
  $nodeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath

  try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(238, 242, 255))
    $tileRadius = $size * 0.22
    $tileDiameter = $tileRadius * 2
    $tilePath.AddArc(1, 1, $tileDiameter, $tileDiameter, 180, 90)
    $tilePath.AddArc($size - 1 - $tileDiameter, 1, $tileDiameter, $tileDiameter, 270, 90)
    $tilePath.AddArc($size - 1 - $tileDiameter, $size - 1 - $tileDiameter, $tileDiameter, $tileDiameter, 0, 90)
    $tilePath.AddArc(1, $size - 1 - $tileDiameter, $tileDiameter, $tileDiameter, 90, 90)
    $tilePath.CloseFigure()
    $graphics.FillPath($tileBrush, $tilePath)

    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $left = New-Object System.Drawing.PointF(($size * 0.26), ($size * 0.52))
    $top = New-Object System.Drawing.PointF(($size * 0.72), ($size * 0.26))
    $middle = New-Object System.Drawing.PointF(($size * 0.72), ($size * 0.52))
    $bottom = New-Object System.Drawing.PointF(($size * 0.72), ($size * 0.78))
    $join = New-Object System.Drawing.PointF(($size * 0.42), ($size * 0.52))

    $graphics.DrawLine($linePen, $left, $join)
    $graphics.DrawBezier($linePen, $join, (New-Object System.Drawing.PointF(($size * 0.50), ($size * 0.48))), (New-Object System.Drawing.PointF(($size * 0.56), ($size * 0.26))), $top)
    $graphics.DrawLine($linePen, $join, $middle)
    $graphics.DrawBezier($linePen, $join, (New-Object System.Drawing.PointF(($size * 0.50), ($size * 0.58))), (New-Object System.Drawing.PointF(($size * 0.56), ($size * 0.78))), $bottom)

    $nodeRadius = $size * 0.11
    foreach ($center in @($left, $top, $middle, $bottom)) {
      $graphics.DrawEllipse($ringPen, $center.X - $nodeRadius, $center.Y - $nodeRadius, $nodeRadius * 2, $nodeRadius * 2)
      $graphics.FillRectangle($nodeBrush, $center.X - $nodeRadius * 0.42, $center.Y - $nodeRadius * 0.28, $nodeRadius * 0.84, $nodeRadius * 0.56)
    }

    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [System.IO.File]::Create($path)
    try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose() }
  }
  finally {
    $tilePath.Dispose()
    $nodeBrush.Dispose()
    $ringPen.Dispose()
    $linePen.Dispose()
    $tileBrush.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
  }
}

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (!(Test-Path $csc)) { throw 'Could not find csc.exe. Install .NET Framework Developer Pack or Visual Studio Build Tools.' }

$buildIcon = Join-Path ([System.IO.Path]::GetTempPath()) "PbiLineageStudio-$PID.ico"
$versionSource = Join-Path ([System.IO.Path]::GetTempPath()) "PbiLineageStudio-Version-$PID.cs"
$releaseNotesResource = "/resource:$releaseNotes,PbiLineageStudio.ReleaseNotes.md"
try {
  $versionAttributes = @"
[assembly: System.Reflection.AssemblyVersion("$assemblyVersion")]
[assembly: System.Reflection.AssemblyFileVersion("$assemblyVersion")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
"@
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($versionSource, $versionAttributes, $utf8NoBom)

  New-AppIcon $buildIcon
  & $csc /target:winexe /out:$out /win32icon:$buildIcon /optimize+ $releaseNotesResource /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $source $versionSource
  if ($LASTEXITCODE -ne 0) { throw 'Could not build native Windows executable.' }
}
finally {
  if (Test-Path $buildIcon) { Remove-Item -LiteralPath $buildIcon -Force }
  if (Test-Path $versionSource) { Remove-Item -LiteralPath $versionSource -Force }
}
Write-Host "Created $out (application version $version; Windows file version $assemblyVersion)"
