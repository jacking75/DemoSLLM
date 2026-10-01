async page => {
  await page.setContent('<!doctype html><html lang="ko"><meta charset="utf-8"><title>LocalMind Stage3 Fixture</title><body style="padding:40px;font:24px sans-serif"><h1>가짜 선택 텍스트 검증</h1><textarea id="fixture" aria-label="가짜 업무 문장" style="width:90%;height:250px;font-size:24px">가상 고객에게 내일 오후 세 시까지 견적서를 보내 주세요.</textarea></body></html>');
  await page.bringToFront();
  await page.locator('#fixture').focus();
  return { title: await page.title(), selection: await page.locator('#fixture').inputValue() };
}
