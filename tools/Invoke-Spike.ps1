param(
    [string]$Config = "$PSScriptRoot\spike-config.json",
    [switch]$SelfTest,
    [switch]$Preflight
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw 'Python 3 런타임과 py 런처가 필요하다. py -3 --version으로 확인한다.'
}
$spikeArgs = @('-3', '-X', 'utf8', "$PSScriptRoot\spike.py")
if ($SelfTest) { $spikeArgs += '--self-test' }
else {
    $spikeArgs += @('--config', $Config)
    if ($Preflight) { $spikeArgs += '--preflight' }
}
& py @spikeArgs
if ($LASTEXITCODE -ne 0) { throw "스파이크 종료 코드: $LASTEXITCODE" }
