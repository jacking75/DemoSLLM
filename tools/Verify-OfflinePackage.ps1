param([string]$PackageRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw '배포 폴더가 아니다.' }
$items = @(Get-Item -LiteralPath $folder) + @(Get-ChildItem -LiteralPath $folder -Recurse -Force)
if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw '배포 폴더에는 링크/정션을 사용할 수 없다.' }
$manifestPath = Join-Path $folder 'package-manifest.json'
$expectedManifestHash = [IO.File]::ReadAllText((Join-Path $folder 'package-manifest.sha256')).Trim()
if ($expectedManifestHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $expectedManifestHash) { throw '패키지 목록 무결성 오류다.' }
$manifest = Get-Content -Raw -Encoding utf8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.version -notin @(1,2) -or $manifest.model -ne 'Gemma 4 E2B Q4_0' -or $manifest.context -ne 4096 -or @($manifest.files).Count -eq 0) { throw '알 수 없는 패키지 구성이다.' }
function Get-CheckedPath($file) {
    $path = [string]$file.path
    if (-not $path -or $path -match '[\\:]' -or [IO.Path]::IsPathRooted($path) -or
        @($path.Split('/') | Where-Object { -not $_ -or $_ -in @('.','..') -or $_ -match '[. ]$' }).Count -or
        $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $null -eq $file.size -or $file.size -lt 0 -or
        $path -in @('package-manifest.json','package-manifest.sha256')) { throw '패키지 목록 항목이 유효하지 않다.' }
    $target = [IO.Path]::GetFullPath((Join-Path $folder $path))
    if (-not $target.StartsWith($folder + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '패키지 경계를 벗어난 경로다.' }
    return $target
}
function Assert-CheckedFile([string]$target, $file) {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne $file.size) { throw ('파일 없음/크기 오류: ' + $file.path) }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw ('SHA-256 오류: ' + $file.path) }
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$required = @{}
[long]$bytes = 0
foreach ($file in $manifest.files) {
    $target = Get-CheckedPath $file
    if (-not $seen.Add($file.path)) { throw '중복 파일 목록이다.' }
    Assert-CheckedFile $target $file
    $required[$file.path] = $file
    $bytes += $file.size
}
$missingModels = [Collections.Generic.List[string]]::new()
$partialModels = [Collections.Generic.List[string]]::new()
$downloadedModels = [Collections.Generic.List[string]]::new()
if ($manifest.version -eq 2) {
    if ($manifest.modelMode -notin @('download','bundled') -or @($manifest.modelFiles).Count -ne 3) { throw '알 수 없는 모델 배포 방식이다.' }
    $modelPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($model in $manifest.modelFiles) {
        $target = Get-CheckedPath $model
        if ($model.path -notmatch '^models/[^/]+/[^/]+\.gguf$' -or -not $modelPaths.Add($model.path)) { throw '모델 파일 목록이 유효하지 않다.' }
        if ($manifest.modelMode -eq 'bundled') {
            $entry = $required[$model.path]
            if (-not $entry -or $entry.size -ne $model.size -or $entry.sha256 -ne $model.sha256) { throw '동봉 모델이 파일 목록과 다르다.' }
        } else {
            if ($required.ContainsKey($model.path)) { throw '모델 제외 배포본에 모델이 동봉됐다.' }
            $null = $seen.Add($model.path)
            if (Test-Path -LiteralPath $target) {
                Assert-CheckedFile $target $model
                $bytes += $model.size
                $downloadedModels.Add($model.path)
            } else { $missingModels.Add($model.path) }
            $null = $seen.Add($model.path + '.part')
            if (Test-Path -LiteralPath ($target + '.part')) {
                if (-not (Test-Path -LiteralPath ($target + '.part') -PathType Leaf) -or (Get-Item -LiteralPath ($target + '.part')).Length -gt $model.size) { throw ('부분 다운로드 크기 오류: ' + $model.path) }
                $partialModels.Add($model.path + '.part')
            }
        }
    }
}
$actual = @($items | Where-Object { -not $_.PSIsContainer -and $_.FullName -ne $manifestPath -and $_.FullName -ne (Join-Path $folder 'package-manifest.sha256') })
foreach ($file in $actual) {
    $relative = $file.FullName.Substring($folder.Length + 1).Replace('\','/')
    if (-not $seen.Contains($relative)) { throw ('패키지 목록 밖 파일: ' + $relative) }
}
[pscustomobject]@{ passed = $true; fileCount = $actual.Count; totalBytes = $bytes; model = $manifest.model; context = $manifest.context;
    modelsReady = ($missingModels.Count -eq 0 -and $partialModels.Count -eq 0); missingModels = $missingModels.ToArray();
    partialModels = $partialModels.ToArray(); downloadedModels = $downloadedModels.ToArray(); root = $folder }
