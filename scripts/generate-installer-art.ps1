[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $repoRoot "installer"
$iconPath = Join-Path $repoRoot "AD-Guardian-logo-_2_.ico"

function New-InstallerArt {
    param(
        [int]$Width,
        [int]$Height,
        [int]$IconSize,
        [string]$OutputPath,
        [System.Drawing.Color]$BackgroundColor
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($BackgroundColor)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $icon = New-Object System.Drawing.Icon($iconPath)
        try {
            $iconBitmap = $icon.ToBitmap()
            try {
                $x = [int](($Width - $IconSize) / 2)
                $y = [int](($Height - $IconSize) / 2)
                $graphics.DrawImage($iconBitmap, $x, $y, $IconSize, $IconSize)
            }
            finally {
                $iconBitmap.Dispose()
            }
        }
        finally {
            $icon.Dispose()
        }

        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-InstallerArt -Width 164 -Height 314 -IconSize 96 -OutputPath (Join-Path $installerDir "wizard-image.png") -BackgroundColor ([System.Drawing.Color]::FromArgb(38, 56, 132))
New-InstallerArt -Width 55 -Height 55 -IconSize 34 -OutputPath (Join-Path $installerDir "wizard-small.png") -BackgroundColor ([System.Drawing.Color]::White)
