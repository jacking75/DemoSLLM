# 1단계 기반 구현·실측 보고

- [x] Google QAT 채택·C/D 보류 및 0단계 통과 보고
- [x] 셸·모델 관리·Job Object·SSE·취소·투명성 패널 구현
- [x] C# 실제 채팅·취소 후 재요청·정상 종료 검증
- [x] GUI 실제 채팅·정상 종료·강제 종료 검증
- [x] E2B·4K 폴백 실모델 및 GUI 경로 검증
- [x] 다운로드 이어받기·해시·경로 범위·기존 파일 보호 모의 검증
- [x] self-contained 배포 폴더 GUI 실행 확인
- [x] 사용자 요청에 따른 실제 인터넷 단절 검증 면제 기록
- [x] 나머지 실측과 면제 조건으로 1단계 진행 게이트 확정

## 결과

최종 로컬 Publish 빌드는 경고 0개·오류 0개이며 [빌드 로그](stage1-build.log)에 있다. 빌드 성공과 남은 인터넷 단절 게이트는 별개이다.

0단계는 [최종 게이트 보고](spike-final.md)의 범위에서 통과했다. 이후 사용자가 인터넷 단절 테스트를 생략하고 진행하라고 명시했다. **1단계는 나머지 실측 게이트 충족과 사용자 검증 면제 조건으로 다음 단계 진행을 확정한다.** 인터넷 단절 동작은 여전히 미확인이며 검증 성공으로 바꾸지 않는다. 인터넷 단절 실측만 면제하고 루프백 추론·외부 AI 금지·외부 소켓 감시는 유지한다.

현재 셸은 WPF·.NET 10·CommunityToolkit.Mvvm이다. WinUI 소스는 `src/App`에 보존했다. WinUI 빌드는 최초 코드 오류와 PRI 빌드 도구 경로 문제를 거쳐 Visual Studio MSBuild에서 성공했지만, GUI 실행은 PID 59436, 65732, 59088의 세 번 연속 실패했다. 마지막 원인 기록은 [XamlParseException](stage1-runs/app-startup-error.txt)이다. 명세 4절의 사전 정의된 3회 실패 폴백 정책으로 `src/App.Wpf`로 전환했다. 이는 WinUI가 이 PC에서 원천적으로 불가능하다는 결론이 아니다.

## 게이트 실측

| 항목 | 실제 측정 | 판정 |
|---|---|---|
| C# 채팅·취소 | TTFT 0.140초, 70.53tok/s, 취소 후 답변 `2` | 확인 |
| GUI 기본 채팅 | TTFT 0.187초, 패널 66.5tok/s | 확인 |
| GUI 정상 종료 | 서버 잔존 0, VRAM 6315→2035MiB | 확인 |
| GUI 강제 종료 | 서버 잔존 0, VRAM 6353→2072MiB | 확인 |
| 진단 부모 강제 종료 | 서버 잔존 0, VRAM 6380→2130MiB | 추가 확인 |
| 외부 소켓 | 패널 0건, Windows TCP 목록은 루프백 통신·루프백 리스너만, UDP 없음 | 확인 |
| 패널과 동일 샘플 수치 | E2B: 5795258368bytes→5.397GiB, 109.210→109.2tok/s, 0.1435902→0.144초, GPU 89%·53℃ | 표시 반올림 일치 |
| E2B·4096 C# 채팅 | 106.58tok/s, TTFT 0.147초, 취소·재요청·종료 성공 | 확인 |
| GUI 폴백 경로 | E2B·4096, 109.2tok/s, 정상 종료 후 서버 0개 | 확인 |
| 전력 | NVML 반환 없음, 패널 미확인 | 미확인 유지 |
| 실제 인터넷 단절 | Ethernet Up, Windows 연결 상태에서 시험했다 | 미확인 |

종료 전후 VRAM은 nvidia-smi 시점 값이며 50ms 관측 피크가 아니다. 데스크톱 등 다른 프로세스 때문에 종료 후 VRAM 0을 요구하지 않는다. 이번 종료 시험에서 모델 점유가 내려가고 해당 서버가 없어졌음을 확인했다. 반복 세션·다른 GPU의 보장은 아니다.

근거는 [C# 기본](stage1-runs/20260930-164917-9cfcac/result.json), [진단 강제 종료](stage1-runs/20260930-165034-22b881/forced-exit.json), [GUI 정상 종료](stage1-runs/20260930-170740-gui-cdfcac/result.json), [GUI 강제 종료](stage1-runs/20260930-170857-gui-485e71/result.json), [E2B C#](stage1-runs/20260930-171403-37f602/result.json), [배포 폴더 GUI·폴백·동일 샘플 대조](stage1-runs/20260930-171944-gui-49414d/result.json)에 있다.

기본 GUI 첫 자동 시험은 당시 여유 VRAM 6GB 미만으로 시작이 차단됐다. 기준을 낮추지 않았고 E2B를 준비했다. GUI 폴백은 사용자 확인 후 전환하며 기본 모델을 몰래 교체하지 않는다. E2B의 한국어·이미지 품질은 이번 채팅 검증만으로 확정하지 않는다.

## 구현 범위

- Core: IInferenceBackend, 요청·통계 DTO, 고정 모델 경로, 모델 목록, SHA-256·크기 검증, Range 이어받기, 기존 파일 보호, 참고 단가 설정 저장이다.
- Backend: 일시 정지 CreateProcess→Job Object 등록→재개 순서이다. KILL_ON_JOB_CLOSE, 루프백 동적 포트, 무작위 API 키, 웹 UI 비활성화, 3분 요청·5분 기동 제한, SSE 취소와 재토큰화 추정치이다. 잘린 응답은 완료로 표시하지 않는다.
- Telemetry: 시스템 경로 NVML DLL, 장치 전체 VRAM·GPU·온도·가능한 전력, IPv4/IPv6 TCP/UDP 소유 PID 표, Windows 인터넷 상태이다. 외부 전송 통계를 모으지 않는다.
- WPF: 채팅·시연 글자 확대·다음 단계 시나리오 내비게이션·투명성 패널·단가 선택·모델 검증/다운로드·작업 취소이다. A/B/E/F의 실제 기능은 다음 단계이며 현재 버튼은 미구현 안내만 한다. EmbedAsync는 2단계 CPU 서버 연동 전까지 명시적으로 미지원이다.

모델 다운로드 이어받기는 가짜 HTTP Range 응답으로 검증했다. 실제 고정 파일 준비는 Node 도구로 수행했다. 앱 다운로드를 실제 수 GB 전송으로 재검증했다고 주장하지 않는다. 설정 저장 구현은 완료했으며 설정 복구·마이그레이션의 종합 검증은 미확인이다.

E2B와 같은 저장소 BF16 프로젝터는 [고정 revision](https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/tree/b4243c156154b6dca9324415f8c7ccc098b4aed1)에서 준비했고 `tools/spike-artifacts.json`의 SHA-256과 일치했다. UI 패키지 선택 시 agent-reach로 공식 배포 문서와 저장소 자료를 확인했다. [Windows App SDK self-contained 문서](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)와 [Job Object 문서](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)를 참고했다.

## 남은 게이트

사용자 요청으로 실제 인터넷 단절 실측은 면제했다. 스크립트와 [안내](stage1-guide.md)는 선택 검증용으로 보존하며 실행하지 않는다. 오프라인 동작을 확인했다고 말하지 않는다. 2단계 A/B/F의 나머지 통과 기준은 실제 측정한다.
