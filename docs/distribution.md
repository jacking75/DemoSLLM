# 배포본 만들기와 전달 방법

- [x] 모델을 제외한 기본 배포본과 모델 포함 옵션을 구현한다.
- [x] 고정 llama.cpp·CUDA ZIP과 Visual C++ x64 런타임을 함께 묶는다.
- [x] 배포 파일 목록·해시 검사와 모델 다운로드 이후의 검증을 구현한다.
- [x] 시작 안내·사용자 매뉴얼을 동봉하고 준비물 설명을 갱신한다.
- [x] 로컬 빌드·회귀 검사·실제 배포 ZIP 생성 결과와 미검증 범위를 기록한다.

한 명령으로 Release 빌드와 파일 수집·검증을 수행하고, 사용자에게 전달할 ZIP과 SHA-256 파일을 만든다. 기본 ZIP은 모델을 제외한다. 사용자는 앱에서 모델 3개를 약 4.16GB 다운로드한다. 인터넷 없는 PC에는 모델 포함 옵션을 사용한다.

## 사용자 PC에 필요한 것

| 항목 | 준비 방법 |
| --- | --- |
| Windows | Windows 11 x64를 기준으로 한다. |
| GPU | NVIDIA GPU·호환 드라이버가 필요하다. 기본 구성은 VRAM 8GB 기준이다. |
| .NET | 자체 포함 EXE에 들어 있다. 별도 설치할 필요가 없다. |
| CUDA Toolkit | CUDA 실행 DLL을 동봉한다. Toolkit을 설치할 필요가 없다. |
| Visual C++ 런타임 | x64 CRT DLL을 추론 엔진 옆에 동봉한다. 별도 설치가 필요 없도록 구성했다. |
| 모델 | 기본 ZIP은 앱에서 다운로드한다. 모델 포함 ZIP은 파일 검증 후 사용한다. |
| 저장 공간 | 앱·CUDA 런타임과 모델 4.16GB, ZIP·압축 해제본을 모두 보관할 여유 공간이 필요하다. |

NVIDIA 드라이버는 CUDA Toolkit과 별개다. 드라이버의 `nvcuda.dll`은 배포본에 넣지 않는다. 새 PC의 DLL 로딩·드라이버 호환성·GPU 추론은 이번 작업에서 실행하지 않았다. 위 구성은 별도 런타임 설치 부담을 줄이기 위한 배포 방식이며 모든 사용자 환경에서의 실행 보장은 아니다.

## 배포본을 만드는 PC 준비

프로젝트 루트에서 Windows PowerShell로 실행한다. .NET 10 SDK와 Visual Studio 또는 Build Tools의 MSBuild·.NET 데스크톱 빌드 도구가 필요하다. Visual C++ x64 재배포 CRT 폴더도 필요하다. Visual Studio Installer의 C++ 도구를 통해 준비하거나, 재배포할 수 있는 x64 CRT 폴더를 아래 옵션으로 지정한다. VS의 `VC/Redist/MSVC/<버전>/x64/Microsoft.VC*.CRT`를 사용하며 `System32`·Debug CRT에서 가져오지 않는다.

Git에는 대용량 모델·런타임 ZIP을 저장하지 않는다. 새 체크아웃에서도 **모델을 받지 않고 기본 배포본을 만들 수 있다**. 인터넷은 NuGet 복원과 최초 고정 런타임 ZIP 확보에 필요하다.

## 기본 배포본 만들기

```powershell
Set-Location C:\github_dev\DemoSLLM
& .\tools\New-DistributionPackage.ps1 -DownloadRuntime
```

이 명령은 다음 순서로 처리한다.

1. `tools/spike-artifacts.json`에 고정한 llama.cpp b11146·CUDA 12.4 원본 ZIP 2개를 확인한다. 없는 ZIP만 다운로드하고 크기·SHA-256을 검사한다. 이미 있지만 변조된 ZIP은 자동 교체하지 않고 실패한다.
2. 설치된 Visual Studio에서 최신 x64 CRT를 찾는다. 명시한 CRT 폴더가 있으면 그 폴더를 사용한다. 필요한 DLL이 없거나 x86 파일이면 실패한다.
3. 앱을 Release로 빌드·자체 포함 게시한다. `bin/LocalMindStudio.exe`를 사용한다.
4. 앱·샘플·다운로드 목록·추론 엔진·CUDA·CRT DLL·라이선스·시작 안내·스크린샷 매뉴얼을 모은다. 모델은 넣지 않는다.
5. 네이티브 파일의 x64 PE 헤더와 정적 DLL import를 검사한다. Windows 기본 DLL과 NVIDIA 드라이버 DLL 외에 빠진 의존성이 있으면 실패한다.
6. 전체 파일 목록·크기·SHA-256을 기록하고 무결성 검사 후 ZIP과 ZIP 해시를 만든다.

결과는 `dist/LocalMindStudio-win-x64-<시각>/`, 같은 이름의 `.zip`, `.zip.sha256`이다. 사용자에게는 ZIP을 전달한다. 해시 파일을 함께 전달하면 다운로드·복사 오류를 확인할 수 있다. 이전 이름의 폴더·ZIP·해시가 있으면 덮어쓰지 않는다.

캐시를 이미 준비했다면 다운로드 옵션을 생략한다. 기본 캐시는 `third_party/downloads`이며 기존 캐시도 직접 사용할 수 있다.

```powershell
& .\tools\New-DistributionPackage.ps1 `
    -RuntimeArchiveDirectory 'C:\WorkingDev\DemoSLLM\third_party\downloads'

# 자동으로 CRT를 찾지 못하는 PC에서 실제 x64 CRT 폴더를 지정한다.
& .\tools\New-DistributionPackage.ps1 -DownloadRuntime `
    -VcRuntimeDirectory 'C:\배포 준비\Microsoft.VC145.CRT'
```

`-SkipBuild`는 이미 확인한 최신 Release EXE를 다시 포장할 때만 사용한다. Debug·Release가 같은 `bin`을 사용하므로 오래된 EXE나 Debug 결과를 포장하지 않도록 기본 명령을 권장한다.

## 인터넷 없는 PC용 모델 포함 배포본

먼저 프로젝트 루트의 다음 모델을 확보한다. 앱의 다운로드 기능이나 README의 고정 파일 준비 절차를 사용한다.

| 파일 | 경로 |
| --- | --- |
| 기본 모델 | `models/fallback/gemma-4-E2B-it-Q4_0.gguf` |
| 이미지 프로젝터 | `models/fallback/mmproj-gemma-4-E2B-it-BF16.gguf` |
| CPU 임베딩 모델 | `models/embedding/embeddinggemma-300M-Q8_0.gguf` |

```powershell
& .\tools\New-DistributionPackage.ps1 -DownloadRuntime -IncludeModels

# 기존 오프라인 배포 명령도 같은 도구로 모델 포함 ZIP을 만든다.
& .\tools\New-OfflinePackage.ps1 -DownloadRuntime
```

모델 크기·SHA-256이 고정 목록과 다르면 실패한다. 약 4.16GB가 추가되며 ZIP 생성과 검사에 더 오래 걸린다. 모델 접근 조건과 동봉 라이선스도 확인한다. 패키징 도구는 모델을 자동 다운로드하거나 접근 약관에 동의하지 않는다.

## 사용자에게 전달할 안내

ZIP과 함께 다음 내용을 전달하면 된다.

> ZIP을 내 문서 같은 쓰기 가능한 폴더에 모두 압축 해제한다. `처음 읽어 주세요.html`을 열고 `LocalMindStudio.exe`를 더블클릭한다. 모델이 없으면 “없는 모델 다운로드”, 이미 있으면 “모델 SHA-256 검증”을 누른다. 완료 후 “로컬 서버 시작”을 누른다. NVIDIA GPU·드라이버가 필요하다. 이 ZIP에는 .NET·CUDA 실행 DLL·Visual C++ 실행 DLL이 들어 있다. EXE와 주변 폴더를 함께 보관한다.

배포 폴더의 `사용자 매뉴얼.html`은 스크린샷을 자체 포함한다. `사용자 매뉴얼.pdf`도 함께 넣는다. 모델이 없는 상태에서도 안전 재생으로 화면을 익힐 수 있다. 자세한 기능·백업·문제 해결은 매뉴얼을 따른다.

## 전달 전 파일 검사

ZIP 해시가 제작자가 전달한 값과 같은지 확인한다.

```powershell
$zipPath = '.\dist\LocalMindStudio-win-x64-예시.zip' # 실제 파일명으로 바꾼다.
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne
    [IO.File]::ReadAllText((Resolve-Path -LiteralPath ($zipPath + '.sha256')).Path).Trim()) {
    throw 'ZIP 해시가 다르다.'
}
```

압축 해제한 폴더에서 다음 명령으로 전체 파일을 검사한다. 출력의 `passed=True`는 배포 파일 무결성 통과다. `modelsReady=True`도 확인해야 실제 모델 준비가 끝난 상태다.

```powershell
Set-Location 'C:\사용자\내 문서\LocalMindStudio'
& .\tools\Verify-OfflinePackage.ps1 -PackageRoot .
```

모델 제외 배포본에서는 아직 없는 모델을 `missingModels`에, 정상 이어받기 파일을 `partialModels`에 표시한다. 완성된 모델은 크기·SHA-256을 검사한다. 정해진 모델 외의 추가 파일, 변조·누락, 중복·외부 경로, 링크·정션은 거부한다. 동봉 모델의 누락은 실패다. 기존 v1 패키지 검사도 지원한다. 앱의 “모델 SHA-256 검증”은 모델을 검사하는 기능이며 이 명령의 배포 파일 검사와 범위가 다르다.

해시 파일은 전송 오류와 현재 목록의 일치를 확인하는 용도다. 신뢰할 수 있는 제작자에게 받은 해시와 비교해야 하며 코드 서명을 대신하지 않는다. EXE에 코드 서명을 추가한 배포 방식은 이번 작업 범위에 포함하지 않았다.

## 실패·업데이트 대응

| 상황 | 조치 |
| --- | --- |
| 원본 ZIP이 없다 | `-DownloadRuntime`을 지정하거나 기존 캐시 폴더를 지정한다. |
| ZIP 다운로드가 끊겼다 | `.part`를 보존한다. 원인을 확인하고 다른 캐시 폴더로 다시 다운로드하거나 완전한 ZIP을 준비한다. 런타임 ZIP 자동 이어받기는 지원하지 않는다. |
| x64 CRT를 찾지 못했다 | Visual Studio의 C++ 재배포 구성 요소나 `-VcRuntimeDirectory`를 준비한다. |
| 네이티브 DLL이 빠졌다 | 원본 ZIP·CRT 폴더와 버전을 확인한다. 검사 예외로 누락을 숨기지 않는다. |
| 패키징 중 실패했다 | 부분 산출물을 전달하지 않는다. 원인을 해결하고 새 `-PackageName LocalMindStudio-win-x64-새이름`으로 다시 만든다. |
| 사용자 업데이트 | 앱과 서버를 종료하고 새 ZIP을 별도 폴더에 푼다. 이전 모델 3개를 같은 상대 경로로 복사한 뒤 파일 검증을 실행한다. 기존 사용자 DB를 삭제하지 않는다. |

CRT를 폴더에 동봉하는 방식은 Microsoft가 지원한다. 설치형 재배포 패키지와 달리 동봉 DLL은 자동으로 보안 업데이트되지 않으므로 제작자가 업데이트된 CRT로 ZIP을 다시 만들어 전달해야 한다. 자세한 조건은 [Microsoft 공식 재배포 문서](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170)와 [동봉 고지](../third_party/licenses/Visual-Cpp-Runtime-NOTICE.md)에 있다. 전체 제삼자 고지는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)를 따른다.

## 확인 결과와 남은 범위

2026-10-01 13:06 KST에 로컬 결과를 확인했다. 기계 판독 결과는 [distribution-result.json](distribution-result.json)에 기록한다.

| 확인 항목 | 결과 |
| --- | --- |
| Release 빌드·자체 포함 게시 | 경고 0개·오류 0개다. `docs/distribution-release-build.log`에 로컬 로그가 있다. |
| 기존 패키지 검사 회귀 | 7/7 통과다. |
| 새 배포 회귀 | 20/20 통과다. 다운로드 전후·부분 파일·동봉 모델 누락·변조·추가 파일·경로·정션·x64 CRT 등을 검사했다. |
| 기존 배포본 보존 | 같은 이름의 재생성을 거부했고 ZIP 해시를 보존했다. |
| 네이티브 구성 | 실제 EXE·DLL 44개의 x64 PE 헤더·정적 import 검사가 통과했다. CRT 10개를 동봉했다. |
| 최종 기본 ZIP | `dist/LocalMindStudio-win-x64-20261001-distribution.zip`, 734,683,236바이트다. |
| 압축 해제본 | 약 1.26GB다. 한글·공백 경로에 풀고 파일 109개의 목록·크기·SHA-256을 검사했다. 목록·해시 파일 2개는 개수에서 제외한다. |
| 모델 상태 | 기본 ZIP에서 모델 3개의 부재를 정상적으로 보고하며 `modelsReady=False`다. |
| 시작 안내 | HTML·PDF 매뉴얼 링크 2개와 휴대폰 폭에서의 줄바꿈을 확인했다. |
| 사용자 매뉴얼 | HTML 브라우저 검사 15개가 통과했다. PDF 24쪽 렌더링과 수정 페이지의 한글·배치를 확인했다. |

최종 ZIP SHA-256은 `a71a04e2666f376abe151b636597ae091f713a83ef71a38fc3dceafc6228f1fd`다. 모델을 포함하는 실제 대용량 ZIP, 신규 런타임 ZIP·모델 다운로드, 새 PC의 실행·DLL 로딩·GPU 추론은 실행하지 않았다. 원본 런타임 ZIP은 기존 고정 캐시의 크기·SHA-256을 검증해 재사용했고, 모델 포함 상태는 작은 합성 파일로 검사했다. 이 로컬 확인을 새 PC 실행 성공으로 표시하지 않는다.

새 PC에서의 실제 실행 검증은 이번 작업의 완료 조건으로 두지 않는다. 공개 업로드나 사용자 PC의 드라이버·시스템 설정 변경은 수행하지 않았다. 배포본·새 검사 실행 폴더·빌드 로그는 Git에서 제외하며 문서·도구·요약 결과만 저장한다.
