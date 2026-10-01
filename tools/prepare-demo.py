"""이미 보존된 실제 응답만 데모 자산으로 정리한다. 추론이나 다운로드를 수행하지 않는다."""
import hashlib
import json
import shutil
from pathlib import Path

root = Path(__file__).resolve().parent.parent
destination = root / "assets/demo"
(destination / "evidence").mkdir(parents=True, exist_ok=True)
run = "docs/stage2-runs/20260930-200608-02eb4c/"
sources = [
    ("receipt", "receipt", run + "image-receipt.json", "2026-09-30 20:06 KST"),
    ("error", "error", run + "image-error.json", "2026-09-30 20:06 KST"),
    ("vault", "vault", run + "question-01.json", "2026-09-30 20:06 KST"),
    ("limits", "limits", run + "limits.json", "2026-09-30 20:06 KST"),
    ("shortcut", "shortcut", "docs/stage3-runs/popup-20261001-000827/result.json", "2026-10-01 00:08 KST"),
]
replays = []
for key, kind, source, captured in sources:
    target = destination / "evidence" / (key + ".json")
    shutil.copyfile(root / source, target)
    replays.append(dict(id=key, kind=kind, evidence=target.relative_to(root).as_posix(),
                        sha256=hashlib.sha256(target.read_bytes()).hexdigest(),
                        capturedAt=captured, model="Gemma 4 E2B Q4_0", context=4096, item=0))

steps = [
    dict(id="setup", title="PC 안에서 동작하는 AI", screen="setup", seconds=40, action="setup", sample=None, question=None, replay=None,
         instruction="체크리스트를 확인한 뒤 ‘현재 단계 실행’을 누른다. 실제 모드에서는 모델을 검증하고 서버를 시작한다. 투명성 패널의 인터넷 상태와 외부 연결을 설명한다.",
         notes="시연 전에 모델 검증·서버 시작을 완료하면 관객 대기 시간을 줄인다. Wi-Fi를 끄는 작업은 사용자가 직접 한다. 인터넷 연결 상태를 그대로 소개하며 끊김을 꾸미지 않는다."),
    dict(id="receipt", title="민감한 영수증을 로컬에서 읽는다", screen="A", seconds=45, action="receipt", sample="assets/samples/images/receipt.png", question=None, replay="receipt",
         instruction="가짜 영수증이 준비됐다. ‘현재 단계 실행’을 누르고 Notebook 6,000 + Pen 3,000 = 합계 9,000을 원본과 대조한다.",
         notes="AI가 JSON을 만들고 표로 정리한다. 형식 검증과 내용 정확성은 별개다. 구매 증빙의 검토 단계에 적용할 수 있다."),
    dict(id="error", title="오류 화면을 외부로 보내지 않는다", screen="A", seconds=35, action="error", sample="assets/samples/images/error.png", question=None, replay="error",
         instruction="가짜 오류 화면이 준비됐다. ‘현재 단계 실행’을 누르고 연결 거부 메시지와 가능한 원인을 확인한다.",
         notes="127.0.0.1:8080은 가짜 오류 화면의 주소다. 실제 추론 서버 오류라고 소개하지 않는다. AI의 진단은 담당자가 확인한다."),
    dict(id="vault", title="문서 답변과 출처를 함께 확인한다", screen="B", seconds=90, action="vault", sample=None, question="국내 출장의 하루 식비 한도는?", replay="vault",
         instruction="‘현재 단계 실행’을 누르면 가상 문서 5개를 별도 데모 금고에 색인하고 질문한다. 답변의 35,000원과 출장규정 원문을 대조한다.",
         notes="CPU 임베딩과 SQLite 검색을 사용한다. 모든 자료는 가상이며 업로드하지 않는다. 기존 사용자 문서 금고를 교체하지 않는다."),
    dict(id="shortcut", title="업무 문장을 바로 다듬는다", screen="E", seconds=60, action="shortcut", sample=None, question="가상 고객에게 내일 오후 세 시까지 견적서를 보내 주세요.", replay="shortcut",
         instruction="‘현재 단계 실행’으로 가짜 업무 문장 팝업을 열고 영어 번역을 누른다. 실제 선택 읽기를 보려면 메모장/브라우저에서 문장을 선택하고 Ctrl+Alt+Space를 누른다.",
         notes="데모 팝업은 문장을 직접 전달한다. 전역 단축키의 실제 선택 읽기는 별도 검증이 미확인이다. 권한 오류가 나면 안전 재생으로 전환하고 한계를 설명한다."),
    dict(id="review", title="결과를 업무 원문과 대조한다", screen="review", seconds=60, action="review", sample="assets/samples/images/receipt.png", question=None, replay="vault",
         instruction="‘현재 단계 실행’으로 이번 실행의 영수증과 문서 응답을 다시 본다. 합계 9,000과 사규 원문의 35,000원을 함께 확인한다.",
         notes="사람의 검토가 업무 흐름에 들어가야 한다. 재생 모드에서는 저장된 응답과 원문을 비교하며 현재 실행 결과로 소개하지 않는다."),
    dict(id="limits", title="AI가 틀리는 순간을 확인한다", screen="F", seconds=45, action="limits", sample=None, question=None, replay="limits",
         instruction="‘현재 단계 실행’을 누르고 1847 × 2963의 모델 응답을 검산값 5,472,661과 비교한다. 정답이 나오더라도 검산 절차가 필요함을 설명한다.",
         notes="실제 응답의 성공과 실패를 그대로 보여준다. 안전 재생에는 이전에 관측한 오답을 표시한다. 현재 모델이 반드시 같은 오답을 낸다고 주장하지 않는다."),
    dict(id="ideas", title="우리 회사라면 어디에 적용할까?", screen="ideas", seconds=45, action="ideas", sample=None, question=None, replay=None,
         instruction="아이디어 카드에서 우리 회사의 업무 하나를 고른다. 필요한 입력 자료, 검토 담당자, 성공 기준을 함께 이야기한 뒤 ‘데모 종료’를 누른다.",
         notes="민감 자료 보호, 외부 API 호출 없는 처리, 사람의 검토를 연결해 질문한다. 단가를 입력하지 않으면 절감 금액을 임의로 계산하지 않는다."),
]
guide = dict(version=1, steps=steps, replays=replays, ideas={
    "A": ["구매 영수증 검토", "사내 오류 화면 1차 분류", "민감한 보고서 표 전산 입력"],
    "B": ["내부 사규 안내", "계약 조건 원문 확인", "회의 결정사항 조회"],
    "E": ["메일 문장 다듬기", "선택한 보고서 문단 요약", "해외 고객에게 보낼 문장 번역"],
    "F": ["금액 검산 단계", "원문 대조 후 결재", "근거 없는 응답의 담당자 이관"],
})
(destination / "guide.json").write_text(json.dumps(guide, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(destination / "SAMPLES.md").write_text("# 데모 샘플\n\n모든 이미지와 문서는 가상 자료다. 영수증 합계는 9,000, 출장 식비 한도는 1인 1일 35,000원이다.\n\n"
    "guide.json의 instructions/notes와 ideas는 시연 문구 초안이다. 7분 배정은 seconds 합계 420으로 검증한다. 카드 문구는 사용자와 협의해 변경할 수 있다.\n\n"
    "evidence는 보존된 실제 E2B·4096 응답의 원본 복사다. 원문을 수정하면 해시 검증에서 재생을 거부한다. "
    "팝업 기록은 가짜 입력 직접 전달 결과이며 전역키 검증이 아니다. 현재 성능 수치로 사용하지 않는다.\n", encoding="utf-8")
print("데모 8단계·420초·실측 근거 5개 정리 완료")
