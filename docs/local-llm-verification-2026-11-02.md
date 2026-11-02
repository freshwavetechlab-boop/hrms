# Server100 LLM verification — 2 November 2026

The Server100 supervisor is **READY** with the verified LoRA adapter `dd2b4537-31a8-4ce5-b592-34f8188dc9e8` active. The public authenticated inference route returned HTTP 200 with a completed response in 2.187 seconds after activation. This is a health probe, not a controlled latency benchmark. Local UI 5173 and API 5062 returned 200; the public English/Hindi site pages returned 200.

## Actual training and evaluation

- Eight actual CPU optimizer steps completed on Server100. The saved safetensors checkpoint is 57,640 bytes; the exported GGUF adapter is 57,664 bytes. These are learned LoRA weights, applied over the existing serving base, rather than a replacement full model.
- The isolated worker used one Torch thread with a Windows job hard cap of 20% total CPU and 24 GiB aggregate memory. Observed worker working set was 17.38 GiB. Inference continued responding during training.
- The gateway retained 33 verified synthetic/reference professional examples at dataset revision 4. The first job's training pool contained 25 source-disjoint examples; four held-out resume cases were used for the recovered final evaluation. This small regression batch is not broad resume-accuracy certification.
- Held-out loss decreased from **0.2784527764 to 0.2665150315**, a **4.287% reduction in loss**. This is not a 4.287-point increase in field accuracy.
- Both baseline and candidate passed all six serving checks: four professional fact-extraction cases and two English/Hindi canaries. No measured field/canary regression occurred.
- The approved manifest and adapter hashes matched their active pointer. The supervisor drained generation, loaded the approved adapter and reported READY with the matching active ID. Main model load took 20.407 seconds. No quarantine or readiness failure occurred.

The first export attempt exposed that the generic Qwen converter indexes full original safetensors shards, which are unnecessary for the existing-GGUF training path. The failure remains in the job's `conversion.log`. The checked v-projection exporter now writes the required LoRA metadata/tensors directly through the pinned GGUF library. Its actual adapter loaded successfully in both the evaluation runtime and the live service.

The completed checkpoint was recovered by hash, preserving `recovered-original.safetensors`; evaluation resumed without additional optimizer steps. Real held-out metrics were recomputed. Original per-step training losses were not retained by the first instrumentation, so the manifest records `trainingLosses: null` rather than fabricated values. The completed recovery marker retains the operator verification reference.

Server artifacts are under `E:/LLM/training/adapters/dd2b4537-31a8-4ce5-b592-34f8188dc9e8`. The adapter SHA256 is `e7f63a563ddd5dad00942e691188c5e2e51865b9a5d555eb7ef17a07904c0445`; serving base SHA256 is `00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4`.

## Application, recovery and scope checks

The HRMS encrypted outbox delivered a real authenticated request to the Server100 learning gateway and removed it only after acknowledgment. The probe used an unused synthetic subject and created/edited no business candidate record. Forwarding is enabled on the running localhost API; other HRMS deployments require their normal code deployment and protected learning-key configuration.

The focused backend suite passed 445 checks with one environment-dependent check skipped. The latest outbox checks passed eight tests. Ten Python training/export tests, three PowerShell recovery/deployment/adapter suites, PHP maintenance/learning crypto contracts, source syntax checks and the new skill validator passed. Latest installed worker/export/core source hashes matched the reviewed workspace files.

Seven protected configuration/runtime hashes remained identical: global Apache/PHP settings, WordPress configuration, the original public inference stub, private inference gateway, launch-arguments JSON and original llama-server executable. Changes were confined to LLM-owned scripts/tasks/storage/routes and the related HRMS integration. The base GGUF was preserved.

Failure journals, independent supervisor recovery, health confirmation, guarded adapter selection and fallback to the original base remain enabled. Resource/site guards and deliberate maintenance remain authoritative; this is automatic recovery rather than an absolute uptime guarantee.

The installed `frevo-resume-learning` skill and repository guidance retain future source-verified parser regression cases and submit deidentified professional examples through the dedicated learning gateway. See [the operating guide](local-llm-verified-learning.md) for intake format, status and training behavior.

Optional task-owned full-weight transfer caches remain after automatic approval review rejected their deletion with “blocked by policy.” They are not used by the serving-GGUF training pipeline.
