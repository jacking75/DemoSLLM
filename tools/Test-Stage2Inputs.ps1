$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$startedAt = Get-Date
$appPath = Join-Path $repoRoot 'bin\LocalMindStudio.exe'
$app = Start-Process -FilePath $appPath -ArgumentList '--diagnostics','--input-self-test' -WindowStyle Hidden -PassThru
if (-not $app.WaitForExit(30000)) { throw "입력 진단 시간 초과이다. 시험 앱 PID $($app.Id)의 캡처 창을 Esc로 닫아 클립보드 복원 경로를 실행해야 한다." }
$latest = Get-ChildItem (Join-Path $repoRoot 'docs\stage2-runs') -Directory -Filter 'inputs-*' | Where-Object LastWriteTime -ge $startedAt.AddSeconds(-1) | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) { throw '이번 실행의 입력 진단 결과 폴더가 없다.' }
$resultPath = Join-Path $latest.FullName 'result.json'
$result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Output "입력 진단 원자료: $resultPath"
$result | ConvertTo-Json -Depth 5
if ($result.error) { throw '입력 진단 미통과이다. 원자료의 실제 실패 사유를 확인해야 한다.' }
