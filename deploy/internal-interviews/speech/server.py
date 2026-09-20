"""Private HRMS speech sidecar. Pre-provision models; never download models at runtime.

No candidate identifiers, transcripts, request bodies or credentials are logged.
Run behind an internal-only container network; HRMS supplies its own authenticated APIs.
"""
import hmac
import io
import json
import os
from pathlib import Path
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_AUDIO = 12 * 1024 * 1024
MAX_TEXT = 2000
SLOT = threading.BoundedSemaphore(1)
KEY = os.environ.get("SPEECH_API_KEY", "")
WHISPER = None
VOICES = {}


def load_models():
    global WHISPER
    if len(KEY) < 32:
        raise RuntimeError("Configure a private speech credential of at least 32 characters.")
    from faster_whisper import WhisperModel
    from piper import PiperVoice
    model = Path(os.environ.get("WHISPER_MODEL_DIR", "/models/whisper"))
    if not (model / "model.bin").is_file():
        raise RuntimeError("Provision the local CTranslate2 Whisper model before starting.")
    WHISPER = WhisperModel(str(model), device="cpu", compute_type="int8",
                           cpu_threads=int(os.environ.get("SPEECH_THREADS", "2")), num_workers=1,
                           local_files_only=True)
    for language in ("en", "hi"):
        path = Path(os.environ.get("PIPER_" + language.upper() + "_MODEL", "/models/" + language + ".onnx"))
        if path.is_file() and Path(str(path) + ".json").is_file():
            VOICES[language] = PiperVoice.load(str(path))
    if not VOICES:
        raise RuntimeError("Provision at least one licensed Piper voice and its JSON configuration.")


def transcribe(audio, language):
    # MediaRecorder WebM often omits duration. Bound actual decoded samples instead;
    # memory streams cannot make PyAV fetch a candidate-supplied URL or local path.
    import av
    import numpy as np
    resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
    chunks, samples = [], 0
    with av.open(io.BytesIO(audio)) as container:
        for frame in container.decode(audio=0):
            for converted in resampler.resample(frame):
                chunk = converted.to_ndarray().reshape(-1)
                samples += chunk.size
                if samples > 910 * 16000:
                    raise ValueError("Decoded audio exceeds the interview limit.")
                chunks.append(chunk)
        for converted in resampler.resample(None):
            chunks.append(converted.to_ndarray().reshape(-1))
    if not chunks:
        raise ValueError("No audio frames detected.")
    decoded = np.concatenate(chunks).astype(np.float32) / 32768.0
    if decoded.size > 910 * 16000:
        raise ValueError("Decoded audio exceeds the interview limit.")
    segments, _ = WHISPER.transcribe(decoded, language=language, beam_size=1,
                                    vad_filter=True, condition_on_previous_text=False)
    parts = []
    for segment in segments:
        parts.append(segment.text.strip())
        if sum(len(part) for part in parts) > 8000:
            raise ValueError("Transcript is too long; enter the answer manually.")
    return json.dumps({"text": " ".join(parts), "draft": True}, ensure_ascii=False).encode()


def synthesize(text, language):
    if language not in VOICES:
        raise ValueError("The selected language voice has not been provisioned.")
    if not isinstance(text, str) or not 0 < len(text) <= MAX_TEXT:
        raise ValueError("Invalid question length.")
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        VOICES[language].synthesize_wav(text, wav)
    audio = output.getvalue()
    if len(audio) > MAX_AUDIO:
        raise ValueError("Voice output exceeded the configured limit.")
    return audio


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def setup(self):
        super().setup()
        self.connection.settimeout(100)

    def reply(self, status, body, content_type="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path != "/health":
            return self.reply(404, b'{}')
        self.reply(200 if WHISPER is not None else 503,
                   json.dumps({"ready": WHISPER is not None, "voices": list(VOICES)}).encode())

    def do_POST(self):
        if not KEY or not hmac.compare_digest(self.headers.get("X-Speech-Key", ""), KEY):
            return self.reply(401, b'{"error":"Unauthorized"}')
        if self.path not in ("/transcribe", "/synthesize"):
            return self.reply(404, b'{}')
        language = self.headers.get("X-Language", "en")
        if language not in ("en", "hi"):
            return self.reply(400, b'{"error":"Unsupported language"}')
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return self.reply(400, b'{"error":"Invalid length"}')
        if not 0 < length <= (MAX_AUDIO if self.path == "/transcribe" else 12000):
            return self.reply(413, b'{"error":"Payload too large"}')
        if not SLOT.acquire(blocking=False):
            return self.reply(429, b'{"error":"Speech service busy; retry"}')
        try:
            body = self.rfile.read(length)
            if len(body) != length:
                raise ValueError("Incomplete request.")
            if self.path == "/transcribe":
                self.reply(200, transcribe(body, language))
            else:
                self.reply(200, synthesize(json.loads(body).get("text"), language), "audio/wav")
        except (ValueError, KeyError, json.JSONDecodeError):
            self.reply(422, b'{"error":"Input could not be processed; review the answer manually"}')
        except Exception:
            self.reply(503, b'{"error":"Local speech failed; check provisioned models and service health"}')
        finally:
            SLOT.release()


if __name__ == "__main__":
    load_models()
    ThreadingHTTPServer(("0.0.0.0", 8094), Handler).serve_forever()
