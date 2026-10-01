$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$run = Join-Path $repoRoot ('docs/stage4-runs/package-verifier-' + (Get-Date -Format yyyyMMdd-HHmmss))
$null = New-Item -ItemType Directory -Path $run
$folder = Join-Path $run 'fixture'
$null = New-Item -ItemType Directory -Path $folder
$sample = Join-Path $folder 'sample.txt'
[IO.File]::WriteAllText($sample,'synthetic fixture',[Text.UTF8Encoding]::new($false))
$valid = [pscustomobject]@{ version = 1; model = 'Gemma 4 E2B Q4_0'; context = 4096; files = @([pscustomobject]@{ path = 'sample.txt'; size = (Get-Item -LiteralPath $sample).Length; sha256 = (Get-FileHash -LiteralPath $sample -Algorithm SHA256).Hash }) }
$passed = [Collections.Generic.List[string]]::new()
function Write-Manifest($manifest) {
 $path = Join-Path $folder 'package-manifest.json'
 [IO.File]::WriteAllText($path,($manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $folder 'package-manifest.sha256'),(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash,[Text.UTF8Encoding]::new($false))
}
function Reject([string]$name) {
 $rejected = $false
 try { $null = & (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $folder } catch { $rejected = $true }
 if (-not $rejected) { throw ('거부 실패: ' + $name) }; $passed.Add($name)
}
Write-Manifest $valid
$verified = & (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $folder
if (-not $verified.passed) { throw '정상 패키지 검사 실패이다.' }; $passed.Add('정상 고정 파일 통과')
[IO.File]::WriteAllText($sample,'changed fixture',[Text.UTF8Encoding]::new($false)); Reject '파일 변조 거부'
[IO.File]::WriteAllText($sample,'synthetic fixture',[Text.UTF8Encoding]::new($false))
$valid.files[0].path = '../outside.txt'; Write-Manifest $valid; Reject '목록의 외부 경로 거부'
$valid.files[0].path = 'missing.txt'; Write-Manifest $valid; Reject '필수 파일 누락 거부'
$valid.files[0].path = 'sample.txt'; $duplicate = [pscustomobject]@{ version=1; model=$valid.model; context=4096; files=@($valid.files[0],$valid.files[0]) }; Write-Manifest $duplicate; Reject '중복 파일 목록 거부'
Write-Manifest $valid; [IO.File]::AppendAllText((Join-Path $folder 'package-manifest.json'),' '); Reject '패키지 목록 변조 거부'
Write-Manifest $valid; [IO.File]::WriteAllText((Join-Path $folder 'unexpected.txt'),'extra'); Reject '목록 밖 추가 파일 거부'
[IO.File]::WriteAllText((Join-Path $run 'result.json'),(@{ passed=$passed.ToArray(); count=$passed.Count } | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
Write-Output ('패키지 검증기 회귀 ' + $passed.Count + '/' + $passed.Count + ' 통과: ' + $run)
