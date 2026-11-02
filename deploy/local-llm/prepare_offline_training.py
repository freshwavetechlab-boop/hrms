"""Prepare verified offline prerequisites when Server100 has no outbound access.

Run on the connected Windows laptop. No credentials are accepted in argv/logs;
the existing authenticated SMB session supplies access to the LLM share only.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(r"D:\FrevOHRMS\.codex-tmp\offline-llm-training-20261102")
REMOTE = Path(r"\\10.10.91.100\E$\LLM\training")
LOCK = json.loads((Path(__file__).parent / "training-lock.json").read_text(encoding="utf-8-sig"))


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url, path, size=None, expected=None):
    import msvcrt
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.with_name(path.name + '.download.lock').open('a+b') as lock:
        deadline = time.monotonic() + 14400
        while True:
            try:
                lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1); break
            except OSError:
                if time.monotonic() > deadline:
                    raise TimeoutError('Pinned asset download is already active')
                time.sleep(5)
        return _download(url, path, size, expected)


def _download(url, path, size=None, expected=None):
    if not url.startswith("https://"):
        raise ValueError("HTTPS source required")
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and (size is None or path.stat().st_size == size) and (not expected or sha256(path) == expected):
        return
    partial = path.with_name(path.name + ".partial")
    for attempt in range(12):
        start = partial.stat().st_size if partial.exists() else 0
        request = urllib.request.Request(url, headers={"User-Agent": "Frevo-LLM-Offline-Setup", **({"Range": f"bytes={start}-"} if start else {})})
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                append = start > 0 and response.status == 206
                with partial.open("ab" if append else "wb") as output:
                    last_report = time.monotonic()
                    while chunk := response.read(1024 * 1024):
                        output.write(chunk)
                        if time.monotonic() - last_report > 30:
                            print(json.dumps({"file": path.name, "downloaded": output.tell(), "expected": size}), flush=True)
                            last_report = time.monotonic()
            if size is not None and partial.stat().st_size != size:
                raise IOError("Downloaded size does not match pinned metadata")
            if expected and sha256(partial) != expected:
                partial.unlink()
                raise IOError("Downloaded hash does not match pinned metadata")
            partial.replace(path)
            print(json.dumps({"file": path.name, "verified": True, "bytes": path.stat().st_size}), flush=True)
            return
        except (OSError, urllib.error.URLError):
            if attempt == 11:
                raise
            time.sleep(min(30, 2 + attempt * 3))


def sync_file(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() and destination.stat().st_size == source.stat().st_size and (source.suffix not in ('.json', '.lock') or destination.read_bytes() == source.read_bytes()):
        return
    temporary = destination.with_name(destination.name + ".offline-copy.tmp")
    offset = temporary.stat().st_size if temporary.exists() else 0
    if offset > source.stat().st_size:
        raise IOError('Offline partial copy exceeds source')
    with source.open('rb') as original, temporary.open('r+b' if temporary.exists() else 'wb') as output:
        if offset:
            for position in {0, max(0, offset - 1024 * 1024)}:
                original.seek(position); output.seek(position)
                size = min(1024 * 1024, offset - position)
                if original.read(size) != output.read(size):
                    raise IOError('Offline partial copy does not match the pinned source')
        original.seek(offset); output.seek(offset)
        last_report = time.monotonic()
        while chunk := original.read(16 * 1024 * 1024):
            output.write(chunk)
            if time.monotonic() - last_report > 30:
                print(json.dumps({'file': source.name, 'serverCopied': output.tell(), 'bytes': source.stat().st_size}), flush=True)
                last_report = time.monotonic()
    if temporary.stat().st_size != source.stat().st_size:
        raise IOError("SMB copy size mismatch")
    temporary.replace(destination)


def assets():
    files = {
        "python-3.11.9-embed-amd64.zip": "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip",
        "get-pip.py": "https://bootstrap.pypa.io/get-pip.py",
        f"llama-{LOCK['converterRevision']}.zip": f"https://codeload.github.com/ggml-org/llama.cpp/zip/{LOCK['converterRevision']}",
    }
    manifest = []
    for name, url in files.items():
        local = ROOT / "downloads" / name
        download(url, local)
        manifest.append({"name": name, "sha256": sha256(local), "bytes": local.stat().st_size, "source": url})
        sync_file(local, REMOTE / "downloads" / name)
    (ROOT / "asset-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    sync_file(ROOT / "asset-manifest.json", REMOTE / "downloads" / "asset-manifest.json")


def wheels():
    directory = ROOT / "wheels"
    directory.mkdir(parents=True, exist_ok=True)
    common = [sys.executable, "-m", "pip", "download", "--disable-pip-version-check", "--dest", str(directory),
              "--platform", "win_amd64", "--python-version", "311", "--implementation", "cp", "--abi", "cp311", "--only-binary=:all:"]
    subprocess.run(common + ["--no-deps", "--index-url", "https://download.pytorch.org/whl/cpu", "torch==" + LOCK["torchCpuVersion"]], check=True)
    requirements = ["torch==" + LOCK["torchCpuVersion"], "pip==26.2.2", "numpy==2.3.5", "setuptools", "wheel",
                    "transformers==" + LOCK["transformersVersion"], "peft==" + LOCK["peftVersion"],
                    "accelerate==" + LOCK["accelerateVersion"], "cryptography==" + LOCK["cryptographyVersion"],
                    "psutil==" + LOCK["psutilVersion"], "safetensors==" + LOCK["safetensorsVersion"],
                    "sentencepiece", "pillow", "pyyaml", "tqdm", "poetry-core", "requests"]
    subprocess.run(common + ["--index-url", "https://pypi.org/simple", "--find-links", str(directory)] + requirements, check=True)
    resolved = {}
    manifest = []
    for path in sorted(directory.glob("*.whl")):
        parts = path.name.split("-")
        name, version = parts[0].replace("_", "-"), parts[1]
        if name in resolved and resolved[name][0] != version:
            raise ValueError("Conflicting offline wheel versions")
        digest = sha256(path)
        resolved[name] = (version, digest)
        manifest.append({"name": path.name, "bytes": path.stat().st_size, "sha256": digest})
        sync_file(path, REMOTE / "downloads" / "wheels" / path.name)
    lock = "\n".join(f"{name}=={version} --hash=sha256:{digest}" for name, (version, digest) in sorted(resolved.items())) + "\n"
    (directory / "requirements.lock").write_text(lock, encoding="utf-8")
    (directory / "offline-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    sync_file(directory / "requirements.lock", REMOTE / "downloads" / "wheels" / "requirements.lock")
    sync_file(directory / "offline-manifest.json", REMOTE / "downloads" / "wheels" / "offline-manifest.json")
    # Last marker: the server must not start a partial wheelhouse installation.
    (directory / "offline-ready.json").write_text(json.dumps({"ready": True, "wheelCount": len(manifest)}), encoding="utf-8")
    sync_file(directory / "offline-ready.json", REMOTE / "downloads" / "wheels" / "offline-ready.json")
    print(json.dumps({"offline_wheels_ready": len(manifest)}), flush=True)


def base(metadata_only=False):
    revision = LOCK["baseRevision"]
    url = f"https://huggingface.co/api/models/{LOCK['baseModel']}/revision/{revision}?blobs=true"
    with urllib.request.urlopen(url, timeout=40) as response:
        metadata = json.load(response)
    if metadata["sha"] != revision:
        raise ValueError("Pinned model revision mismatch")
    manifest = {"model": LOCK["baseModel"], "revision": revision, "metadataOnly": metadata_only, "files": []}
    for item in metadata["siblings"]:
        name = item["rfilename"]
        if metadata_only and name.endswith('.safetensors'):
            continue
        if not re.fullmatch(r"[A-Za-z0-9._-]+", name) or not (name.endswith((".json", ".safetensors", ".model", ".txt", ".jinja")) or name == "LICENSE"):
            continue
        local = ROOT / "base" / name
        expected = (item.get("lfs") or {}).get("sha256")
        download(f"https://huggingface.co/{LOCK['baseModel']}/resolve/{revision}/{name}", local, item["size"], expected)
        if not expected:
            content = local.read_bytes()
            blob = hashlib.sha1(f"blob {len(content)}\0".encode() + content).hexdigest()
            if blob != item["blobId"]:
                raise ValueError("Pinned model configuration blob mismatch")
        manifest["files"].append({"name": name, "sha256": sha256(local), "bytes": local.stat().st_size})
        sync_file(local, REMOTE / "base" / name)
    target = ROOT / "base" / "offline-manifest.json"
    target.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    sync_file(target, REMOTE / "base" / "offline-manifest.json")
    print(json.dumps({"base_metadata_ready": True, "metadataOnly": metadata_only, "files": len(manifest["files"]), "revision": revision}), flush=True)


def metadata():
    base(metadata_only=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["assets", "wheels", "base", "metadata"])
    args = parser.parse_args()
    ROOT.mkdir(parents=True, exist_ok=True)
    globals()[args.mode]()
