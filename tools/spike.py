"""Stage 0 only. Python standard library; inference traffic is loopback only."""
import argparse
import base64
import ctypes as C
import datetime as dt
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import threading
import time
import wave

ROOT = Path(__file__).resolve().parents[1]
VRAM_GATE_BYTES = 7 * 1024**3  # User-approved option B: 7 GiB.


class GateConflict(RuntimeError):
    pass


TEXTS = [
    '사내 문서를 외부로 보내지 않는 AI의 장점과 한계를 각각 두 가지 설명하라.',
    '다음 문장을 정중하게 고쳐라: 자료 오늘까지 보내줘.',
    '회의 메모를 요약하라: 김 대리는 금요일까지 견적서를 작성한다. 이 과장은 다음 월요일에 검토한다.',
    '다음을 영어로 번역하라: 계약서는 내부 검토 후 전달하겠습니다.',
    '다음을 한국어로 번역하라: Please review the attached proposal by Friday.',
    '문장에서 담당자와 기한을 JSON으로 추출하라: 박 과장은 10월 5일까지 보고서를 제출한다.',
    '맞춤법을 고쳐라: 내일 뵈요. 자료를 검토해 주시면 됨니다.',
    '근거에만 따라 답하라. 근거: 출장 숙박비 상한은 12만원이다. 질문: 상한은 얼마인가?',
    '근거에 없으면 문서에서 찾지 못했습니다라고 답하라. 근거: 점심시간은 12시다. 질문: 해외 출장 수당은?',
    '1847 곱하기 2963을 계산하고 계산 과정과 검산 필요 여부를 말하라.'
]
DOCS = [
    '출장 숙박비는 하루 12만원까지 지원한다.', '연차 신청은 사용일 3일 전에 제출한다.',
    '구매 금액 500만원 이상은 부서장 승인이 필요하다.', '계약 해지 통지는 30일 전에 서면으로 한다.',
    '보안 사고는 발견 즉시 보안 담당자에게 보고한다.', '회의실 예약은 사내 포털에서 한다.',
    '신입 사원 교육은 입사 첫 주에 실시한다.', '재택근무는 주 2회까지 가능하다.',
    '고객 문의는 영업일 기준 24시간 안에 회신한다.', '출장 교통비 정산에는 영수증이 필요하다.'
]
QUERIES = ['호텔 비용 한도가 얼마인가?', '휴가 신청은 며칠 전에 하는가?', '고액 구매 승인자는 누구인가?',
           '계약을 끝내려면 언제 알려야 하는가?', '정보 유출을 발견하면 누구에게 알려야 하는가?',
           '회의 공간을 어떻게 예약하는가?', '새 직원 교육 시기는 언제인가?', '집에서 일할 수 있는 횟수는?',
           '고객 질문에 답하는 기한은?', '출장 운임을 돌려받으려면 무엇을 제출하는가?']
PARAMS = {
    'find_files': {'extension': {'type': 'string'}, 'older_than_days': {'type': 'integer'}},
    'read_text_file': {'name': {'type': 'string'}},
    'propose_organize_plan': {'rule': {'type': 'string'}},
    'apply_plan': {'plan_id': {'type': 'string'}}
}
TOOLS = [{'type': 'function', 'function': {'name': name, 'description': 'DemoWorkspace 전용 검증용 도구',
          'parameters': {'type': 'object', 'properties': props, 'required': list(props),
                         'additionalProperties': False}}} for name, props in PARAMS.items()]


def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(4 * 1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def save(path, obj):
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding='utf-8')


class Memory(C.Structure):
    _fields_ = [('total', C.c_ulonglong), ('free', C.c_ulonglong), ('used', C.c_ulonglong)]


class NVML:
    def __init__(self, index):
        # Explicit system paths avoid DLL resolution from the working directory.
        candidates = [Path(os.environ['SystemRoot']) / 'System32/nvml.dll',
                      Path(os.environ.get('ProgramW6432', 'C:/Program Files')) / 'NVIDIA Corporation/NVSMI/nvml.dll']
        path = next((p for p in candidates if p.is_file()), None)
        if not path:
            raise RuntimeError('nvml.dll 미확인: NVIDIA 드라이버 설치를 확인한다.')
        self.dll = C.WinDLL(str(path))
        self.dll.nvmlInit_v2.restype = C.c_int
        self.dll.nvmlDeviceGetHandleByIndex_v2.argtypes = [C.c_uint, C.POINTER(C.c_void_p)]
        self.dll.nvmlDeviceGetMemoryInfo.argtypes = [C.c_void_p, C.POINTER(Memory)]
        self.dll.nvmlDeviceGetName.argtypes = [C.c_void_p, C.c_char_p, C.c_uint]
        self.check(self.dll.nvmlInit_v2())
        self.handle = C.c_void_p()
        self.check(self.dll.nvmlDeviceGetHandleByIndex_v2(index, C.byref(self.handle)))
        name = C.create_string_buffer(128)
        self.check(self.dll.nvmlDeviceGetName(self.handle, name, 128))
        self.name = name.value.decode('utf-8')

    @staticmethod
    def check(code):
        if code:
            raise RuntimeError(f'NVML 오류 코드 {code}; VRAM은 미확인이다.')

    def read(self):
        m = Memory()
        self.check(self.dll.nvmlDeviceGetMemoryInfo(self.handle, C.byref(m)))
        return {'total': m.total, 'used': m.used, 'free': m.free}

    def close(self):
        self.check(self.dll.nvmlShutdown())


class Monitor:
    def __init__(self, nvml, path):
        self.nvml, self.path = nvml, path
        self.stop_event = threading.Event()
        self.phase = 'baseline'
        self.samples, self.errors = [], []
        self.thread = threading.Thread(target=self.run, daemon=True)

    def run(self):
        with self.path.open('w', encoding='utf-8') as f:
            f.write('elapsed_seconds,phase,total_bytes,used_bytes,free_bytes\n')
            start = time.perf_counter()
            while not self.stop_event.is_set():
                try:
                    m = self.nvml.read()
                    row = {'seconds': time.perf_counter() - start, 'phase': self.phase, **m}
                    self.samples.append(row)
                    f.write(f"{row['seconds']:.6f},{self.phase},{m['total']},{m['used']},{m['free']}\n")
                    f.flush()
                except Exception as exc:
                    self.errors.append(str(exc))
                self.stop_event.wait(0.05)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *unused):
        self.stop_event.set()
        self.thread.join(timeout=5)

    def peak(self, phase=None):
        rows = [s['used'] for s in self.samples if phase is None or s['phase'] == phase]
        return max(rows) if rows else None


class Server:
    def __init__(self, cfg, model, projector, output, embedding=False):
        self.cfg, self.model, self.projector, self.output = cfg, model, projector, output
        self.embedding = embedding
        self.proc = None
        self.key = secrets.token_hex(32)
        with socket.socket() as s:
            s.bind(('127.0.0.1', 0))
            self.port = s.getsockname()[1]

    def __enter__(self):
        args = [self.cfg['server'], '-m', str(self.model), '-c', '8192', '-np', '1', '-ngl', '0' if self.embedding else '99',
                '--host', '127.0.0.1', '--port', str(self.port), '--api-key', self.key]
        if self.embedding:
            args += self.cfg['embedding'].get('extra_args', [])
        else:
            args += ['--mmproj', str(self.projector), '--jinja'] + self.cfg.get('extra_args', [])
        save(self.output / 'server-args.json', [x if x != self.key else '<redacted>' for x in args])
        self.log = (self.output / 'server.log').open('w', encoding='utf-8')
        try:
            env = os.environ.copy()
            if self.embedding:
                env['CUDA_VISIBLE_DEVICES'] = ''
            self.proc = subprocess.Popen(args, stdout=self.log, stderr=subprocess.STDOUT, env=env,
                                         creationflags=subprocess.CREATE_NO_WINDOW)
            start = time.perf_counter()
            while time.perf_counter() - start < self.cfg.get('startup_timeout_seconds', 300):
                if self.proc.poll() is not None:
                    raise RuntimeError(f'llama-server 시작 실패: 종료 코드 {self.proc.returncode}')
                try:
                    self.request('/health', None, timeout=2)
                    return self
                except (OSError, RuntimeError, ValueError):
                    time.sleep(0.25)
            raise RuntimeError('서버 준비 시간 초과')
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def request(self, endpoint, body, timeout=None, stream=False):
        connection = http.client.HTTPConnection('127.0.0.1', self.port,
                       timeout=timeout or self.cfg.get('request_timeout_seconds', 180))
        start = time.perf_counter()
        try:
            connection.request('POST' if body is not None else 'GET', endpoint,
                               None if body is None else json.dumps(body, ensure_ascii=False).encode('utf-8'),
                               {'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.key})
            response = connection.getresponse()
            if response.status != 200:
                raise RuntimeError(f'HTTP {response.status}: {response.read(4096).decode("utf-8", errors="replace")}')
            if not stream:
                return json.loads(response.read())
            events, chunks, ttft, first_any = [], [], None, None
            for line in response:
                if time.perf_counter() - start > self.cfg.get('request_timeout_seconds', 180):
                    raise TimeoutError('스트리밍 전체 요청 시간 초과')
                if not line.startswith(b'data:'):
                    continue
                data = line[5:].strip()
                if data == b'[DONE]':
                    break
                event = json.loads(data)
                events.append(event)
                delta = (event.get('choices') or [{}])[0].get('delta', {})
                if (delta.get('content') or delta.get('reasoning_content')) and first_any is None:
                    first_any = time.perf_counter() - start
                if delta.get('content'):
                    if ttft is None:
                        ttft = time.perf_counter() - start
                    chunks.append(delta['content'])
            timings = next((e['timings'] for e in reversed(events) if e.get('timings')), {})
            usage = next((e['usage'] for e in reversed(events) if e.get('usage')), {})
            # Never equate SSE chunks with model tokens.
            tokens = timings.get('predicted_n')
            seconds = (timings.get('predicted_ms') or 0) / 1000
            rate = tokens / seconds if tokens is not None and seconds > 0 else None
            return {'content': ''.join(chunks), 'ttft_seconds': ttft, 'first_any_token_seconds': first_any,
                    'elapsed_seconds': time.perf_counter() - start, 'tokens_per_second': rate,
                    'timings': timings, 'usage': usage, 'events': events}
        finally:
            connection.close()

    def __exit__(self, *unused):
        if self.proc is not None and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=10)
        if hasattr(self, 'log'):
            self.log.close()


def chat(server, content, temperature=1.0, stream=False, **kwargs):
    body = {'messages': [{'role': 'user', 'content': content}], 'temperature': temperature,
            'top_p': 0.95, 'top_k': 64, 'max_tokens': 512, 'seed': 42, 'stream': stream,
            'chat_template_kwargs': {'enable_thinking': server.cfg.get('enable_thinking', False)}, **kwargs}
    if stream:
        body['stream_options'] = {'include_usage': True}
    response = server.request('/v1/chat/completions', body, stream=stream)
    if stream:
        observed = any((e.get('choices') or [{}])[0].get('delta', {}).get('reasoning_content')
                       for e in response.get('events', []))
    else:
        observed = bool((response.get('choices') or [{}])[0].get('message', {}).get('reasoning_content'))
    response['thinking_requested'] = body['chat_template_kwargs']['enable_thinking']
    response['reasoning_observed'] = observed
    return response


def retokenize_output(server, response):
    """Keep the server generation rate separate from the client end-to-end estimate."""
    response['server_tokens_per_second'] = response.get('tokens_per_second')
    response['estimated_tokens_per_second'] = None
    response['retokenized_output_tokens'] = None
    response['estimate_basis'] = 'visible_content_tokens / request_to_stream_end_seconds'
    response['estimate_label'] = '재토큰화 추정치 (첫 토큰 대기 포함)'
    response['rate_source'] = 'server' if response['server_tokens_per_second'] is not None else 'unconfirmed'
    # This duration was captured before the additional tokenizer request.
    elapsed = response['elapsed_seconds']
    content = response['content']
    if not content or not math.isfinite(elapsed) or elapsed <= 0:
        response['tokenizer_error'] = '출력 없음 또는 유효한 스트리밍 측정 시간 없음'
        return response
    try:
        tokenization = server.request('/tokenize', {'content': content, 'add_special': False, 'parse_special': False})
        tokens = tokenization.get('tokens')
        if not isinstance(tokens, list) or not tokens or any(type(t) is not int for t in tokens):
            raise ValueError('tokenize 응답의 tokens가 비어 있거나 정수 목록이 아니다.')
        response['retokenized_output_tokens'] = len(tokens)
        response['estimated_tokens_per_second'] = len(tokens) / elapsed
        if response['server_tokens_per_second'] is None:
            response['tokens_per_second'] = response['estimated_tokens_per_second']
            response['rate_source'] = 'retokenized_estimate'
    except Exception as exc:
        # A metric failure must not turn a successful inference into a failed text gate.
        response['tokenizer_error'] = str(exc)
    return response


def stream_chat(server, content):
    return retokenize_output(server, chat(server, content, stream=True))


def measure(results, output, identifier, fn):
    print(identifier, flush=True)
    start = time.perf_counter()
    try:
        result = {'id': identifier, 'transport_ok': True, 'response': fn()}
    except Exception as exc:
        result = {'id': identifier, 'transport_ok': False, 'error': str(exc)}
    result['elapsed_seconds'] = time.perf_counter() - start
    results.append(result)
    save(output / (identifier + '.json'), result)
    return result


def tool_case(i):
    kind = i % 4
    if kind == 0:
        args = {'extension': '.txt', 'older_than_days': i + 1}
        prompt = f"DemoWorkspace에서 {i + 1}일보다 오래된 .txt 파일을 찾아라. find_files를 한 번 호출하라."
    elif kind == 1:
        args = {'name': f'demo-{i}.txt'}
        prompt = f"DemoWorkspace의 demo-{i}.txt를 read_text_file로 읽어라."
    elif kind == 2:
        args = {'rule': f'rule-{i}'}
        prompt = f"propose_organize_plan에 rule 값을 정확히 rule-{i}로 전달하여 계획만 만들어라."
    else:
        args = {'plan_id': f'approved-{i}'}
        prompt = f"테스트용 승인된 계획 ID approved-{i}를 apply_plan으로 요청하라. 이 테스트는 실제 실행하지 않는다."
    return list(PARAMS)[kind], args, prompt


def grade_tool(response, name, expected):
    message = response.get('choices', [{}])[0].get('message', {})
    calls = message.get('tool_calls') or []
    valid = False
    if len(calls) == 1:
        fn = calls[0].get('function', {})
        try:
            valid = fn.get('name') == name and json.loads(fn.get('arguments', '')) == expected
        except (TypeError, ValueError):
            pass
    return {'exact_call_success': valid, 'content_present': bool(message.get('content')),
            'content_leak_suspected': not calls and bool(message.get('content'))}


def cosine(a, b):
    if len(a) != len(b) or not a or not all(math.isfinite(x) for x in a + b):
        raise ValueError('임베딩 차원 또는 유한 값 확인 실패')
    denom = math.sqrt(sum(x*x for x in a) * sum(x*x for x in b))
    if not denom:
        raise ValueError('영벡터 임베딩')
    return sum(x*y for x, y in zip(a, b)) / denom


def wav_info(path):
    with wave.open(str(path), 'rb') as f:
        seconds = f.getnframes() / f.getframerate()
        if f.getnchannels() != 1 or f.getframerate() != 16000 or f.getsampwidth() != 2 or not (0 < seconds <= 30):
            raise ValueError('16kHz 모노 PCM16, 0초 초과 30초 이하 WAV가 필요하다.')
        return seconds


def run_candidate(cfg, candidate, nvml, out):
    out.mkdir()
    results = []
    model, projector = Path(candidate['model']), Path(candidate['mmproj'])
    meta = {**candidate, 'context': 8192, 'vram_gate_bytes': VRAM_GATE_BYTES,
            'thinking_requested': cfg.get('enable_thinking', False),
            'baseline_vram': nvml.read(), 'manual_quality': '미확인'}
    for key, path in [('model', model), ('mmproj', projector)]:
        if not path.is_file():
            meta['error'] = f'{key} 파일 미확인: {path}'
            save(out / 'summary.json', meta)
            return meta
        meta[key + '_sha256'] = digest(path)
    with Monitor(nvml, out / 'vram.csv') as monitor:
        time.sleep(0.2)
        try:
            monitor.phase = 'loading'
            with Server(cfg, model, projector, out) as server:
                def check_vram():
                    peak = monitor.peak()
                    if peak is not None and peak > VRAM_GATE_BYTES:
                        raise GateConflict(f'총 VRAM 관측 피크 {peak} bytes가 7GiB ({VRAM_GATE_BYTES} bytes) 게이트를 초과했다. 설정 변경은 사용자 결정 대기이다.')
                time.sleep(0.1)
                check_vram()
                monitor.phase = 'text'
                for i in range(3):
                    measure(results, out, f'text-{i+1}', lambda: stream_chat(server, TEXTS[0]))
                    check_vram()
                for i, prompt in enumerate(TEXTS):
                    measure(results, out, f'korean-{i+1:02}', lambda p=prompt: chat(server, p))
                    check_vram()
                for name, prompt in [('receipt', '영수증의 합계 금액을 숫자로 답하라.'),
                                     ('error', '오류 메시지와 원인, 조치 방법을 한국어로 설명하라.'),
                                     ('table', '표의 부서별 1월과 2월 값을 JSON으로 추출하라.')]:
                    monitor.phase = 'image'
                    def image_request(n=name, p=prompt):
                        data = base64.b64encode((ROOT / f'assets/samples/images/{n}.png').read_bytes()).decode()
                        return chat(server, [{'type': 'image_url', 'image_url': {'url': 'data:image/png;base64,' + data}},
                                             {'type': 'text', 'text': p}], temperature=0.2)
                    measure(results, out, 'image-' + name, image_request)
                    check_vram()
                # A separate long-input test; 8K allocated does not imply 8K actually occupied.
                monitor.phase = 'image-long'
                def long_image():
                    prefix = '참고 메모: 이것은 컨텍스트 부하 검증이며 영수증 합계 질문과 무관하다.\n' * 800
                    tokens = server.request('/tokenize', {'content': prefix})['tokens'][:4500]
                    filler = server.request('/detokenize', {'tokens': tokens})['content']
                    data = base64.b64encode((ROOT / 'assets/samples/images/receipt.png').read_bytes()).decode()
                    return chat(server, [{'type': 'image_url', 'image_url': {'url': 'data:image/png;base64,' + data}},
                                         {'type': 'text', 'text': filler + '\n영수증 합계 금액을 답하라.'}], temperature=0.2)
                measure(results, out, 'image-long', long_image)
                check_vram()
                monitor.phase = 'audio'
                for i, audio in enumerate(cfg.get('audio', [])):
                    def audio_request(a=audio):
                        path = Path(a['file'])
                        duration = wav_info(path)
                        result = chat(server, [{'type': 'text', 'text': '한국어 음성을 들리는 그대로 받아써라. 추측하여 보충하지 마라.'},
                                       {'type': 'input_audio', 'input_audio': {'data': base64.b64encode(path.read_bytes()).decode(), 'format': 'wav'}}])
                        return {'duration_seconds': duration, 'reference': a['reference'], 'result': result}
                    measure(results, out, f'audio-{i+1}', audio_request)
                monitor.phase = 'tools'
                for temp in [1.0, 0.2]:
                    for i in range(20):
                        name, expected, prompt = tool_case(i)
                        def tool_request(n=name, e=expected, p=prompt, t=temp):
                            response = chat(server, p, temperature=t, tools=TOOLS, tool_choice='auto')
                            return {'expected_name': n, 'expected_arguments': e, 'result': response,
                                    **grade_tool(response, n, e)}
                        measure(results, out, f'tool-{temp}-{i+1:02}', tool_request)
        except GateConflict as exc:
            meta['error'] = str(exc)
            meta['conflict'] = 'VRAM_GATE_EXCEEDED'
        except Exception as exc:
            meta['error'] = str(exc)
        finally:
            monitor.phase = 'after-stop'
            time.sleep(1)
    meta.update({'sampled_peak_bytes': monitor.peak(), 'image_peak_bytes': monitor.peak('image'),
                 'image_long_peak_bytes': monitor.peak('image-long'), 'nvml_errors': monitor.errors,
                 'nvml_samples': len(monitor.samples), 'after_stop_vram': nvml.read(), 'results': results})
    texts = [r for r in results if r['id'].startswith('text-')]
    images = [r for r in results if r['id'].startswith('image-')]
    auto_ready = (len(texts) == 3 and all(r['transport_ok'] and r['response']['content'].strip() for r in texts)
                  and len(images) == 4 and all(r['transport_ok'] for r in images)
                  and meta['sampled_peak_bytes'] is not None and meta['sampled_peak_bytes'] <= VRAM_GATE_BYTES
                  and not monitor.errors)
    meta['gate'] = '자동 조건 충족; 이미지 사람 평가와 시나리오 결정 대기' if auto_ready else '미통과 또는 측정 부족'
    save(out / 'summary.json', meta)
    return meta


def run_embedding(cfg, nvml, out):
    out.mkdir()
    result = {'repository': cfg['embedding'].get('repository', '미확인'), 'device': 'CPU (-ngl 0)',
              'prompt_format': 'EmbeddingGemma task prefixes', 'results': []}
    model = Path(cfg['embedding']['model'])
    try:
        result['sha256'] = digest(model)
        with Monitor(nvml, out / 'vram.csv') as monitor:
            with Server(cfg, model, None, out, embedding=True) as server:
                def embed(text):
                    response = server.request('/v1/embeddings', {'input': text, 'model': 'local'})
                    return response['data'][0]['embedding']
                docs = [embed('title: 사내 규정 | text: ' + d) for d in DOCS]
                for i, q in enumerate(QUERIES):
                    vector = embed('task: search result | query: ' + q)
                    ranking = sorted(range(len(docs)), key=lambda j: cosine(vector, docs[j]), reverse=True)
                    result['results'].append({'query': q, 'expected_index': i, 'ranking': ranking,
                                               'top1_correct': ranking[0] == i, 'top5_correct': i in ranking[:5]})
            result['sampled_peak_bytes'] = monitor.peak()
            result['nvml_errors'] = monitor.errors
        result['top1'] = sum(r['top1_correct'] for r in result['results'])
        result['top5'] = sum(r['top5_correct'] for r in result['results'])
        result['count'] = len(QUERIES)
        result['status'] = '측정 완료; 검색 품질 수용 기준은 사용자 결정 대기'
    except Exception as exc:
        result['error'] = str(exc)
        result['status'] = '미확인 또는 실패'
    save(out / 'summary.json', result)
    return result


def resolve_config(path):
    cfg = json.loads(path.read_text(encoding='utf-8-sig'))
    def absolute(p):
        return str((path.parent / p).resolve())
    cfg['server'] = absolute(cfg['server'])
    for candidate in cfg['candidates']:
        for key in ['model', 'mmproj']:
            candidate[key] = absolute(candidate[key])
    cfg['embedding']['model'] = absolute(cfg['embedding']['model'])
    for audio in cfg.get('audio', []):
        audio['file'] = absolute(audio['file'])
    names = [c['name'] for c in cfg['candidates']]
    if len(set(names)) != len(names) or any(not n or any(x not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for x in n) for n in names):
        raise ValueError('후보 이름은 중복 없는 영문, 숫자, 하이픈, 밑줄만 허용한다.')
    # Reject overrides that could escape loopback or alter the mandatory experiment.
    allowed = {'--no-webui', '--no-mmproj-offload', '--image-min-tokens', '--image-max-tokens'}
    args = cfg.get('extra_args', [])
    i = 0
    while i < len(args):
        flag = args[i]
        if flag not in allowed:
            raise ValueError(f'extra_args 허용되지 않는 옵션: {flag}')
        i += 1
        if flag in {'--image-min-tokens', '--image-max-tokens'}:
            if i >= len(args) or str(args[i]) not in {'70', '140', '280', '560', '1120'}:
                raise ValueError('이미지 토큰 값은 70,140,280,560,1120 중 하나이다.')
            i += 1
    if cfg['embedding'].get('extra_args', []) != ['--embedding', '--pooling', 'mean']:
        raise ValueError('임베딩 옵션은 --embedding --pooling mean으로 고정한다. 충돌 시 사용자 결정이 필요하다.')
    return cfg


def report(out, cfg, device, candidates, embedding):
    lines = ['# 0단계 검증 스파이크 결과', '', '- [x] 측정 도구 실행',
             '- [ ] 사람 품질 평가와 미검증 항목 확정', '- [ ] 사용자 결정 및 게이트 최종 확정', '',
             f"측정 시각: {dt.datetime.now(dt.timezone(dt.timedelta(hours=9))).isoformat()}",
             f"GPU: {device}", f"런타임 릴리스(사용자 기록): {cfg['release']}",
             f"백엔드(사용자 기록): {cfg['backend']}", '',
             'VRAM은 장치 전체 사용량을 NVML로 50ms 주기로 읽은 관측 최댓값이다. 샘플 사이 순간 피크는 미확인이다.',
             f'사용자 선택 B의 기준은 7GiB = {VRAM_GATE_BYTES:,} bytes이다. GB와 GiB를 함께 기록한다.',
             '첫 토큰은 응답 content의 첫 비어 있지 않은 델타 기준이다. reasoning을 포함한 첫 토큰은 원본에 별도 기록한다.',
             '생성 tok/s는 서버 predicted_n / predicted_ms이다. 별도 추정치는 출력 content를 같은 서버의 /tokenize로 재토큰화한 수 / 요청 시작부터 스트림 종료까지 시간이다.',
             '추정치는 첫 토큰 대기를 포함하며 tokenizer 요청 시간은 제외한다. 숨겨진 사고·특수 토큰은 출력 토큰 수에 포함하지 않는다.',
             '두 속도는 측정 범위가 달라 직접 비교하지 않는다. 생성 20 tok/s 목표의 서버 수치가 없으면 목표 판정은 미확인이다. SSE 청크 수는 사용하지 않는다.', '',
             '| 후보 | 총 VRAM 관측 피크 | 텍스트 첫 토큰 3회 | 서버 생성 tok/s 3회 | 재토큰화 추정 tok/s 3회 (대기 포함) | 게이트 |',
             '|---|---|---|---|---|---|']
    for c in candidates:
        peak = c.get('sampled_peak_bytes')
        memory = '미확인' if peak is None else f'{peak/1e9:.3f} GB / {peak/2**30:.3f} GiB'
        texts = [r for r in c.get('results', []) if r['id'].startswith('text-') and r['transport_ok']]
        def metric(key):
            return ', '.join('미확인' if r['response'].get(key) is None else f"{r['response'][key]:.3f}" for r in texts) or '미확인'
        lines.append(f"| {c['name']} | {memory} | {metric('ttft_seconds')} | {metric('server_tokens_per_second')} | {metric('estimated_tokens_per_second')} | {c.get('gate', '미확인')} |")
    lines += ['', '도구 호출은 temperature별 최초 시도 20회이다. 재시도 성공률이나 실제 파일 정리 성공률을 의미하지 않는다.',
              '| 후보 | temperature | 정확한 이름·인자 성공 | content 누출 의심 |', '|---|---|---|---|']
    for c in candidates:
        for temp in [1.0, 0.2]:
            rows = [r for r in c.get('results', []) if r['id'].startswith(f'tool-{temp}-')]
            success = sum(r.get('response', {}).get('exact_call_success', False) for r in rows)
            leaks = sum(r.get('response', {}).get('content_leak_suspected', False) for r in rows)
            score = f'{success}/20 (측정 {len(rows)}/20)' if len(rows) == 20 else f'미확인 (측정 {len(rows)}/20; 관측 성공 {success}회)'
            leak_score = str(leaks) if rows else '미확인'
            lines.append(f"| {c['name']} | {temp} | {score} | {leak_score} |")
    lines += ['', f"임베딩: {embedding.get('status')}; Top1 {embedding.get('top1', '미확인')}/10, Top5 {embedding.get('top5', '미확인')}/10",
              '', '이미지 의미 정확성, 한국어 응답 10개 품질, 음성 5개 실사용 가능 여부: 미확인이다.',
              '저장소 mmproj 포함 여부와 최신 안정 릴리스는 별도 공식 자료 확인 기록을 참고한다. 이슈 #22786 재현 확정은 도구 원본 응답 검토 전 미확인이다.',
              '오프라인 동작, 외부 연결 수, 강제 종료 정리: 이번 도구의 자동 판정 범위 밖이며 미확인이다.',
              '1단계는 시작하지 않는다. 원본 응답과 manual-review.json을 검토한 뒤 명세서 수정안을 제시한다.']
    (out / 'spike-report.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')


def self_test():
    from http.server import BaseHTTPRequestHandler, HTTPServer
    import tempfile
    assert VRAM_GATE_BYTES == 7_516_192_768
    class CapturingServer:
        cfg = {'enable_thinking': False}
        def request(self, endpoint, body, stream=False):
            assert endpoint == '/v1/chat/completions'
            assert body['chat_template_kwargs'] == {'enable_thinking': False}
            return {'choices': [{'message': {'content': '정답', 'reasoning_content': ''}}]}
    response = chat(CapturingServer(), '답하라.')
    assert response['thinking_requested'] is False and response['reasoning_observed'] is False
    class IgnoringThinkingServer(CapturingServer):
        def request(self, endpoint, body, stream=False):
            return {'choices': [{'message': {'content': '', 'reasoning_content': '사고 모드를 무시했다.'}}]}
    assert chat(IgnoringThinkingServer(), '답하라.')['reasoning_observed'] is True
    good = {'choices': [{'message': {'tool_calls': [{'function': {'name': 'read_text_file', 'arguments': '{"name":"a.txt"}'}}]}}]}
    assert grade_tool(good, 'read_text_file', {'name': 'a.txt'})['exact_call_success']
    assert not grade_tool(good, 'read_text_file', {'name': 'b.txt'})['exact_call_success']
    assert grade_tool({'choices': [{'message': {'content': 'call read_text_file'}}]}, 'read_text_file', {})['content_leak_suspected']
    assert abs(cosine([1., 0.], [1., 0.]) - 1) < 1e-9
    assert cosine([1., 0.], [0., 1.]) == 0
    for i in range(20):
        name, args, prompt = tool_case(i)
        assert set(args) == set(PARAMS[name]) and prompt
    assert len(TEXTS) == 10 and len(QUERIES) == len(DOCS) == 10
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            if self.path == '/tokenize':
                assert body == {'content': '가나', 'add_special': False, 'parse_special': False}
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps({'tokens': [100, 101, 102, 103, 104]}).encode())
                return
            assert body['stream'] is True
            if self.path == '/failure':
                self.send_response(500)
                self.end_headers()
                self.wfile.write(b'test failure')
                return
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.end_headers()
            for value in [
                {'choices': [{'delta': {'role': 'assistant'}}]},
                {'choices': [{'delta': {'reasoning_content': '생각'}}]},
                {'choices': [{'delta': {'content': '가'}}]},
                {'choices': [{'delta': {'content': '나'}}]},
                {'choices': [], 'timings': {'predicted_n': 20, 'predicted_ms': 1000}}
            ]:
                self.wfile.write(('data: ' + json.dumps(value) + '\n\n').encode())
                self.wfile.flush()
            self.wfile.write(b'data: [DONE]\n\n')

    fake = HTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=fake.serve_forever, daemon=True)
    thread.start()
    try:
        client = Server({'request_timeout_seconds': 5}, None, None, None)
        client.port = fake.server_port
        response = client.request('/stream', {'stream': True}, stream=True)
        assert response['content'] == '가나'
        assert response['tokens_per_second'] == 20  # Not the two content chunks.
        assert response['first_any_token_seconds'] <= response['ttft_seconds']
        streamed_elapsed = response['elapsed_seconds']
        response = retokenize_output(client, response)
        assert response['retokenized_output_tokens'] == 5
        assert response['elapsed_seconds'] == streamed_elapsed
        assert response['estimated_tokens_per_second'] == 5 / streamed_elapsed
        assert response['tokens_per_second'] == 20 and response['rate_source'] == 'server'
        fallback = retokenize_output(client, {'content': '가나', 'elapsed_seconds': 2., 'tokens_per_second': None})
        assert fallback['tokens_per_second'] == 2.5 and fallback['rate_source'] == 'retokenized_estimate'
        assert fallback['server_tokens_per_second'] is None
        try:
            client.request('/failure', {'stream': True}, stream=True)
            raise AssertionError('HTTP 오류를 성공으로 처리했다.')
        except RuntimeError as exc:
            assert '500' in str(exc)
    finally:
        fake.shutdown()
        fake.server_close()
        thread.join(timeout=5)
    class BrokenTokenizer:
        def request(self, *args):
            raise RuntimeError('HTTP 404: tokenize unavailable')
    failed_metric = retokenize_output(BrokenTokenizer(), {'content': '정상 답변', 'elapsed_seconds': 2., 'tokens_per_second': None})
    assert failed_metric['content'] == '정상 답변' and failed_metric['tokens_per_second'] is None
    assert failed_metric['rate_source'] == 'unconfirmed' and '404' in failed_metric['tokenizer_error']
    retained = retokenize_output(BrokenTokenizer(), {'content': '정상 답변', 'elapsed_seconds': 2., 'tokens_per_second': 20.})
    assert retained['tokens_per_second'] == 20. and retained['rate_source'] == 'server'
    class InvalidTokenizer:
        def request(self, *args):
            return {'tokens': [True]}
    invalid = retokenize_output(InvalidTokenizer(), {'content': '답변', 'elapsed_seconds': 2., 'tokens_per_second': None})
    assert invalid['estimated_tokens_per_second'] is None and 'tokenizer_error' in invalid
    class FakeNVML:
        def read(self):
            return {'total': 8_000_000_000, 'used': 2_000_000_000, 'free': 6_000_000_000}
    with tempfile.TemporaryDirectory(prefix='localmind-spike-test-') as folder:
        with Monitor(FakeNVML(), Path(folder) / 'vram.csv') as monitor:
            time.sleep(0.16)
        assert len(monitor.samples) >= 2 and not monitor.errors
        assert monitor.peak() == 2_000_000_000
    print('로컬 함수 자체 검증 통과이다. 실제 모델 추론과 성능은 미확인이다.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', type=Path)
    parser.add_argument('--preflight', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.config:
        parser.error('--config가 필요하다.')
    cfg = resolve_config(args.config.resolve())
    nvml = NVML(cfg.get('gpu_index', 0))
    try:
        if args.preflight:
            print(json.dumps({'gpu': nvml.name, 'memory': nvml.read(),
                              'server_exists': Path(cfg['server']).is_file(),
                              'candidates': [{**c, 'model_exists': Path(c['model']).is_file(),
                                              'projector_exists': Path(c['mmproj']).is_file()} for c in cfg['candidates']]},
                             ensure_ascii=False, indent=2))
            return
        if not Path(cfg['server']).is_file():
            raise FileNotFoundError('llama-server.exe 미확인이다. config의 경로를 수정한다.')
        stamp = dt.datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3)
        out = ROOT / 'docs' / 'spike-runs' / stamp
        out.mkdir(parents=True)
        save(out / 'config.json', cfg)
        inputs = [ROOT / f'assets/samples/images/{name}.png' for name in ['receipt', 'error', 'table']]
        inputs += [Path(a['file']) for a in cfg.get('audio', [])]
        save(out / 'input-sha256.json', {str(p): digest(p) if p.is_file() else '미확인: 파일 없음' for p in inputs})
        version = subprocess.run([cfg['server'], '--version'], capture_output=True, text=True,
                                 encoding='utf-8', errors='replace', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        help_result = subprocess.run([cfg['server'], '--help'], capture_output=True, text=True,
                                    encoding='utf-8', errors='replace', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        (out / 'runtime-version.txt').write_text(version.stdout + version.stderr, encoding='utf-8')
        (out / 'runtime-help.txt').write_text(help_result.stdout + help_result.stderr, encoding='utf-8')
        files = [Path(cfg['server'])] + sorted(Path(cfg['server']).parent.glob('*.dll'))
        save(out / 'runtime-sha256.json', {p.name: digest(p) for p in files})
        candidates = []
        for candidate in cfg['candidates']:
            measured = run_candidate(cfg, candidate, nvml, out / candidate['name'])
            candidates.append(measured)
            if measured.get('conflict'):
                for pending in cfg['candidates'][len(candidates):]:
                    candidates.append({**pending, 'gate': '미확인: 선행 VRAM 충돌로 실행 보류'})
                break
        embedding = ({'status': '미확인: 선행 VRAM 충돌로 실행 보류'}
                     if any(c.get('conflict') for c in candidates) else run_embedding(cfg, nvml, out / 'embedding'))
        review = {c['name']: {'images': {'receipt_total_9000': None, 'error_correct': None, 'table_correct': None,
                                       'long_receipt_total_9000': None},
                             'korean_usable_10': [None]*10, 'audio_usable_5': [None]*5,
                             'notes': ''} for c in cfg['candidates']}
        save(out / 'manual-review.json', review)
        report(out, cfg, nvml.name, candidates, embedding)
        print(f'결과 경로: {out}')
    finally:
        nvml.close()


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('사용자 중단이다. 완료되지 않은 측정은 미확인이다.', file=sys.stderr)
        sys.exit(130)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
