param(
    [string]$PackageName = ('LocalMindStudio-offline-win-x64-' + (Get-Date -Format yyyyMMdd-HHmmss)),
    [switch]$SkipBuild,
    [string]$RuntimeArchiveDirectory,
    [string]$VcRuntimeDirectory,
    [switch]$DownloadRuntime
)
$ErrorActionPreference = 'Stop'
# 기존 명령은 모델 포함 배포 방식으로 유지한다.
if (-not $PSBoundParameters.ContainsKey('PackageName')) { $PSBoundParameters['PackageName'] = $PackageName }
& (Join-Path $PSScriptRoot 'New-DistributionPackage.ps1') -IncludeModels @PSBoundParameters
