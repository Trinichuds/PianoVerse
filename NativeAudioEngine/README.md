# Native Piano Backend

C++ audio engine for the Unity MIDI piano sampler. It exposes a small C ABI that Unity calls via `DllImport`, accepts PCM sample data uploaded from `PianoSampleBank`, and mixes active voices in a dedicated render loop.

Supports WASAPI Shared, WASAPI Exclusive, and ASIO output via [JUCE](https://juce.com/).

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

Requires CMake 3.24+ and MSVC. JUCE is fetched automatically by CMake.

From the repo root:

```powershell
cmake -S NativeAudioEngine -B NativeAudioEngine/build-juce -A x64
cmake --build NativeAudioEngine/build-juce --config Release
```

Copy the output DLL to:

```text
Assets/Plugins/x86_64/NativePianoBackend.dll
```

## Source files

| File | Description |
|---|---|
| `src/JucePianoBackend.cpp` | Active implementation with WASAPI + ASIO support |
| `src/NativePianoBackend.cpp` | Legacy WASAPI-only implementation |
| `src/NativeAudioInterop.h` | Exported C ABI |
| `CMakeLists.txt` | Build definition and JUCE fetch |

## Backend selection

The engine selects the output path at `np_start_engine` time based on the `BackendKind` value from C#:

| Value | Behaviour |
|---|---|
| `-1` Auto | Tries Focusrite-style ASIO first, then Windows Audio low-latency, exclusive, and shared fallbacks |
| `0` WasapiShared | Forces WASAPI shared mode |
| `1` WasapiExclusive | Forces WASAPI exclusive mode |
| `2` Asio | Forces ASIO and matches the preferred device name |

Default preferred device name is `Focusrite USB ASIO`.

## Voice engine details

- Up to 64 simultaneous voices by default
- Pitch-shifting by playback-rate step
- Velocity gain floor of `0.2`
- Configurable release fade
- Oldest voice is stolen when the pool is exhausted
