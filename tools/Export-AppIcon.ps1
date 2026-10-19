# Run with Windows PowerShell -STA; exports the existing project-owned WPF drawing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path -Parent $PSScriptRoot
[xml]$xaml = Get-Content -LiteralPath (Join-Path $root 'src/RepoTracker.Desktop/Themes/Controls.xaml') -Raw -Encoding UTF8
$node = $xaml.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'DrawingImage' -and $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') -eq 'AppIcon' }
$markup = $node.OuterXml -replace ' x:Key="AppIcon"', ''
$drawing = [Windows.Markup.XamlReader]::Parse($markup)
$frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, (New-Object Windows.Rect(0, 0, $size, $size)))
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32))
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object IO.MemoryStream
    $encoder.Save($memory)
    [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
    $memory.Dispose()
}
$assets = Join-Path $root 'src/RepoTracker.Desktop/Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$file = [IO.File]::Create((Join-Path $assets 'RepoTracker.ico'))
$writer = New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
