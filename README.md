# LocalMind Studio

Windows에서 회사 문서와 이미지를 로컬 LLM으로 처리하는 업무 AI 데모 앱이다. 추론은 이 PC의 `llama-server.exe`에서 수행하며 클라우드 AI API나 원격 텔레메트리를 사용하지 않는다. 모델을 사전 설치하면 인터넷 없이 실행하도록 구성한다.

현재 실행 앱은 **WPF + .NET 10 + CommunityToolkit.Mvvm**이다. 초기 WinUI 소스는 `src/App`에 보존하며 현재 빌드 대상은 `src/App.Wpf`이다. 음성 입력(C)과 파일 정리(D)는 이번 데모에서 보류한다.

## 실행 환경과 준비 파일

| 구분 | 필요한 환경 |
| --- | --- |
| 앱 실행 | Windows 11 x64, NVIDIA GPU·드라이버, VRAM 8GB 기준 |
| 소스 빌드 | .NET 10 SDK, Visual Studio 또는 Build Tools의 MSBuild와 .NET 데스크톱 빌드 도구 |
| 명령 실행 | 프로젝트 루트의 Windows PowerShell |
| 최초 복원 | NuGet 패키지를 확보할 인터넷 또는 로컬 패키지 캐시 |
| 실제 추론·GUI 테스트 | 고정 런타임과 모델 파일, 연결되고 잠금 해제된 사용자 데스크톱 |

오프라인 배포본은 .NET 및 Windows Desktop 런타임을 자체 포함하므로 실행 PC에 SDK나 .NET을 별도로 설치할 필요가 없다. NVIDIA 드라이버는 PC에 설치돼 있어야 한다. 안전 재생 GUI 검사는 모델 추론을 수행하지 않지만 현재 앱의 실행 환경은 Windows이다.

기본 구성은 Gemma 4 E2B Q4_0·컨텍스트 4096이며 임베딩은 CPU·컨텍스트 2048을 사용한다. 파일 경로는 다음과 같다.

| 파일 | 프로젝트 루트 기준 경로 |
| --- | --- |
| 추론 서버와 동봉 DLL | `third_party/llama-b11146-cuda12/` |
| 기본 모델 | `models/fallback/gemma-4-E2B-it-Q4_0.gguf` |
| 이미지 프로젝터 | `models/fallback/mmproj-gemma-4-E2B-it-BF16.gguf` |
| CPU 임베딩 모델 | `models/embedding/embeddinggemma-300M-Q8_0.gguf` |
| 고정 URL·크기·SHA-256 | `tools/spike-artifacts.json` |

모델이 없으면 앱의 ‘없는 모델 다운로드’ 버튼으로 명시적으로 다운로드한다. 데모 전에 다운로드와 SHA-256 검증을 끝내야 한다. 서버 바이너리·DLL은 위 경로에 사전 배치한다. 비교용 E4B 파일과 설정은 소스에 보존하며 기본값으로 자동 전환하지 않는다.

## Git 저장소와 외부 파일 준비

Git에는 소스·NuGet 잠금 파일·구현 명세·문서·도구·가상 샘플·과거 검증 근거·라이선스를 보관한다. `models/`, 추론 서버와 CUDA DLL, `third_party/downloads/`, `bin/`, `obj/`, `dist/`, 새 테스트 실행 결과와 사용자 SQLite DB는 제외한다. `.gitattributes`는 원본 바이트를 보존하므로 체크아웃의 줄바꿈 변환으로 안전 재생 SHA-256이 달라지지 않는다. 복사 범위와 누락 검증은 [저장소 복사 검증](docs/repository-transfer.md)에 기록한다.

현재 PC에 있는 기존 검증 파일을 재사용하려면 새 저장소 루트에서 필요한 외부 폴더만 복사한다. 아래 파일은 Git 관리 대상에 추가하지 않는다.

```powershell
Set-Location C:\github_dev\DemoSLLM
Copy-Item -LiteralPath C:\WorkingDev\DemoSLLM\models -Destination . -Recurse
Copy-Item -LiteralPath C:\WorkingDev\DemoSLLM\third_party\llama-b11146-cuda12 -Destination .\third_party -Recurse
Copy-Item -LiteralPath C:\WorkingDev\DemoSLLM\third_party\downloads -Destination .\third_party -Recurse
```

기존 파일이 없는 PC에서는 Node.js의 내장 `fetch`를 사용할 수 있는 환경에서 다음 명령으로 기본 모델 3개와 원본 ZIP 2개만 확보한다. 실행 시 여러 GB를 다운로드하고 고정 크기·SHA-256을 확인한다. 기존 파일과 이어받기 파일은 보존한다. 다운로드 실패 시 원인과 모델 접근 조건을 확인한 뒤 재시도하며 압축 해제를 먼저 실행하지 않는다.

```powershell
node --input-type=module -e "import { prepare } from './tools/prepare-spike.mjs'; const r = await prepare(['llama-cuda12', 'cudart-cuda12', 'fallback', 'fallback-mmproj', 'embedding']); console.log(JSON.stringify(r, null, 2)); if (r.error) process.exitCode = 1;"
if ($LASTEXITCODE -ne 0) { throw '고정 파일 준비에 실패했다.' }
Expand-Archive -LiteralPath .\third_party\downloads\llama-b11146-bin-win-cuda-12.4-x64.zip -DestinationPath .\third_party\llama-b11146-cuda12
Expand-Archive -LiteralPath .\third_party\downloads\cudart-llama-bin-win-cuda-12.4-x64.zip -DestinationPath .\third_party\llama-b11146-cuda12
```

소스 빌드와 아래 모델 없는 회귀 검사는 외부 모델·서버 없이 수행할 수 있다. 실제 모델 추론과 모델 포함 패키징은 외부 파일 준비가 필요하다.

## 빌드

다음 명령은 이 작업 폴더를 기준으로 한다. 다른 위치에 복사했다면 첫 줄의 경로를 해당 프로젝트 루트로 바꾼다.

```powershell
Set-Location C:\github_dev\DemoSLLM
dotnet --version

# 개발용 Debug 빌드와 단일 파일 게시
& .\tools\Build-App.ps1 -Configuration Debug -LogName readme-debug-build.log

# 배포용 Release 빌드와 단일 파일 게시
& .\tools\Build-App.ps1 -Configuration Release -LogName readme-release-build.log
```

`Build-App.ps1`은 설치된 Visual Studio의 MSBuild를 찾아 NuGet 복원·빌드·단일 파일 게시를 수행한다. 일반 WPF 프로젝트의 `Build`도 게시까지 수행하므로 `-Publish`는 생략할 수 있으며 기존 호출과 호환한다. 로그는 `docs/` 아래에 생성한다. 빌드를 동시에 실행하지 않고 앞선 명령이 끝난 뒤 다음 명령을 실행한다. 일반 작업에서는 `-WinUI` 옵션을 사용하지 않는다.

일반 .NET CLI를 사용해도 같은 위치에 단일 실행 파일을 만든다.

```powershell
dotnet build .\src\App.Wpf\App.Wpf.csproj -c Release
```

| 결과 | 실행 파일 |
| --- | --- |
| Debug·Release 빌드 | `bin/LocalMindStudio.exe` |

프로젝트 루트의 `bin`에는 실행 파일 하나를 생성하며 `x64`, .NET 버전, 런타임, 구성별 하위 디렉터리를 만들지 않는다. Debug와 Release는 같은 실행 파일 경로를 사용하므로 마지막으로 빌드한 구성이 적용된다. 앱의 중간 빌드 파일은 `src/App.Wpf/obj/app-output/`에 둔다.

`PublishSingleFile=true`, `SelfContained=true`, `PublishAot=false`, `PublishReadyToRun=false`를 사용한다. 앱·.NET 런타임·관리 DLL·앱용 네이티브 DLL을 단일 EXE에 묶어 일반 JIT 방식으로 실행한다. 네이티브 DLL은 실행 시 .NET의 임시 추출 경로에 풀릴 수 있다. 모델·샘플·별도 자식 프로세스인 `llama-server`와 CUDA DLL은 기존 외부 데이터 폴더를 사용한다.

```powershell
# 마지막 빌드 결과를 실행한다.
& .\bin\LocalMindStudio.exe
```

실제 추론에는 외부 모델·추론 서버·샘플이 필요하다. 다른 PC에는 아래 오프라인 패키지로 함께 배치한다.

### 모델 포함 오프라인 패키지 만들기

```powershell
# Release 게시와 파일 검증 후 새 이름의 폴더·ZIP을 생성한다.
& .\tools\New-OfflinePackage.ps1

# 루트 bin의 현재 빌드 결과를 그대로 포장할 때만 사용한다.
& .\tools\New-OfflinePackage.ps1 -SkipBuild
```

패키지 생성에는 위 모델 3개 외에 `third_party/downloads/`의 고정 llama.cpp·CUDA 원본 ZIP 2개와 `third_party/licenses/`의 고지 파일이 필요하다. 스크립트는 원본의 크기와 SHA-256을 검사하며 필요한 파일을 자동 다운로드하지 않는다. 결과는 `dist/LocalMindStudio-offline-win-x64-<시각>/`와 같은 이름의 ZIP·`.zip.sha256`이다. 기존 이름의 패키지를 덮어쓰지 않는다.

4단계에서 생성·검증한 `dist/LocalMindStudio-offline-win-x64-stage4-v2.zip`은 단일 파일 빌드 변경 이전의 로컬 배포본이며 Git에는 포함하지 않는다. 현재 구성을 배포하려면 패키지 생성 명령을 다시 실행한다. 모델 포함 패키지는 수 GB 크기이므로 압축 해제 공간을 별도로 확보한다. 압축을 풀고 최상위 `LocalMindStudio.exe`를 실행하며 데이터 하위 폴더를 함께 유지한다. 외부 배포 전에는 [라이선스 고지](docs/THIRD_PARTY_NOTICES.md)와 명세의 법무 검토 조건을 확인해야 한다.

## 사용 방법

### 기본 실행

1. 브라우저 하드웨어 가속·게임·녹화 등 GPU를 사용하는 다른 프로그램을 닫는다.
2. 앱의 ‘모델 SHA-256 검증’을 눌러 기본 모델·프로젝터·CPU 임베딩을 확인한다.
3. ‘로컬 서버 시작’을 누르고 준비 완료를 기다린다. 시작·추론 중에는 ‘취소’를 사용할 수 있다.
4. 왼쪽에서 로컬 채팅 또는 업무 시나리오를 선택한다.
5. 투명성 패널에서 현재 모델·VRAM·인터넷 상태·외부 연결 수를 확인한다. 수치가 없으면 미확인으로 표시한다.

참고 비용을 보고 싶으면 ‘백만 토큰당 참고 단가’에 사용자가 직접 단가를 입력한다. 기본값은 비어 있으며 입력한 단가 기준 참고값만 계산한다.

### 업무 시나리오

| 화면 | 조작 | 확인할 내용 |
| --- | --- | --- |
| 로컬 채팅 | 질문 입력 → 질문 보내기 | 실제 스트리밍 응답·취소 |
| A · 화면·문서 읽기 | PNG/JPEG 선택·드롭·붙여넣기·영역 캡처 → 설명/오류 진단/추출 | 원본과 결과 대조, 표·영수증 JSON·DataGrid·CSV |
| B · 내 문서 금고 | 폴더 선택·드롭 또는 가상 문서 색인 → 질문 | 답변과 출처 파일명·문단 원문 |
| E · 전역 단축키 | 다른 앱에서 텍스트 선택 → `Ctrl+Alt+Space` → 변환 동작 | 요약·맞춤법 교정·정중한 어투·영어 번역 |
| F · AI 한계 | 계산·장문 조건·없는 규정 예시 실행 | 실제 응답과 검산값·원문 비교 |

A는 PNG/JPEG·16MiB 이하를 지원한다. B는 폴더 최상위의 TXT/MD/PDF/DOCX·파일당 20MiB 이하·최대 100개를 지원하며 스캔 PDF는 지원하지 않는다. 일반 문서 금고에서 다른 폴더를 색인하면 기존 색인을 교체한다.

E의 출력은 원래 앱에 자동 입력하지 않는다. 원래 클립보드 복원을 시도하며 포커스·권한·형식 읽기 오류가 있으면 안내한다. 실제 메모장·브라우저 선택 읽기와 모든 클립보드 형식의 완전 복원은 아직 검증이 남아 있다.

창을 최소화하면 트레이에서 계속 실행한다. 트레이의 ‘앱 열기’로 복원하고 ‘종료’ 또는 창 닫기로 앱과 자식 서버를 종료한다.

### 7분 데모

‘7분 데모 시작’ → ‘현재 단계 실행’ → ‘다음 단계’ 순서로 진행한다. 단계 이동은 샘플을 준비하고 실행 버튼은 실제 처리를 시작한다. ‘이전 단계’, ‘시연자 노트’, ‘시연 글자 확대’를 사용할 수 있다.

| 시간 | 화면 |
| --- | --- |
| 0:00–0:40 | 준비·투명성 패널 |
| 0:40–1:25 | 영수증 추출 |
| 1:25–2:00 | 오류 화면 진단 |
| 2:00–3:30 | 문서 질문과 출처 |
| 3:30–4:30 | 업무 문장 변환 |
| 4:30–5:30 | 결과와 원문 대조 |
| 5:30–6:15 | AI 한계 |
| 6:15–7:00 | 아이디어 카드·질의 |

준비와 모델 로딩을 관객 입장 전에 완료하면 대기를 줄일 수 있다. 배정 시간은 진행 안내이며 앱이 응답을 자동으로 끊거나 다음 단계로 넘기지 않는다. 마지막 화면에서 ‘데모 종료’를 누른다.

응답이 지연되거나 실패하면 ‘취소’ 후 ‘안전 재생’을 선택한다. 저장된 실제 응답에는 ‘재생 결과’와 기록 시점을 표시한다. 재생을 현재 추론이나 현재 성능 측정으로 소개하지 않는다. 모드 전환 시 완료 결과를 비워 실제·재생 결과 혼합을 방지한다. 데모의 E 팝업은 가짜 문장을 직접 전달하므로 다른 앱의 선택 읽기 검증과 구분한다.

Wi-Fi 끄기는 사용자가 직접 수행한다. 앱의 실제 연결 상태를 그대로 보여주며 연결 끊김을 꾸미지 않는다. 상세 설명은 [데모 체크리스트](docs/demo-checklist.md)에 있다.

### 저장 위치와 문구 수정

| 내용 | 위치 |
| --- | --- |
| 참고 단가 설정 | `%LOCALAPPDATA%/LocalMindStudio/settings.json` |
| 일반 문서 금고 | `%LOCALAPPDATA%/LocalMindStudio/vault.sqlite` |
| 데모 전용 문서 금고 | `%LOCALAPPDATA%/LocalMindStudio/demo-vault.sqlite` |
| 단계·안내·노트·아이디어 카드 | `assets/demo/guide.json` |
| 가상 문서·이미지 | `assets/documents/`, `assets/samples/images/` |
| 안전 재생 근거 | `assets/demo/evidence/` |

데모 색인은 일반 문서 금고를 교체하지 않는다. 아이디어 카드 문구는 사용자 협의용 초안이다. 단계 시간의 합은 420초여야 한다. 재생 근거는 SHA-256으로 확인하므로 원문을 임의로 편집하면 재생을 거부한다. 배포본 파일을 수정하면 기존 패키지 무결성 검사도 실패하므로 배포 변경 후 새 패키지를 생성한다.

## 자동 테스트

다음 명령은 프로젝트 루트에서 **순서대로** 실행한다. GUI 검사는 별도로 띄운 앱과 단축키가 충돌할 수 있으므로 기존 LocalMind Studio 앱을 먼저 종료한다. 테스트 결과의 통과는 해당 검사 범위에 한정하며 사람의 7분 시연 검증을 대신하지 않는다.

### 모델 추론 없는 회귀 검사

```powershell
dotnet build .\src\Diagnostics\Diagnostics.csproj -c Release --nologo

# 진행표·재생 근거·경로 경계 검사
dotnet run --project .\src\Diagnostics\Diagnostics.csproj -c Release --no-build -- --stage4-self-test

# 이미지 JSON·CSV·문서 파서·SQLite·출처 처리 검사
dotnet run --project .\src\Diagnostics\Diagnostics.csproj -c Release --no-build -- --feature-self-test

# 패키지 변조·누락·외부 경로·중복·추가 파일 거부 검사
& .\tools\Test-OfflinePackageVerifier.ps1
```

2026-10-01 기록은 각각 16/16, 40/40, 7/7이다. 이 검사는 실제 모델의 답변 품질이나 GPU 성능을 측정하지 않는다. 결과는 `docs/stage4-runs/`, `docs/stage2-runs/`의 해당 실행 폴더에 저장한다.

### 실제 모델·서버 검사

모델과 런타임·NVIDIA 드라이버를 사전 준비한 뒤 실행한다.

```powershell
# 현재 기본 E2B의 채팅·취소·후속 요청·정상 종료 검사
# --fallback을 생략하면 비교용 E4B를 사용하므로 반드시 지정한다.
dotnet run --project .\src\Diagnostics\Diagnostics.csproj -c Release --no-build -- --fallback

# A 이미지 3종·B 문서 질문 10개·F 예시·동시 서버 VRAM 검사
dotnet run --project .\src\Diagnostics\Diagnostics.csproj -c Release --no-build -- --stage2

# ModelStore·Windows Job Object·NVML 자체 검사
dotnet run --project .\src\Diagnostics\Diagnostics.csproj -c Release --no-build -- --self-test
```

`--stage2`는 현재 기본 E2B를 사용한다. `--primary`를 추가하면 비교용 E4B를 사용하므로 일반 데모 검사에는 추가하지 않는다. 실제 모델 검사는 GPU를 점유하며 일반 채팅과 동시에 실행하지 않는다. 결과 파일에서 오류·인용 출처·영수증 합계·VRAM·외부 소켓·서버 종료를 함께 확인한다.

### 데모 GUI 검사

앱을 먼저 빌드해 `bin/LocalMindStudio.exe`를 만든 뒤 실행한다. Windows UI Automation과 화면 캡처를 사용하므로 사용자 데스크톱을 연결·잠금 해제한 상태로 유지한다.

```powershell
# 추론 서버를 시작하지 않고 8개 화면을 빠르게 3회 순회한다.
& .\tools\Test-Stage4Gui.ps1 -Mode Replay -Cycles 3

# 실제 모델로 화면·문서·가짜 문장 팝업·모드 전환·종료를 검사한다.
& .\tools\Test-Stage4Gui.ps1 -Mode Live -Cycles 1
```

배포본을 검사하려면 새 폴더에 압축을 풀고 경로를 지정한다. 아래의 폴더는 예시이며 실제 압축 해제 경로로 바꾼다.

```powershell
$demoPackageRoot = 'C:\Demo\LocalMindStudio'

# 앱 실행 전에 원본 압축 해제본의 무결성을 확인한다.
& .\tools\Verify-OfflinePackage.ps1 -PackageRoot $demoPackageRoot

& .\tools\Test-Stage4Gui.ps1 -Mode Live -Cycles 1 `
    -AppPath (Join-Path $demoPackageRoot 'LocalMindStudio.exe') `
    -DataRoot $demoPackageRoot
```

패키지 검증기는 파일의 크기·SHA-256과 목록 밖 추가 파일을 검사한다. `--diagnostics`를 사용하는 GUI 검사는 압축 해제 폴더에 진단 파일을 추가하므로 검증기를 먼저 실행한다. 다시 원본 무결성을 검사할 때는 새 폴더에 압축을 푼다. 빠른 3회 GUI 순회는 실제 7분 시연을 연속 3회 수행한 결과가 아니다. 검은 캡처는 정상 시각 검증 근거로 사용하지 않는다.

### 실제 단축키·입력 검사

2·3·4단계 GUI 검사 스크립트는 기본적으로 **루트 `bin/LocalMindStudio.exe`**를 사용한다. 해당 검사를 실행하기 전에 앱을 빌드한다.

```powershell
& .\tools\Build-App.ps1 -Configuration Debug -LogName readme-debug-build.log

# 붙여넣기·드롭·영역 캡처 입력 진단
& .\tools\Test-Stage2Inputs.ps1

# 가짜 문장 직접 전달의 팝업 검사
& .\tools\Test-Stage3Popup.ps1
```

팝업 검사만으로 실제 전역 단축키가 통과했다고 판단하지 않는다. 메모장·브라우저 선택 읽기 검사는 별도 브라우저 fixture와 `playwright-cli`가 필요하다. 준비·실행 명령은 [3단계 보고서](docs/stage3-report.md)의 마지막 검증 절차와 `tools/Test-Stage3.ps1`을 따른다. 검사 중에는 다른 앱에서 입력하거나 클립보드를 조작하지 않는다. 접근 거부가 발생하면 실제 실패를 기록하고 권한·UIPI·보안 설정을 임의로 바꾸지 않는다.

## 직접 테스트할 항목

아래 체크박스는 새 실행에서 직접 확인할 항목이다. 이전 자동 테스트의 통과 여부와 별도로 관리한다.

- [ ] 연결된 화면에서 기본 글자·확대 글자·재생 배너·표·출처·아이디어 카드가 잘리고 겹치지 않는지 확인한다.
- [ ] A의 영수증·오류·표 이미지에서 결과가 나오고 영수증 합계가 9,000인지 원본과 대조한다.
- [ ] 실제 이미지 드롭·클립보드 붙여넣기·마우스 영역 캡처·Esc 취소·CSV 저장을 확인한다.
- [ ] B의 10개 질문 중 8개 이상에서 올바른 출처와 원문이 표시되는지 확인한다.
- [ ] B의 근거 없는 질문에는 ‘문서에서 찾지 못했습니다’가 표시되고 출처를 만들어 내지 않는지 확인한다.
- [ ] 데모 문서 색인이 일반 사용자 문서 금고를 교체하지 않는지 확인한다.
- [ ] 메모장과 브라우저 텍스트 영역에서 실제 선택 텍스트·Ctrl+Alt+Space·4가지 변환을 확인한다.
- [ ] 전역 단축키 사용 후 기존 클립보드가 복원되고 트레이 최소화 상태에서도 동작하는지 확인한다.
- [ ] 실제→재생→실제 전환에서 표시가 명확하고 이전 결과가 검토 화면에 섞이지 않는지 확인한다.
- [ ] 응답 지연·모델 파일 누락·잘못된 입력 시 안내와 취소·안전 재생 전환이 동작하는지 확인한다.
- [ ] F의 응답을 검산값·문서 원문과 비교하고 AI 결과의 검토 필요성을 설명할 수 있는지 확인한다.
- [ ] 처음 보는 사람이 문서 없이 화면 안내만으로 7분 데모를 끝까지 진행하는지 확인한다.
- [ ] 실제 7분 데모를 연속 3회 진행하며 크래시·멈춤이 없는지 확인한다.
- [ ] 데모 전체의 장치 VRAM이 7GiB 이하이고 추론·임베딩 서버의 외부 연결 관측값이 0인지 확인한다.
- [ ] 첫 토큰 2초 이내·텍스트 생성 20 tok/s 이상 목표를 서버 측정값으로 확인한다. 재토큰화 추정치만 있으면 서버 생성 속도 목표 판정은 미확인으로 남긴다.
- [ ] 정상 종료와 시험 앱 강제 종료 후 해당 자식 서버가 남지 않고 VRAM이 해제되는지 확인한다.

인터넷 물리 단절 검사는 현재 사용자 면제 항목이다. 필요 시 Wi-Fi 등을 수동으로 끈 상태에서 P0와 채택 P1을 다시 검사하고 실제 결과를 기록한다. 면제를 실제 오프라인 동작 검증 완료로 표시하지 않는다.

## 현재 검증 상태와 관련 문서

2026-10-01 4단계 기록에서 빌드 경고·오류 0개, 자동 회귀 63항목, 배포 파일 583개 무결성, 실제 GUI 빠른 순회·안전 재생·모드 전환·서버 종료를 확인했다. 해당 실제 GUI 실행의 장치 전체 VRAM 관측 최대는 5.176884GiB이며 모든 환경이나 순간 피크를 보장하는 값은 아니다.

**4단계 기능과 내부 배포본은 구현했으나 전체 게이트는 미통과다.** 연결된 화면의 시각 가독성, 실제 메모장·브라우저 단축키, 초심자 문서 없는 7분 진행, 실제 7분 연속 3회 시연은 미확인이다. 새 테스트는 자신의 `result.json`과 화면을 확인해 판정한다.

| 문서 | 내용 |
| --- | --- |
| [구현명세서](구현명세서.md) | 요구사항·금지 사항·단계별 통과 기준 |
| [1단계 보고서](docs/stage1-report.md) | WPF 전환·서버 수명·기반 검증 |
| [2단계 보고서](docs/stage2-report.md) | 이미지·문서·한계 검증 |
| [3단계 보고서](docs/stage3-report.md) | 단축키 구현·실제 선택 입력의 미확인 항목 |
| [4단계 보고서](docs/stage4-report.md) | 데모·배포·측정 결과와 남은 게이트 |
| [데모 체크리스트](docs/demo-checklist.md) | 시연 순서·준비·안전 재생 안내 |
| [라이선스 고지](docs/THIRD_PARTY_NOTICES.md) | 런타임·NuGet·모델의 제삼자 고지 |
