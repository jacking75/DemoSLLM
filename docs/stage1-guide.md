# 1단계 실행과 남은 게이트 측정

- [x] 빌드·실행 경로 안내
- [x] 정상·강제 종료 및 패널 대조 도구 준비
- [x] 사용자 요청에 따른 인터넷 단절 실측 면제 기록 (실측 미수행)

사용자가 인터넷 단절 검증 없이 진행하라고 명시했다. 아래 오프라인 절차는 선택 검증 참고용이며 자동 실행하지 않는다. 인터넷 단절 동작은 미확인으로 유지한다.

작업 폴더는 `C:\WorkingDev\DemoSLLM`이다. 실행 셸은 명세 폴백 정책에 따른 WPF이다. WinUI 소스는 보존했으며 기본 빌드 대상이 아니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-App.ps1 -Publish
$demoApp = Start-Process -FilePath .\src\App.Wpf\bin\x64\Debug\net10.0-windows\win-x64\publish\LocalMindStudio.exe -ArgumentList '--diagnostics' -PassThru
```

`--diagnostics`는 패널의 동일 샘플 원본을 로컬 `docs/stage1-runs`에 저장하는 검증 모드이다. 일반 실행은 인자를 빼면 된다. 서버 시작 후 질문을 입력한다. 다음 단계 기능 버튼은 현재 미구현 안내만 한다.

정상 종료 자동 측정은 다음과 같다. 자동 측정은 창을 닫는다. 여유 VRAM이 낮으면 GUI가 E2B 폴백 확인을 요청하며 확인/취소를 직접 선택해야 한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-Gui.ps1 -WindowPid $demoApp.Id
```

강제 종료 검증은 새로 실행한 앱을 대상으로 같은 명령에 `-Force`를 추가한다. 이 도구는 지정한 이번 앱과 그 자식 서버만 확인·종료하며 다른 서버를 일괄 종료하지 않는다.

## 실제 인터넷 단절 게이트

모델을 모두 준비하고 실행 중인 앱을 종료한 뒤, 사용자 본인이 Ethernet 연결과 Wi-Fi를 끊는다. PC의 다른 업무 연결도 끊기므로 이 도구가 대신 변경하지 않는다. 가상/추가 WAN 연결도 확인한다. 연결을 끊은 상태에서 새 앱을 실행한 뒤 다음을 수행한다.

```powershell
$demoApp = Start-Process -FilePath .\src\App.Wpf\bin\x64\Debug\net10.0-windows\win-x64\publish\LocalMindStudio.exe -ArgumentList '--diagnostics' -PassThru
# 창이 표시된 뒤 실행한다.
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-Gui.ps1 -WindowPid $demoApp.Id -RequireOffline
```

도구는 물리 어댑터 Up 0개, 패널의 Windows 인터넷 끊김, 실제 GUI 응답, 루프백 소켓, 종료 후 서버 해제를 기록한다. 자동 결과는 `docs/stage1-runs/<시각>-gui-<고유값>/result.json`이다. 해당 JSON을 붙여 넣거나 인터넷을 끊은 뒤 알려주면 남은 검증을 이어갈 수 있다. 검증 후 사용자가 연결을 복구한다.

현재 실제 인터넷 단절 게이트는 미확인이다. 단절 상태에서도 모든 미래 요청이 성공하거나 다른 앱이 데이터를 전송하지 않는다는 보장으로 확대하지 않는다. 1단계 최종 통과 전에 2단계를 실행하지 않는다.
