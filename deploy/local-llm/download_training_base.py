"""Fetch pinned, official training weights only into the LLM training folder."""
import json
import hashlib
import os
from pathlib import Path


def main():
    from transformers import AutoConfig, AutoTokenizer

    root = Path(r"E:\LLM\training")
    lock = json.loads(Path(r"E:\LLM\maintenance\training-lock.json").read_text(encoding="utf-8-sig"))
    if lock["baseModel"] != "Qwen/Qwen3.5-4B" or len(lock["baseRevision"]) != 40:
        raise ValueError("Unrecognized base model")
    os.environ["HF_HOME"] = str(root / "cache" / "huggingface")
    os.environ["HF_HUB_DISABLE_XET"] = "1"
    manifest = json.loads((root / "base" / "offline-manifest.json").read_text(encoding="utf-8"))
    if manifest["model"] != lock["baseModel"] or manifest["revision"] != lock["baseRevision"]:
        raise ValueError("Offline model revision mismatch")
    for item in manifest["files"]:
        if Path(item["name"]).name != item["name"]:
            raise ValueError("Invalid offline model filename")
        path = root / "base" / item["name"]
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != item["bytes"] or digest != item["sha256"]:
            raise ValueError("Offline model integrity check failed")
    if lock.get('trainingBaseMode') != 'serving-gguf-fp32' or not manifest.get('metadataOnly'):
        raise ValueError('Expected pinned metadata and the existing serving GGUF')
    serving = Path(r'E:\LLM\Qwen3.5-4B-Q4_K_M.gguf')
    if serving.stat().st_size != 2740937888:
        raise ValueError('Existing serving GGUF identity changed')
    config = AutoConfig.from_pretrained(root / "base", trust_remote_code=False, local_files_only=True)
    if config.model_type != "qwen3_5":
        raise ValueError("Model architecture does not match the serving model")
    AutoTokenizer.from_pretrained(root / "base", trust_remote_code=False, local_files_only=True)
    from transformers import Qwen3_5ForCausalLM, GgufConfig
    print(json.dumps({"architecture": config.model_type, "training_class": Qwen3_5ForCausalLM.__name__,
                      "revision": lock["baseRevision"], "metadata_ready": True, 'training_base_mode': 'serving-gguf-fp32'}))


if __name__ == "__main__":
    main()
