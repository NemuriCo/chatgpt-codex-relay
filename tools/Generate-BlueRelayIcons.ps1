param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot ".."))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$sourcePath = Join-Path $RepositoryRoot "src\BlueRelay\Assets\Icons\BlueRelay-flow-20-regular.svg"
$windowsIconPath = Join-Path $RepositoryRoot "src\BlueRelay\Assets\Icons\BlueRelay.ico"
$extensionIconDirectory = Join-Path $RepositoryRoot "browser-extension\icons"
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$extensionSizes = @(16, 32, 48, 128)

$svg = Get-Content -Raw -LiteralPath $sourcePath
$pathMatch = [regex]::Match($svg, '<path[^>]+d="([^"]+)"')
if (-not $pathMatch.Success) {
    throw "Could not find the Flow20 path in $sourcePath"
}

$geometry = [System.Windows.Media.Geometry]::Parse($pathMatch.Groups[1].Value)
$accent = [System.Windows.Media.Color]::FromRgb(0x62, 0xB5, 0xE8)

function Write-FlowPng {
    param(
        [int]$Size,
        [string]$Path
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawingContext = $visual.RenderOpen()
    try {
        $scale = $Size / 20.0
        $drawingContext.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        $drawingContext.DrawGeometry([System.Windows.Media.SolidColorBrush]::new($accent), $null, $geometry)
        $drawingContext.Pop()
    }
    finally {
        $drawingContext.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $fileStream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $encoder.Save($fileStream)
    }
    finally {
        $fileStream.Dispose()
    }
}

function New-PngBytes {
    param([int]$Size)

    $temporaryPath = Join-Path $RepositoryRoot (".icon-temp-{0}.png" -f [guid]::NewGuid().ToString("N"))
    try {
        Write-FlowPng -Size $Size -Path $temporaryPath
        return ,([System.IO.File]::ReadAllBytes($temporaryPath))
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Write-FlowIco {
    param(
        [int[]]$IconSizes,
        [string]$Path
    )

    $pngEntries = foreach ($size in $IconSizes) {
        [pscustomobject]@{ Size = $size; Bytes = New-PngBytes -Size $size }
    }
    $directorySize = 6 + (16 * $pngEntries.Count)
    $offset = $directorySize
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$pngEntries.Count)
        foreach ($entry in $pngEntries) {
            $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$entry.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $entry.Bytes.Length
        }
        foreach ($entry in $pngEntries) {
            $writer.Write([byte[]]$entry.Bytes, 0, $entry.Bytes.Length)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $extensionIconDirectory | Out-Null
Write-FlowIco -IconSizes $sizes -Path $windowsIconPath
foreach ($size in $extensionSizes) {
    Write-FlowPng -Size $size -Path (Join-Path $extensionIconDirectory ("icon{0}.png" -f $size))
}

Write-Output "Generated $windowsIconPath with sizes: $($sizes -join ', ')"
Write-Output "Generated extension PNG sizes: $($extensionSizes -join ', ')"
