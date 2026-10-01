function Assert-ArtifactFile([string]$Path, $Artifact) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -ne $Artifact.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Artifact.sha256) {
        throw ('고정 원본 크기/SHA-256 오류: ' + $Artifact.name + ' (' + $Path + ')')
    }
}

function Get-VcRuntimeDirectory([string]$Directory) {
    if ($Directory) { return (Resolve-Path -LiteralPath $Directory).Path }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $installations = @(& $vswhere -products '*' -property installationPath)
        $candidates = @(foreach ($installation in $installations) {
            Get-ChildItem -Path (Join-Path $installation 'VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT') -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'vcruntime140.dll') }
        })
        $selected = $candidates | Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'vcruntime140.dll')).VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
        if ($selected) { return $selected.FullName }
    }
    throw 'Visual C++ x64 재배포 DLL이 없다. Visual Studio의 C++ 재배포 구성 요소를 준비하거나 -VcRuntimeDirectory로 x64 CRT 폴더를 지정해야 한다.'
}

# PE imports are inspected without loading the DLLs or starting inference.
function Get-NativePeInfo([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw ('PE 파일이 아니다: ' + $Path) }
        $stream.Position = 0x3c; $pe = $reader.ReadUInt32(); $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw ('PE 헤더 오류: ' + $Path) }
        $machine = $reader.ReadUInt16(); $sectionCount = $reader.ReadUInt16()
        $stream.Position = $pe + 20; $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24; $stream.Position = $optional; $magic = $reader.ReadUInt16()
        if ($machine -ne 0x8664 -or $magic -ne 0x20b) { throw ('Windows x64 파일이 아니다: ' + $Path) }
        $stream.Position = $optional + 120; $importRva = $reader.ReadUInt32()
        $sections = @(for ($i = 0; $i -lt $sectionCount; $i++) {
            $stream.Position = $optional + $optionalSize + 40 * $i + 8
            $virtualSize = $reader.ReadUInt32(); $virtualAddress = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32(); $rawPointer = $reader.ReadUInt32()
            [pscustomobject]@{ address = $virtualAddress; length = [Math]::Max($virtualSize, $rawSize); raw = $rawPointer }
        })
        $imports = [Collections.Generic.List[string]]::new()
        if ($importRva -ne 0) {
            $section = $sections | Where-Object { $importRva -ge $_.address -and $importRva -lt $_.address + $_.length } | Select-Object -First 1
            if (-not $section) { throw ('PE import 경계 오류: ' + $Path) }
            $descriptor = $section.raw + $importRva - $section.address
            for ($i = 0; $i -lt 1024; $i++) {
                $stream.Position = $descriptor + 20 * $i + 12; $nameRva = $reader.ReadUInt32()
                if ($nameRva -eq 0) { break }
                $nameSection = $sections | Where-Object { $nameRva -ge $_.address -and $nameRva -lt $_.address + $_.length } | Select-Object -First 1
                if (-not $nameSection) { throw ('PE DLL 이름 경계 오류: ' + $Path) }
                $stream.Position = $nameSection.raw + $nameRva - $nameSection.address
                $name = [Text.StringBuilder]::new()
                for ($j = 0; $j -lt 512; $j++) { $value = $reader.ReadByte(); if ($value -eq 0) { break }; $null = $name.Append([char]$value) }
                if ($j -eq 512 -or $name.Length -eq 0) { throw ('PE DLL 이름 오류: ' + $Path) }
                $imports.Add($name.ToString())
            }
            if ($i -eq 1024) { throw ('PE import 개수 초과: ' + $Path) }
        }
        [pscustomobject]@{ file = [IO.Path]::GetFileName($Path); architecture = 'x64'; imports = $imports.ToArray() }
    } finally { $reader.Dispose() }
}

function Get-NativeRuntimeAudit([string]$Directory) {
    $required = @('llama-server.exe','llama-server-impl.dll','llama-common.dll','llama.dll','mtmd.dll',
        'ggml.dll','ggml-base.dll','ggml-cuda.dll','libomp.dll','cudart64_12.dll','cublas64_12.dll','cublasLt64_12.dll',
        'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')
    foreach ($name in $required) { if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { throw ('배포 런타임 누락: ' + $name) } }
    $systemDlls = @('kernel32.dll','kernelbase.dll','ntdll.dll','advapi32.dll','user32.dll','gdi32.dll','ws2_32.dll','crypt32.dll',
        'bcrypt.dll','bcryptprimitives.dll','shell32.dll','shlwapi.dll','ole32.dll','oleaut32.dll','combase.dll','rpcrt4.dll',
        'psapi.dll','version.dll','winmm.dll','setupapi.dll','cfgmgr32.dll','secur32.dll','iphlpapi.dll','netapi32.dll','propsys.dll',
        'dbghelp.dll','dwmapi.dll','wintrust.dll','normaliz.dll','msvcrt.dll','ucrtbase.dll','powrprof.dll','imm32.dll','comdlg32.dll')
    $binaries = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -in @('.dll','.exe') })
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($binary in $binaries) { $null = $names.Add($binary.Name) }
    $audit = @(foreach ($binary in $binaries) {
        $info = Get-NativePeInfo $binary.FullName
        foreach ($dependency in $info.imports) {
            if ($names.Contains($dependency) -or $dependency -ieq 'nvcuda.dll' -or $dependency -like 'api-ms-win-*' -or $dependency -like 'ext-ms-win-*' -or $dependency -in $systemDlls) { continue }
            throw ('동봉하지 않은 네이티브 의존성: ' + $binary.Name + ' → ' + $dependency)
        }
        $info
    })
    [pscustomobject]@{ architecture = 'x64'; files = $audit; driverProvided = @('nvcuda.dll'); scope = 'PE 정적 import와 파일 존재 검사; 새 PC의 DLL 로딩·GPU 추론은 미검증' }
}
