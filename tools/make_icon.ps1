# ============================================================================
#  DSH Balance Pet - app/tray icon generator (development helper, English only)
#
#  Crops the character's face out of sprite.png and writes pet.ico with the
#  sizes Windows asks for. Multi-size ICO, PNG-compressed entries (Vista+).
#
#  Usage:
#     powershell -ExecutionPolicy Bypass -File .\tools\make_icon.ps1
#     powershell -ExecutionPolicy Bypass -File .\tools\make_icon.ps1 -X 430 -Y 230 -Size 420
# ============================================================================
[CmdletBinding()]
param(
    [string]$Source,
    [int]$X = 430,
    [int]$Y = 230,
    [int]$Size = 420,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# A 32bpp bottom-up DIB plus its 1bpp AND mask: the icon entry format GDI+
# actually understands.
function ConvertTo-DibEntry {
    param([System.Drawing.Bitmap]$Bmp, [int]$Size)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try {
        # BITMAPINFOHEADER; height is doubled because the AND mask follows the pixels
        $bw.Write([uint32]40)
        $bw.Write([int32]$Size)
        $bw.Write([int32]($Size * 2))
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]0)                       # BI_RGB
        $bw.Write([uint32]($Size * $Size * 4))
        $bw.Write([int32]0); $bw.Write([int32]0)
        $bw.Write([uint32]0); $bw.Write([uint32]0)

        for ($y = $Size - 1; $y -ge 0; $y--) {      # bottom-up
            for ($x = 0; $x -lt $Size; $x++) {
                $c = $Bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
                $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }

        $rowBytes = [math]::Ceiling($Size / 8.0)
        $pad = (4 - ($rowBytes % 4)) % 4
        for ($y = $Size - 1; $y -ge 0; $y--) {
            for ($b = 0; $b -lt $rowBytes; $b++) {
                $value = 0
                for ($bit = 0; $bit -lt 8; $bit++) {
                    $x = $b * 8 + $bit
                    if ($x -lt $Size -and $Bmp.GetPixel($x, $y).A -lt 128) {
                        $value = $value -bor (0x80 -shr $bit)
                    }
                }
                $bw.Write([byte]$value)
            }
            for ($p = 0; $p -lt $pad; $p++) { $bw.Write([byte]0) }
        }
        $bw.Flush()
        # The leading comma is load-bearing: without it PowerShell enumerates the
        # byte[] into the pipeline and the caller gets Object[], which makes
        # BinaryWriter.Write below silently resolve to Write(bool) and emit a
        # single 0x01 byte per entry.
        return ,$ms.ToArray()
    }
    finally { $bw.Dispose(); $ms.Dispose() }
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'sprite.png' }
if (-not $Out) { $Out = Join-Path $root 'pet.ico' }
if (-not (Test-Path $Source)) { throw "sprite not found: $Source" }

$sizes = 16, 24, 32, 48, 64, 128, 256

$src = [System.Drawing.Bitmap]::FromFile($Source)
try {
    if ($X + $Size -gt $src.Width -or $Y + $Size -gt $src.Height) {
        throw "crop $X,$Y ${Size}x${Size} does not fit inside $($src.Width)x$($src.Height)"
    }
    $crop = New-Object System.Drawing.Rectangle($X, $Y, $Size, $Size)

    # Render every size once. Small sizes are stored as 32bpp DIBs and only the
    # 256px one as PNG: Explorer understands PNG entries, but GDI+ (which is what
    # System.Drawing.Icon and the tray icon go through) reads a PNG entry as a
    # DIB and renders noise.
    $payloads = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()

        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $payloads += , @{ Size = $s; Bytes = $ms.ToArray() }
            $ms.Dispose()
        } else {
            $payloads += , @{ Size = $s; Bytes = (ConvertTo-DibEntry $bmp $s) }
        }
        $bmp.Dispose()
    }

    # ICONDIR + ICONDIRENTRY[] + payloads
    $fs = [System.IO.File]::Create($Out)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)                 # reserved
        $bw.Write([uint16]1)                 # type: icon
        $bw.Write([uint16]$payloads.Count)

        $offset = 6 + 16 * $payloads.Count
        foreach ($p in $payloads) {
            $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
            $bw.Write([byte]$dim)            # width  (0 means 256)
            $bw.Write([byte]$dim)            # height
            $bw.Write([byte]0)               # palette colours
            $bw.Write([byte]0)               # reserved
            $bw.Write([uint16]1)             # colour planes
            $bw.Write([uint16]32)            # bits per pixel
            $bw.Write([uint32]$p.Bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $p.Bytes.Length
        }
        foreach ($p in $payloads) { $bw.Write([byte[]]$p.Bytes) }
    }
    finally { $bw.Dispose(); $fs.Dispose() }

    Write-Host "icon written: $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join '/'))" -ForegroundColor Green

    # A preview sheet, so the crop and the small sizes can be eyeballed.
    $sheet = New-Object System.Drawing.Bitmap(320, 200)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 30, 34, 48))
    $icon = New-Object System.Drawing.Icon($Out)
    $x = 8
    foreach ($s in 16, 32, 48) {
        $g.DrawIcon($icon, (New-Object System.Drawing.Rectangle($x, 8, $s, $s)))
        $x += $s + 12
    }
    $g.DrawIcon($icon, (New-Object System.Drawing.Rectangle(8, 72, 128, 128)))   # shell picks the best size
    $g.DrawString("16 / 32 / 48 / 128", (New-Object System.Drawing.Font('Arial', 9)), [System.Drawing.Brushes]::LightGray, 150, 80)
    $g.Dispose()
    $preview = Join-Path $root 'icon_preview.png'
    $sheet.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    $icon.Dispose()
    Write-Host "preview: $preview"
}
finally { $src.Dispose() }
