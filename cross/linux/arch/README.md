# Capri on Arch Linux

This branch adds what the Mac and Linux preview was missing for an Arch desktop:

- **Voice chat through your mic.** TALK on her pill now works on Linux
- **An Arch package** built with `makepkg`
- **A local voice** that runs offline on your CPU
- **Hyprland fixes**, for sway and niri too

## Install

```bash
sudo pacman -S --needed dotnet-sdk pipewire whisper-cpp
cd cross/linux/arch
makepkg -si
capri-desktop
```

`makepkg -si` builds a self-contained Capri from this checkout. It installs her to `/opt/capri-desktop` and adds `capri-desktop` to your PATH, plus a desktop entry and an icon.

Optional packages:

| Package | What it gives her |
| --- | --- |
| `pipewire` | Her mic, through `pw-record` (recommended) |
| `libpulse` / `alsa-utils` | Her mic without PipeWire (`parecord` / `arecord`) |
| `whisper-cpp` | Offline ears for voice chat |
| `ollama` | A local brain (`ollama pull llama3.2:3b`, then pick it in Mainframe > BRAIN) |

To stop her, use **Quit** in her menu, or run `pkill -x capri-desktop`.

## Voice chat

Press **TALK** on her pill, or **Voice > Voice chat** in her menu, and talk to her.

- She records 16 kHz mono from `pw-record`, or from `parecord` or `arecord` if you don't have PipeWire.
- A phrase starts when the sound rises well above the room's noise and ends after about 0.8 s of quiet. Each phrase goes to her brain as if you'd typed it.
- She doesn't listen while she's thinking or talking, so she never hears herself.
- The mic button on the right of her pill mutes her ears. The meter shows your mic level.
- **Voice > Voice chat ears** lets you choose:
  - **Offline (whisper.cpp):** she runs `whisper-server` on a free localhost port. The first time, she downloads Whisper base (about 150 MB) into her data folder.
  - **Transcription server:** any OpenAI-style `/audio/transcriptions` server, such as Groq (`https://api.groq.com/openai/v1/audio/transcriptions`), OpenAI, speaches or LocalAI.
  - **Microphone:** any PipeWire or PulseAudio source.
  - **Sensitivity:** Hear quieter voices, Normal, or Ignore more noise.

## Local voice

[`../voice`](../voice) has a small server that puts [Kokoro](https://github.com/thewh1teagle/kokoro-onnx) behind OpenAI's speech API on `localhost:8880`. That's the address Capri's **CUSTOM LOCAL** voice expects, so she needs no setup.

```bash
cross/linux/voice/install.sh
```

The installer:
- sets Kokoro up in its own virtual environment under `~/.local/share/capri-voice`
- downloads the int8 model (about 115 MB)
- adds a user service, `capri-voice`, that starts it at login

On a 4-core laptop, a sentence takes about 1.5 s.

Then, in **Capri Mainframe > VOICE**, pick **CUSTOM LOCAL** and set the voice to `capri-bright`.

### Voice blends

`blends.json` mixes Kokoro's own voices into original ones:

```json
{ "capri-bright": [["af_nova", 0.5], ["af_sky", 0.3], ["af_bella", 0.2]] }
```

Any request can also ask for a blend on the fly, like `af_bella*0.6+af_nova*0.4`. `GET /v1/audio/voices` lists the blends and all 54 Kokoro voices. These are original mixes, not copies of any actor's voice.

## Hyprland

Her windows run through XWayland. Add this rule so Hyprland floats them and leaves her own frame alone. Without it, Hyprland tiles the Mainframe and animates every step she takes.

`hyprland.lua`:

```lua
hl.window_rule({
    name        = "capri",
    match       = { class = "^Capri$" },
    float       = true,
    border_size = 0,
    rounding    = 0,
    no_shadow   = true,
    no_blur     = true,
    no_anim     = true,
    no_dim      = true,
})
```

`hyprland.conf`:

```ini
windowrule = float, border_size 0, rounding 0, no_shadow, no_blur, no_anim, no_dim, match:class ^Capri$
```

Fixed in the code:

- **Mainframe close and minimize:** both asked for a minimized window, which Hyprland, sway and niri don't have, so the buttons did nothing. There they now hide the window, and opening the Mainframe again shows it.
- **Closing her pill or bubble:** your close-window shortcut used to close them, and she crashed ("Cannot re-show a closed window") the next time she talked. Now it only hides them.

With fractional scaling, such as 1.5, XWayland draws her at 100% and stretches her up, so she looks soft. `xwayland { force_zero_scaling = true }` makes her sharp, but it changes every XWayland app.

## Tests

```bash
dotnet publish cross/Capri.Cross.csproj -c Release -r linux-x64 --self-contained -o out/Capri
out/Capri/Capri --smoke "$PWD/smoke"
grep -c ^PASS smoke/smoke-test.txt   # 148, no FAIL lines
```

The smoke test checks voice chat's endpointing, Whisper's made-up lines on silence, the WAV it sends, the mic button on her pill and the recorder's arguments. It also checks that the Mainframe closes and reopens under Hyprland.
