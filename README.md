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
    CalibrationSFX.cs             Procedural blip/chime feedback for calibration
    PianoKeyboardMapper.cs        Builds LED-style key strips from calibration points
    PianoKeyLights.cs             Lights strips and emits sparkle particles from MIDI notes
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

The in-headset keyboard overlay uses a lightweight two-anchor calibration flow with a preview marker:

1. Press `A` once to enter marker mode. A translucent preview ball appears in front of the right controller.
2. Move the controller to the left edge of `A0` and press `A` again to place the left anchor.
3. Move the controller to the right edge of `C8` and press `B` to place the right anchor and calibrate.
4. `PianoCalibrationInput` forwards those two world-space points into `PianoKeyboardMapper`.
5. The mapper treats that span as the full keyboard width, divides it into `52` equal white-key widths, and derives all `88` key centers from standard piano spacing.
6. Press `A` again later to re-enter marker mode and recalibrate.

Calibration feedback:

- A short blip plays when marker mode starts and when the left anchor is placed
- A chime plays when calibration completes
- Green/red marker spheres briefly confirm the final anchor positions

Overlay visuals:

- White keys render as slim LED bars
- Black keys render as inverted `T` caps so their tops align with the white-key bar
- A faint top guide line spans from `A0` to `C8`
- Per-key glow meshes turn on while notes are active
- `PianoKeyLights` adds sparkle particles on note-on plus a soft stream while keys are held

Default active colors:

- Soft green for white keys
- Warm orange for black keys

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
