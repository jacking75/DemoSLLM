param([switch]$Force)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System.Runtime.InteropServices;
public static class Stage2Capture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hwnd);
    private delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, System.IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(System.IntPtr hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(System.IntPtr hwnd);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(System.IntPtr hwnd, EnumProc callback, System.IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(System.IntPtr hwnd);
    [DllImport("user32.dll")] private static extern System.IntPtr GetParent(System.IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr hwnd,uint message,System.IntPtr wparam,string value);
    [DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr hwnd,uint message,System.IntPtr wparam,System.IntPtr lparam);
    public static System.IntPtr FileNameEdit(System.IntPtr dialog) {
        System.IntPtr found = System.IntPtr.Zero;
        EnumChildWindows(dialog, (hwnd, parameter) => {
            var name = new System.Text.StringBuilder(256); GetClassName(hwnd,name,name.Capacity);
            int id = GetDlgCtrlID(hwnd), parentId = GetDlgCtrlID(GetParent(hwnd));
            if(name.ToString()=="Edit" && (id==1148 || id==1001 || parentId==1001 || parentId==1148)) {found=hwnd;return false;}
            return true;
        }, System.IntPtr.Zero);
        return found;
    }
    public static System.IntPtr FileDialog(int pid) {
        System.IntPtr found = System.IntPtr.Zero;
        EnumWindows((hwnd, parameter) => {
            uint owner; GetWindowThreadProcessId(hwnd, out owner);
            var name = new System.Text.StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            if (owner == pid && IsWindowVisible(hwnd) && name.ToString() == "#32770") { found = hwnd; return false; }
            return true;
        }, System.IntPtr.Zero);
        return found;
    }
}
'@
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$run = Join-Path $repoRoot ('docs\stage2-runs\' + (Get-Date -Format yyyyMMdd-HHmmss) + '-gui-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
$null = New-Item -ItemType Directory -Path $run
$appPath = Join-Path $repoRoot 'bin\LocalMindStudio.exe'
$app = Start-Process -FilePath $appPath -ArgumentList '--diagnostics' -WindowStyle Hidden -PassThru
$outcomes = New-Object Collections.Generic.List[object]
$failure = $null
$owned = @()
$peak = [uint64]0
$external = 0
function Find-Id([string]$id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $item = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if ($null -eq $item) { throw "UI 항목이 없다: $id" }
    return $item
}
function Click-Id([string]$id) { (Find-Id $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Click-Name([string]$name) {
    $byName = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,$name)
    $byType = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
    $condition = [Windows.Automation.AndCondition]::new($byName,$byType)
    $item = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    if ($null -eq $item) { throw "버튼이 없다: $name" }
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
}
function Read-Result { return (Find-Id 'FeatureResult').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Save-ReceiptCsv {
    Click-Id 'ImageCsv'
    $deadline = (Get-Date).AddSeconds(10)
    $dialog = $null
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
    while (-not $dialog -and (Get-Date) -lt $deadline) {
        $dialogHandle = [Stage2Capture]::FileDialog($app.Id)
        if ($dialogHandle -ne [IntPtr]::Zero) { $dialog = [Windows.Automation.AutomationElement]::FromHandle($dialogHandle) }
        Start-Sleep -Milliseconds 100
    }
    if (-not $dialog) { throw 'CSV 저장 대화상자가 없다.' }
    $by1001 = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'1001')
    $by1148 = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'1148')
    $byId = [Windows.Automation.OrCondition]::new($by1001,$by1148)
    $byEdit = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)
    $fileName = $null
    $deadline = (Get-Date).AddSeconds(10)
    while (-not $fileName -and (Get-Date) -lt $deadline) {
        $fileName = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new($byId,$byEdit))
        Start-Sleep -Milliseconds 200
    }
    $nativeEdit = [Stage2Capture]::FileNameEdit($dialogHandle)
    if (-not $fileName -and $nativeEdit -eq [IntPtr]::Zero) {
        $dialogRect = New-Object Stage2Capture+Rect
        $null = [Stage2Capture]::GetWindowRect($dialogHandle,[ref]$dialogRect)
        $bitmap = New-Object Drawing.Bitmap(($dialogRect.Right-$dialogRect.Left),($dialogRect.Bottom-$dialogRect.Top))
        $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
        try { $null = [Stage2Capture]::PrintWindow($dialogHandle,$dc,2) } finally { $graphics.ReleaseHdc($dc) }
        try { $bitmap.Save((Join-Path $run 'csv-dialog.png'),[Drawing.Imaging.ImageFormat]::Png) } finally {$graphics.Dispose();$bitmap.Dispose()}
        [pscustomobject]@{name=$dialog.Current.Name;class=$dialog.Current.ClassName;handle=$dialogHandle.ToInt64();type=$dialog.Current.ControlType.ProgrammaticName} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'csv-dialog.json') -Encoding UTF8
        $controls = $dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
        @($controls | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Edit -or $_.Current.ControlType -eq [Windows.Automation.ControlType]::ComboBox -or $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button } | ForEach-Object { [pscustomobject]@{ id=$_.Current.AutomationId; name=$_.Current.Name; type=$_.Current.ControlType.ProgrammaticName } }) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'csv-dialog-controls.json') -Encoding UTF8
        throw 'CSV 파일명 입력란이 없다.'
    }
    $target = Join-Path $run 'receipt.csv'
    if ($fileName) {
        $fileName.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($target)
        $saveId = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'1')
        $save = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,$saveId)
        $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    } else {
        $null = [Stage2Capture]::SendMessage($nativeEdit,0x000C,[IntPtr]::Zero,$target)
        if (-not [Stage2Capture]::PostMessage($dialogHandle,0x0111,[IntPtr]1,[IntPtr]::Zero)) {throw '저장 버튼 명령 전달 실패이다.'}
    }
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path -LiteralPath $target) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $rows = @(Import-Csv -LiteralPath $target -Encoding UTF8)
    if ($rows.Count -lt 2 -or $rows[-1].'금액/둘째 열' -ne '9000') { throw 'CSV 저장 금액이 다르다.' }
    $outcomes.Add(@{scenario='A'; test='실제 SaveFileDialog CSV 저장 및 재읽기'; passed=$true; path=$target})
}
function Observe {
    $sourcePath = Join-Path $repoRoot ('docs\stage1-runs\gui-telemetry-' + $app.Id + '.json')
    if (Test-Path -LiteralPath $sourcePath) {
        $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($source.gpu) { $script:peak = [Math]::Max($script:peak,[uint64]$source.gpu.UsedBytes) }
        if ($source.sockets -match '외부 연결 (\d+)건') { $script:external = [Math]::Max($script:external,[int]$matches[1]) }
    }
}
function Wait-Feature {
    Start-Sleep -Milliseconds 250
    $deadline = (Get-Date).AddMinutes(3)
    while ((Find-Id 'CancelButton').Current.IsEnabled) {
        Observe
        if ((Get-Date) -gt $deadline) { throw 'GUI 처리 시간 초과이다.' }
        Start-Sleep -Milliseconds 200
    }
    Observe
    if (-not (Find-Id 'Status').Current.Name.Contains('처리 완료')) { throw ('GUI 처리 실패: ' + (Find-Id 'Status').Current.Name) }
}
function Capture-Owned([string]$name) {
    $null = [Stage2Capture]::ShowWindow($app.MainWindowHandle,5)
    $null = [Stage2Capture]::SetForegroundWindow($app.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    $rect = $window.Current.BoundingRectangle
    $bitmap = New-Object Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { if (-not [Stage2Capture]::PrintWindow($app.MainWindowHandle,$dc,2)) { throw '앱 캡처 실패이다.' } }
    finally { $graphics.ReleaseHdc($dc) }
    try { $bitmap.Save((Join-Path $run "$name.png"),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    $visualPath = Join-Path $repoRoot ('docs\stage2-runs\gui-render-' + $app.Id + '-' + $name + '.png')
    if (Test-Path -LiteralPath $visualPath) { Copy-Item -LiteralPath $visualPath -Destination (Join-Path $run "$name-render.png") }
}
try {
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 200; $app.Refresh() } while ($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'GUI 실행 실패이다.' }
    $window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $null = [Stage2Capture]::ShowWindow($app.MainWindowHandle,5)
    $null = [Stage2Capture]::SetForegroundWindow($app.MainWindowHandle)
    Click-Id 'Start'
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Find-Id 'Send').Current.IsEnabled) {
        Observe
        if ((Find-Id 'Start').Current.IsEnabled -or (Get-Date) -gt $deadline) { throw ('모델 시작 실패: ' + (Find-Id 'Status').Current.Name) }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Milliseconds 1200
    $telemetry = Get-Content (Join-Path $repoRoot ('docs\stage1-runs\gui-telemetry-' + $app.Id + '.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($telemetry.stats.Context -ne 4096) { throw 'E2B·4K 기본값이 아니다.' }
    Click-Name 'A · 화면·문서 읽기'
    foreach ($sample in @('receipt','error','table')) {
        Click-Id ('Sample-' + $sample)
        Click-Id $(if ($sample -eq 'error') { 'ImageDiagnose' } else { 'ImageExtract' })
        Wait-Feature
        $answer = Read-Result
        if ([string]::IsNullOrWhiteSpace($answer) -or ($sample -eq 'receipt' -and -not $answer.Contains('9,000'))) { throw '이미지 GUI 기준 미충족이다.' }
        $outcomes.Add(@{ scenario='A'; sample=$sample; answer=$answer; passed=$true })
        if ($sample -eq 'receipt') { Save-ReceiptCsv }
    }
    Capture-Owned 'A'
    (Find-Id 'FeatureHost').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1,100)
    Capture-Owned 'A-grid'
    Click-Name 'B · 내 문서 금고'
    Click-Id 'VaultSamples'; Wait-Feature
    $questions = Get-Content (Join-Path $repoRoot 'assets\vault-questions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($question in $questions) {
        (Find-Id 'VaultQuestion').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($question.question)
        Click-Id 'VaultAsk'; Wait-Feature
        $answer = Read-Result
        $sources = @([regex]::Matches($answer,'(?m)^출처: (.+?) · 문단') | ForEach-Object { $_.Groups[1].Value })
        $correct = $sources.Count -gt 0 -and @($sources | Where-Object { $_ -ne $question.file }).Count -eq 0
        $outcomes.Add(@{ scenario='B'; question=$question.question; answer=$answer; sourceCorrect=$correct; expectedFactPresent=$answer.Replace(',','').Contains($question.fact) })
    }
    Capture-Owned 'B'
    Click-Name 'F · AI 한계'
    foreach ($index in 0..2) {
        Click-Id ('Limit-' + $index); Wait-Feature
        $answer = Read-Result
        if (-not $answer.Contains('실제 모델 응답:') -or -not ($answer.Contains('검산값:') -or $answer.Contains('기대 동작:'))) { throw 'F 실제 응답/비교 기준 표시가 없다.' }
        $outcomes.Add(@{ scenario='F'; index=$index; answer=$answer; passed=$true })
    }
    Capture-Owned 'F'
    $owned = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $app.Id) | Where-Object Name -eq 'llama-server.exe' | Select-Object -ExpandProperty ProcessId)
    if ($owned.Count -ne 2) { throw '두 서버 동시 실행이 아니다.' }
} catch { $failure = $_.Exception.Message }
finally {
    $owned = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $app.Id) | Where-Object Name -eq 'llama-server.exe' | Select-Object -ExpandProperty ProcessId)
    if (-not $app.HasExited) {
        if ($Force -or $failure) { Stop-Process -Id $app.Id -Force }
        elseif ($window) { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
        else { $null = $app.CloseMainWindow() }
    }
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $remaining = @($owned | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }) } while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)
    $correct = @($outcomes | Where-Object { $_.scenario -eq 'B' -and $_.sourceCorrect }).Count
    $result = @{ parentPid=$app.Id; serverPids=$owned; force=[bool]$Force; outcomes=@($outcomes.ToArray()); correctSources=$correct; observedPeakBytes=$peak; sampling='GUI 1초 텔레메트리 읽기, 50ms 서비스 실측과 별개'; externalSocketsObservedMax=$external; remainingServers=$remaining; error=$failure; offline='사용자 면제 / 미확인'; visualGate='별도 이미지 검사 필요. render PNG는 실제 WPF 시각 트리이며 OS 화면 캡처가 아니다.'; time=(Get-Date).ToString('o'); passed=($null -eq $failure -and $correct -ge 8 -and $remaining.Count -eq 0 -and $peak -le 7516192768 -and $external -eq 0) }
    [IO.File]::WriteAllText((Join-Path $run 'result.json'),($result | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    Write-Output "GUI 측정 폴더: $run"
    Write-Output ([pscustomobject]$result | Select-Object passed,correctSources,observedPeakBytes,remainingServers,error | ConvertTo-Json -Depth 3)
}
if (-not $result.passed) { throw '2단계 GUI 검증 미통과이다.' }
