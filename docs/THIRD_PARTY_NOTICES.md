# LocalMind Studio 제삼자 고지

이 패키지는 내부 로컬 시연용이다. 아래 고지와 원문은 `third_party/licenses`에 포함한다. 고지 수집은 법률 동의나 외부 배포 허가를 대신하지 않는다. 프로젝트 명세에 따라 외부 배포 전 법무 검토가 필요하다.

| 구성 요소 | 출처·버전 | 고지 |
| --- | --- | --- |
| llama.cpp | ggml-org/llama.cpp b11146 | MIT, 하위 JSON/HTTP/hash 고지 포함 |
| LLVM OpenMP | 고정 llama.cpp 런타임의 LICENSE-LLVM-OpenMP | 원문을 `third_party/licenses/LLVM-OpenMP-LICENSE.txt`에 보존한다. |
| NVIDIA CUDA 라이브러리 | 고정 CUDA 12.4 ZIP의 cudart/cublas/cublasLt | NVIDIA CUDA 12.4 EULA 포함 |
| .NET 자체 포함 런타임 | 게시본의 runtimeconfig/deps와 고정 게시 결과 | MIT·ThirdPartyNotices 포함 |
| Visual C++ x64 CRT | 배포 PC의 Visual Studio Redist/MSVC x64 CRT | app-local DLL 동봉, 버전·SHA-256과 `Visual-Cpp-Runtime-NOTICE.md` 포함 |
| CommunityToolkit.Mvvm | 8.4.2 | NuGet의 MIT·ThirdPartyNotices 포함 |
| DocumentFormat.OpenXml | 2.16.0 | MIT 원문 포함 |
| Microsoft.Data.Sqlite/Core | 10.0.11 | MIT, .NET 고지와 NuGet 메타데이터 포함 |
| PdfPig | 0.1.10 | Apache 2.0 원문 포함 |
| SQLitePCLRaw 계열 | 2.1.12 | Apache 2.0 원문·NuGet 메타데이터 포함 |
| Gemma 4 E2B Q4_0·프로젝터 | ggml-org/gemma-4-E2B-it-GGUF revision b4243c156154b6dca9324415f8c7ccc098b4aed1 | 고정 모델 카드의 Apache 2.0 표기·원문 포함 |
| EmbeddingGemma Q8_0 | ggml-org/embeddinggemma-300M-GGUF revision 0f741b5a6585bd53aeb15cd1372c56f2a0f65e12 | 원본 Google 모델은 Gemma 라이선스다. Gemma Terms·금지 사용 정책 포함 |

EmbeddingGemma는 Apache 2.0이라고 일괄 표기하지 않는다. 원본 [Google 모델 카드](https://huggingface.co/google/embeddinggemma-300m)는 Gemma 라이선스를 명시하며 [Gemma Terms](https://ai.google.dev/gemma/terms)가 적용된다. Gemma 모델 고지: “Gemma is provided under and subject to the Gemma Terms of Use found at https://ai.google.dev/gemma/terms”. 파일 접근 약관에 동의하거나 로그인하는 작업은 수행하지 않았다. 이미 사전 설치된 고정 GGUF만 패키지에 복사한다.

CUDA 원문은 [NVIDIA CUDA 12.4 EULA](https://docs.nvidia.com/cuda/archive/12.4.0/eula/index.html)에서 확보한다. 이 패키지의 라이브러리 고지는 사용·재배포 조건이 충족됐다는 법률 판정이 아니다. 별도 NVIDIA 드라이버는 패키지에 넣지 않는다.

NuGet 전체 직접·전이 종속성은 `third_party/licenses/nuget/packages.json`과 각 패키지의 원본 nuspec에 기록한다. 모델의 고정 URL·크기·SHA-256은 `tools/spike-artifacts.json`, 실제 배포 파일은 `package-manifest.json`에 기록한다. 기록 수집 날짜는 2026-10-01 KST이다.
