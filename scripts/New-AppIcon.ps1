#Requires -Version 7.0
# Windows-only export of the code-native geometry in Assets/FolderTwin.svg.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$iconPath = Join-Path $PSScriptRoot '../experiments/CopyLab/Assets/FolderTwin.ico'
$sizes = @(16, 24, 32, 48, 64, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $tile = [Drawing.Drawing2D.GraphicsPath]::new()
    $dark = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#14253c'))
    $back = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#47c1bf'))
    $front = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#daf4ee'))
    $check = [Drawing.Pen]::new($dark.Color, 6)
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 128.0, $size / 128.0)
        $tile.AddArc(4, 4, 56, 56, 180, 90)
        $tile.AddArc(68, 4, 56, 56, 270, 90)
        $tile.AddArc(68, 68, 56, 56, 0, 90)
        $tile.AddArc(4, 68, 56, 56, 90, 90)
        $tile.CloseFigure()
        $graphics.FillPath($dark, $tile)
        $graphics.FillPolygon($back, [Drawing.PointF[]]@(
            [Drawing.PointF]::new(22,32), [Drawing.PointF]::new(47,32), [Drawing.PointF]::new(56,42),
            [Drawing.PointF]::new(91,42), [Drawing.PointF]::new(91,85), [Drawing.PointF]::new(22,85)))
        $graphics.FillPolygon($front, [Drawing.PointF[]]@(
            [Drawing.PointF]::new(38,52), [Drawing.PointF]::new(60,52), [Drawing.PointF]::new(68,61),
            [Drawing.PointF]::new(106,61), [Drawing.PointF]::new(106,101), [Drawing.PointF]::new(38,101)))
        $check.StartCap = $check.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $check.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLines($check, [Drawing.PointF[]]@(
            [Drawing.PointF]::new(58,81), [Drawing.PointF]::new(67,90), [Drawing.PointF]::new(87,69)))
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $images += ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $check.Dispose()
        $front.Dispose()
        $back.Dispose()
        $dark.Dispose()
        $tile.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
$output = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
}
finally { $writer.Dispose() }
