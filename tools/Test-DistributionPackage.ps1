$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $PSScriptRoot 'Test-OfflinePackageVerifier.ps1')
$run = Join-Path $repoRoot ('docs/distribution-runs/verifier-' + (Get-Date -Format yyyyMMdd-HHmmss))
$folder = Join-Path $run '한글 테스트 폴더'
$null = New-Item -ItemType Directory -Path (Join-Path $folder 'models/test')
$encoding = [Text.UTF8Encoding]::new($false)
$sample = Join-Path $folder 'sample.txt'
[IO.File]::WriteAllText($sample,'synthetic fixture',$encoding)
$file = [pscustomobject]@{ path='sample.txt'; size=(Get-Item $sample).Length; sha256=(Get-FileHash $sample).Hash }
$modelBytes = [Text.Encoding]::UTF8.GetBytes('synthetic model')
$sha = [Security.Cryptography.SHA256]::Create()
try { $modelHash = [BitConverter]::ToString($sha.ComputeHash($modelBytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
$models = @('a','b','c') | ForEach-Object { [pscustomobject]@{path=('models/test/' + $_ + '.gguf');size=$modelBytes.Length;sha256=$modelHash} }
$valid = [pscustomobject]@{version=2;model='Gemma 4 E2B Q4_0';context=4096;modelMode='download';modelFiles=@($models);files=@($file)}
$passed = [Collections.Generic.List[string]]::new()
function Write-Manifest {
    $path = Join-Path $folder 'package-manifest.json'
    [IO.File]::WriteAllText($path,($valid | ConvertTo-Json -Depth 6),$encoding)
    [IO.File]::WriteAllText((Join-Path $folder 'package-manifest.sha256'),(Get-FileHash $path).Hash,$encoding)
}
function Check([string]$name,[scriptblock]$assertion) {
    $result = & (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $folder
    if (-not $result.passed -or -not (& $assertion $result)) { throw ('검사 실패: ' + $name) }
    $passed.Add($name)
}
function Reject([string]$name,[scriptblock]$action) {
    $rejected=$false
    try { if ($action) { & $action | Out-Null } else { & (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $folder | Out-Null } } catch { $rejected=$true }
    if (-not $rejected) { throw ('거부 실패: ' + $name) }; $passed.Add($name)
}
Write-Manifest
Check '모델 제외 배포본은 무결성 통과/모델 미준비' {param($r) -not $r.modelsReady -and $r.missingModels.Count -eq 3}
foreach ($model in $models) { [IO.File]::WriteAllBytes((Join-Path $folder $model.path),$modelBytes) }
Check '다운로드 완료된 모델 3개 통과' {param($r) $r.modelsReady -and $r.downloadedModels.Count -eq 3}
$first = Join-Path $folder $models[0].path
[IO.File]::WriteAllBytes($first,[byte[]](1,2,3)); Reject '다운로드 모델 크기 변조 거부'
[IO.File]::WriteAllBytes($first,([byte[]]::new($modelBytes.Length))); Reject '다운로드 모델 해시 변조 거부'
[IO.File]::WriteAllBytes($first,$modelBytes)
Remove-Item -LiteralPath $first
[IO.File]::WriteAllBytes(($first + '.part'),[byte[]](1,2,3))
Check '이어받기 파일은 미완료 상태로 구분' {param($r) -not $r.modelsReady -and $r.partialModels.Count -eq 1 -and $r.missingModels.Count -eq 1}
[IO.File]::WriteAllBytes(($first + '.part'),([byte[]]::new($modelBytes.Length + 1))); Reject '원본보다 큰 부분 파일 거부'
Remove-Item -LiteralPath ($first + '.part')
[IO.File]::WriteAllBytes($first,$modelBytes)
$extra = Join-Path $folder 'models/test/unknown.gguf'; [IO.File]::WriteAllBytes($extra,$modelBytes); Reject '고정 목록 밖 모델 거부'; Remove-Item -LiteralPath $extra
$valid.modelMode='bundled'; $valid.files=@($file)+@($models); Write-Manifest
Check '동봉 모델 3개 정상 통과' {param($r) $r.modelsReady}
Remove-Item -LiteralPath $first; Reject '동봉 모델 누락 거부'; [IO.File]::WriteAllBytes($first,$modelBytes)
$valid.files=@($file)+@($models[1],$models[2]); Write-Manifest; Reject '동봉 모델 목록 누락 거부'
$valid.modelMode='download'; $valid.files=@($file); $valid.modelFiles=@($models[0],$models[0],$models[2]); Write-Manifest; Reject '중복 모델 목록 거부'
$valid.modelFiles=@($models); $valid.modelMode='unknown'; Write-Manifest; Reject '알 수 없는 배포 방식 거부'
$valid.modelMode='download'; $file.path='sub/../sample.txt'; Write-Manifest; Reject '같은 파일을 가리키는 별칭 경로 거부'
$file.path='sample.txt'; $valid.files=@($file,[pscustomobject]@{path='SAMPLE.TXT';size=$file.size;sha256=$file.sha256}); Write-Manifest; Reject '대소문자 중복 경로 거부'
$valid.files=@($file); Write-Manifest
$outside=Join-Path $run 'outside'; $null=New-Item -ItemType Directory -Path $outside
$junction=Join-Path $folder 'linked'; $null=New-Item -ItemType Junction -Path $junction -Target $outside
Reject '폴더 정션 거부'
# 테스트 정션은 외부 대상 폴더를 건드리지 않고 링크 자체만 제거한다.
[IO.Directory]::Delete($junction)
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$vc=Get-VcRuntimeDirectory
$info=Get-NativePeInfo (Join-Path $vc 'vcruntime140.dll')
if ($info.architecture -ne 'x64' -or $info.imports.Count -eq 0) { throw '실제 CRT PE 분석 실패다.' }; $passed.Add('실제 x64 CRT import 분석 통과')
$invalid=Join-Path $run 'invalid.dll'; [IO.File]::WriteAllBytes($invalid,[byte[]](0,0,0,0)); Reject 'PE 아닌 DLL 거부' {Get-NativePeInfo $invalid}
$x86=Join-Path $run 'wrong-architecture.dll'; $peBytes=[IO.File]::ReadAllBytes((Join-Path $vc 'vcruntime140.dll')); $peOffset=[BitConverter]::ToInt32($peBytes,0x3c); $peBytes[$peOffset+4]=0x4c; $peBytes[$peOffset+5]=0x01
[IO.File]::WriteAllBytes($x86,$peBytes); Reject 'x86 DLL 혼입 거부' {Get-NativePeInfo $x86}
Reject '고정 원본 해시/크기 불일치 거부' {Assert-ArtifactFile $sample ([pscustomobject]@{name='fixture';size=$file.size;sha256=('0'*64)})}
Reject '배포 이름의 외부 경로 거부' {& (Join-Path $PSScriptRoot 'New-DistributionPackage.ps1') -PackageName '../outside' -SkipBuild}
[IO.File]::WriteAllText((Join-Path $run 'result.json'),(@{passed=$passed.ToArray();count=$passed.Count} | ConvertTo-Json -Depth 5),$encoding)
Write-Output ('새 배포 회귀 ' + $passed.Count + '/' + $passed.Count + ' 통과: ' + $run)
