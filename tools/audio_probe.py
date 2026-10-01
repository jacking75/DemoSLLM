"""Supplementary local synthetic-audio API probe; never a human-speech quality gate."""
import argparse
import base64
import datetime as dt
import json
from pathlib import Path
import secrets
import time
import wave
import spike


def pad_samples(cfg):
    for audio in cfg['audio']:
        path = Path(audio['file'])
        with wave.open(str(path), 'rb') as f:
            params = f.getparams()
            frames = f.readframes(f.getnframes())
        if params.nchannels != 1 or params.framerate != 16000 or params.sampwidth != 2:
            raise ValueError('PCM16 16kHz mono input required')
        target = 30 * 16000 * 2
        if len(frames) > target:
            raise ValueError('Synthetic speech exceeds 30 seconds; preserving original')
        if len(frames) < target:
            with wave.open(str(path), 'wb') as f:
                f.setparams(params)
                f.writeframes(frames + b'\x00' * (target - len(frames)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--pad-only', action='store_true')
    args = parser.parse_args()
    cfg = spike.resolve_config(spike.ROOT / 'tools/spike-audio-probe.json')
    if args.pad_only:
        pad_samples(cfg)
        print('합성 WAV 5개를 무음으로 30초까지 패딩했다. 실제 사용자 발화 30초 검증은 아니다.')
        return
    out = spike.ROOT / 'docs/spike-runs' / (dt.datetime.now().strftime('%Y%m%d-%H%M%S') + '-audio-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    spike.save(out / 'config.json', cfg)
    spike.save(out / 'input-sha256.json', {a['file']: spike.digest(a['file']) for a in cfg['audio']})
    nvml = spike.NVML(cfg['gpu_index'])
    summaries = []
    try:
        for candidate in cfg['candidates']:
            folder = out / candidate['name']
            folder.mkdir()
            results = []
            summary = {'candidate': candidate['name'], 'source': cfg['audio_source'],
                       'real_user_speech_quality': '미확인', 'results': results}
            with spike.Monitor(nvml, folder / 'vram.csv') as monitor:
                try:
                    with spike.Server(cfg, candidate['model'], candidate['mmproj'], folder) as server:
                        monitor.phase = 'audio'
                        for i, audio in enumerate(cfg['audio']):
                            def request(a=audio):
                                duration = spike.wav_info(a['file'])
                                data = base64.b64encode(Path(a['file']).read_bytes()).decode()
                                response = spike.chat(server, [
                                    {'type':'text', 'text':'한국어 음성을 들리는 그대로 받아써라. 추측하여 보충하지 마라.'},
                                    {'type':'input_audio', 'input_audio':{'data':data, 'format':'wav'}}])
                                return {'duration_seconds':duration, 'reference':a['reference'], 'result':response}
                            spike.measure(results, folder, f'audio-{i+1}', request)
                            if monitor.peak() is not None and monitor.peak() > spike.VRAM_GATE_BYTES:
                                raise spike.GateConflict('합성 오디오 검증에서 7GiB 초과; 설정 변경은 사용자 결정 대기이다.')
                except Exception as exc:
                    summary['error'] = str(exc)
                finally:
                    monitor.phase = 'after-stop'
                    time.sleep(1)
            summary.update({'sampled_peak_bytes':monitor.peak(), 'nvml_errors':monitor.errors})
            spike.save(folder / 'summary.json', summary)
            summaries.append(summary)
            if summary.get('error'):
                break
        spike.save(out / 'summary.json', summaries)
        print('합성 오디오 경로 검증 결과: ' + str(out))
    finally:
        nvml.close()


if __name__ == '__main__':
    main()
