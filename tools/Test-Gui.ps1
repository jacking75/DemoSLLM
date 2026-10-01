param([Parameter(Mandatory=$true)][int]$WindowPid, [switch]$Force, [switch]$RequireOffline)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System.Runtime.InteropServices;
public static class LocalMindGuiNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr window);
    [DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr window, System.IntPtr device, uint flags);
}
'@
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$app = Get-Process -Id $WindowPid
if ($app.ProcessName -ne 'LocalMindStudio' -or -not $app.Path.Equals((Join-Path $repoRoot 'bin\LocalMindStudio.exe'), [StringComparison]::OrdinalIgnoreCase)) { throw '이번 GUI가 아니다.' }
$window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
$null = [LocalMindGuiNative]::SetForegroundWindow($app.MainWindowHandle)
$physical = @(Get-NetAdapter -Physical -ErrorAction Stop | Select-Object Name,Status)
if ($RequireOffline -and @($physical | Where-Object Status -eq 'Up').Count -gt 0) { throw '실제 인터넷 단절 검증에는 물리 어댑터 연결을 먼저 끊어야 한다. 이 스크립트는 어댑터를 변경하지 않는다.' }
function Find-Ui([string]$id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $item = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) { throw "UI 항목이 없다: $id" }
    return $item
}
function Invoke-Ui([string]$id) {
    $element = Find-Ui $id
    $pattern = $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
$run = Join-Path $repoRoot ("docs\stage1-runs\" + (Get-Date -Format yyyyMMdd-HHmmss) + '-gui-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
$null = New-Item -ItemType Directory -Path $run
$before = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits
Invoke-Ui 'Start'
$deadline = (Get-Date).AddSeconds(120)
while (-not (Find-Ui 'Send').Current.IsEnabled) {
    if ((Find-Ui 'Start').Current.IsEnabled) { throw ('GUI 시작 실패: ' + (Find-Ui 'Status').Current.Name) }
    if ((Get-Date) -gt $deadline) { throw ('GUI 시작 시간 초과: ' + (Find-Ui 'Status').Current.Name) }
    Start-Sleep -Milliseconds 250
}
$owned = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$WindowPid" | Where-Object Name -eq 'llama-server.exe' | Select-Object -ExpandProperty ProcessId)
if ($owned.Count -ne 1) { throw 'GUI 소유 서버 개수가 1이 아니다.' }
$serverPid = $owned[0]
$textPattern = (Find-Ui 'Input').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
$textPattern.SetValue('로컬 AI가 사내 문서를 처리할 때의 장점을 한 문장으로 설명하라.')
Invoke-Ui 'Send'
Start-Sleep -Milliseconds 500
$deadline = (Get-Date).AddSeconds(120)
while (-not (Find-Ui 'Send').Current.IsEnabled) {
    if ((Get-Date) -gt $deadline) { throw 'GUI 채팅 시간 초과이다.' }
    Start-Sleep -Milliseconds 250
}
$answer = (Find-Ui 'Answer').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
$metrics = (Find-Ui 'Metrics').Current.Name
$sockets = (Find-Ui 'Sockets').Current.Name
$status = (Find-Ui 'Status').Current.Name
if ($RequireOffline -and -not $metrics.Contains('인터넷: 끊김')) { throw 'Windows 인터넷 상태가 끊김이 아니므로 오프라인 게이트 미충족이다.' }
$sourcePath = Join-Path $repoRoot "docs\stage1-runs\gui-telemetry-$WindowPid.json"
$source = if (Test-Path -LiteralPath $sourcePath) { Get-Content -Raw -Encoding UTF8 -LiteralPath $sourcePath | ConvertFrom-Json } else { $null }
$tcp = @(Get-NetTCPConnection -OwningProcess $serverPid -ErrorAction SilentlyContinue | Select-Object LocalAddress,RemoteAddress,State)
$udp = @(Get-NetUDPEndpoint -OwningProcess $serverPid -ErrorAction SilentlyContinue | Select-Object LocalAddress)
$during = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits
$null = [LocalMindGuiNative]::SetForegroundWindow($app.MainWindowHandle)
Start-Sleep -Milliseconds 300
$rect = $window.Current.BoundingRectangle
$bitmap = New-Object Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$device = $graphics.GetHdc()
try {
    if (-not [LocalMindGuiNative]::PrintWindow($app.MainWindowHandle, $device, 2)) { throw '앱 전용 캡처 실패이다.' }
} finally { $graphics.ReleaseHdc($device) }
$bitmap.Save("$run\gui.png", [Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
if ($Force) { Stop-Process -Id $WindowPid -Force }
else { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
$deadline = (Get-Date).AddSeconds(15)
do {
    Start-Sleep -Milliseconds 250
    $remaining = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
} while ($null -ne $remaining -and (Get-Date) -lt $deadline)
Start-Sleep -Seconds 2
$after = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits
$offline = if ($RequireOffline) { '물리 어댑터 Up 0개·Windows 끊김·GUI 채팅 확인' } else { '미확인: 인터넷 단절 조작 안 함' }
$result = [ordered]@{ parentPid=$WindowPid; serverPid=$serverPid; force=[bool]$Force; answer=$answer; status=$status; metrics=$metrics; sockets=$sockets; sameSampleSource=$source; tcp=$tcp; udp=$udp; physicalAdapters=$physical; memoryUsedMiB=@{ before=$before; during=$during; after=$after }; serverRemaining=($null -ne $remaining); offlineGate=$offline; time=(Get-Date).ToString('o') }
$json = $result | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText("$run\result.json", $json, (New-Object Text.UTF8Encoding($false)))
Write-Output "GUI 측정 폴더: $run"
Write-Output $json
if ($null -ne $remaining -or [string]::IsNullOrWhiteSpace($answer)) { throw 'GUI 게이트 실패이다.' }
