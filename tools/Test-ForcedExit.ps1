param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
if (-not $run.StartsWith("$repoRoot\docs\stage1-runs\", [StringComparison]::OrdinalIgnoreCase)) { throw '측정 폴더 범위 밖이다.' }
$record = Get-Content -Raw -Encoding UTF8 -LiteralPath "$run\started.json" | ConvertFrom-Json
$parent = Get-Process -Id $record.parentPid
if ($parent.ProcessName -ne 'Diagnostics' -or -not $parent.Path.StartsWith("$repoRoot\src\Diagnostics\", [StringComparison]::OrdinalIgnoreCase)) { throw '이번 진단 프로세스가 아니다.' }
$server = Get-Process -Id $record.serverPid
if ($server.ProcessName -ne 'llama-server' -or -not $server.Path.StartsWith("$repoRoot\third_party\", [StringComparison]::OrdinalIgnoreCase)) { throw '이번 서버 프로세스가 아니다.' }
$before = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits
Stop-Process -Id $parent.Id -Force
$deadline = (Get-Date).AddSeconds(15)
do {
    Start-Sleep -Milliseconds 250
    $remaining = Get-Process -Id $record.serverPid -ErrorAction SilentlyContinue
} while ($null -ne $remaining -and (Get-Date) -lt $deadline)
Start-Sleep -Seconds 2
$after = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits
$result = [ordered]@{ parentPid=$record.parentPid; serverPid=$record.serverPid; serverRemaining=($null -ne $remaining); memoryUsedMiBBefore=$before; memoryUsedMiBAfter=$after; time=(Get-Date).ToString('o') }
$json = $result | ConvertTo-Json
[IO.File]::WriteAllText("$run\forced-exit.json", $json, (New-Object Text.UTF8Encoding($false)))
Write-Output $json
if ($null -ne $remaining) { throw '서버 잔존으로 게이트 실패이다.' }
