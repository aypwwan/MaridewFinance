param(
    [Parameter(Mandatory = $true)]
    [string]$OutPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Draw one size of the brand mark: rounded gradient square + white "M".
function New-BrandPng {
    param([int]$Size, [string]$File)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background (matches the dashboard logo tile)
    $pad = [Math]::Max(1, [int]($Size * 0.045))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($Size - 2 * $pad), ($Size - 2 * $pad))
    $radius = [Math]::Max(2, [int]($Size * 0.22))
    $d = $radius * 2

    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $tile.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $tile.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $tile.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $tile.CloseFigure()

    # Brand gradient: indigo-600 -> emerald-400
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 79, 70, 229),
        [System.Drawing.Color]::FromArgb(255, 52, 211, 153),
        45.0)
    $g.FillPath($brush, $tile)

    # White "M" for Maridew, centered
    $font = New-Object System.Drawing.Font('Segoe UI', ($Size * 0.58), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF(0, (-$Size * 0.02), $Size, $Size)
    $g.DrawString('M', $font, [System.Drawing.Brushes]::White, $box, $fmt)

    $bmp.Save($File, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $tile.Dispose()
    $brush.Dispose()
    $font.Dispose()
    $bmp.Dispose()
}

# Build the multi-size ICO container (PNG-compressed entries, valid on Win10/11)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @{}
foreach ($s in $sizes) {
    $tmp = Join-Path $env:TEMP ("maridew_{0}.png" -f $s)
    New-BrandPng -Size $s -File $tmp
    $blobs[$s] = [System.IO.File]::ReadAllBytes($tmp)
    Remove-Item $tmp -Force
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$sizes.Count)      # image count

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $blobs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in ICO dir
    $bw.Write([byte]$dim)            # width
    $bw.Write([byte]$dim)            # height
    $bw.Write([byte]0)               # color palette
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # color planes
    $bw.Write([uint16]32)            # bits per pixel
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($blobs[$s]) }
$bw.Flush()

[System.IO.File]::WriteAllBytes($OutPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Host ("Wrote {0} ({1} bytes, sizes: {2})" -f $OutPath, (Get-Item $OutPath).Length, ($sizes -join '/'))
