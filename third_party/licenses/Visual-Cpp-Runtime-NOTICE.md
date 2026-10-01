# Microsoft Visual C++ 런타임 동봉 안내

배포 도구는 Visual Studio의 `VC/Redist/MSVC/<버전>/x64/Microsoft.VC*.CRT`에서 재배포용 DLL을 가져와 `llama-server.exe`와 같은 폴더에 배치한다. Windows의 System32나 개발용 Debug CRT에서 DLL을 수집하지 않는다. DLL별 버전과 SHA-256은 배포본의 `visual-cpp-runtime-files.json`에 기록한다.

이 자료는 Microsoft의 원문 라이선스를 대신하지 않는다. 배포자는 자신이 사용하는 Visual Studio의 라이선스와 재배포 허용 목록을 확인해야 한다. 로컬 패키지 생성이 공개 배포 승인이나 약관 동의를 뜻하지 않는다.

- [Microsoft Visual C++ 파일 재배포 안내](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170)
- [Visual Studio 라이선스 원문](https://visualstudio.microsoft.com/license-terms/)

Microsoft는 중앙 설치 방식이 런타임 업데이트 관리에 유리하다고 설명한다. 이 프로젝트는 사용자 설치 부담을 줄이기 위해 앱 폴더 동봉 방식을 제공하며, 런타임 보안 업데이트 시 배포자가 새 DLL로 배포본을 다시 생성해야 한다. 새 PC에서의 실제 로딩·실행은 별도 검증 대상이다.
