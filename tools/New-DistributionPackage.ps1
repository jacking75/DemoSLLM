param(
    [string]$PackageName = ('LocalMindStudio-win-x64-' + (Get-Date -Format yyyyMMdd-HHmmss)),
    [switch]$IncludeModels,
    [switch]$SkipBuild,
    [string]$RuntimeArchiveDirectory,
    [string]$VcRuntimeDirectory,
    [switch]$DownloadRuntime
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'Distribution.Common.ps1')
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($PackageName -notmatch '^LocalMindStudio-(?:offline-)?win-x64-[A-Za-z0-9_-]+$') { throw '패키지 이름은 LocalMindStudio-win-x64- 또는 LocalMindStudio-offline-win-x64-로 시작하고 영문·숫자·하이픈·밑줄만 사용해야 한다.' }
$dist = Join-Path $repoRoot 'dist'; $package = Join-Path $dist $PackageName; $archive = $package + '.zip'
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath ($archive + '.sha256'))) { throw '기존 배포본은 덮어쓰지 않는다. 새로운 패키지 이름을 사용해야 한다.' }
$artifacts = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $PSScriptRoot 'spike-artifacts.json') | ConvertFrom-Json
$models = @($artifacts | Where-Object { $_.name -in @('fallback','fallback-mmproj','embedding') })
$runtimeArtifacts = @($artifacts | Where-Object { $_.name -in @('llama-cuda12','cudart-cuda12') })
if ($models.Count -ne 3 -or $runtimeArtifacts.Count -ne 2) { throw '고정 모델/런타임 목록이 불완전하다.' }
if (-not $RuntimeArchiveDirectory) { $RuntimeArchiveDirectory = Join-Path $repoRoot 'third_party/downloads' }
$RuntimeArchiveDirectory = [IO.Path]::GetFullPath($RuntimeArchiveDirectory)
foreach ($artifact in $runtimeArtifacts) {
    $source = Join-Path $RuntimeArchiveDirectory ([IO.Path]::GetFileName($artifact.path))
    if (-not (Test-Path -LiteralPath $source) -and $DownloadRuntime) {
        $uri = [Uri]$artifact.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com') { throw '고정 런타임 다운로드 주소가 잘못됐다.' }
        $null = New-Item -ItemType Directory -Force -Path $RuntimeArchiveDirectory
        $partial = $source + '.part'
        if (Test-Path -LiteralPath $partial) { throw ('기존 부분 다운로드를 보존한다. 다른 캐시 폴더를 지정하거나 파일을 확인해야 한다: ' + $partial) }
        Write-Host ('고정 런타임 다운로드: ' + $artifact.name)
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $artifact.url -OutFile $partial -UseBasicParsing
        Assert-ArtifactFile $partial $artifact
        Move-Item -LiteralPath $partial -Destination $source
    }
    if (-not (Test-Path -LiteralPath $source)) { throw ('원본 ZIP이 없다: ' + $source + '. -DownloadRuntime을 지정하거나 -RuntimeArchiveDirectory로 기존 캐시를 지정해야 한다.') }
    Assert-ArtifactFile $source $artifact
}
if ($IncludeModels) { foreach ($model in $models) { Assert-ArtifactFile (Join-Path $repoRoot $model.path) $model } }
$vcSource = Get-VcRuntimeDirectory $VcRuntimeDirectory
foreach ($required in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $vcSource $required))) { throw ('Visual C++ 재배포 DLL 누락: ' + $required) }
}
$vcFiles = @(Get-ChildItem -LiteralPath $vcSource -Filter '*.dll' -File)
foreach ($file in $vcFiles) { $null = Get-NativePeInfo $file.FullName }
$licenses = Join-Path $repoRoot 'third_party/licenses'
foreach ($required in @('llama.cpp-LICENSE.txt','OpenXML-LICENSE.txt','PdfPig-LICENSE.txt','SQLitePCLRaw-LICENSE.txt','NVIDIA-CUDA-12.4-EULA.txt','Gemma-Terms.txt','Gemma-Prohibited-Use-Policy.txt','Gemma-E2B-README.md','EmbeddingGemma-README.md','Apache-2.0.txt','dotnet-LICENSE.txt','dotnet-ThirdPartyNotices.txt','LLVM-OpenMP-LICENSE.txt','Visual-Cpp-Runtime-NOTICE.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $licenses $required))) { throw ('배포 고지 누락: ' + $required) }
}
foreach ($required in @('assets/demo/guide.json','docs/manual/LocalMindStudio-사용자-매뉴얼.html','output/pdf/LocalMindStudio-사용자-매뉴얼.pdf','docs/distribution-start.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $required))) { throw ('동봉 자료 누락: ' + $required) }
}
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build-App.ps1') -Configuration Release -LogName distribution-release-build.log | Out-Host }
$app = Join-Path $repoRoot 'bin/LocalMindStudio.exe'
if (-not (Test-Path -LiteralPath $app)) { throw '루트 bin에 앱이 없다. Release 빌드가 필요하다.' }
$null = Get-NativePeInfo $app
$nugetAssetsPath = Join-Path $repoRoot 'src/App.Wpf/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $nugetAssetsPath)) { throw 'NuGet 라이선스 수집에 필요한 project.assets.json이 없다. 앱을 먼저 빌드해야 한다.' }
$nugetAssets = Get-Content -Raw -Encoding utf8 -LiteralPath $nugetAssetsPath | ConvertFrom-Json
$nugetPackages = @(foreach ($library in $nugetAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
    $source = $null
    foreach ($base in $nugetAssets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $base $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
    }
    if (-not $source) { throw ('NuGet 고지 원본 누락: ' + $library.Name) }
    $nuspec = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' | Select-Object -First 1
    if (-not $nuspec) { throw ('NuGet nuspec 누락: ' + $library.Name) }
    [pscustomobject]@{ library = $library; source = $source; nuspec = $nuspec }
})
$null = New-Item -ItemType Directory -Path $package
Copy-Item -LiteralPath $app -Destination (Join-Path $package 'LocalMindStudio.exe')
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets') -Destination (Join-Path $package 'assets') -Recurse
$null = New-Item -ItemType Directory -Path (Join-Path $package 'tools')
[IO.File]::WriteAllText((Join-Path $package 'tools/spike-artifacts.json'), ($models | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -Destination (Join-Path $package 'tools')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/distribution-start.html') -Destination (Join-Path $package '처음 읽어 주세요.html')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/manual/LocalMindStudio-사용자-매뉴얼.html') -Destination (Join-Path $package '사용자 매뉴얼.html')
Copy-Item -LiteralPath (Join-Path $repoRoot 'output/pdf/LocalMindStudio-사용자-매뉴얼.pdf') -Destination (Join-Path $package '사용자 매뉴얼.pdf')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $package 'THIRD_PARTY_NOTICES.md')
if ($IncludeModels) {
    foreach ($model in $models) {
        $target = Join-Path $package $model.path
        $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
        Copy-Item -LiteralPath (Join-Path $repoRoot $model.path) -Destination $target
    }
}
$runtime = Join-Path $package 'third_party/llama-b11146-cuda12'
$null = New-Item -ItemType Directory -Force -Path $runtime
foreach ($artifact in $runtimeArtifacts) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $RuntimeArchiveDirectory ([IO.Path]::GetFileName($artifact.path))))
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.Name.EndsWith('.dll') -or $_.Name -eq 'llama-server.exe' -or $_.Name.StartsWith('LICENSE') }) {
            if ($entry.FullName -ne $entry.Name) { throw '고정 ZIP 내부 경로가 달라졌다.' }
            $target = Join-Path $runtime $entry.Name
            if (Test-Path -LiteralPath $target) { throw ('런타임 중복 파일: ' + $entry.Name) }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
        }
    } finally { $zip.Dispose() }
}
foreach ($file in $vcFiles) {
    $target = Join-Path $runtime $file.Name
    if (Test-Path -LiteralPath $target) { throw ('Visual C++ 런타임 중복 파일: ' + $file.Name) }
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$nativeAudit = Get-NativeRuntimeAudit $runtime
Copy-Item -LiteralPath $licenses -Destination (Join-Path $package 'third_party/licenses') -Recurse
$nugetTarget = Join-Path $package 'third_party/licenses/nuget'; $null = New-Item -ItemType Directory -Path $nugetTarget
$nugetInfo = @(foreach ($dependency in $nugetPackages) {
    $target = Join-Path $nugetTarget ($dependency.library.Name -replace '/', '-'); $null = New-Item -ItemType Directory -Path $target
    Copy-Item -LiteralPath $dependency.nuspec.FullName -Destination $target
    [xml]$spec = Get-Content -Raw -Encoding utf8 -LiteralPath $dependency.nuspec.FullName
    Get-ChildItem -LiteralPath $dependency.source -File | Where-Object { $_.Name -match '(?i)license|notice' } | Copy-Item -Destination $target
    [pscustomobject]@{ package = $dependency.library.Name; license = [string]$spec.package.metadata.license.InnerText; repository = [string]$spec.package.metadata.repository.url }
})
[IO.File]::WriteAllText((Join-Path $nugetTarget 'packages.json'), ($nugetInfo | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $package 'tools/native-runtime-audit.json'), ($nativeAudit | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$vcInfo = @(foreach ($file in $vcFiles) { [pscustomobject]@{ file = $file.Name; version = $file.VersionInfo.FileVersion; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
[IO.File]::WriteAllText((Join-Path $package 'third_party/licenses/visual-cpp-runtime-files.json'), ($vcInfo | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($package.Length + 1).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$modelMode = if ($IncludeModels) { 'bundled' } else { 'download' }
$manifest = [pscustomobject]@{ version = 2; builtAt = (Get-Date).ToString('o'); model = 'Gemma 4 E2B Q4_0'; context = 4096;
    embedding = 'EmbeddingGemma Q8_0 CPU 2048'; runtime = 'llama.cpp b11146 CUDA 12.4'; modelMode = $modelMode;
    prerequisites = @('Windows 11 x64','NVIDIA GPU and compatible driver'); vcRuntime = 'app-local';
    distribution = '로컬 배포 준비; 외부 공개 승인을 뜻하지 않는다'; modelFiles = @($models | ForEach-Object { [pscustomobject]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } }); files = $files }
$manifestPath = Join-Path $package 'package-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $package 'package-manifest.sha256'), (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(), [Text.UTF8Encoding]::new($false))
$verified = & (Join-Path $PSScriptRoot 'Verify-OfflinePackage.ps1') -PackageRoot $package
if (-not $verified.passed) { throw '배포본 검증 실패다.' }
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
[IO.File]::WriteAllText(($archive + '.sha256'), (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ package = $package; archive = $archive; archiveBytes = (Get-Item -LiteralPath $archive).Length; manifestFiles = $files.Count;
    modelMode = $modelMode; vcRuntime = 'app-local'; nativeBinariesChecked = @($nativeAudit.files).Count; cleanPcExecution = '미검증' }
