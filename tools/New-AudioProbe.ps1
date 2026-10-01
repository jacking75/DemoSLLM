param([string]$Destination = "$PSScriptRoot\..\assets\samples\audio-probe")
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Speech
$null = New-Item -ItemType Directory -Path $Destination -Force
$speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $speech.SelectVoice('Microsoft Heami Desktop')
    $speech.Rate = 0
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $sentences = @(
        '김 대리는 금요일까지 견적서를 작성하고 이 과장은 다음 주 월요일에 검토한다.',
        '출장 숙박비는 하루 십이만 원까지 지원하고 교통비 정산에는 영수증이 필요하다.',
        '박 과장은 십월 오일까지 보고서를 제출하고 고객에게 검토 결과를 전달한다.',
        '다음 회의는 화요일 오후 세 시에 시작한다. 참석자는 영업팀과 지원팀 담당자이다.',
        '계약 해지 통지는 삼십 일 전에 서면으로 한다. 구매 금액 오백만 원 이상은 부서장 승인이 필요하다.'
    )
    for ($i = 0; $i -lt $sentences.Count; $i++) {
        $target = Join-Path ([IO.Path]::GetFullPath($Destination)) ('{0:00}.wav' -f ($i + 1))
        if (Test-Path -LiteralPath $target) { throw "기존 샘플을 보존한다: $target" }
        $speech.SetOutputToWaveFile($target, $format)
        $speech.Speak($sentences[$i])
        $speech.SetOutputToNull()
        Write-Output $target
    }
} finally { $speech.Dispose() }
