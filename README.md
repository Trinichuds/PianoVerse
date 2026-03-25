# MIDI Piano Sampler

A Unity-based low-latency MIDI piano sample player for Windows. It plays multi-velocity piano samples in real time from a MIDI keyboard and uses a native C++ audio engine for low-latency output via WASAPI or ASIO.

## How it works

```text
MIDI Keyboard
  -> Midi88KeyInput.cs
    -> NativePianoSampler.cs
      -> NativePianoBackend.dll
        -> WASAPI or ASIO output
```

Unity's built-in `AudioSource` path in `MidiPianoSampler.cs` still exists as a fallback, but the native backend is the primary low-latency path.

## Project structure

```text
Assets/
  inputs/
    Midi88KeyInput.cs             MIDI input handler for notes and sustain pedal
  pianosampler/
    NativePianoSampler.cs         Native backend bridge
    MidiPianoSampler.cs           Unity AudioSource fallback
    PianoCalibrationInput.cs      VR calibration input for the keyboard overlay
    PianoKeyboardMapper.cs        Builds LED-style key strips from calibration points
    PianoKeyLights.cs             Lights strip segments from MIDI note events
    PianoSampleBank.cs            ScriptableObject sample bank
    PianoVoice.cs                 Voice state and fade logic
    AudioLatencyDiagnostics.cs    Reports DSP buffer and theoretical latency
  Plugins/x86_64/
    NativePianoBackend.dll        Compiled native audio engine

NativeAudioEngine/
  src/
    JucePianoBackend.cpp          Active JUCE backend with WASAPI + ASIO support
    NativePianoBackend.cpp        Legacy WASAPI-only implementation
    NativeAudioInterop.h          Exported C ABI
```

## Setup

### Requirements

- Windows 10/11
- Unity 2022.3 LTS
- A MIDI keyboard connected before entering Play mode
- For ASIO output: an ASIO-capable interface such as Focusrite

### Sample bank

The project uses [Salamander Grand Piano](https://sfzinstruments.github.io/pianos/salamander) samples. Import the samples and build a `PianoSampleBank` asset from the editor tool.

### Scene wiring

1. Add `Midi88KeyInput` to a GameObject.
2. Add `NativePianoSampler` and assign the `Midi88KeyInput` and `PianoSampleBank` references.
3. Set `BackendKind` in the inspector to `Auto`, `WasapiShared`, `WasapiExclusive`, or `Asio`.
4. Enter Play mode and let the native backend upload samples and start.

### Piano keyboard mapping

The in-headset keyboard overlay uses a two-point calibration flow:

1. Press `A` with the controller positioned at the left edge of `A0`.
2. Press `B` with the controller positioned at the right edge of `C8`.
3. `PianoCalibrationInput` passes those two world-space controller positions into `PianoKeyboardMapper`.
4. The mapper treats that span as the full piano width, divides it into `52` white-key widths, and derives all `88` key centers from standard piano spacing.
5. The visual overlay is intentionally lightweight: it creates thin LED-style strips only, not a full virtual piano.

Default strip colors:

- Grey when idle
- Green for active white keys
- Orange for active black keys

### Building the native DLL

See [NativeAudioEngine/README.md](NativeAudioEngine/README.md).

## Audio backend options

| Backend | Latency | Notes |
|---|---|---|
| WASAPI Shared | ~10-30 ms | Works on any Windows PC |
| WASAPI Exclusive | ~5-15 ms | Takes exclusive control of the device |
| ASIO | ~1-5 ms | Requires an ASIO driver |
| Auto | varies | Tries Focusrite ASIO first, then Windows audio fallbacks |

## Voice parameters

- `Max voices`: polyphony cap
- `Release fade ms`: fade duration after note-off
- `Preferred device name`: preferred output device substring, default `Focusrite USB ASIO`

## Dependencies

- [Keijiro Minis](https://github.com/keijiro/Minis)
- [JUCE](https://juce.com/) 8.0.12
- Unity Input System
- TextMeshPro
