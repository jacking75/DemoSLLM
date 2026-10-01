param([switch]$Publish, [switch]$WinUI, [string]$LogName = 'stage1-build.log', [ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
if ($LogName -notmatch '^[A-Za-z0-9_-]+\.log$') { throw '로그 이름은 경로 없는 영문·숫자·밑줄·하이픈의 .log 파일명이어야 한다.' }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools가 필요하다.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild가 없다.' }
# WPF의 Build는 단일 파일 게시까지 수행한다. -Publish는 기존 호출과 호환한다.
$target = if ($WinUI -and $Publish) { 'Publish' } else { 'Build' }
$project = if ($WinUI) { "$repoRoot\src\App\App.csproj" } else { "$repoRoot\src\App.Wpf\App.Wpf.csproj" }
& $msbuild $project /restore "/t:$target" "/p:Configuration=$Configuration" /p:Platform=x64 /verbosity:minimal /nologo /fl "/flp:logfile=$repoRoot\docs\$LogName;verbosity=normal;encoding=UTF-8"
if ($LASTEXITCODE -ne 0) { throw "MSBuild 실패: $LASTEXITCODE" }
if (-not $WinUI -and -not (Test-Path -LiteralPath (Join-Path $repoRoot 'bin/LocalMindStudio.exe'))) { throw '루트 bin의 단일 실행 파일이 없다.' }
