#requires -version 7
<#
.SYNOPSIS
  Regenerates Pia's brand raster assets from the vector logo geometry.

.DESCRIPTION
  Single source of truth for the shipped logo bitmaps. Rasterises the Pia mark
  and wordmark with WPF (no external tooling). Everything lives under
  src/Pia.Wpf/Resources/:

    Icons/Pia.ico                  the "P" mark, 16..256 px
    Installer/logo.bmp             wordmark on the MSI banner strip
    Icons/pia-logo_blau_RGB.svg    blue wordmark, tight viewBox — README header

  The brand kit's black originals sit next to them as the vector source:
  Icons/pia-logo-kurz_schwarz_RGB.svg and Icons/pia-logo_schwarz_RGB.svg.

  In-app XAML does not consume any of this — it uses the vector geometry in
  Icons/PiaLogo.xaml so the mark can follow the theme accent. Keep the path
  data below and PiaLogo.xaml in sync.

.EXAMPLE
  ./scripts/generate-brand-assets.ps1 -PreviewPath preview.png
  Also writes a contact sheet of every icon size over light and dark
  backgrounds — the only way to catch a mark that vanishes in the tray.
#>
[CmdletBinding()]
param(
    # Pia blue. Matches PiaAccentColor in Resources/Theme/PiaTokens.Light.xaml.
    [string]$Blue = '#2563EB',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# --- Geometry -----------------------------------------------------------------
# Path data lifted verbatim from Resources/Icons/pia-logo-kurz_schwarz_RGB.svg
# and Resources/Icons/pia-logo_schwarz_RGB.svg. The wordmark's <polygon> (the
# "i") is rewritten as M/L/Z because WPF's mini-language has no polygon form.
$MarkFigures = 'M152.1,121.58h-40.43c-15.32,0-27.81,12.49-27.81,27.81v76.19l20-11.55v-64.52c0-4.64,3.82-8.46,8.46-8.46h39.13c4.64,0,8.41,3.82,8.41,8.46v22.16c0,4.64-3.77,8.41-8.41,8.41h-35.11v19.5h35.77c15.32,0,27.76-12.44,27.76-27.76v-22.43c0-15.32-12.43-27.81-27.76-27.81Z'
$WordFigures = $MarkFigures +
    ' M195.86,202.92L215.86,191.37L215.86,118.14L195.86,129.69Z' +
    ' M300.05,121.58h-40.43c-15.32,0-27.76,12.49-27.76,27.81v22.43c0,15.32,12.43,27.76,27.76,27.76h35.74v-19.5h-35.09c-4.64,0-8.41-3.77-8.41-8.41v-22.16c0-4.64,3.77-8.46,8.41-8.46h39.13c4.64,0,8.46,3.82,8.46,8.46v53.41l20-9.33v-44.2c0-15.32-12.49-27.81-27.81-27.81Z'

# "F1" = nonzero fill rule. SVG's default; WPF's default is EvenOdd, which would
# fill the counter of the P.
$MarkData = "F1 $MarkFigures"
$WordData = "F1 $WordFigures"

$brush = [System.Windows.Media.SolidColorBrush]::new(
    [System.Windows.Media.ColorConverter]::ConvertFromString($Blue))
$brush.Freeze()

# --- Rendering ----------------------------------------------------------------
# Draws $Data scaled to $ContentW x $ContentH (uniform, tight-cropped to its own
# ink bounds) with its top-left at $X,$Y on a $PixelWidth x $PixelHeight canvas.
function Render-Glyph {
    param(
        [string]$Data,
        [int]$PixelWidth,
        [int]$PixelHeight,
        [double]$ContentW,
        [double]$ContentH,
        [double]$X,
        [double]$Y,
        [System.Windows.Media.Brush]$Background
    )

    $geo = [System.Windows.Media.Geometry]::Parse($Data)
    $ink = $geo.Bounds
    $scale = [Math]::Min($ContentW / $ink.Width, $ContentH / $ink.Height)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    if ($Background) {
        $dc.DrawRectangle($Background, $null,
            [System.Windows.Rect]::new(0, 0, $PixelWidth, $PixelHeight))
    }
    $t = [System.Windows.Media.TransformGroup]::new()
    $t.Children.Add([System.Windows.Media.TranslateTransform]::new(-$ink.X, -$ink.Y))
    $t.Children.Add([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $t.Children.Add([System.Windows.Media.TranslateTransform]::new(
            $X + ($ContentW - $ink.Width * $scale) / 2,
            $Y + ($ContentH - $ink.Height * $scale) / 2))
    $dc.PushTransform($t)
    $dc.DrawGeometry($brush, $null, $geo)
    $dc.Pop()
    $dc.Close()

    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $PixelWidth, $PixelHeight, 96, 96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $rtb.Freeze()
    return $rtb
}

# Square icon frame: the mark centred with $Pad of the canvas free on each side.
function Render-Mark {
    param([int]$Size, [double]$Pad = 0.06)
    $inset = $Size * $Pad
    $box = $Size - 2 * $inset
    Render-Glyph -Data $MarkData -PixelWidth $Size -PixelHeight $Size `
        -ContentW $box -ContentH $box -X $inset -Y $inset
}

function Get-PngBytes {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $ms = [System.IO.MemoryStream]::new()
    $enc.Save($ms)
    return , $ms.ToArray()   # comma keeps PowerShell from unrolling the byte[]
}

# Straight (non-premultiplied) BGRA rows, top-down. Both the ICO and BMP
# containers below want straight alpha; RenderTargetBitmap hands back Pbgra32.
function Get-Bgra32 {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $stride = $Bitmap.PixelWidth * 4
    $buf = [byte[]]::new($stride * $Bitmap.PixelHeight)
    $conv = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $Bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $conv.CopyPixels($buf, $stride, 0)
    return , $buf
}

# 32bpp DIB icon frame: BITMAPINFOHEADER with biHeight = 2*h (colour rows plus
# AND mask), bottom-up rows, then an all-zero 1bpp mask padded to 4-byte rows.
# Alpha carries the transparency; the mask is vestigial but the doubled height
# declares it, so it has to be there.
function Get-DibFrame {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $w = $Bitmap.PixelWidth
    $h = $Bitmap.PixelHeight
    $pixels = Get-Bgra32 $Bitmap
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([uint32]40)              # biSize
    $bw.Write([int32]$w)               # biWidth
    $bw.Write([int32]($h * 2))         # biHeight
    $bw.Write([uint16]1)               # biPlanes
    $bw.Write([uint16]32)              # biBitCount
    $bw.Write([uint32]0)               # biCompression = BI_RGB
    $bw.Write([uint32]($w * $h * 4))   # biSizeImage
    1..4 | ForEach-Object { $bw.Write([uint32]0) }  # ppm x/y, clrUsed, clrImportant

    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $w * 4, $w * 4) }
    $bw.Write([byte[]]::new($maskStride * $h))
    $bw.Flush()
    return , $ms.ToArray()
}

function Write-Ico {
    param([string]$Path, [hashtable[]]$Frames)

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([uint16]0)                # reserved
    $bw.Write([uint16]1)                # ICO
    $bw.Write([uint16]$Frames.Count)

    $offset = 6 + 16 * $Frames.Count
    foreach ($f in $Frames) {
        $dim = [byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size }))  # 0 means 256
        $bw.Write($dim); $bw.Write($dim)
        $bw.Write([byte]0)              # palette entries
        $bw.Write([byte]0)              # reserved
        $bw.Write([uint16]1)            # planes
        $bw.Write([uint16]32)           # bits per pixel
        $bw.Write([uint32]([byte[]]$f.Bytes).Length)
        $bw.Write([uint32]$offset)
        $offset += ([byte[]]$f.Bytes).Length
    }
    foreach ($f in $Frames) { $bw.Write([byte[]]$f.Bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

# Bottom-up 32bpp BI_RGB bitmap with a plain 54-byte header — the layout WiX
# expects, and the layout of the file this replaces.
function Write-Bmp32 {
    param([string]$Path, [System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $w = $Bitmap.PixelWidth
    $h = $Bitmap.PixelHeight
    $pixels = Get-Bgra32 $Bitmap
    $imageSize = $w * $h * 4

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([byte]0x42); $bw.Write([byte]0x4D)   # "BM"
    $bw.Write([uint32](54 + $imageSize))
    $bw.Write([uint16]0); $bw.Write([uint16]0)
    $bw.Write([uint32]54)              # pixel data offset
    $bw.Write([uint32]40)              # biSize
    $bw.Write([int32]$w)
    $bw.Write([int32]$h)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)               # BI_RGB
    $bw.Write([uint32]$imageSize)
    $bw.Write([int32]3780)             # 96 dpi in pixels/metre, as the original
    $bw.Write([int32]3780)
    $bw.Write([uint32]0); $bw.Write([uint32]0)   # clrUsed, clrImportant

    # Alpha is passed through, not zeroed. BI_RGB calls the 4th byte unused, but
    # the file this replaces stores 0xFF in all 28594 of them and consumers are
    # free to honour it — an all-zero channel can render the banner transparent.
    # The canvas is drawn over an opaque background, so pass-through gives 0xFF.
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $w * 4, $w * 4) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

# Blue SVG variant, tight-cropped: same path data, viewBox narrowed to the ink
# bounds so the file can be dropped into a README without stray padding.
function Write-BlueSvg {
    param([string]$Path, [string]$Data, [string]$Figures)
    $b = ([System.Windows.Media.Geometry]::Parse($Data)).Bounds
    # Invariant culture — a de-DE decimal comma would make the viewBox unparseable.
    $inv = [cultureinfo]::InvariantCulture
    $n = $b.X, $b.Y, $b.Width, $b.Height | ForEach-Object {
        [Math]::Round($_, 2).ToString('0.##', $inv) }
    $vb = $n -join ' '
    # width/height as well as viewBox: consumers that need an intrinsic size fall
    # back to 300x150 without them, which collapses the mark.
    $svg = '<?xml version="1.0" encoding="UTF-8"?>' +
        "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"$vb`" " +
        "width=`"$($n[2])`" height=`"$($n[3])`" fill=`"$Blue`">" +
        "<path d=`"$Figures`"/></svg>"
    [System.IO.File]::WriteAllText($Path, $svg, [System.Text.UTF8Encoding]::new($false))
}

# --- Icon ---------------------------------------------------------------------
# Windows reads the directory and picks by size, so frame order is free; small
# sizes stay uncompressed DIBs because WPF's ICO decoder hands Frames[0] to
# Window.Icon and the tray, and both want a crisp 16 px.
$IcoSizes = 16, 20, 24, 32, 48, 64, 128, 256
$icoPath = Join-Path $RepoRoot 'src\Pia.Wpf\Resources\Icons\Pia.ico'
$frames = foreach ($size in $IcoSizes) {
    $bmp = Render-Mark -Size $size
    @{
        Size  = $size
        Bytes = if ($size -ge 128) { Get-PngBytes $bmp } else { Get-DibFrame $bmp }
    }
}
Write-Ico -Path $icoPath -Frames $frames
Write-Host ("wrote {0} ({1:N0} bytes, {2} frames)" -f
    $icoPath, (Get-Item $icoPath).Length, $frames.Count)

# --- MSI banner ---------------------------------------------------------------
# 493x58 white strip; installer text sits on the left, so the wordmark is
# right-aligned and vertically centred. Dimensions and pixel format must stay
# exactly as-is — vpk hands the file straight to WiX.
$bannerPath = Join-Path $RepoRoot 'src\Pia.Wpf\Resources\Installer\logo.bmp'
$bannerH = 36.0
$marginRight = 16.0
$wordAspect = ([System.Windows.Media.Geometry]::Parse($WordData)).Bounds
$bannerW = $bannerH * $wordAspect.Width / $wordAspect.Height
$banner = Render-Glyph -Data $WordData -PixelWidth 493 -PixelHeight 58 `
    -ContentW $bannerW -ContentH $bannerH `
    -X (493 - $marginRight - $bannerW) -Y ((58 - $bannerH) / 2) `
    -Background ([System.Windows.Media.Brushes]::White)
Write-Bmp32 -Path $bannerPath -Bitmap $banner
Write-Host ("wrote {0} ({1:N0} bytes)" -f $bannerPath, (Get-Item $bannerPath).Length)

# --- SVG variant --------------------------------------------------------------
# Only the wordmark: it is what the README embeds. A blue short mark has no
# consumer — the icon surfaces take Pia.ico and in-app XAML takes PiaLogo.xaml.
$svgPath = Join-Path $RepoRoot 'src\Pia.Wpf\Resources\Icons\pia-logo_blau_RGB.svg'
Write-BlueSvg -Path $svgPath -Data $WordData -Figures $WordFigures
Write-Host "wrote $svgPath"

# --- Preview ------------------------------------------------------------------
if ($PreviewPath) {
    $swatches = @(
        @{ Name = 'light'; Bg = '#FFF3F3F3' }   # Windows 11 light taskbar
        @{ Name = 'dark';  Bg = '#FF202020' }   # Windows 11 dark taskbar
        @{ Name = 'canvas'; Bg = '#FF0B1220' }  # BgCanvasBrush, dark theme
    )
    # Shown at 1:1 — scaling a preview up hides exactly the mush it should expose.
    $previewSizes = 16, 20, 24, 32, 48, 64
    $cell = 80
    $sheetW = $cell * $previewSizes.Count
    $sheetH = $cell * $swatches.Count
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    for ($r = 0; $r -lt $swatches.Count; $r++) {
        $bg = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.ColorConverter]::ConvertFromString($swatches[$r].Bg))
        $dc.DrawRectangle($bg, $null,
            [System.Windows.Rect]::new(0, $r * $cell, $sheetW, $cell))
        for ($c = 0; $c -lt $previewSizes.Count; $c++) {
            $size = $previewSizes[$c]
            $frame = Render-Mark -Size $size
            $dc.DrawImage($frame, [System.Windows.Rect]::new(
                    $c * $cell + [Math]::Floor(($cell - $size) / 2),
                    $r * $cell + [Math]::Floor(($cell - $size) / 2), $size, $size))
        }
    }
    $dc.Close()
    $sheet = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $sheetW, $sheetH, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($visual)
    [System.IO.File]::WriteAllBytes($PreviewPath, (Get-PngBytes $sheet))
    Write-Host "wrote preview $PreviewPath"
}
