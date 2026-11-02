"""Submit reviewed professional JSONL examples; credential file is never logged."""
import argparse
import json
from pathlib import Path
import urllib.request
from training_core import validated_example


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path)
    parser.add_argument('--key-file', type=Path, default=Path(r'D:\AllCreds\EESL-LLM-learning-100.key'))
    parser.add_argument('--train', action='store_true')
    args = parser.parse_args()
    key = args.key_file.read_text().strip()
    if len(key) != 64 or any(ch not in '0123456789abcdef' for ch in key):
        raise ValueError('Invalid dedicated learning credential')
    examples = []
    if args.input:
        for line in args.input.read_text(encoding='utf-8-sig').splitlines():
            if line.strip():
                examples.append(validated_example(json.loads(line)))
        if len(examples) > 10000:
            raise ValueError('Dataset limit exceeded')
    def request(body=None):
        data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
        if data and len(data) > 1048576:
            raise ValueError('Batch exceeds gateway limit')
        req = urllib.request.Request('https://eeslindia.org/llm-learning.php', data=data,
            headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
        # Keep the dedicated credential on the pinned HTTPS route.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None
        with urllib.request.build_opener(NoRedirect()).open(req, timeout=35) as response:
            raw = response.read(4097)
            if len(raw) > 4096:
                raise ValueError('Invalid gateway response')
            result = json.loads(raw)
        print(json.dumps(result, ensure_ascii=True))
    for offset in range(0, len(examples), 25):
        request({'action': 'ingest', 'examples': examples[offset:offset + 25]})
    if args.train:
        request({'action': 'train'})
    request()


if __name__ == '__main__':
    main()
