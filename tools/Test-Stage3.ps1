param([string]$BrowserSession = 'localmind-stage3')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ShortcutTestNative {
 [StructLayout(LayoutKind.Sequential)] private struct Input {public uint Type; public Union Value;}
 [StructLayout(LayoutKind.Explicit)] private struct Union {[FieldOffset(0)] public Keyboard Keyboard;[FieldOffset(0)]public Mouse Mouse;}
 [StructLayout(LayoutKind.Sequential)] private struct Keyboard {public ushort Key,Scan; public uint Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Sequential)] private struct Mouse {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] inputs,int size);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
 [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
 public static void Keys(params ushort[] keys) {
  var inputs=new Input[keys.Length*2];
  for(int i=0;i<keys.Length;i++){inputs[i]=new Input{Type=1,Value=new Union{Keyboard=new Keyboard{Key=keys[i]}}};inputs[keys.Length+i]=new Input{Type=1,Value=new Union{Keyboard=new Keyboard{Key=keys[keys.Length-1-i],Flags=2}}};}
  int size=Marshal.SizeOf(typeof(Input));uint sent=SendInput((uint)inputs.Length,inputs,size);
  if(sent!=inputs.Length)throw new Exception("실제 키 입력 전달 실패이다. sent="+sent+" expected="+inputs.Length+" size="+size+" Win32="+Marshal.GetLastWin32Error());
 }
}
'@
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$run = Join-Path $repoRoot ('docs\stage3-runs\' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
$null = New-Item -ItemType Directory -Path $run
$fixtureText = '가상 고객에게 내일 오후 세 시까지 견적서를 보내 주세요.'
$notePath = Join-Path $run ('stage3-note-' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.txt')
[IO.File]::WriteAllText($notePath,$fixtureText,(New-Object Text.UTF8Encoding($false)))
$guard = $null
$app = $null; $notepadWindow = $null; $popup = $null; $failure = $null
$outcomes = New-Object Collections.Generic.List[object]
$peak = [uint64]0; $external = 0; $owned = @()
function Find-Id($parent,[string]$id) {
 $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $element=$parent.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
 if(-not $element){throw "UI 항목 없음: $id"}; return $element
}
function Invoke-Id($parent,[string]$id){(Find-Id $parent $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Activate-Owned($target) {
 $handle=[IntPtr]$target.Current.NativeWindowHandle
 $null=[ShortcutTestNative]::ShowWindow($handle,9); $null=[ShortcutTestNative]::SetForegroundWindow($handle); Start-Sleep -Milliseconds 300
 if([ShortcutTestNative]::GetForegroundWindow() -ne $handle){[ShortcutTestNative]::Keys(0x12);$null=[ShortcutTestNative]::SetForegroundWindow($handle);Start-Sleep -Milliseconds 300}
 if([ShortcutTestNative]::GetForegroundWindow() -ne $handle){throw '시험 입력 창의 포커스를 얻지 못했다.'}
}
function Observe {
 $path=Join-Path $repoRoot ('docs\stage1-runs\gui-telemetry-'+$app.Id+'.json')
 if(Test-Path -LiteralPath $path){$sample=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json; if($sample.gpu){$script:peak=[Math]::Max($script:peak,[uint64]$sample.gpu.UsedBytes)};if($sample.sockets -match '외부 연결 (\d+)건'){$script:external=[Math]::Max($script:external,[int]$matches[1])}}
}
function Test-Selection($source,[string]$kind,[string]$action) {
 Activate-Owned $source
 if($kind -eq 'browser'){ & playwright-cli "-s=$BrowserSession" click '#fixture' | Out-Null; if($LASTEXITCODE -ne 0){throw '브라우저 입력란 포커스 실패이다.'} }
 else {
  $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document)
  $editor=$source.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
  if(-not $editor){throw '시험 메모장 편집 영역이 없다.'}; $editor.SetFocus()
 }
 [ShortcutTestNative]::Keys(0x11,0x41); Start-Sleep -Milliseconds 150
 $sentinel='LocalMind clipboard sentinel '+[Guid]::NewGuid().ToString('N')
 $clipboard=New-Object Windows.DataObject; $clipboard.SetText($sentinel,[Windows.TextDataFormat]::UnicodeText);$clipboard.SetData('LocalMindFixture','custom-format-sentinel');[Windows.Clipboard]::SetDataObject($clipboard,$true)
 [ShortcutTestNative]::Keys(0x11,0x12,0x20)
 $deadline=(Get-Date).AddSeconds(8); $script:popup=$null
 $byPid=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
 $byName=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'어디서나 부르는 AI')
 while(-not $script:popup -and (Get-Date) -lt $deadline){$script:popup=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.AndCondition]::new($byPid,$byName));Start-Sleep -Milliseconds 100}
 if(-not $script:popup){throw '실제 전역 단축키로 팝업이 뜨지 않았다.'}
 $selected=(Find-Id $script:popup 'ShortcutSelection').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 if($selected.Trim() -ne $fixtureText){throw ('선택 텍스트가 원본과 다르다: '+$selected)}
 if([Windows.Clipboard]::GetText([Windows.TextDataFormat]::UnicodeText) -ne $sentinel -or [Windows.Clipboard]::GetData('LocalMindFixture') -ne 'custom-format-sentinel'){throw '텍스트 또는 사용자 지정 클립보드 형식 복원이 다르다.'}
 $clock=[Diagnostics.Stopwatch]::StartNew(); Invoke-Id $script:popup ('Shortcut-'+$action)
 $deadline=(Get-Date).AddMinutes(2)
 do {Start-Sleep -Milliseconds 150;Observe;$state=(Find-Id $script:popup 'ShortcutState').Current.Name} while($state -eq '실제 로컬 모델 처리 중이다.' -and (Get-Date) -lt $deadline)
 $clock.Stop(); $answer=(Find-Id $script:popup 'ShortcutOutput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 if(-not $state.Contains('처리 완료') -or [string]::IsNullOrWhiteSpace($answer)){throw ('로컬 처리 실패: '+$state)}
 $outcomes.Add(@{source=$kind;action=$action;selection=$selected;answer=$answer;milliseconds=$clock.ElapsedMilliseconds;clipboardTextRestored=$true;clipboardCustomRestored=$true;passed=$true})
 $script:popup.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close();$script:popup=$null
}
try {
 $guard=Start-Process -FilePath (Join-Path $repoRoot 'bin\LocalMindStudio.exe') -ArgumentList '--shortcut-clipboard-guard','--guard-folder',('"'+$run+'"') -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(10)
 while(-not (Test-Path (Join-Path $run 'guard-ready.json')) -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 150;if($guard.HasExited){break}}
 if(-not (Test-Path (Join-Path $run 'guard-ready.json'))){throw '원래 클립보드 메모리 백업에 실패해 입력 검사를 시작하지 않았다.'}
 $app=Start-Process -FilePath (Join-Path $repoRoot 'bin\LocalMindStudio.exe') -ArgumentList '--diagnostics' -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(15); do {Start-Sleep -Milliseconds 150;$app.Refresh()} while($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
 if($app.MainWindowHandle -eq 0){throw '시험 앱 실행 실패이다.'}
 $window=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle);Activate-Owned $window;Invoke-Id $window 'Start'
 $deadline=(Get-Date).AddMinutes(3)
 while(-not (Find-Id $window 'Send').Current.IsEnabled){Observe;Start-Sleep -Milliseconds 200;if((Get-Date) -gt $deadline){throw '로컬 서버 시작 시간 초과이다.'}}
 $registered=[ShortcutTestNative]::RegisterHotKey([IntPtr]::Zero,0x3344,0x4003,0x20)
 if($registered){$null=[ShortcutTestNative]::UnregisterHotKey([IntPtr]::Zero,0x3344);throw '앱의 단축키 예약을 확인하지 못했다.'}
 $null=Start-Process -FilePath 'notepad.exe' -ArgumentList ('"'+$notePath+'"') -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(15)
 $byTitle=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)
 while(-not $notepadWindow -and (Get-Date) -lt $deadline){foreach($candidate in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byTitle)){if($candidate.Current.Name.Contains([IO.Path]::GetFileNameWithoutExtension($notePath))){$notepadWindow=$candidate;break}};Start-Sleep -Milliseconds 150}
 if(-not $notepadWindow){throw '가짜 파일을 연 메모장 창이 없다.'}
 foreach($action in @('요약','맞춤법 교정','정중한 어투','영어 번역')){Test-Selection $notepadWindow 'notepad' $action}
 Activate-Owned $window
 $button=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'E · 전역 단축키')
 $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$button).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Invoke-Id $window 'ShortcutTray';Start-Sleep -Milliseconds 300
 if(-not $window.Current.IsOffscreen){throw '트레이 최소화 상태가 아니다.'}
 $browserWindow=$null
 foreach($candidate in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$byTitle)){if($candidate.Current.Name.Contains('LocalMind Stage3 Fixture')){$browserWindow=$candidate;break}}
 if(-not $browserWindow){throw '별도 브라우저 fixture 창이 없다.'}
 foreach($action in @('요약','맞춤법 교정','정중한 어투','영어 번역')){Test-Selection $browserWindow 'browser' $action}
 $outcomes.Add(@{test='트레이 상태에서 실제 단축키 처리';passed=$true})
} catch {$failure=$_.Exception.ToString()}
finally {
 if($popup){try{$popup.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}catch{}}
 if($notepadWindow){try{Activate-Owned $notepadWindow;[ShortcutTestNative]::Keys(0x11,0x57)}catch{}}
 if($app){$owned=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$app.Id) | Where-Object Name -eq 'llama-server.exe' | Select-Object -ExpandProperty ProcessId);if(-not $app.HasExited){if($failure){Stop-Process -Id $app.Id -Force}else{$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}}}
 $deadline=(Get-Date).AddSeconds(15);do{Start-Sleep -Milliseconds 150;$remaining=@($owned|Where-Object{Get-Process -Id $_ -ErrorAction SilentlyContinue})}while($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)
 if($guard -and -not $guard.HasExited){
  $signalTemp=Join-Path $run 'guard-done.tmp'
  [IO.File]::WriteAllText($signalTemp,(@{expectedSequence=[ShortcutTestNative]::GetClipboardSequenceNumber()}|ConvertTo-Json))
  [IO.File]::Move($signalTemp,(Join-Path $run 'guard-done.json'))
  if(-not $guard.WaitForExit(10000)){$failure=($failure+' 클립보드 복원 보조 프로세스 종료 시간 초과이다.')}
 }
 if(Test-Path (Join-Path $run 'guard-result.json')){$guardResult=Get-Content (Join-Path $run 'guard-result.json') -Raw -Encoding UTF8|ConvertFrom-Json;if(-not $guardResult.restored){$failure=($failure+' 클립보드 복원 실패: '+$guardResult.error)}}else{$failure=($failure+' 클립보드 복원 확인 결과가 없다.')}
 $result=@{time=(Get-Date).ToString('o');outcomes=@($outcomes.ToArray());error=$failure;observedPeakBytes=$peak;sampling='1초 GUI NVML';externalSocketsObservedMax=$external;serverPids=$owned;remainingServers=$remaining;offline='사용자 검증 면제·미확인';passed=($null -eq $failure -and $outcomes.Count -eq 9 -and $remaining.Count -eq 0 -and $peak -le 7516192768 -and $external -eq 0)}
 [IO.File]::WriteAllText((Join-Path $run 'result.json'),($result | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
 Write-Output "3단계 측정 폴더: $run";[pscustomobject]$result|Select-Object passed,error,observedPeakBytes,remainingServers|ConvertTo-Json -Depth 3
}
if(-not $result.passed){throw '3단계 검증 미통과이다.'}
