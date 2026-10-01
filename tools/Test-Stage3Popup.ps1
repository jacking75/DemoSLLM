$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class PopupTestCapture {
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int L,T,R,B;}
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
 [DllImport("user32.dll")]public static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
 [DllImport("user32.dll")]public static extern bool UnregisterHotKey(IntPtr h,int id);
}
'@
$repoRoot=(Resolve-Path "$PSScriptRoot\..").Path
$run=Join-Path $repoRoot ('docs\stage3-runs\popup-'+(Get-Date -Format yyyyMMdd-HHmmss))
$null=New-Item -ItemType Directory -Path $run
$app=$null;$failure=$null;$owned=@();$peak=[uint64]0;$external=0
$outcomes=New-Object Collections.Generic.List[object]
function Find-Id($parent,[string]$id){$c=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id);$item=$parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$c);if(-not $item){throw "UI 없음: $id"};return $item}
function Click-Id($parent,[string]$id){(Find-Id $parent $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Observe{
 $path=Join-Path $repoRoot ('docs\stage1-runs\gui-telemetry-'+$app.Id+'.json')
 if(Test-Path $path){$t=Get-Content $path -Raw -Encoding UTF8|ConvertFrom-Json;if($t.gpu){$script:peak=[Math]::Max($script:peak,[uint64]$t.gpu.UsedBytes)};if($t.sockets -match '외부 연결 (\d+)건'){$script:external=[Math]::Max($script:external,[int]$matches[1])}}
}
try{
 $app=Start-Process -FilePath (Join-Path $repoRoot 'bin\LocalMindStudio.exe') -ArgumentList '--diagnostics','--shortcut-fixture' -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(15);do{Start-Sleep -Milliseconds 150;$app.Refresh()}while($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
 if($app.MainWindowHandle -eq 0){throw '앱 창이 없다.'}
 $byPid=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
 $byPopup=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'어디서나 부르는 AI')
 $popup=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.AndCondition]::new($byPid,$byPopup))
 $main=$null
 foreach($candidate in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byPid)){if($candidate.Current.Name.StartsWith('LocalMind Studio')){$main=$candidate;break}}
 if(-not $main -or -not $popup){throw '기반 창 또는 가짜 팝업이 없다.'}
 $null=[PopupTestCapture]::ShowWindow([IntPtr]$main.Current.NativeWindowHandle,5);Click-Id $main 'Start'
 $deadline=(Get-Date).AddMinutes(3)
 while(-not (Find-Id $main 'Send').Current.IsEnabled){Start-Sleep -Milliseconds 200;Observe;if((Get-Date) -gt $deadline){throw '모델 시작 시간 초과이다.'}}
 $reserved=[PopupTestCapture]::RegisterHotKey([IntPtr]::Zero,0x3366,0x4003,0x20)
 if($reserved){$null=[PopupTestCapture]::UnregisterHotKey([IntPtr]::Zero,0x3366);throw '단축키 예약이 확인되지 않았다.'}
 foreach($action in @('요약','맞춤법 교정','정중한 어투','영어 번역')){
  Click-Id $popup ('Shortcut-'+$action);Start-Sleep -Milliseconds 150
  $deadline=(Get-Date).AddMinutes(2)
  do{Observe;Start-Sleep -Milliseconds 150;$state=(Find-Id $popup 'ShortcutState').Current.Name}while($state -eq '실제 로컬 모델 처리 중이다.' -and (Get-Date) -lt $deadline)
  $answer=(Find-Id $popup 'ShortcutOutput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
  if(-not $state.Contains('처리 완료') -or [string]::IsNullOrWhiteSpace($answer)){throw ('팝업 처리 실패: '+$state)}
  $outcomes.Add(@{action=$action;answer=$answer;passed=$true;source='가짜 입력 직접 전달, 외부 앱 선택 아님'})
 }
 $rect=New-Object PopupTestCapture+Rect;$handle=[IntPtr]$popup.Current.NativeWindowHandle;$null=[PopupTestCapture]::GetWindowRect($handle,[ref]$rect)
 $bitmap=New-Object Drawing.Bitmap(($rect.R-$rect.L),($rect.B-$rect.T));$graphics=[Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc()
 try{$null=[PopupTestCapture]::PrintWindow($handle,$dc,2)}finally{$graphics.ReleaseHdc($dc)}
 try{$bitmap.Save((Join-Path $run 'popup.png'),[Drawing.Imaging.ImageFormat]::Png)}finally{$graphics.Dispose();$bitmap.Dispose()}
 $popup.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
 $byE=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'E · 전역 단축키')
 $main.FindFirst([Windows.Automation.TreeScope]::Descendants,$byE).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Click-Id $main 'ShortcutTray';Start-Sleep -Milliseconds 200
 if(-not $main.Current.IsOffscreen){throw '트레이 최소화로 창이 숨겨지지 않았다.'}
 $outcomes.Add(@{test='전역키 예약과 트레이 최소화 창 숨김';passed=$true})
}catch{$failure=$_.Exception.ToString()}
finally{
 if($app){$owned=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$app.Id)|Where-Object Name -eq 'llama-server.exe'|Select-Object -ExpandProperty ProcessId);if(-not $app.HasExited){if($failure){Stop-Process -Id $app.Id -Force}else{$main.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}}}
 $deadline=(Get-Date).AddSeconds(15);do{Start-Sleep -Milliseconds 150;$remaining=@($owned|Where-Object{Get-Process -Id $_ -ErrorAction SilentlyContinue})}while($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)
 $result=@{time=(Get-Date).ToString('o');outcomes=@($outcomes.ToArray());error=$failure;observedPeakBytes=$peak;externalSocketsObservedMax=$external;remainingServers=$remaining;systemHotkeySelection='메모장·브라우저 SendInput은 별도 미확인';passed=($null -eq $failure -and $outcomes.Count -eq 5 -and $remaining.Count -eq 0 -and $peak -le 7516192768 -and $external -eq 0)}
 [IO.File]::WriteAllText((Join-Path $run 'result.json'),($result|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
 Write-Output "팝업 검사: $run";[pscustomobject]$result|Select-Object passed,error,observedPeakBytes,remainingServers|ConvertTo-Json -Depth 3
}
if(-not $result.passed){throw '팝업 자체 검사 미통과이다.'}
