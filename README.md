# MR Piano Waterfall

A Unity-based mixed-reality piano visualizer and sample player for Windows. It combines real MIDI piano input, a native C++ sample-playback engine, an in-headset keyboard overlay, falling-note visualization, and VR menu-driven song playback in `Practice`, `RealTime`, and `Watch` modes.

## How it works

```text
MIDI Keyboard
  -> Midi88KeyInput.cs
    -> NoteMapPlayer.cs / PianoKeyLights.cs
    -> NativePianoSampler.cs
      -> NativePianoBackend.dll
        -> WASAPI or ASIO output
    -> WaterfallRenderer.cs / keyboard overlay / HUD
```

Unity's built-in `AudioSource` path in `MidiPianoSampler.cs` still exists as a fallback, but the native backend is the primary realtime path.

## Project structure

```text
Assets/
  inputs/
    Midi88KeyInput.cs             MIDI input handler for notes and sustain pedal
  notemap/
    MidiParser.cs                 Converts MIDI files into runtime note maps
    NoteMap.cs                    Serializable note/song data model
    NoteMapLoader.cs              Loads and auto-converts maps from StreamingAssets/Maps
    NoteMapPlayer.cs              Practice / RealTime / Watch playback and scoring
    SongSelector.cs               Older controller-based song selector
    WaterfallRenderer.cs          Falling-note guide bars aligned to the keyboard overlay
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
  ui/
    GameManager.cs                Runtime-built VR menu flow and audio mode options
    GameHUD.cs                    In-game HUD for playback controls and mode display
    VRMenuPanel.cs                Procedural world-space menu panel
  StreamingAssets/
    Maps/                         Runtime-loaded .mid + cached .json song maps
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
4. Add `NoteMapLoader`, `GameManager`, and `WaterfallRenderer` if you want the full song-playback/menu flow.
5. Enter Play mode and let the native backend upload samples and start.

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
- A bright top guide line plus a soft glow line span from `A0` to `C8`
- Per-key glow meshes turn on while notes are active
- `PianoKeyLights` adds sparkle particles on note-on plus a soft stream while keys are held
- Sustained notes shift to purple until they fully end

Default active colors:

- Soft green for white keys
- Warm orange for black keys

### Song maps and playback modes

The project can load songs from `Assets/StreamingAssets/Maps/` at runtime:

- `.mid` or `.midi` files are scanned on startup
- If a matching `.json` note map does not exist yet, `NoteMapLoader` parses the MIDI and writes a cached JSON file
- All loaded maps are sorted by title and exposed to the VR map-select menu
- The repository currently includes a larger demo song library under `StreamingAssets/Maps`

`NoteMapPlayer` supports three modes:

- `Practice`: playback pauses at grouped note steps until the required keys are pressed; it also supports limited early input buffering so slightly-ahead playing can still register
- `RealTime`: notes scroll continuously after a short countdown and timing is scored as `Perfect` / `Great` / `Good` / `Miss`
- `Watch`: the song plays through automatically with guide visuals and programmatic sample playback, without requiring input

`WaterfallRenderer` uses the calibrated keyboard top line to draw falling bars so note timing lands exactly on the overlay.

### VR menus and controls

The current build is driven by `GameManager` and runtime-built `VRMenuPanel` menus:

- `B` when not playing: summon or dismiss the menu
- Right thumbstick up/down: move menu selection
- Right trigger: activate the highlighted item
- `B` inside submenus: go back
- Main menu: `Start`, `Options`, `Exit`
- Map select: choose a song from `StreamingAssets/Maps`
- Options: cycle audio backend mode and reinitialize the sampler

During playback, `GameHUD` exposes pause, restart, stop, and mode switching.

### Building the native DLL

See [NativeAudioEngine/README.md](NativeAudioEngine/README.md).

## Audio backend options

| Backend | Latency | Notes |
|---|---|---|
| WASAPI Shared | Can easily feel well over `100-200 ms` input-to-output in practice | Safest compatibility path, but also the worst choice for responsive live playing |
| WASAPI Exclusive | Often better than shared, but still hardware/driver dependent and not guaranteed to feel low-latency | Takes exclusive control of the device |
| ASIO | Usually the best option here; on a decent interface it is often within roughly `20 ms` total feel, sometimes better | Requires a proper ASIO driver |
| Auto | Varies | Tries preferred ASIO first, then Windows fallbacks |

Important latency note:

- The numbers reported by Unity, the driver, or `AudioLatencyDiagnostics` are best treated as theoretical/output-side buffer figures, not guaranteed end-to-end MIDI-input-to-audible-output latency.
- Real felt latency depends on the MIDI device, USB stack, driver buffering, output device, and overall Windows audio path.
- For live playing, ASIO is strongly recommended. WASAPI should be treated mainly as a compatibility fallback unless it has been tested on the target machine.

## Voice parameters

- `Max voices`: polyphony cap
- `Requested buffer frames`: requested audio buffer size for the native engine
- `Release fade ms`: fade duration after note-off
- `Preferred device name`: preferred output device substring, default `Focusrite USB ASIO`

## Dependencies

- [Keijiro Minis](https://github.com/keijiro/Minis)
- [JUCE](https://juce.com/) 8.0.12
- Unity Input System
- TextMeshPro
