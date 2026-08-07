Add-Type -AssemblyName System.Drawing

$assetDir = $PSScriptRoot
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $rect = [System.Drawing.RectangleF]::new(1, 1, $size - 2, $size - 2)
    $radius = [Math]::Max(3, $size * 0.20)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, [System.Drawing.Color]::FromArgb(255, 29, 39, 54),
        [System.Drawing.Color]::FromArgb(255, 10, 15, 23), 45)
    $graphics.FillPath($background, $path)
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 229, 173, 67), [Math]::Max(1, $size / 32))
    $graphics.DrawPath($outline, $path)

    if ($size -ge 32) {
        $gold = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 242, 190, 89), [Math]::Max(1.5, $size / 28))
        $cx = $size * 0.50; $cy = $size * 0.55; $r = $size * 0.21
        $top = [System.Drawing.PointF]::new($cx, $cy - $r)
        $left = [System.Drawing.PointF]::new($cx - $r * 0.86, $cy - $r * 0.50)
        $right = [System.Drawing.PointF]::new($cx + $r * 0.86, $cy - $r * 0.50)
        $bottom = [System.Drawing.PointF]::new($cx, $cy + $r)
        $graphics.DrawLine($gold, $top, $left); $graphics.DrawLine($gold, $top, $right)
        $leftLower = [System.Drawing.PointF]::new($left.X, $cy + $r * 0.48)
        $rightLower = [System.Drawing.PointF]::new($right.X, $cy + $r * 0.48)
        $center = [System.Drawing.PointF]::new($top.X, $cy)
        $graphics.DrawLine($gold, $left, $leftLower); $graphics.DrawLine($gold, $right, $rightLower)
        $graphics.DrawLine($gold, $left.X, $cy + $r * 0.48, $bottom.X, $bottom.Y)
        $graphics.DrawLine($gold, $right.X, $cy + $r * 0.48, $bottom.X, $bottom.Y)
        $graphics.DrawLine($gold, $top.X, $cy, $bottom.X, $bottom.Y)
        $graphics.DrawLine($gold, $left, $center); $graphics.DrawLine($gold, $right, $center)
        $gold.Dispose()
    } else {
        $font = [System.Drawing.Font]::new('Segoe UI', $size * 0.40, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 242, 190, 89))
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $graphics.DrawString('D', $font, $brush, $rect, $format)
        $format.Dispose(); $brush.Dispose(); $font.Dispose()
    }

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $assetDir 'app-icon.png'), $stream.ToArray()) }
    $stream.Dispose(); $outline.Dispose(); $background.Dispose(); $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

$iconPath = Join-Path $assetDir 'app-icon.ico'
$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Dispose(); $output.Dispose()
