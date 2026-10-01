# 0단계 실행·측정 안내

- [x] 실행 절차와 측정 정의 작성
- [x] 실제 런타임·두 후보 모델·프로젝터·임베딩 모델 준비
- [ ] 사용자가 정한 한국어 음성 샘플 5개 준비
- [x] 두 후보 실측과 Codex 원본 응답 검토
- [ ] 사용자 품질 수용 평가
- [x] 최초 Google QAT 실측과 VRAM·사고 모드 충돌 보고
- [x] 충돌 대안 B 선택 후 재실측
- [ ] 결과 보고 및 명세서 수정안 합의

## 범위와 현재 상태

재측정은 완료했고 [최신 보고](spike-round2.md)와 [수정안](spike-spec-proposal.md)에 정리했다. Microsoft Heami Desktop 한국어 합성 문장에 무음을 붙인 30초 WAV 5개로 보조 실측도 수행했다. 사용자 녹음과 연속 30초 발화를 대신하지 않는다. 보조 실측 재실행은 아래 순서이다. 실제 사용자 음성 품질과 최종 게이트는 미확인·결정 대기이다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\New-AudioProbe.ps1
py -3 -X utf8 .\tools\audio_probe.py --pad-only
py -3 -X utf8 .\tools\audio_probe.py
```

0단계만 수행한다. 앱, 모델 다운로드 기능, CI는 만들지 않는다. 외부 추론 호출과 통계 전송은 없다. Python 표준 라이브러리만 사용한다. HTTP 요청 대상은 코드에 고정된 127.0.0.1이다. 이 사실 자체로 외부 연결 수를 실측했다고 주장하지 않는다.

2026-09-30 작업 환경에서 `nvidia-smi`가 NVIDIA GeForce RTX 4060, VRAM 8188 MiB, 드라이버 591.86을 보고했다. `py -3 --version` 결과는 Python 3.13.13이다. 사용자 지시에 따라 두 E4B 모델과 같은 저장소의 BF16 프로젝터, EmbeddingGemma Q8_0, CUDA 12.4 런타임을 다운로드하고 공개 SHA-256과 일치함을 확인했다. 고정 파일은 `tools/spike-artifacts.json`, 실제 설정은 `tools/spike-config.json`에 있다. 최신 안정 릴리스 v0.5.0이 공식 연결한 b11146 바이너리는 `--version`에서 0.5.0-dev/build 11146/commit 7fe450e19를 보고한다. 사용자 한국어 WAV 5개는 아직 없어 audio 배열은 비어 있으며 음성 품질은 미확인이다. 준비한 설정에서 병렬 슬롯은 1개, 컨텍스트는 8192, 이미지 토큰 상한은 1120이다.

## 1. 입력 준비

프로젝트 루트 `C:\WorkingDev\DemoSLLM`에서 Windows PowerShell을 연다. 관리자 권한은 필요 없다.

```powershell
Copy-Item -LiteralPath .\tools\spike-config.example.json -Destination .\tools\spike-config.json
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\New-SpikeImages.ps1
```

합성 이미지 세 개를 만든다. 영수증 정답은 9000원이다. 오류 화면 정답은 로컬 127.0.0.1:8080 연결 거부와 서비스 미실행이다. 표 정답은 Sales 120/150, Support 80/90이다. 이것은 0단계 검증 입력이며 최종 앱 내장 샘플은 아니다. 이미지를 변경하면 두 후보 모두 같은 파일로 다시 측정한다.

`tools/spike-config.json`을 실제 파일에 맞게 수정한다. 상대 경로는 config 파일이 있는 tools 폴더 기준이다.

- `server`: 사전 준비한 Windows x64 CUDA llama-server.exe 경로이다. 배포 압축의 DLL과 필요한 CUDA 런타임 DLL을 같은 폴더에 둔다.
- `release`, `backend`: 실제 릴리스 태그와 CUDA 빌드 종류를 기록한다. 최신 안정 릴리스 여부는 공식 릴리스 화면에서 확인한 뒤 기록한다. 도구는 자동 업데이트하지 않는다.
- `candidates`: Google QAT E4B Q4_0와 ggml-org E4B Q4_0의 실제 GGUF·mmproj 경로를 각각 입력한다. BF16 파일을 대신 사용하지 않는다. 다른 저장소의 프로젝터를 섞어 쓰지 않는다.
- `projector_provenance`: 프로젝터를 받은 저장소·파일 이름을 기록한다. Google 저장소에 mmproj가 없다면 그 사실을 보고하고 후보를 미확인으로 남긴다. 코드가 임의로 프로젝터를 대체하지 않는다.
- `embedding`: 실제 EmbeddingGemma 300M 양자화 GGUF 저장소와 파일을 기록한다. 서버는 `-ngl 0 --embedding --pooling mean`으로 실행하고 CUDA 장치도 숨긴다. CPU 실행 여부는 로그·NVML로 확인한다.
- `audio`: 사용자가 정한 문장 5개의 WAV 경로와 정답 문장을 기록한다. 개인정보 대신 가짜 회사명·담당자·금액을 사용한다.

각 음성은 16kHz, 모노, PCM 16bit, 0초 초과 30초 이하이다. 30초 경계 검증을 위해 가능하면 30초 길이 샘플로 준비한다. 짧은 샘플만 사용하면 30초 전체 입력 동작은 미확인으로 남긴다. 이미 설치된 ffmpeg가 있다면 변환 예시는 다음과 같다. 30초 초과 파일을 자동으로 자르지 않는다.

```powershell
ffmpeg -i .\recording.wav -ar 16000 -ac 1 -c:a pcm_s16le .\assets\samples\audio\01.wav
```

`extra_args`는 기본 빈 배열이다. 선택한 서버의 `--help`가 옵션을 지원하는 경우에만 `--no-webui`, 이미지 토큰 옵션을 추가한다. OCR 예산 1120 검증 시 예시는 `["--no-webui", "--image-max-tokens", "1120"]`이다. `--no-mmproj-offload`도 허용하지만 기본 GPU 프로젝터 실측과 다른 실험이므로 이유를 기록한다. 두 후보에 동일한 옵션을 사용한다. 옵션이 지원되지 않으면 충돌로 보고하고 사용자 결정을 기다린다.

다운로드·저장소 권한·버전 호환은 자동 확인하지 않는다. 모델 카드의 공식 권고와 실제 파일 구성도 준비 시 근거를 함께 남겨야 한다. 대용량 파일 SHA-256 계산 때문에 시작 전 준비 시간이 걸릴 수 있다.

## 2. 사전 점검과 실행

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-Spike.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-Spike.ps1 -Preflight
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-Spike.ps1
```

브라우저·게임·녹화 프로그램 등 VRAM 사용 앱을 닫은 뒤 측정한다. 서버는 동적 루프백 포트와 실행마다 다른 API 키로 시작한다. 후보는 하나씩 실행하고 종료한 뒤 다음 후보를 실행한다. 임베딩은 마지막에 단독 실행한다. 포트 예약과 서버 바인딩 사이 경쟁으로 시작에 실패하면 실패로 기록한다.

중단은 Ctrl+C이다. 정상 종료·Python 예외·Ctrl+C 때 이 도구가 시작한 서버를 정리한다. 측정 도구 자체를 작업 관리자에서 강제 종료하는 경우의 정리는 미확인이다. Windows Job Object 강제 종료 검증은 1단계 범위이다. 필요하면 이번 실행이 시작한 서버 PID만 확인해서 종료한다. 다른 llama-server를 일괄 종료하지 않는다.

한 요청은 기본 180초, 서버 준비는 300초 제한이다. 스트리밍은 읽기 타임아웃과 전체 시간 점검을 적용한다. 최악의 경우 두 후보의 반복 요청으로 오래 걸릴 수 있으며 콘솔에 현재 테스트 ID가 표시된다. 새 실행은 고유한 결과 폴더를 생성해 이전 결과를 덮어쓰지 않는다.

## 3. 측정 정의

| 항목 | 측정 방법 | 판정 범위 |
|---|---|---|
| 후보 비교 | 같은 이미지·문장·음성·도구 요청·seed·설정 | 응답 수신과 의미 정확성을 분리한다. 후보당 한 세션이므로 변동성은 미확인이다. |
| 텍스트 속도 | 워밍업을 따로 제외하지 않고 3회 SSE 실행 | 서버 생성 속도와 재토큰화 추정치(첫 토큰 대기 포함)를 별도 기록한다. tokenizer 추가 호출 시간은 제외한다. 둘 다 실패하면 미확인이다. |
| 첫 토큰 | 요청 직전부터 첫 content 델타까지 monotonic clock | reasoning 첫 델타도 별도 기록한다. |
| VRAM | NVML 장치 전체 used bytes, 약 50ms 주기 | Windows와 다른 프로세스 포함이다. 사용자 선택 B에 따라 7GiB=7,516,192,768 bytes로 판정하고 GB·GiB를 병기한다. 샘플 사이 순간 피크는 미확인이다. |
| 이미지·8K | `-c 8192`, 이미지 3종과 긴 입력 이미지 1회 | 긴 입력은 약 4500 텍스트 토큰을 추가한다. 실제 점유 8192 토큰 검증은 아니다. 이미지 예산은 설정·서버 로그로 확인한다. |
| 음성 | 지시문 뒤 input_audio, WAV 5개 | 사람이 정답과 대조한다. 숫자·인명·기한 누락 없이 의도를 보존해야 실사용 가능으로 평가하는 안이며 사용자 확정 대상이다. |
| 도구 | 4개 스키마, temperature 1.0/0.2 각각 최초 20회 | 이름과 인자가 기대값과 일치해야 성공이다. HTTP 실패도 분모에 포함한다. 실제 파일 작업은 실행하지 않는다. |
| content 이슈 | tool_calls 없이 content만 있으면 의심 표시 | 원본 검토 전 이슈 #22786 재현을 확정하지 않는다. |
| 한국어 응답 | 고정 요청 10개, 원본 저장 | 사실 정확성·지시 준수·자연스러움을 사람이 평가한다. |
| 한국어 검색 | CPU 임베딩, 가짜 규정·질문 각 10개, cosine Top1/Top5 | task prefix를 사용한다. 최종 RAG 출처 정확도와 다르며 수용 기준은 미정이다. |

`--jinja`를 사용한 결과만 측정한다. 필요성의 인과 검증, 사고 모드 on/off 비교, E2B·4K 폴백, 여유 VRAM 6GB 임계치 최적화는 이번 결과만으로 확정할 수 없다. RTX 4060의 CUDA 12 성공은 RTX 50 시리즈 호환을 입증하지 않는다.

### 사용자 선택 B 반영

명세서 7절은 서버 타이밍이 없으면 스트림 청크 수로 tok/s를 계산하도록 한다. SSE 청크 하나에 여러 토큰 또는 역할·종료 정보가 들어갈 수 있어 청크 수를 모델 토큰 수로 간주하면 정확한 tok/s가 아니다. 자체 검증에서도 content 청크 2개와 서버 토큰 20개가 함께 오는 응답을 처리한다. 실제 선택 런타임의 청크별 토큰 분포는 미확인이다.

사용자가 대안 B를 선택했다. 완료된 출력 content를 동일 모델을 로딩한 서버의 `/tokenize`로 재토큰화한다. `add_special=false`, `parse_special=false`로 요청하며 토큰 수를 요청 시작부터 스트림 종료까지 시간으로 나눈다. tokenizer 추가 호출 시간은 분모에 포함하지 않는다. 추정치에는 첫 토큰 대기가 포함되고 숨겨진 사고·특수 토큰 수는 포함되지 않는다. 서버 생성 tok/s와 측정 범위가 다르므로 별도 열로 보고한다. 서버 수치가 없으면 추정치를 사용하되 생성 20 tok/s 목표 충족 여부는 미확인으로 남긴다. tokenizer 실패는 원본 텍스트 응답과 텍스트 동작 판정을 지우지 않는다. 명세서 7절에 승인된 B 방식만 반영했다. 다른 미검증 항목은 실측 대기이다.

## 4. 결과 확인과 회신

결과 경로는 `docs/spike-runs/<실행시각-고유값>/`이다. `spike-report.md`, 후보별 `summary.json`, 개별 응답 JSON, `vram.csv`, `server.log`, 런타임 버전·help·파일 해시가 있다. API 키는 server-args.json에서 가린다. 서버 자체 로그를 공유하기 전 비밀값이 없는지 확인한다.

`manual-review.json`에서 각 null을 true/false로 평가하고 notes에 이유를 적는다. 요청 실패 등으로 평가할 수 없으면 null을 유지한다. 체크박스와 미확인 문구는 도구 실행만으로 자동 확정하지 않는다.

다음 내용을 대화에 붙여 넣는다.

1. `spike-report.md` 전체와 `runtime-version.txt` 내용이다.
2. `manual-review.json` 전체이다.
3. 실패 항목 오류 메시지와 관련 서버 로그 부분이다.
4. 모델·프로젝터·임베딩 실제 파일 이름, Google mmproj 포함 여부, 최신 안정 릴리스 확인 근거이다.

필요한 개별 응답은 결과를 본 뒤 추가 요청한다. 음성 원본이나 전체 모델을 공유할 필요는 없다.

## 5. 게이트와 명세서 수정안 절차

게이트는 총 VRAM 관측 피크 ≤7GiB (7,516,192,768 bytes), 텍스트 정상 동작, 이미지 의미 확인으로 판정한다. 이미지 3종과 긴 입력 정확성은 추가 증거로 보고한다. 자동 조건을 만족해도 이미지 평가 전에는 최종 통과로 표시하지 않는다. 첫 토큰 ≤2초·생성 ≥20 tok/s는 전체 수용 목표로 따로 보고하며 임의로 낮추지 않는다.

사용자가 VRAM 대안 B도 선택했다. E4B·8K·GPU 프로젝터를 유지하며 기본 요청에 `chat_template_kwargs.enable_thinking=false`를 명시한다. JSON 응답에 thinking_requested와 reasoning_observed를 기록해 실제 사고 출력 유무를 확인한다. 앞선 십진수 7GB 기준 실행은 비교 자료로 보존하고 새 폴더에서 재실측한다.

오디오가 5개 중 4개 미만 실사용 가능하면 C 강등을 제안하되 사용자 합의 전 확정하지 않는다. 도구 성공률에는 명세상 임계치가 없어 측정값을 보고하고 D 채택·강등을 결정한다. 임베딩 품질에도 임의 통과선을 만들지 않는다. 측정과 평가가 끝나기 전에 후보를 자동 채택하지 않는다.

결과가 오면 항목별 측정값·실패·미확인 표, 후보 비교, 미검증 항목의 확정 가능한 범위, 명세서 변경 전/후 수정안을 제시한다. 승인된 B 방식 외의 변경은 사용자 결정 전에 적용하지 않는다.

실제 충돌이 발견되면 해당 항목에서 멈추고 대안 두 가지를 제시한다. 예를 들어 VRAM 초과는 A: E4B·8K를 유지하면서 프로젝터 CPU 오프로딩 등 설정 재실측, B: E2B·4K로 범위 변경 후 재실측이다. 지원되지 않는 API·모델·프로젝터를 다른 것으로 조용히 대체하지 않는다. 게이트 보고와 사용자 결정 전에는 1단계를 시작하지 않는다.
