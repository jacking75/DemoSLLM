param([string]$Destination = "$PSScriptRoot\..\assets\samples\images")
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path $Destination -Force
$cases = @{
    'receipt.png' = @('DEMO RECEIPT', 'Notebook  2 x 3000 = 6000', 'Pen       3 x 1000 = 3000', 'TOTAL: 9000 KRW', 'Synthetic sample - no real customer data')
    'error.png' = @('DEMO APPLICATION ERROR', 'Error: Connection refused', 'Target: 127.0.0.1:8080', 'The local service is not running.', 'Synthetic sample')
    'table.png' = @('DEMO SALES TABLE', 'Department | January | February', 'Sales      | 120     | 150', 'Support    | 80      | 90', 'Synthetic sample')
}
foreach ($name in $cases.Keys) {
    $bitmap = New-Object System.Drawing.Bitmap(1000, 500)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $font = New-Object System.Drawing.Font('Consolas', 24)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $y = 40
        foreach ($line in $cases[$name]) {
            $graphics.DrawString($line, $font, [System.Drawing.Brushes]::Black, 30, $y)
            $y += 75
        }
        $bitmap.Save((Join-Path ([IO.Path]::GetFullPath($Destination)) $name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
Write-Output "합성 이미지 3종 생성 경로: $Destination"
