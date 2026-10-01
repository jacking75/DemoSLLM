param([string]$PackageName = ('LocalMindStudio-offline-win-x64-' + (Get-Date -Format yyyyMMdd-HHmmss)), [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($PackageName -notmatch '^LocalMindStudio-offline-win-x64-[A-Za-z0-9_-]+$') { throw '패키지 이름 형식이 유효하지 않다.' }
$dist = Join-Path $repoRoot 'dist'
$package = Join-Path $dist $PackageName
$archive = $package + '.zip'
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $archive)) { throw '기존 패키지를 교체하지 않는다. 새 이름을 사용해야 한다.' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build-App.ps1') -Publish -Configuration Release -LogName stage4-release-build.log }
$publish = Join-Path $repoRoot 'bin'
if (-not (Test-Path -LiteralPath (Join-Path $publish 'LocalMindStudio.exe'))) { throw '루트 bin의 단일 실행 파일이 없다.' }
$artifacts = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $PSScriptRoot 'spike-artifacts.json') | ConvertFrom-Json
$models = @($artifacts | Where-Object { $_.name -in @('fallback','fallback-mmproj','embedding') })
if ($models.Count -ne 3) { throw '데모 모델 목록이 불완전하다.' }
foreach ($artifact in @($models) + @($artifacts | Where-Object { $_.name -in @('llama-cuda12','cudart-cuda12') })) {
    $source = Join-Path $repoRoot $artifact.path
    if (-not (Test-Path -LiteralPath $source) -or (Get-Item -LiteralPath $source).Length -ne $artifact.size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $artifact.sha256) { throw ('고정 원본 무결성 오류: ' + $artifact.name) }
}
$licenses = Join-Path $repoRoot 'third_party/licenses'
foreach ($required in @('llama.cpp-LICENSE.txt','OpenXML-LICENSE.txt','PdfPig-LICENSE.txt','SQLitePCLRaw-LICENSE.txt','NVIDIA-CUDA-12.4-EULA.txt','Gemma-Terms.txt','Gemma-Prohibited-Use-Policy.txt','Gemma-E2B-README.md','EmbeddingGemma-README.md','Apache-2.0.txt','dotnet-LICENSE.txt','dotnet-ThirdPartyNotices.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $licenses $required))) { throw ('배포 고지 누락: ' + $required) }
}
$null = New-Item -ItemType Directory -Path $package
Copy-Item -LiteralPath (Join-Path $publish 'LocalMindStudio.exe') -Destination $package
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets') -Destination (Join-Path $package 'assets') -Recurse
$null = New-Item -ItemType Directory -Path (Join-Path $package 'tools')
[IO.File]::WriteAllText((Join-Path $package 'tools/spike-artifacts.json'), ($models | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -Destination (Join-Path $package 'tools')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/demo-checklist.md') -Destination (Join-Path $package '시작안내.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $package 'THIRD_PARTY_NOTICES.md')
foreach ($model in $models) {
    $target = Join-Path $package $model.path
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $target)
    Copy-Item -LiteralPath (Join-Path $repoRoot $model.path) -Destination $target
}
$runtime = Join-Path $package 'third_party/llama-b11146-cuda12'
$null = New-Item -ItemType Directory -Force -Path $runtime
foreach ($artifact in $artifacts | Where-Object { $_.name -in @('llama-cuda12','cudart-cuda12') }) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $repoRoot $artifact.path))
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.Name.EndsWith('.dll') -or $_.Name -eq 'llama-server.exe' -or $_.Name.StartsWith('LICENSE') }) {
            if ($entry.FullName -ne $entry.Name) { throw '고정 ZIP 내부 경로가 달라졌다.' }
            $target = Join-Path $runtime $entry.Name
            if (Test-Path -LiteralPath $target) { throw '런타임 ZIP 간 중복 파일이다.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
        }
    } finally { $zip.Dispose() }
}
Copy-Item -LiteralPath $licenses -Destination (Join-Path $package 'third_party/licenses') -Recurse
$nugetTarget = Join-Path $package 'third_party/licenses/nuget'
$null = New-Item -ItemType Directory -Path $nugetTarget
$assets = Get-Content -Raw -Encoding utf8 (Join-Path $repoRoot 'src/App.Wpf/obj/project.assets.json') | ConvertFrom-Json
$nugetInfo = @()
foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
    $source = $null
    foreach ($base in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $base $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
    }
    if (-not $source) { throw ('NuGet 고지 원본 없음: ' + $library.Name) }
    $target = Join-Path $nugetTarget ($library.Name -replace '/', '-')
    $null = New-Item -ItemType Directory -Path $target
    $nuspec = Get-ChildItem -LiteralPath $source -Filter *.nuspec | Select-Object -First 1
    Copy-Item -LiteralPath $nuspec.FullName -Destination $target
    [xml]$spec = Get-Content -Raw -Encoding utf8 $nuspec.FullName
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '(?i)license|notice' } | Copy-Item -Destination $target
    $nugetInfo += [pscustomobject]@{ package = $library.Name; license = [string]$spec.package.metadata.license.InnerText; repository = [string]$spec.package.metadata.repository.url }
}
[IO.File]::WriteAllText((Join-Path $nugetTarget 'packages.json'), ($nugetInfo | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($package.Length + 1).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifestPath = Join-Path $package 'package-manifest.json'
$manifest = [pscustomobject]@{ version = 1; builtAt = (Get-Date).ToString('o'); model = 'Gemma 4 E2B Q4_0'; context = 4096; embedding = 'EmbeddingGemma Q8_0 CPU 2048'; runtime = 'llama.cpp b11146 CUDA 12.4'; distribution = '내부 로컬 시연용; 외부 공개 미승인'; files = $files }
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $package 'package-manifest.sha256'), (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(), [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $package
# Compress-Archive의 대용량 파일 제한 대신 ZIP64를 지원하는 .NET API를 사용한다.
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::NoCompression, $false)
[IO.File]::WriteAllText(($archive + '.sha256'), (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ package = $package; archive = $archive; archiveBytes = (Get-Item -LiteralPath $archive).Length; manifestFiles = $files.Count }
