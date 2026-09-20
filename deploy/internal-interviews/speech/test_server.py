"""Standard-library contract tests, no real model download/inference or external network."""
import importlib.util
import io
import json
from pathlib import Path
import threading
import unittest
import urllib.error
import urllib.request
import wave

spec = importlib.util.spec_from_file_location("interview_speech", Path(__file__).with_name("server.py"))
speech = importlib.util.module_from_spec(spec)
spec.loader.exec_module(speech)


class SpeechContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        speech.KEY = "synthetic-private-test-key-not-for-deployment"
        speech.WHISPER = object()
        cls.original_transcribe = speech.transcribe
        speech.transcribe = lambda audio, language: json.dumps({"text": "Synthetic answer", "draft": True}).encode()
        cls.server = speech.ThreadingHTTPServer(("127.0.0.1", 0), speech.Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        speech.transcribe = cls.original_transcribe

    def request(self, path, body=b"audio", key=None, language="en"):
        request = urllib.request.Request("http://127.0.0.1:%d%s" % (self.server.server_port, path), data=body,
                                         headers={"X-Speech-Key": speech.KEY if key is None else key, "X-Language": language})
        try:
            response = urllib.request.urlopen(request, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read()

    def test_unauthorized_request_cannot_run_speech(self):
        self.assertEqual(401, self.request("/transcribe", key="incorrect")[0])

    def test_transcript_is_explicitly_draft(self):
        status, body = self.request("/transcribe")
        self.assertEqual(200, status)
        self.assertTrue(json.loads(body)["draft"])

    def test_busy_does_not_queue_unbounded_generations(self):
        speech.SLOT.acquire()
        try:
            self.assertEqual(429, self.request("/transcribe")[0])
        finally:
            speech.SLOT.release()

    def test_invalid_language_is_not_a_cloud_fallback(self):
        self.assertEqual(400, self.request("/transcribe", language="xx")[0])

    def test_missing_voice_returns_failure_not_fake_audio(self):
        speech.VOICES.clear()
        self.assertEqual(422, self.request("/synthesize", b'{"text":"Question"}')[0])

    def test_service_error_does_not_leak_document_or_secret(self):
        original = speech.transcribe
        def failing(*_):
            raise RuntimeError("private candidate content and secret")
        speech.transcribe = failing
        try:
            status, body = self.request("/transcribe")
            self.assertEqual(503, status)
            self.assertNotIn(b"private candidate", body)
        finally:
            speech.transcribe = original

    def test_tts_produces_wave_with_a_mock_local_voice(self):
        class FakeVoice:
            def synthesize_wav(self, text, output):
                output.setnchannels(1)
                output.setsampwidth(2)
                output.setframerate(16000)
                output.writeframes(b"\0\0" * 160)
        speech.VOICES["en"] = FakeVoice()
        status, body = self.request("/synthesize", b'{"text":"Question"}')
        self.assertEqual(200, status)
        with wave.open(io.BytesIO(body)) as audio:
            self.assertEqual(16000, audio.getframerate())


if __name__ == "__main__":
    unittest.main()
