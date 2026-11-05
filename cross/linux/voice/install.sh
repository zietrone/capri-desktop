#!/bin/sh
# Installs Capri's local voice for this user: Kokoro in its own venv, its model, and a user service on localhost:8880.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
dest="${XDG_DATA_HOME:-$HOME/.local/share}/capri-voice"
models=https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0
mkdir -p "$dest"
cp "$here/server.py" "$here/blends.json" "$dest/"
[ -x "$dest/venv/bin/python" ] || python3 -m venv "$dest/venv"
"$dest/venv/bin/pip" install -q -r "$here/requirements.txt"
[ -f "$dest/kokoro-v1.0.int8.onnx" ] || curl -fL -o "$dest/kokoro-v1.0.int8.onnx" "$models/kokoro-v1.0.int8.onnx"
[ -f "$dest/voices-v1.0.bin" ] || curl -fL -o "$dest/voices-v1.0.bin" "$models/voices-v1.0.bin"
mkdir -p "$HOME/.config/systemd/user"
cp "$here/capri-voice.service" "$HOME/.config/systemd/user/"
systemctl --user daemon-reload
systemctl --user enable --now capri-voice
echo "Capri's voice is on http://localhost:8880/v1. In Capri Mainframe > VOICE pick CUSTOM LOCAL, voice capri-bright."
