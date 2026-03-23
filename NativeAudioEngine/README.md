# Native Piano Backend

C++ audio engine for the Unity MIDI piano sampler. Exposes a small C ABI that Unity calls via `DllImport`, accepts PCM sample data uploaded from `PianoSampleBank`, and mixes active voices in a dedicated render loop.

Supports **WASAPI Shared**, **WASAPI Exclusive**, and **ASIO** output via the [JUCE](https://juce.com/) audio framework.

## C API

```c
np_create_engine()   / np_destroy_engine()
np_start_engine()    / np_stop_engine()
np_clear_samples()   / np_register_sample()
np_note_on()         / np_note_off()
np_set_sustain()
np_get_last_error()
```

See `src/NativeAudioInterop.h` for the full signatures.

## Build

Requires CMake 3.24+ and MSVC (Visual Studio 2022 or a standalone Build Tools install). JUCE is fetched automatically by CMake — no manual download needed.

From the **repo root**:

```powershell
cmake -S NativeAudioEngine -B NativeAudioEngine/build-juce -A x64
cmake --build NativeAudioEngine/build-juce --config Release
```

Copy the output DLL to the Unity plugins folder:

```
Assets/Plugins/x86_64/NativePianoBackend.dll
```

Unity resolves the `NativeAudioInterop` imports automatically on Windows x64.

## Source files

| File | Description |
|---|---|
| `src/JucePianoBackend.cpp` | Active implementation — JUCE-based, supports WASAPI + ASIO |
| `src/NativePianoBackend.cpp` | Legacy WASAPI-only implementation (kept for reference) |
| `src/NativeAudioInterop.h` | Exported C ABI |
| `CMakeLists.txt` | Build definition; fetches JUCE 8.0.12 via FetchContent |

## Backend selection

The engine selects a device type and name at `np_start_engine` time based on the `BackendKind` value passed from C#:

| Value | Behaviour |
|---|---|
| `0` Auto | Tries ASIO → WASAPI Exclusive → WASAPI Shared |
| `1` WasapiShared | Forces WASAPI shared mode |
| `2` WasapiExclusive | Forces WASAPI exclusive mode |
| `3` Asio | Forces ASIO; matches preferred device name substring |

Default preferred device name is `"Focusrite USB ASIO"`. Change it in `NativePianoSampler.cs` inspector or in code.

## Voice engine details

- Up to 64 simultaneous voices (configurable)
- Pitch-shifting via playback step: `step = srcRate / outRate * pow(2, semitones/12)`
- Velocity gain floor: 0.2 (soft notes never go fully silent)
- Release fade: linear over configurable duration (default 150 ms)
- Thread safety: recursive mutex guards sample bank access from audio thread
- Voice stealing: oldest-started voice is reused when the pool is exhausted
