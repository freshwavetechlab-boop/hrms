# Verified resume learning on Server100

The deployment is confined to the EESL LLM installation on DCDB1 (`10.10.91.100`), its LLM-only PHP routes, scheduled tasks and `E:/LLM` storage. WordPress configuration, Apache/PHP global configuration, databases and other applications are outside this change. The inference route remains `https://eeslindia.org/llm-api.php`.

## What learns

The shared parser and AI review run before final upload according to the job's parsing/AI settings. Preview/public/model responses cannot enroll themselves as training examples. After a successful authenticated recruiter correction, existing client-scoped verified memory still updates. Supported professional corrections additionally enter an encrypted durable outbox, when `LocalLlmLearning:Enabled=true`. The existing portable encryption key is reused with a distinct authenticated purpose; no credential key rotation or database migration is required.

Outbox data is restricted to source-supported company, current designation, academic qualification and skills. Names, contact details, addresses, salary, identifiers and protected-trait lines are excluded. Exact candidate facts remain in their existing client-scoped memory. The shared adapter trains general professional extraction from deidentified evidence. A model response is never labeled verified because it is confident or repeated.

Configure `LocalLlmLearning:ApiKeyFile` to a protected file containing the **dedicated learning key**. Do not use the inference/control key or put any key into checked-in configuration. The current authorized localhost installation references `D:/AllCreds/EESL-LLM-learning-100.key`. Other API deployments must receive their own protected file/secret mount through their normal deployment process. Default learning forwarding is disabled for deployments that have not explicitly configured it.

Candidate saves do not wait for network access. Outbox delivery has a 30-second deadline, bounded exponential backoff, idempotent remote ingestion and no redirects. Only an accepted receipt removes a normal item. Permanent schema rejections remain encrypted in `training-outbox/rejected` for review; they cannot block unrelated corrections. Revocations take priority, delete pending/rejected local examples for the subject and request remote purge across its client scopes. Keep `App_Data/Recruitment` on persistent protected storage.

## Dataset gateway

In **Security → Integrations → AI Integration**, use **Training JSON examples** to view two checked examples side by side with their source evidence. **Copy selected JSON** copies one dataset row; **Download examples (.jsonl)** downloads both rows, one JSON object per line. The dialog explains client IDs, stable subject references and human verification. These are reference/download actions only; they do not upload a dataset or start a training job.

`https://eeslindia.org/llm-learning.php` requires the dedicated bearer learning key. GET returns counts/revision, environment and training state, without sample contents. POST permits only:

- `ingest`: 1–25 verified JSON examples, maximum 1 MiB request. Source evidence must support each label. Same subject/source updates the existing example and merges prior verified fields rather than creating versions.
- `train`: request the fixed bounded training job. It accepts no commands, model paths, arbitrary hyperparameters or remote-code options.
- `purge_subject`: remove a SHA256 subject reference in a client, or `clientId: -1` across scopes, and persist revocation before deleting rows. Revoked subjects cannot be silently re-enrolled.

Use `deploy/local-llm/submit_verified_examples.py --input <professional-reviewed.jsonl>`. Without `--input` it reads status only. `--train` requests a batch after intake. The helper reads the key file; never provide a bearer key on the command line. The synthetic dataset in `deploy/local-llm/tests/verified-professional-patterns.jsonl` demonstrates the JSONL schema. Replace its evidence and labels with checked, deidentified examples; do not mark an unreviewed dataset verified.

Encrypted datasets are stored outside the web root under `E:/LLM/training/datasets/<clientId>`. AES-256-GCM authenticates tenant, subject and source identity. Dataset revision changes are crash-reconciled. No PHP endpoint exposes arbitrary files or executes caller commands. Gateway and training-data encryption use separate keys.

## Actual weight training

The isolated Windows Python runtime, CPU Torch, Transformers, PEFT and converter versions are pinned in `deploy/local-llm/training-lock.json`. Since the server cannot reach the package/model origins, the management PC downloads official packages and pinned Qwen tokenizer/configuration metadata, verifies hashes and transfers an offline wheelhouse/metadata manifest to `E:/LLM/training`. Training dequantizes the **existing serving GGUF** into float32 in memory through the pinned Transformers Qwen3.5 GGUF loader. It does not replace/requantize that serving file or claim to reconstruct the unquantized original checkpoint exactly. This avoids a separate 9.3 GB transfer and trains against the actual serving base. No machine PATH, registry Python installation, global package install, firewall change or weakened TLS validation is needed.

The `EESL-LLM-VerifiedTraining` LocalService task checks every five minutes. A Windows job imposes 20% total CPU and 24 GiB aggregate job memory, including evaluation children. Torch uses one thread and Idle priority. At least 24 GiB available memory is required before loading; subsequent stages require 18 GiB and CPU below 80%, plus the existing local website routing checks. Unsafe conditions defer/stop training rather than disable serving safety guards.

At least sixteen distinct verified professional sources are required. Identical sources are deduplicated across clients before a deterministic source-disjoint train/holdout split. Each bounded batch starts from the existing serving base, not the previous adapter; tokenizer/configuration metadata is pinned to the recorded official Qwen revision and the serving GGUF is fingerprinted in each adapter manifest. Long examples use verbatim professional context around verified labels within a fixed CPU token budget; ambiguous/oversized cases wait for review. The initial conservative CPU configuration updates a rank-4 LoRA on the last full-attention value projection for eight optimizer steps. This is genuine adapter-weight training; it is intentionally small and does not establish broad recruitment accuracy.

Each adapter folder contains actual safetensors weights, exported GGUF, a manifest, held-out loss and serving metrics. The restricted exporter uses the pinned llama.cpp GGUF library and only accepts the rank-4 layer-31 value-projection pair with verified dimensions. It emits the adapter metadata/tensor names required by the llama.cpp loader without indexing missing full-precision base shards. Other projections require their own verified layout transforms and are rejected. Baseline/candidate serving comparisons use separate loopback port 8092, leaving production inference at 8091 running. Candidate JSON must be valid/source-grounded, no held-out row may regress, loss may not materially worsen and at least one measured quality metric must improve. English/Hindi canaries are included. A rejected adapter is retained for diagnosis and does not change the live model.

A verified operator recovery marker may resume evaluation of a completed, hash-matched checkpoint after an export failure; it supplies no arbitrary path or code and must match the prior job and current dataset revision. The original checkpoint is preserved. Recovery recomputes real held-out metrics and reruns serving checks without claiming new optimizer steps or reconstructing missing historical loss numbers.

An approved pointer is validated against manifest/hash/base revision and revocations. The supervisor closes admission, drains accepted generations and loads the selected adapter on its owned model. Failed readiness quarantines it and restores the original base. The base GGUF and normal launch arguments are never overwritten by a training job. A revoked subject disqualifies the adapter, causing fallback; retraining from base excludes its data. This is not a claim of instant mathematical unlearning of previously downloaded checkpoints.

## Reliability and evidence

The supervisor confirms three consecutive model-health failures before recovery, retains immediate resource/website safety authority and limits restart bursts to three per fifteen minutes with backoff. Independent recovery also restarts a crashed supervisor; maintenance and identity mismatches remain explicit gates. No service can promise absolute uptime.

Server evidence is in `E:/LLM/logs/host-status.json`, `recovery-history.jsonl`, `last-failure.json`, `deployment-status.json` and `training-setup.json`; learning state is in `E:/LLM/training/state/training-status.json` and each adapter's manifest. History contains fixed codes/metrics, not keys, prompts or candidate facts. Operational history rotates at 5 MiB with three backups.

Future parser incidents follow the installed `frevo-resume-learning` Codex skill and the repository's `AGENTS.md`: reproduce, verify source labels, fix the shared layer, pass regression tests, submit a deidentified checked case, verify intake, and distinguish intake/training/evaluation/live activation.
