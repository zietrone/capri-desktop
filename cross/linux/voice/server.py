"""Capri's local voice: Kokoro (kokoro-onnx) behind OpenAI's speech API, POST /v1/audio/speech, on localhost:8880.

Capri's "Local server" voice provider talks to this with no setup, since 8880 is her default.
"""
import io
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
import soundfile as sf
from kokoro_onnx import Kokoro

HERE = os.path.dirname(os.path.abspath(__file__))
HOST = os.environ.get("CAPRI_VOICE_HOST", "127.0.0.1")
PORT = int(os.environ.get("CAPRI_VOICE_PORT", "8880"))
DEFAULT_VOICE = os.environ.get("CAPRI_VOICE", "capri-bright")
SPEED = float(os.environ.get("CAPRI_VOICE_SPEED", "1.08"))

kokoro = Kokoro(os.path.join(HERE, "kokoro-v1.0.int8.onnx"), os.path.join(HERE, "voices-v1.0.bin"))
VOICES = sorted(kokoro.get_voices())
lock = threading.Lock()

# Original blends, mixed from Kokoro's own voices: "name": [(voice, weight), ...]. Any request may also ask for an ad hoc
# blend like "af_bella*0.6+af_nova*0.4".
BLENDS = {}
blends_file = os.path.join(HERE, "blends.json")
if os.path.exists(blends_file):
    with open(blends_file) as f:
        BLENDS = {k: [tuple(x) for x in v] for k, v in json.load(f).items()}


def style(voice):
    """A voice name, a saved blend, or "a*0.6+b*0.4", as the style vector Kokoro takes."""
    parts = BLENDS.get(voice)
    if parts is None and "+" not in voice and "*" not in voice:
        return voice if voice in VOICES else DEFAULT_VOICE
    if parts is None:
        parts = []
        for piece in voice.split("+"):
            name, _, weight = piece.strip().partition("*")
            parts.append((name.strip(), float(weight or 1)))
    parts = [(n, w) for n, w in parts if n in VOICES]
    if not parts:
        return DEFAULT_VOICE
    total = sum(w for _, w in parts)
    return sum(kokoro.get_voice_style(n) * (w / total) for n, w in parts)


def speak(text, voice, speed):
    first = (BLENDS.get(voice) or [(voice.split("*")[0].split("+")[0].strip(), 1)])[0][0]
    lang = "en-gb" if first[:1] == "b" else "en-us"
    with lock:
        samples, rate = kokoro.create(text, voice=style(voice), speed=speed, lang=lang)
    return np.asarray(samples, dtype=np.float32), rate


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body, kind):
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _json(self, code, value):
        self._send(code, json.dumps(value).encode(), "application/json")

    def do_GET(self):
        if self.path.rstrip("/") in ("/v1/audio/voices", "/v1/voices"):
            return self._json(200, {"voices": sorted(BLENDS) + VOICES})
        if self.path.rstrip("/") in ("/v1/models", "/models"):
            return self._json(200, {"object": "list", "data": [{"id": "kokoro", "object": "model"}]})
        if self.path in ("/", "/health"):
            return self._json(200, {"status": "ok"})
        self._json(404, {"error": "not found"})

    def do_POST(self):
        if self.path.rstrip("/") not in ("/v1/audio/speech", "/audio/speech"):
            return self._json(404, {"error": "not found"})
        try:
            length = int(self.headers.get("Content-Length") or 0)
            request = json.loads(self.rfile.read(length) or b"{}")
            text = (request.get("input") or "").strip()
            if not text:
                return self._json(400, {"error": "input is empty"})
            voice = request.get("voice") or DEFAULT_VOICE
            speed = float(request.get("speed") or SPEED)
            fmt = (request.get("response_format") or "wav").lower()
            samples, rate = speak(text, voice, speed)
            if fmt == "pcm":
                # OpenAI's raw pcm: 24 kHz 16-bit mono, which is Kokoro's own rate.
                pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
                return self._send(200, pcm, "audio/pcm")
            out = io.BytesIO()
            sf.write(out, samples, rate, format="WAV", subtype="PCM_16")
            self._send(200, out.getvalue(), "audio/wav")
        except Exception as error:  # noqa: BLE001 - any failure becomes an HTTP error Capri shows
            self._json(500, {"error": str(error)})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    # Warm up so her first line isn't slow.
    speak("Hi.", DEFAULT_VOICE, SPEED)
    print(f"Capri voice on http://{HOST}:{PORT}/v1 with {len(VOICES)} voices", flush=True)
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()
