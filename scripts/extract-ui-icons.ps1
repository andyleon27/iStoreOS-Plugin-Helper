# Development asset preparation only; the application embeds the generated files.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function Save-CroppedIcon($sourcePath, $targetPath, [int]$x, [int]$y, [int]$size, [bool]$circle) {
    $sheet = [Drawing.Bitmap]::new($sourcePath)
    $tile = [Drawing.Bitmap]::new(128,128,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($tile)
    $clip = [Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        if ($circle) { $clip.AddEllipse(3,3,122,122) }
        else {
            $clip.AddArc(3,3,48,48,180,90); $clip.AddArc(77,3,48,48,270,90)
            $clip.AddArc(77,77,48,48,0,90); $clip.AddArc(3,77,48,48,90,90); $clip.CloseFigure()
        }
        $graphics.SetClip($clip)
        $graphics.DrawImage($sheet, [Drawing.Rectangle]::new(0,0,128,128), [Drawing.Rectangle]::new($x,$y,$size,$size), [Drawing.GraphicsUnit]::Pixel)
        $tile.Save($targetPath, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $clip.Dispose(); $graphics.Dispose(); $tile.Dispose(); $sheet.Dispose() }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$sheetRoot = Join-Path $projectRoot 'docs\ui\assets\icon-sheets'
$iconRoot = Join-Path $projectRoot 'src\iStoreOS.PluginHelper\Resources\Icons'
New-Item -ItemType Directory -Path $iconRoot -Force | Out-Null
$systemSheet = Join-Path $sheetRoot 'v0.3.1-icons-system.png'
# The supplied action/status filenames have a doubled extension; retain the originals.
$actionSheet = Join-Path $sheetRoot 'v0.3.1-icons-actions.png.png'
$statusSheet = Join-Path $sheetRoot 'v0.3.1-icons-status.png.png'
$tiles = @(
    @('app-logo',$systemSheet,45,352,278,$false), @('connect',$systemSheet,345,350,278,$true),
    @('router',$systemSheet,637,352,278,$false), @('system',$systemSheet,938,350,278,$true),
    @('sdk',$systemSheet,45,666,278,$false), @('architecture',$systemSheet,345,666,278,$true),
    @('package-manager',$systemSheet,637,666,278,$false), @('quickstart',$systemSheet,938,666,278,$true),
    @('software-source',$actionSheet,39,321,276,$false), @('shield-ok',$actionSheet,342,321,276,$true),
    @('refresh',$actionSheet,646,321,276,$false), @('plugin',$actionSheet,947,321,276,$true),
    @('upload',$actionSheet,39,667,276,$false), @('file',$actionSheet,342,667,276,$true),
    @('folder',$actionSheet,646,667,276,$false), @('log',$actionSheet,947,667,276,$true),
    @('success',$statusSheet,39,260,344,$true), @('warning',$statusSheet,455,260,344,$true),
    @('error',$statusSheet,873,260,344,$true), @('copy',$statusSheet,39,689,344,$false),
    @('eye-show',$statusSheet,455,689,344,$false), @('eye-hide',$statusSheet,873,689,344,$false)
)
foreach ($tile in $tiles) { Save-CroppedIcon $tile[1] (Join-Path $iconRoot ($tile[0] + '.png')) $tile[2] $tile[3] $tile[4] $tile[5] }
$pngBytes = [IO.File]::ReadAllBytes((Join-Path $iconRoot 'app-logo.png'))
$icoStream = [IO.File]::Create((Join-Path $iconRoot 'app-logo.ico'))
$writer = [IO.BinaryWriter]::new($icoStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]128); $writer.Write([byte]128); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngBytes.Length); $writer.Write([uint32]22)
    $writer.Write($pngBytes)
} finally { $writer.Dispose() }
# A contact sheet on white makes transparent edges and crop alignment reviewable.
$preview = [Drawing.Bitmap]::new(660, 440)
$canvas = [Drawing.Graphics]::FromImage($preview)
$font = [Drawing.Font]::new('Segoe UI', 9)
try {
    $canvas.Clear([Drawing.Color]::White)
    for ($i=0; $i -lt $tiles.Count; $i++) {
        $x = ($i % 6) * 110; $y = [Math]::Floor($i / 6) * 110
        $tileImage = [Drawing.Bitmap]::new((Join-Path $iconRoot ($tiles[$i][0] + '.png')))
        try { $canvas.DrawImage($tileImage, [int]($x+23), [int]$y, 64, 64) } finally { $tileImage.Dispose() }
        $canvas.DrawString($tiles[$i][0], $font, [Drawing.Brushes]::Black, [single]($x+4), [single]($y+72))
    }
    $preview.Save((Join-Path $projectRoot 'artifacts\icons-v0.3.1.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $font.Dispose(); $canvas.Dispose(); $preview.Dispose() }
Write-Output ('Generated ' + $tiles.Count + ' transparent PNG icons and app-logo.ico')
