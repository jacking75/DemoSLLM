param([string]$PackageRoot = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\')
$manifestPath = Join-Path $folder 'package-manifest.json'
$expectedManifestHash = [IO.File]::ReadAllText((Join-Path $folder 'package-manifest.sha256')).Trim()
if ($expectedManifestHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $expectedManifestHash) { throw '패키지 목록 무결성 오류이다.' }
$manifest = Get-Content -Raw -Encoding utf8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.model -ne 'Gemma 4 E2B Q4_0' -or $manifest.context -ne 4096 -or @($manifest.files).Count -eq 0) { throw '알 수 없는 패키지 구성이다.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[long]$bytes = 0
foreach ($file in $manifest.files) {
    if ([IO.Path]::IsPathRooted($file.path) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $file.size -lt 0 -or -not $seen.Add($file.path)) { throw '패키지 목록 항목이 유효하지 않다.' }
    $target = [IO.Path]::GetFullPath((Join-Path $folder $file.path))
    if (-not $target.StartsWith($folder + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '패키지 경계를 벗어난 경로이다.' }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne $file.size) { throw ('파일 없음/크기 오류: ' + $file.path) }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw ('SHA-256 오류: ' + $file.path) }
    $bytes += $file.size
}
$actual = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.FullName -ne $manifestPath -and $_.FullName -ne (Join-Path $folder 'package-manifest.sha256') })
if ($actual.Count -ne $seen.Count) { throw '패키지에 목록과 다른 파일이 있다.' }
[pscustomobject]@{ passed = $true; fileCount = $seen.Count; totalBytes = $bytes; model = $manifest.model; context = $manifest.context; root = $folder }
