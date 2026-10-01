param([string]$AppPath, [string]$DataRoot, [ValidateSet('Replay','Live')][string]$Mode = 'Replay', [int]$Cycles = 1)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Stage4Capture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
}
'@
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $AppPath) { $AppPath = Join-Path $repoRoot 'bin/LocalMindStudio.exe' }
if (-not $DataRoot) { $DataRoot = $repoRoot }
$run = Join-Path $repoRoot ('docs/stage4-runs/gui-' + $Mode + '-' + (Get-Date -Format yyyyMMdd-HHmmss))
$null = New-Item -ItemType Directory -Path $run
$app = $null; $main = $null; $failure = $null; $owned = @(); $remaining = @(); [uint64]$peak = 0; $external = 0
$outcomes = [Collections.Generic.List[object]]::new()
$transitions = [Collections.Generic.List[string]]::new()
function Find-Id($parent,[string]$id) {
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $item = $parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if (-not $item) { throw ('UI 없음: ' + $id) }; return $item
}
function Click-Id($parent,[string]$id) { (Find-Id $parent $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Toggle-Id($parent,[string]$id) { (Find-Id $parent $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Observe {
 $path = Join-Path $DataRoot ('docs/stage1-runs/gui-telemetry-' + $app.Id + '.json')
 if (Test-Path -LiteralPath $path) {
  $t = Get-Content -Raw -Encoding utf8 -LiteralPath $path | ConvertFrom-Json
  if ($t.gpu) { $script:peak = [Math]::Max($script:peak,[uint64]$t.gpu.UsedBytes) }
  if ($t.sockets -match '외부 연결 (\d+)건') { $script:external = [Math]::Max($script:external,[int]$matches[1]) }
 }
}
function Wait-Idle {
 $deadline = (Get-Date).AddMinutes(4)
 do {
  Start-Sleep -Milliseconds 150; Observe
  $busy = (Find-Id $main 'Cancel').Current.IsEnabled
  if ((Get-Date) -gt $deadline) { throw '데모 처리 시간 초과이다.' }
 } while ($busy)
}
function Save-Screen([string]$label) {
 $handle = [IntPtr]$main.Current.NativeWindowHandle
 $rect = [Stage4Capture+Rect]::new(); $null = [Stage4Capture]::GetWindowRect($handle,[ref]$rect)
 $bitmap = [Drawing.Bitmap]::new(($rect.R-$rect.L),($rect.B-$rect.T)); $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { $null = [Stage4Capture]::PrintWindow($handle,$dc,2) } finally { $graphics.ReleaseHdc($dc) }
 try { $bitmap.Save((Join-Path $run ($label + '.png')),[Drawing.Imaging.ImageFormat]::Png) } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
try {
 $app = Start-Process -FilePath $AppPath -WorkingDirectory $env:TEMP -ArgumentList '--diagnostics' -WindowStyle Hidden -PassThru
 $deadline = (Get-Date).AddSeconds(20)
 do { Start-Sleep -Milliseconds 150; $app.Refresh() } while ($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
 if ($app.MainWindowHandle -eq 0) { throw '앱 창이 없다.' }
 $main = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $null = [Stage4Capture]::ShowWindow($app.MainWindowHandle,5)
 if ($Mode -eq 'Replay') { Toggle-Id $main 'DemoReplay' }
 Toggle-Id $main 'DemoNotesToggle'
 foreach ($cycle in 1..$Cycles) {
  Click-Id $main 'DemoBegin'; Start-Sleep -Milliseconds 150
  foreach ($step in 0..7) {
   $progress = (Find-Id $main 'DemoProgress').Current.Name
   if (-not $progress.StartsWith((($step+1).ToString() + '/8'))) { throw ('단계 순서 오류: ' + $progress) }
   Click-Id $main 'DemoRun'; Wait-Idle
   $status = (Find-Id $main 'Status').Current.Name
   if ($Mode -eq 'Live' -and $step -eq 4) {
    $byPid = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    $byName = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'어디서나 부르는 AI')
    $popup = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.AndCondition]::new($byPid,$byName))
    if (-not $popup) { throw '데모 가짜 문장 팝업이 없다.' }
    Click-Id $popup 'Shortcut-영어 번역'; Wait-Idle
    $popupState = (Find-Id $popup 'ShortcutState').Current.Name
    $result = (Find-Id $popup 'ShortcutOutput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if (-not $popupState.Contains('처리 완료') -or [string]::IsNullOrWhiteSpace($result)) { throw ('가짜 문장 팝업 실패: ' + $popupState) }
    $popup.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
   } else {
    $result = (Find-Id $main 'FeatureResult').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ([string]::IsNullOrWhiteSpace($result)) { throw ('단계 결과 없음: ' + $status) }
    if ($Mode -eq 'Live' -and $step -in @(1,2,3,6) -and -not $status.Contains('처리 완료')) { throw ('실제 처리 실패: ' + $status) }
    if ($step -eq 1 -and -not $result.Contains('9,000')) { throw '영수증 합계 불일치이다.' }
    if ($step -eq 3 -and -not $result.Contains('출장규정.md')) { throw '문서 인용 파일이 없다.' }
    if ($Mode -eq 'Replay' -and $step -in @(1,2,3,4,5,6) -and -not $result.Contains('재생 결과')) { throw '재생 결과 표시가 없다.' }
   }
   if ($Mode -eq 'Replay' -and @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $app.Id) | Where-Object Name -eq 'llama-server.exe').Count -gt 0) { throw '안전 재생에서 서버가 시작됐다.' }
   $outcomes.Add(@{ cycle = $cycle; step = $step; progress = $progress; status = $status; result = $result; mode = $Mode })
   if ($cycle -eq $Cycles -and $step -in @(1,3,7)) { Save-Screen ('step-' + $step) }
   if ($step -lt 7) { Click-Id $main 'DemoNext'; Start-Sleep -Milliseconds 100 }
  }
  Click-Id $main 'DemoPrevious'; Start-Sleep -Milliseconds 100
  if (-not (Find-Id $main 'DemoProgress').Current.Name.StartsWith('7/8')) { throw '이전 단계 이동 실패이다.' }
  if ($cycle -eq $Cycles) {
   Toggle-Id $main 'DemoReplay'; Start-Sleep -Milliseconds 150
   $cleared = (Find-Id $main 'FeatureResult').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
   if (-not [string]::IsNullOrWhiteSpace($cleared)) { throw '모드 변경 후 이전 결과가 남아 있다.' }
   $transitions.Add('모드 변경 시 화면 결과 비움')
   if ($Mode -eq 'Live') {
    Click-Id $main 'DemoRun'; Wait-Idle
    $saved = (Find-Id $main 'FeatureResult').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if (-not $saved.Contains('재생 결과')) { throw '실제 처리 후 재생 전환 표시 실패이다.' }
    Toggle-Id $main 'DemoReplay'; Start-Sleep -Milliseconds 150
    Click-Id $main 'DemoPrevious'; Click-Id $main 'DemoRun'; Wait-Idle
    $review = (Find-Id $main 'FeatureResult').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if (-not $review.Contains('이번 데모의 완료 결과가 없다')) { throw '모드 변경 후 검토 화면에 이전 결과가 혼입됐다.' }
    $transitions.Add('실제→재생→실제 전환·검토 결과 혼입 방지')
   }
  }
  Click-Id $main 'DemoEnd'; Start-Sleep -Milliseconds 100
  if ((Find-Id $main 'DemoNext').Current.IsEnabled) { throw '데모 종료 후 이동 버튼이 활성 상태이다.' }
 }
} catch { $failure = $_.Exception.ToString() }
finally {
 if ($app) {
  $owned = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $app.Id) | Where-Object Name -eq 'llama-server.exe' | Select-Object -ExpandProperty ProcessId)
  if (-not $app.HasExited) { if ($failure -or -not $main) { Stop-Process -Id $app.Id -Force } else { $main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } }
  $deadline = (Get-Date).AddSeconds(15)
  do { Start-Sleep -Milliseconds 150; $remaining = @($owned | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }) } while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)
 }
 $result = @{ time = (Get-Date).ToString('o'); error = $failure; outcomes = @($outcomes.ToArray()); transitions = $transitions.ToArray(); observedPeakBytes = $peak; externalSocketsObservedMax = $external; remainingServers = $remaining; mode = $Mode; cycles = $Cycles; durationGate = '빠른 자동 GUI 순회이며 실제 7분 시연·초심자 게이트가 아니다'; passed = ($null -eq $failure -and $outcomes.Count -eq 8*$Cycles -and $remaining.Count -eq 0 -and $peak -le 7516192768 -and $external -eq 0) }
 [IO.File]::WriteAllText((Join-Path $run 'result.json'),($result | ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
 Write-Output $run
 [pscustomobject]$result | Select-Object passed,error,mode,cycles,observedPeakBytes,externalSocketsObservedMax,remainingServers | ConvertTo-Json -Depth 3
}
if (-not $result.passed) { throw '4단계 GUI 자체 검사 미통과이다.' }
