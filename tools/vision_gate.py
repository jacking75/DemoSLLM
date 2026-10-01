"""Approved option B: focused stage-0 gate, preserving previous raw runs."""
import base64
import datetime as dt
from pathlib import Path
import secrets
import time
import spike

ERROR_PROMPT = ('이미지의 오류를 한국어로 정확히 3줄로 답하라. '
                '첫 줄은 오류 메시지와 대상 주소, 둘째 줄은 원인, '
                '셋째 줄은 실행 가능한 조치 2개이다. 인사, 서론, 반복 설명은 쓰지 마라. '
                '화면에 없는 사실은 단정하지 마라. 전체 200자 이내로 답하라.')


def main():
    cfg = spike.resolve_config(spike.ROOT / 'tools/spike-config.json')
    candidate = next(c for c in cfg['candidates'] if c['name'] == 'google-qat')
    out = spike.ROOT / 'docs/spike-runs' / (dt.datetime.now().strftime('%Y%m%d-%H%M%S') + '-vision-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    spike.save(out / 'config.json', cfg)
    results = []
    summary = {'candidate': candidate['name'], 'results': results,
               'gate_bytes': spike.VRAM_GATE_BYTES, 'context_setting': 8192,
               'error_prompt': ERROR_PROMPT, 'quality_review': '미확인',
               'source_runs': ['20260930-160024-bc5c95', '20260930-160647-audio-82d91b']}
    nvml = spike.NVML(cfg['gpu_index'])
    try:
        summary['baseline'] = nvml.read()
        with spike.Monitor(nvml, out / 'vram.csv') as monitor:
            try:
                with spike.Server(cfg, candidate['model'], candidate['mmproj'], out) as server:
                    monitor.phase = 'text'
                    spike.measure(results, out, 'text', lambda: spike.stream_chat(server, '사내 문서를 외부에 보내지 않는 로컬 AI의 장점을 한 문장으로 설명하라.'))
                    cases = [('receipt', '영수증의 합계 금액을 숫자로만 답하라.'),
                             ('table', '표의 부서별 1월과 2월 값을 JSON으로 추출하라.')]
                    cases += [('error', ERROR_PROMPT)] * 3
                    for i, (name, prompt) in enumerate(cases):
                        monitor.phase = 'image-' + name
                        path = spike.ROOT / f'assets/samples/images/{name}.png'
                        def request(p=path, text=prompt):
                            data = base64.b64encode(p.read_bytes()).decode()
                            return spike.chat(server, [
                                {'type': 'image_url', 'image_url': {'url': 'data:image/png;base64,' + data}},
                                {'type': 'text', 'text': text}], temperature=0.2)
                        spike.measure(results, out, f'image-{name}-{i+1}', request)
                        if monitor.peak() is not None and monitor.peak() > spike.VRAM_GATE_BYTES:
                            raise spike.GateConflict('7GiB 초과; 추가 설정 변경은 사용자 결정 대상이다.')
                    monitor.phase = 'image-long'
                    def long_request():
                        prefix = '참고 메모: 이것은 컨텍스트 부하 검증이며 영수증 질문과 무관하다.\n' * 800
                        tokens = server.request('/tokenize', {'content': prefix})['tokens'][:4500]
                        filler = server.request('/detokenize', {'tokens': tokens})['content']
                        data = base64.b64encode((spike.ROOT / 'assets/samples/images/receipt.png').read_bytes()).decode()
                        return spike.chat(server, [
                            {'type':'image_url', 'image_url': {'url': 'data:image/png;base64,' + data}},
                            {'type':'text', 'text': filler + '\n영수증 합계 금액을 숫자로만 답하라.'}], temperature=0.2)
                    spike.measure(results, out, 'image-long', long_request)
            except Exception as exc:
                summary['error'] = str(exc)
            finally:
                monitor.phase = 'after-stop'
                time.sleep(1)
        summary.update(sampled_peak_bytes=monitor.peak(), nvml_errors=monitor.errors)
        summary['memory_pass'] = monitor.peak() is not None and monitor.peak() <= spike.VRAM_GATE_BYTES and not monitor.errors
        summary['transport_pass'] = len(results) == 7 and all(r['transport_ok'] for r in results)
        spike.save(out / 'summary.json', summary)
        print('보완 측정 결과: ' + str(out), flush=True)
    finally:
        nvml.close()


if __name__ == '__main__':
    main()
