# Native Piano Backend

This folder contains a Windows native audio backend intended for low-latency piano playback outside Unity's normal `AudioSource` pipeline.

## What it does

- exposes a small C ABI that Unity can call with `DllImport`
- accepts PCM sample layers uploaded from `PianoSampleBank`
- mixes active voices in a native render loop
- targets WASAPI shared or WASAPI exclusive output

## Current status

This is a best-effort backend source implementation and Unity bridge. It was written in-workspace without a native compiler available, so it has not been compiled or runtime-tested here.

## Build

Use a Visual Studio Developer Command Prompt or any shell with CMake and MSVC available:

```powershell
cmake -S NativeAudioEngine -B NativeAudioEngine/build -A x64
cmake --build NativeAudioEngine/build --config Release
```

Then copy the built DLL to:

```text
Assets/Plugins/x86_64/NativePianoBackend.dll
```

Unity should then resolve the `NativeAudioInterop` imports automatically on Windows.

## Suggested next validation steps

1. Build the DLL.
2. Add `NativePianoSampler` to a scene object and wire the same `Midi88KeyInput` + `PianoSampleBank`.
3. Disable or remove the old `MidiPianoSampler` on that test object so both backends are not triggered at once.
4. Test `WasapiExclusive` first with wired output.
5. If exclusive mode fails on the target device, switch to `WasapiShared`.
