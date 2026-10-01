"""Read-only artifact review, no inference and no mutation of raw measurements."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAIN = ROOT / 'docs/spike-runs/20260930-160024-bc5c95'
AUDIO = ROOT / 'docs/spike-runs/20260930-160647-audio-82d91b'


def review():
    reviewed = {}
    for name in ['google-qat', 'ggml-org']:
        summary = json.loads((MAIN / name / 'summary.json').read_text(encoding='utf-8'))
        rows = summary['results']
        reasoning, truncated, tools = [], [], []
        for row in rows:
            response = row.get('response', {})
            actual = response.get('result', response)
            if actual.get('reasoning_observed'):
                reasoning.append(row['id'])
            choices = actual.get('choices') or []
            if choices and choices[0].get('finish_reason') == 'length':
                truncated.append(row['id'])
            if row['id'].startswith('tool-') and not response.get('exact_call_success'):
                calls = (choices[0].get('message', {}).get('tool_calls') or []) if choices else []
                dot_only = False
                if len(calls) == 1:
                    fn = calls[0].get('function', {})
                    try:
                        args = json.loads(fn.get('arguments', ''))
                        expected = response['expected_arguments']
                        dot_only = (fn.get('name') == 'find_files' and args.get('extension') == 'txt'
                                    and expected.get('extension') == '.txt'
                                    and {**args, 'extension': '.txt'} == expected)
                    except (KeyError, ValueError):
                        pass
                tools.append({'id':row['id'], 'extension_dot_only':dot_only})
        reviewed[name] = {'requests':len(rows), 'peak_bytes':summary['sampled_peak_bytes'],
                          'reasoning_output_ids':reasoning, 'truncated_ids':truncated,
                          'failed_exact_tool_calls':tools,
                          'all_exact_failures_extension_dot_only':bool(tools) and all(t['extension_dot_only'] for t in tools)}
    return reviewed


if __name__ == '__main__':
    print(json.dumps(review(), ensure_ascii=False, indent=2))
