"""Fixed, resource-bounded Server100 CPU LoRA job. No untrusted code or paths."""
import ctypes
from ctypes import wintypes
import gc
import json
import math
import os
from pathlib import Path
import random
import subprocess
import time
import urllib.request
import uuid
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from training_core import SYSTEM, atomic, compact_evidence, digest, promotion_allowed, semantic_score, split_examples, target, validated_example

ROOT = Path(r'E:\LLM\training')
STATE = ROOT / 'state'
JOB_HANDLE = None
RUN_JOB = None


def status(state, step, **values):
    atomic(STATE / 'training-status.json', {'state': state, 'step': step, 'utc': time.time(), 'pid': os.getpid(), **values})


def restrict_process():
    """An inherited Windows job caps this worker and its candidate subprocesses."""
    global JOB_HANDLE
    if os.name != 'nt' or os.environ.get('COMPUTERNAME') != 'DCDB1':
        raise RuntimeError('Pinned training host required')
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    class Basic(ctypes.Structure):
        _fields_ = [('per_process', ctypes.c_int64), ('per_job', ctypes.c_int64), ('flags', wintypes.DWORD),
                    ('minimum_ws', ctypes.c_size_t), ('maximum_ws', ctypes.c_size_t), ('active_processes', wintypes.DWORD),
                    ('affinity', ctypes.c_size_t), ('priority', wintypes.DWORD), ('scheduling', wintypes.DWORD)]
    class Io(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in ('r', 'w', 'o', 'rb', 'wb', 'ob')]
    class Extended(ctypes.Structure):
        _fields_ = [('basic', Basic), ('io', Io), ('process_memory', ctypes.c_size_t), ('job_memory', ctypes.c_size_t),
                    ('peak_process', ctypes.c_size_t), ('peak_job', ctypes.c_size_t)]
    class Cpu(ctypes.Structure):
        _fields_ = [('flags', wintypes.DWORD), ('rate', wintypes.DWORD)]
    JOB_HANDLE = kernel.CreateJobObjectW(None, None)
    limits = Extended(); limits.basic.flags = 0x2000 | 0x200; limits.job_memory = 24 * 1024**3
    cpu = Cpu(0x1 | 0x4, 2000)
    if not JOB_HANDLE or not kernel.SetInformationJobObject(JOB_HANDLE, 9, ctypes.byref(limits), ctypes.sizeof(limits)) \
            or not kernel.SetInformationJobObject(JOB_HANDLE, 15, ctypes.byref(cpu), ctypes.sizeof(cpu)) \
            or not kernel.AssignProcessToJobObject(JOB_HANDLE, kernel.GetCurrentProcess()):
        raise ctypes.WinError(ctypes.get_last_error())
    import psutil
    psutil.Process().nice(psutil.IDLE_PRIORITY_CLASS)


def resource_gate(initial=False):
    import psutil
    free = psutil.virtual_memory().available / 1024**3
    if free < (24 if initial else 18):
        raise RuntimeError('MemorySafety')
    if psutil.cpu_percent(interval=2) > 80:
        raise RuntimeError('CpuSafety')
    # Read-only local routing check, with normal Host header. Never modify Apache.
    for language in ('en', 'hi'):
        result = subprocess.run([r'C:\Windows\System32\curl.exe', '--silent', '--noproxy', '*', '--connect-timeout', '2',
                                 '--max-time', '5', '--header', 'Host: eeslindia.org', '--output', 'NUL', '--write-out', '%{http_code}',
                                 f'http://127.0.0.1/{language}/'], capture_output=True, timeout=12)
        if result.returncode or result.stdout != b'301':
            raise RuntimeError('WebsiteSafety')


def load_examples():
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    import msvcrt
    # PHP flock on Windows and this byte-range lock share LockFileEx semantics.
    lock = (STATE / 'dataset.lock').open('a+b')
    try:
        lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        revision = json.loads((STATE / 'dataset-revision.json').read_text())
        revoked_path = STATE / 'revocations.json'
        revoked = json.loads(revoked_path.read_text()) if revoked_path.exists() else {}
        key = bytes.fromhex(Path(r'E:\LLM\secrets\learning-data-key.txt').read_text().strip())
        examples = []
        for path in sorted((ROOT / 'datasets').glob('*/*.enc')):
            envelope = json.loads(path.read_text())
            if envelope['subjectRef'] in revoked:
                continue
            import base64
            aad = f"EESL-LLM-learning:v1:{envelope['clientId']}:{envelope['sourceHash']}:{envelope['subjectRef']}".encode()
            data = AESGCM(key).decrypt(base64.b64decode(envelope['iv']), base64.b64decode(envelope['ciphertext']) + base64.b64decode(envelope['tag']), aad)
            example = validated_example(json.loads(data))
            if example['subjectRef'] != envelope['subjectRef'] or example['clientId'] != envelope['clientId']:
                raise ValueError('Envelope identity mismatch')
            examples.append(dict(example, _reviewedUtc=envelope['updatedUtc']))
        return revision['revision'], examples
    finally:
        lock.close()


def inference(port, messages, key, max_tokens=128):
    request = urllib.request.Request(f'http://127.0.0.1:{port}/v1/chat/completions',
        data=json.dumps({'model': 'hrms-local', 'messages': messages, 'max_tokens': max_tokens,
                         'temperature': 0, 'stream': False, 'chat_template_kwargs': {'enable_thinking': False}}).encode(),
        headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + key})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=240) as reply:
        value = json.load(reply)
    if value['choices'][0].get('finish_reason') != 'stop':
        return ''
    return value['choices'][0]['message']['content'] or ''


def serving_evaluation(adapter, holdout, directory):
    # Candidate port is loopback only. Do not touch the live 8091 listener.
    import socket
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 8092))
    arguments = json.loads(Path(r'E:\LLM\scripts\launch-arguments.json').read_text(encoding='utf-8-sig'))
    for flag, replacement in (('--port', '8092'), ('--threads', '1'), ('--threads-batch', '1'), ('--ctx-size', '2048')):
        arguments[arguments.index(flag) + 1] = replacement
    key = Path(r'E:\LLM\secrets\backend-api-key.txt').read_text().strip()
    results = []
    # A baseline and adapted server run sequentially in the capped job; the live
    # inference service remains available for recruiters throughout evaluation.
    for selected in (None, adapter):
        command = [r'E:\LLM\runtime\b10964\llama-server.exe', *arguments]
        if selected:
            command += ['--lora', str(selected)]
        with (directory / ('candidate.log' if selected else 'baseline.log')).open('wb') as log:
            process = subprocess.Popen(command, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                deadline = time.monotonic() + 600
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                while True:
                    if process.poll() is not None:
                        raise RuntimeError('CandidateLoadFailed')
                    try:
                        with opener.open('http://127.0.0.1:8092/health', timeout=5) as reply:
                            if reply.status == 200:
                                break
                    except OSError:
                        pass
                    if time.monotonic() > deadline:
                        raise RuntimeError('CandidateLoadTimeout')
                    time.sleep(3)
                metrics = []
                for example in holdout[:4]:
                    resource_gate()
                    answer = inference(8092, [{'role': 'system', 'content': SYSTEM},
                        {'role': 'user', 'content': example['sourceText']}], key)
                    metrics.append(semantic_score(answer, target(example), example['sourceText']))
                # General canaries guard against damage outside resume extraction.
                for question, expected in [('Reply with only the number: 2 + 2 =', '4'),
                                           ('केवल यह शब्द लिखें: नमस्ते', 'नमस्ते')]:
                    answer = inference(8092, [{'role': 'user', 'content': question}], key, 16)
                    metrics.append((float(expected in answer), bool(answer)))
                results.append(metrics)
            finally:
                # Only this exact Popen-owned process is terminated. Its launcher
                # child shares the kill-on-close job and is checked by port health.
                import psutil
                children = []
                if process.poll() is None:
                    parent = psutil.Process(process.pid)
                    children = [child for child in parent.children(recursive=True)
                                if child.exe() and str(Path(child.exe()).parent).lower() == r'e:\llm\runtime\b10964']
                    process.terminate()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=10)
                for child in children:
                    try:
                        child.terminate(); child.wait(timeout=15)
                    except psutil.NoSuchProcess:
                        pass
        time.sleep(2)
    return results


def main():
    import msvcrt
    global process_start, RUN_JOB
    process_start = time.time()
    os.environ.update({'HF_HUB_OFFLINE': '1', 'TRANSFORMERS_OFFLINE': '1', 'CUDA_VISIBLE_DEVICES': '',
        'HF_HOME': str(ROOT / 'cache' / 'huggingface'), 'TEMP': str(ROOT / 'tmp'), 'TMP': str(ROOT / 'tmp'),
        'OMP_NUM_THREADS': '1', 'MKL_NUM_THREADS': '1', 'TOKENIZERS_PARALLELISM': 'false'})
    STATE.mkdir(exist_ok=True)
    lock = (STATE / 'worker.lock').open('a+b')
    try:
        lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        return
    restrict_process()
    resource_gate(initial=True)
    setup = json.loads(Path(r'E:\LLM\logs\training-setup.json').read_text(encoding='utf-8-sig'))
    if setup['State'] != 'Ready':
        status('Waiting', 'EnvironmentNotReady'); return
    revision, examples = load_examples()
    if len(examples) < 16:
        status('WaitingForExamples', 'Minimum16DistinctVerifiedSources', exampleCount=len(examples)); return
    request_path = STATE / 'train-request.json'
    request_hash = digest(request_path) if request_path.exists() else ''
    last_path = STATE / 'last-attempt.json'
    recovery = None
    recovery_path = STATE / 'resume-evaluation.json'
    if recovery_path.exists() and last_path.exists():
        proposed = json.loads(recovery_path.read_text())
        prior = json.loads(last_path.read_text())
        if (proposed.get('verified') is True and proposed.get('jobId') == prior.get('jobId')
                and proposed.get('datasetRevision') == revision and proposed.get('completedSteps') == 8
                and str(uuid.UUID(proposed['jobId'])) == proposed['jobId']):
            active_path = STATE / 'active-adapter.json'
            active = json.loads(active_path.read_text()) if active_path.exists() else {}
            if active.get('jobId') == proposed['jobId']:
                status('Failed', 'ActiveCheckpointRecoveryRefused', jobId=proposed['jobId']); return
            if proposed.get('attempts', 0) >= 3:
                status('Failed', 'CheckpointRecoveryRetryBudgetReached', jobId=proposed['jobId']); return
            checkpoint = ROOT / 'adapters' / proposed['jobId'] / 'adapter_model.safetensors'
            original = checkpoint.with_name('recovered-original.safetensors')
            if original.exists():
                checkpoint = original
            if digest(checkpoint) != proposed.get('checkpointSha256'):
                raise ValueError('Recovered checkpoint integrity failed')
            if not original.exists():
                import shutil
                shutil.copyfile(checkpoint, original)
            proposed['attempts'] = proposed.get('attempts', 0) + 1
            atomic(recovery_path, proposed)
            recovery = proposed
    retrying = False
    attempt = 1
    if last_path.exists() and recovery is None:
        last = json.loads(last_path.read_text())
        if last['revision'] == revision and last['requestHash'] == request_hash:
            previous = json.loads((STATE / 'training-status.json').read_text()) if (STATE / 'training-status.json').exists() else {}
            if previous.get('state') == 'Running':
                status('Failed', 'PreviousWorkerExited', jobId=last['jobId'])
                return
            if previous.get('state') != 'SafetyWait' or previous.get('jobId') != last['jobId'] or time.time() < previous.get('retryAfterUtc', 0) or last.get('attempt', 1) >= 3:
                return
            retrying = True; attempt = last.get('attempt', 1) + 1
        if not retrying and revision - last['revision'] < 8 and len(examples) - last.get('exampleCount', 0) < 8 and request_hash == last['requestHash']:
            return
    from transformers import AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(ROOT / 'base', trust_remote_code=False, local_files_only=True)
    def tokenized(example):
        prefix = tokenizer.apply_chat_template([{'role': 'system', 'content': SYSTEM},
            {'role': 'user', 'content': example['sourceText']}], tokenize=False, add_generation_prompt=True, enable_thinking=False)
        return (tokenizer.encode(prefix, add_special_tokens=False),
                tokenizer.encode(json.dumps(target(example), separators=(',', ':')) + tokenizer.eos_token, add_special_tokens=False))
    eligible = [value for example in examples if (value := compact_evidence(example, lambda row: sum(map(len, tokenized(row))) <= 512))]
    if len(eligible) < 16:
        status('WaitingForExamples', 'Minimum16CpuEligibleVerifiedSources', exampleCount=len(examples), eligibleCount=len(eligible)); return
    try:
        train, holdout = split_examples(eligible)
    except ValueError:
        status('WaitingForExamples', 'Minimum16DistinctVerifiedSources', exampleCount=len(examples)); return
    holdout = holdout[:4]
    job_id = recovery['jobId'] if recovery else str(uuid.uuid4())
    RUN_JOB = job_id
    directory = ROOT / 'adapters' / job_id
    directory.mkdir(exist_ok=bool(recovery))
    atomic(last_path, {'revision': revision, 'requestHash': request_hash, 'jobId': job_id, 'exampleCount': len(examples), 'attempt': attempt})
    status('Running', 'LoadingPinnedBase', jobId=job_id, datasetRevision=revision, trainCount=len(train), holdoutCount=len(holdout))
    os.environ['HF_HUB_OFFLINE'] = '1'; os.environ['TRANSFORMERS_OFFLINE'] = '1'
    os.environ['HF_HOME'] = str(ROOT / 'cache' / 'huggingface')
    os.environ['TEMP'] = str(ROOT / 'tmp'); os.environ['TMP'] = str(ROOT / 'tmp')
    import torch
    from transformers import AutoConfig, AutoTokenizer, Qwen3_5ForCausalLM, GgufConfig
    from peft import LoraConfig, get_peft_model, set_peft_model_state_dict
    torch.set_num_threads(1); torch.set_num_interop_threads(1)
    torch.manual_seed(42); random.seed(42)
    config = AutoConfig.from_pretrained(ROOT / 'base', trust_remote_code=False, local_files_only=True).text_config
    serving_base_hash = digest(Path(r'E:\LLM\Qwen3.5-4B-Q4_K_M.gguf'))
    model, loading = Qwen3_5ForCausalLM.from_pretrained(Path(r'E:\LLM'), config=config, dtype=torch.float32,
        gguf_file='Qwen3.5-4B-Q4_K_M.gguf', quantization_config=GgufConfig(dequantize=True),
        local_files_only=True, trust_remote_code=False, attn_implementation='eager', output_loading_info=True)
    missing = [key for key in loading.get('missing_keys', []) if not key.startswith('mtp.')]
    if missing:
        raise RuntimeError('TextBaseWeightsMissing')
    layer = max(i for i, kind in enumerate(config.layer_types) if kind == 'full_attention')
    model = get_peft_model(model, LoraConfig(r=4, lora_alpha=8, lora_dropout=0.0, bias='none',
        task_type='CAUSAL_LM', target_modules=[f'model.layers.{layer}.self_attn.v_proj']))
    optimizer = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad), lr=0.0002)
    if recovery:
        from safetensors.torch import load_file
        recovered = load_file(str(directory / 'recovered-original.safetensors'))
        recovered = {key.replace('model.model.language_model.', 'model.model.'): value for key, value in recovered.items()}
        result = set_peft_model_state_dict(model, recovered, adapter_name='default')
        if result.unexpected_keys or any('.lora_' in key for key in result.missing_keys):
            raise ValueError('Recovered adapter does not match the text model')
        del recovered
    def loss_for(example):
        prefix_ids, answer_ids = tokenized(example)
        if len(prefix_ids) + len(answer_ids) > 512:
            raise ValueError('Professional example exceeds fixed CPU token budget')
        ids = torch.tensor([prefix_ids + answer_ids])
        outputs = model(input_ids=ids, use_cache=False, logits_to_keep=len(answer_ids) + 1)
        logits = outputs.logits[:, :-1].float().reshape(-1, config.vocab_size)
        return torch.nn.functional.cross_entropy(logits, torch.tensor(answer_ids))
    model.eval()
    status('Running', 'HeldOutBaselineLoss', jobId=job_id, datasetRevision=revision, holdoutCount=len(holdout))
    with torch.no_grad(), model.disable_adapter():
        baseline_loss = sum(float(loss_for(row)) for row in holdout) / len(holdout)
    losses = None if recovery else []
    model.train()
    for step in range(0 if recovery else 8):
        resource_gate()
        optimizer.zero_grad(set_to_none=True)
        loss = loss_for(train[step % len(train)])
        if not torch.isfinite(loss):
            raise RuntimeError('NonfiniteTrainingLoss')
        loss.backward(); torch.nn.utils.clip_grad_norm_((p for p in model.parameters() if p.requires_grad), 1.0)
        optimizer.step(); losses.append(float(loss.detach()))
        status('Running', 'CpuLoRA', jobId=job_id, datasetRevision=revision, completedSteps=step + 1, totalSteps=8)
    model.save_pretrained(directory, safe_serialization=True)
    model.eval()
    status('Running', 'HeldOutCandidateLoss', jobId=job_id, datasetRevision=revision, completedSteps=8)
    with torch.no_grad():
        candidate_loss = sum(float(loss_for(row)) for row in holdout) / len(holdout)
    atomic(directory / 'training-metrics.json', {'completedSteps': 8, 'baselineLoss': baseline_loss,
        'candidateLoss': candidate_loss, 'trainingLosses': losses, 'recoveredCheckpoint': bool(recovery)})
    if not any(torch.count_nonzero(value).item() for key, value in model.state_dict().items() if '.lora_B.' in key):
        raise RuntimeError('AdapterWeightsDidNotChange')
    model.save_pretrained(directory, safe_serialization=True)
    del optimizer, model; gc.collect()
    lock_config = json.loads((ROOT / 'training-lock.json').read_text(encoding='utf-8-sig'))
    status('Running', 'ConvertingGGUF', jobId=job_id, completedSteps=8)
    from export_value_lora import export_value_lora
    adapter = export_value_lora(directory)
    status('Running', 'HeldOutServingEvaluation', jobId=job_id, completedSteps=8)
    baseline, candidate = serving_evaluation(adapter, holdout, directory)
    current_revision, current_examples = load_examples()
    if digest(Path(r'E:\LLM\Qwen3.5-4B-Q4_K_M.gguf')) != serving_base_hash:
        raise RuntimeError('ServingBaseChangedDuringTraining')
    subjects = sorted({row['subjectRef'] for row in train + holdout})
    current_subjects = {row['subjectRef'] for row in current_examples}
    passed = (current_revision == revision and promotion_allowed(baseline, candidate, baseline_loss, candidate_loss)
              and set(subjects).issubset(current_subjects))
    manifest = {'schemaVersion': 1, 'jobId': job_id, 'baseRevision': lock_config['baseRevision'], 'datasetRevision': revision,
        'trainingBaseMode': 'serving-gguf-fp32',
        'recoveredCheckpoint': bool(recovery),
        'exporterSha256': digest(Path(__file__).with_name('export_value_lora.py')),
        'subjects': subjects, 'adapterSha256': digest(adapter), 'baseGgufSha256': serving_base_hash,
        'completedSteps': 8, 'trainCount': len(train), 'holdoutCount': len(holdout), 'trainingLosses': losses,
        'baselineLoss': baseline_loss, 'candidateLoss': candidate_loss, 'baselineScores': baseline, 'candidateScores': candidate,
        'evaluationPassed': passed, 'createdUtc': time.time()}
    atomic(directory / 'manifest.json', manifest)
    if recovery:
        atomic(STATE / 'completed-resume-evaluation.json', recovery)
        recovery_path.rename(STATE / ('resume-evaluation-'+job_id+'.completed.json'))
    if passed:
        active = STATE / 'active-adapter.json'
        if active.exists():
            atomic(STATE / 'previous-adapter.json', json.loads(active.read_text()))
        atomic(active, {'schemaVersion': 1, 'jobId': job_id, 'manifestSha256': digest(directory / 'manifest.json')})
    status('Evaluated' if passed else 'Rejected', 'ApprovedForActivation' if passed else 'QualityGatePreservedCurrentModel',
           jobId=job_id, completedSteps=8, datasetRevision=revision, evaluationPassed=passed)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Persist no resume text, model response, key or arbitrary exception text.
        if isinstance(error, RuntimeError) and str(error) in ('CpuSafety', 'MemorySafety', 'WebsiteSafety'):
            status('SafetyWait', str(error), jobId=RUN_JOB, retryAfterUtc=time.time() + 600)
        else:
            status('Failed', type(error).__name__, jobId=RUN_JOB)
        import traceback
        frames = traceback.extract_tb(error.__traceback__)
        failure = {'utc': time.time(), 'type': type(error).__name__,
            'line': frames[-1].lineno if frames else 0, 'frames': [{'module': Path(frame.filename).name, 'line': frame.lineno, 'function': frame.name} for frame in frames[-8:]]}
        atomic(STATE / 'last-training-failure.json', failure)
        atomic(ROOT / 'jobs' / (str(uuid.uuid4()) + '-failure.json'), failure)
        raise SystemExit(1)
