$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskAssetRoot = Join-Path $taskRoot 'src\AccessProbe\Assets\Icons'
$taskExportRoot = Join-Path $taskRoot 'design\icons'
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskCatalog = Get-Content -LiteralPath (Join-Path $taskAssetRoot 'catalog.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$taskFluentStream = [IO.File]::OpenRead((Join-Path $taskAssetRoot 'Fluent\FluentIcons.xaml'))
try { $taskFluentArtwork = [Windows.Markup.XamlReader]::Load($taskFluentStream) } finally { $taskFluentStream.Dispose() }
$taskBrandStream = [IO.File]::OpenRead((Join-Path $taskAssetRoot 'Brand\BrandIcon.xaml'))
try { $taskBrandArtwork = [Windows.Markup.XamlReader]::Load($taskBrandStream) } finally { $taskBrandStream.Dispose() }
New-Item -ItemType Directory -Force -Path (Join-Path $taskExportRoot 'svg'),(Join-Path $taskExportRoot 'png') | Out-Null

function New-IconBrush([string]$value) {
    [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($value))
}

function New-IconPen([Windows.Media.Brush]$brush,[double]$width) {
    $taskPen = [Windows.Media.Pen]::new($brush,$width)
    $taskPen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $taskPen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $taskPen.LineJoin = [Windows.Media.PenLineJoin]::Round
    return $taskPen
}

function Render-Icon($art,[int]$size,[bool]$tile) {
    $taskDrawing = [Windows.Media.DrawingGroup]::new()
    $taskDrawing.Children.Add([Windows.Media.GeometryDrawing]::new([Windows.Media.Brushes]::Transparent,$null,
        [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(0,0,32,32))))
    $taskDefault = if ($tile) { [Windows.Media.Brushes]::White } else { New-IconBrush '#34445B' }
    if ($tile -and $art.brandResource) {
        $taskDrawing.Children.Add($taskBrandArtwork[$art.brandResource].Drawing)
    } elseif ($tile -and $art.colorResource) {
        $taskDrawing.Children.Add($taskFluentArtwork[$art.colorResource].Drawing)
    } elseif ($tile) {
        $taskGradient = [Windows.Media.LinearGradientBrush]::new()
        $taskGradient.StartPoint = [Windows.Point]::new(0.1,0)
        $taskGradient.EndPoint = [Windows.Point]::new(0.9,1)
        for ($taskIndex = 0; $taskIndex -lt $art.colors.Count; $taskIndex++) {
            $taskGradient.GradientStops.Add([Windows.Media.GradientStop]::new(
                [Windows.Media.ColorConverter]::ConvertFromString($art.colors[$taskIndex]),$taskIndex / [double]($art.colors.Count-1)))
        }
        $taskDrawing.Children.Add([Windows.Media.GeometryDrawing]::new($taskGradient,
            (New-IconPen (New-IconBrush '#200C284D') 0.45),[Windows.Media.Geometry]::Parse($taskCatalog.tilePath)))
        $taskDrawing.Children.Add([Windows.Media.GeometryDrawing]::new($null,
            (New-IconPen (New-IconBrush '#55FFFFFF') 0.65),
            [Windows.Media.Geometry]::Parse('M 2 10 C 2 4.6 4.6 2 10 2 H 22 C 27.4 2 30 4.6 30 10')))
    }
    foreach ($taskPart in $art.parts) {
        if ($tile -and ($art.colorResource -or $art.brandResource)) { break }
        $taskColor = if ($tile -and $taskPart.color) { New-IconBrush $taskPart.color } else { $taskDefault }
        $taskFill = if ($taskPart.fill) { $taskColor } else { $null }
        $taskWidth = if ($taskPart.width) { $taskPart.width } else { 1.9 }
        $taskPen = if ($taskPart.stroke) { New-IconPen $taskColor $taskWidth } else { $null }
        $taskLayer = [Windows.Media.DrawingGroup]::new()
        if ($null -ne $taskPart.opacity) { $taskLayer.Opacity = $taskPart.opacity }
        $taskRule = if ($taskPart.fillRule -eq 'nonzero') { 'F1 ' } else { 'F0 ' }
        $taskLayer.Children.Add([Windows.Media.GeometryDrawing]::new($taskFill,$taskPen,
            [Windows.Media.Geometry]::Parse($taskRule + $taskPart.path)))
        $taskDrawing.Children.Add($taskLayer)
    }
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    $taskContext.PushTransform([Windows.Media.ScaleTransform]::new($size / 32.0,$size / 32.0))
    $taskContext.DrawDrawing($taskDrawing)
    $taskContext.Pop()
    $taskContext.Close()
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    return $taskBitmap
}

function Save-IconPng($bitmap,[string]$path) {
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $taskStream = [IO.File]::Create($path)
    try { $taskEncoder.Save($taskStream) } finally { $taskStream.Dispose() }
}

$taskIcoSizes = @(16,20,24,32,40,48,64,96,128,256)
$taskApp = $taskCatalog.icons | Where-Object key -eq 'app'
$taskPngFrames = @()
foreach ($taskSize in $taskIcoSizes) {
    $taskBitmap = Render-Icon $taskApp $taskSize $true
    $taskMemory = [IO.MemoryStream]::new()
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskEncoder.Save($taskMemory)
    $taskPngFrames += ,$taskMemory.ToArray()
    $taskMemory.Dispose()
}
$taskIcoStream = [IO.File]::Create((Join-Path $taskAssetRoot 'app.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskIcoStream)
try {
    $taskWriter.Write([uint16]0)
    $taskWriter.Write([uint16]1)
    $taskWriter.Write([uint16]$taskIcoSizes.Count)
    $taskOffset = 6 + 16 * $taskIcoSizes.Count
    for ($taskIndex = 0; $taskIndex -lt $taskIcoSizes.Count; $taskIndex++) {
        $taskSizeByte = if ($taskIcoSizes[$taskIndex] -eq 256) { 0 } else { $taskIcoSizes[$taskIndex] }
        $taskWriter.Write([byte]$taskSizeByte)
        $taskWriter.Write([byte]$taskSizeByte)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskPngFrames[$taskIndex].Length)
        $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskPngFrames[$taskIndex].Length
    }
    foreach ($taskFrame in $taskPngFrames) { $taskWriter.Write([byte[]]$taskFrame) }
} finally { $taskWriter.Dispose(); $taskIcoStream.Dispose() }

foreach ($taskArt in $taskCatalog.icons) {
    $taskTile = $taskArt.group -in @('brand','category','platform')
    if ($taskTile -and $taskArt.brandSvg) {
        Copy-Item -LiteralPath (Join-Path $taskAssetRoot $taskArt.brandSvg) -Destination (Join-Path $taskExportRoot ('svg\' + $taskArt.key + '.svg'))
        Save-IconPng (Render-Icon $taskArt 256 $taskTile) (Join-Path $taskExportRoot ('png\' + $taskArt.key + '.png'))
        continue
    }
    if ($taskTile -and $taskArt.colorSvg) {
        Copy-Item -LiteralPath (Join-Path $taskAssetRoot $taskArt.colorSvg) -Destination (Join-Path $taskExportRoot ('svg\' + $taskArt.key + '.svg'))
        Save-IconPng (Render-Icon $taskArt 256 $taskTile) (Join-Path $taskExportRoot ('png\' + $taskArt.key + '.png'))
        continue
    }
    $taskSvg = [Text.StringBuilder]::new()
    [void]$taskSvg.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" role="img">')
    [void]$taskSvg.AppendLine('<title>' + [Security.SecurityElement]::Escape($taskArt.label) + '</title>')
    if ($taskTile) {
        [void]$taskSvg.AppendLine('<defs><linearGradient id="tile" x1="10%" y1="0%" x2="90%" y2="100%">')
        for ($taskIndex = 0; $taskIndex -lt $taskArt.colors.Count; $taskIndex++) {
            [void]$taskSvg.AppendLine('<stop offset="' + (100 * $taskIndex / ($taskArt.colors.Count - 1)) + '%" stop-color="' + $taskArt.colors[$taskIndex] + '"/>')
        }
        [void]$taskSvg.AppendLine('</linearGradient></defs>')
        [void]$taskSvg.AppendLine('<path d="' + $taskCatalog.tilePath + '" fill="url(#tile)" stroke="#0C284D" stroke-opacity="0.125" stroke-width="0.45"/>')
        [void]$taskSvg.AppendLine('<path d="M 2 10 C 2 4.6 4.6 2 10 2 H 22 C 27.4 2 30 4.6 30 10" fill="none" stroke="white" stroke-opacity="0.333" stroke-width="0.65"/>')
    }
    foreach ($taskPart in $taskArt.parts) {
        $taskColor = if ($taskTile) { if ($taskPart.color) { $taskPart.color } else { '#FFFFFF' } } else { 'currentColor' }
        $taskFill = if ($taskPart.fill) { $taskColor } else { 'none' }
        $taskStroke = if ($taskPart.stroke) { $taskColor } else { 'none' }
        $taskWidth = if ($taskPart.width) { $taskPart.width } else { 1.9 }
        $taskOpacity = if ($null -ne $taskPart.opacity) { $taskPart.opacity } else { 1 }
        $taskFillRule = if ($taskPart.fillRule -eq 'nonzero') { 'nonzero' } else { 'evenodd' }
        [void]$taskSvg.AppendLine('<path d="' + $taskPart.path + '" fill-rule="' + $taskFillRule + '" fill="' + $taskFill + '" stroke="' + $taskStroke +
            '" stroke-width="' + $taskWidth + '" opacity="' + $taskOpacity + '" stroke-linecap="round" stroke-linejoin="round"/>')
    }
    [void]$taskSvg.AppendLine('</svg>')
    [IO.File]::WriteAllText((Join-Path $taskExportRoot ('svg\' + $taskArt.key + '.svg')),$taskSvg.ToString(),$taskEncoding)
    Save-IconPng (Render-Icon $taskArt 256 $taskTile) (Join-Path $taskExportRoot ('png\' + $taskArt.key + '.png'))
}
Save-IconPng (Render-Icon $taskApp 1024 $true) (Join-Path $taskExportRoot 'app-1024.png')
Copy-Item -LiteralPath (Join-Path $taskAssetRoot 'app.ico') -Destination (Join-Path $taskExportRoot 'app.ico')
Write-Output ('Exported ' + $taskCatalog.icons.Count + ' SVG/PNG icons and a 10-size Windows application icon.')
