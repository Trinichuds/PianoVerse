# MIDI Piano Sampler

A Unity-based low-latency MIDI piano sample player for Windows. Plays multi-velocity piano samples in real time from a MIDI keyboard, using a native C++ audio engine for near-zero latency output via WASAPI or ASIO.

## How it works

```
MIDI Keyboard
    └─ Midi88KeyInput.cs          (tracks 88 keys + sustain pedal via Unity InputSystem)
          └─ NativePianoSampler.cs  (C# bridge)
                └─ NativePianoBackend.dll  (C++ / JUCE engine)
                      └─ WASAPI or ASIO output
```

Unity's built-in `AudioSource` pipeline (`MidiPianoSampler.cs`) is available as a fallback but the native backend is the primary path.

## Project structure

```
Assets/
  inputs/
    Midi88KeyInput.cs             MIDI input handler — note events, sustain pedal (CC64)
  pianosampler/
    NativePianoSampler.cs         Native backend bridge (DllImport)
    MidiPianoSampler.cs           Unity AudioSource fallback (48 voices)
    PianoSampleBank.cs            ScriptableObject — samples keyed by MIDI note + velocity layer
    PianoVoice.cs                 Single voice state and fade-out logic
    AudioLatencyDiagnostics.cs    Reports DSP buffer and theoretical latency on start
  Editor/
    SalamanderSampleBankBuilder.cs  Imports Salamander piano samples into a PianoSampleBank asset
  Plugins/x86_64/
    NativePianoBackend.dll        Compiled native audio engine

NativeAudioEngine/               C++ source for the native backend
  src/
    JucePianoBackend.cpp          Main engine (JUCE, supports WASAPI + ASIO)
    NativePianoBackend.cpp        Legacy WASAPI-only implementation (kept for reference)
    NativeAudioInterop.h          C ABI exported to Unity
  CMakeLists.txt
```

## Setup

### Requirements

- Windows 10/11
- Unity 2022.3 LTS
- A MIDI keyboard (connected before entering Play mode)
- For ASIO output: an ASIO-capable audio interface (default config targets Focusrite USB ASIO)

### Sample bank

The project uses [Salamander Grand Piano](https://sfzinstruments.github.io/pianos/salamander) samples. Place the WAV files somewhere accessible, then use the editor tool:

**Tools → Build Salamander Sample Bank** — point it at the folder and it generates a `PianoSampleBank` asset automatically.

### Scene wiring

1. Add `Midi88KeyInput` to a GameObject.
2. Add `NativePianoSampler` to the same (or another) GameObject and assign the `PianoSampleBank` and `Midi88KeyInput` references.
3. Set `BackendKind` in the inspector (`Auto`, `WasapiShared`, `WasapiExclusive`, or `Asio`).
4. Enter Play mode — the engine starts and registers samples on `Awake`.

### Building the native DLL

See [NativeAudioEngine/README.md](NativeAudioEngine/README.md).

## Audio backend options

| Backend | Latency | Notes |
|---|---|---|
| WASAPI Shared | ~10–30 ms | Works on any Windows PC, no driver needed |
| WASAPI Exclusive | ~5–15 ms | Takes exclusive control of the device |
| ASIO | ~1–5 ms | Requires ASIO driver; targets Focusrite USB ASIO by default |
| Auto | varies | Tries Exclusive → Shared fallback |

## Voice parameters (NativePianoSampler inspector)

- **Max voices** — polyphony cap (default 64); oldest voice stolen when exceeded
- **Release fade ms** — fade-out duration after note-off (default 150 ms)
- **Preferred device name** — substring match for device selection (default `"Focusrite USB ASIO"`)

## Dependencies

- [Keijiro Minis](https://github.com/keijiro/Minis) (`jp.keijiro.minis` v1.3.2) — Unity MIDI input
- [JUCE](https://juce.com/) v8.0.12 — native audio device abstraction (fetched by CMake at build time)
- Unity Input System 1.14
- TextMeshPro
