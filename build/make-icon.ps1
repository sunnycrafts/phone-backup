# Generates AppIcon.ico (multi-resolution) for OnePlus Phone Backup.
# A white smartphone on a Windows 11 accent-blue tile, with a green "sync" badge
# in the corner — reads instantly as "phone backup / kept in sync".
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$ErrorActionPreference = 'Stop'
$outPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'AppIcon.ico'

function C([int]$r,[int]$g,[int]$b) { [System.Windows.Media.Color]::FromRgb($r,$g,$b) }
function Brush($color) { $b = New-Object System.Windows.Media.SolidColorBrush($color); $b.Freeze(); $b }
function RRect($x,$y,$w,$h,$rad) { New-Object System.Windows.Rect($x,$y,$w,$h) | Out-Null; }

function Render-Size([int]$size) {
    $s = [double]$size
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    # --- accent rounded tile (vertical gradient) ---
    $grad = New-Object System.Windows.Media.LinearGradientBrush((C 0x2F 0x93 0xE6), (C 0x00 0x5F 0xB8), 90.0)
    $grad.Freeze()
    $tile = New-Object System.Windows.Rect(($s*0.04), ($s*0.04), ($s*0.92), ($s*0.92))
    $dc.DrawRoundedRectangle($grad, $null, $tile, ($s*0.20), ($s*0.20))

    # --- phone body ---
    $bx = $s*0.305; $by = $s*0.165; $bw = $s*0.39; $bh = $s*0.67
    $body = New-Object System.Windows.Rect($bx, $by, $bw, $bh)
    $shadowPen = $null
    $dc.DrawRoundedRectangle((Brush (C 0xFF 0xFF 0xFF)), (New-Object System.Windows.Media.Pen((Brush (C 0xD6 0xDF 0xEA)), ($s*0.008))), $body, ($s*0.085), ($s*0.085))

    # screen
    $inX = $s*0.045; $inTop = $s*0.085; $inBot = $s*0.085
    $screen = New-Object System.Windows.Rect(($bx+$inX), ($by+$inTop), ($bw-2*$inX), ($bh-$inTop-$inBot))
    $dc.DrawRoundedRectangle((Brush (C 0xCF 0xE6 0xFB)), $null, $screen, ($s*0.045), ($s*0.045))

    # earpiece slot
    $earW = $s*0.10; $earH = $s*0.016
    $ear = New-Object System.Windows.Rect(($bx+$bw/2-$earW/2), ($by+$s*0.038), $earW, $earH)
    $dc.DrawRoundedRectangle((Brush (C 0xB3 0xC4 0xD6)), $null, $ear, ($earH/2), ($earH/2))

    # home indicator
    $hiW = $s*0.13; $hiH = $s*0.020
    $hi = New-Object System.Windows.Rect(($bx+$bw/2-$hiW/2), ($by+$bh-$s*0.058), $hiW, $hiH)
    $dc.DrawRoundedRectangle((Brush (C 0xC4 0xD0 0xDD)), $null, $hi, ($hiH/2), ($hiH/2))

    # --- green sync badge, lower-right corner ---
    $cx = $s*0.72; $cy = $s*0.72
    $dc.DrawEllipse((Brush (C 0xFF 0xFF 0xFF)), $null, (New-Object System.Windows.Point($cx,$cy)), ($s*0.205), ($s*0.205))
    $green = New-Object System.Windows.Media.LinearGradientBrush((C 0x3A 0xC9 0x5F), (C 0x1F 0xA8 0x4A), 90.0)
    $green.Freeze()
    $dc.DrawEllipse($green, $null, (New-Object System.Windows.Point($cx,$cy)), ($s*0.175), ($s*0.175))

    # sync glyph centered in the badge
    $glyph = [char]0xE72C
    $tf = New-Object System.Windows.Media.Typeface(
        (New-Object System.Windows.Media.FontFamily('Segoe Fluent Icons, Segoe MDL2 Assets')),
        [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Bold,
        [System.Windows.FontStretches]::Normal)
    $ft = New-Object System.Windows.Media.FormattedText(
        [string]$glyph, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight, $tf, ($s*0.23),
        (Brush (C 0xFF 0xFF 0xFF)), 1.0)
    $dc.DrawText($ft, (New-Object System.Windows.Point(($cx - $ft.Width/2.0), ($cy - $ft.Height/2.0))))

    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { Render-Size $_ }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $png = $pngs[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$png.Length); $bw.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $out.ToArray())

# preview PNG of the 256 frame
$prev = Render-Size 256
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon-preview.png'), $prev)
Write-Host "Wrote $outPath ($($out.Length) bytes, $($sizes.Count) sizes)"
