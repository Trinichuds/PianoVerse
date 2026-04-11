using System;
using System.Collections.Generic;
using UnityEngine;

public class NativePianoSampler : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Midi88KeyInput midiInput;
    [SerializeField] private PianoSampleBank sampleBank;

    [Header("Backend")]
    [SerializeField] private NativeAudioInterop.BackendKind backendKind = NativeAudioInterop.BackendKind.Auto;
    [SerializeField] private int maxVoices = 64;
    [SerializeField] private int requestedBufferFrames = 128;
    [SerializeField] private bool initializeOnStart = true;
    [SerializeField] private string preferredOutputDeviceName = "Focusrite USB ASIO";

    [Header("Behavior")]
    [SerializeField] private bool sendNoteOffWhenReleased = false;
    [Min(20)] public int releaseFadeMs = 150;
    [SerializeField] private bool logDiagnostics = true;

    private IntPtr engineHandle;
    private bool initialized;
    private bool pluginAvailable = true;
    private bool initializationAttempted;
    private readonly List<float[]> scratchBuffers = new();

    public bool IsReady => initialized && engineHandle != IntPtr.Zero;

    public NativeAudioInterop.BackendKind CurrentBackend => backendKind;

    public void SetBackend(NativeAudioInterop.BackendKind kind)
    {
        backendKind = kind;
        initializationAttempted = false;
    }

    /// <summary>Trigger a note programmatically (for Watch mode playback).</summary>
    public void PlayNote(int midiNote, float velocity)
    {
        if (!EnsureReady()) return;
        NativeAudioInterop.TryNoteOn(engineHandle, midiNote, Mathf.Clamp01(velocity));
    }

    /// <summary>Release a note programmatically (for Watch mode playback).</summary>
    public void StopNote(int midiNote)
    {
        if (!EnsureReady()) return;
        NativeAudioInterop.TryNoteOff(engineHandle, midiNote);
    }

    private void Start()
    {
        if (initializeOnStart)
            Initialize();
    }

    private void OnEnable()
    {
        if (midiInput == null)
            return;

        midiInput.NotePressed += OnNotePressed;
        midiInput.NoteReleased += OnNoteReleased;
        midiInput.NoteFullyEnded += OnNoteFullyEnded;
        midiInput.SustainChanged += OnSustainChanged;
    }

    private void OnDisable()
    {
        if (midiInput == null)
            return;

        midiInput.NotePressed -= OnNotePressed;
        midiInput.NoteReleased -= OnNoteReleased;
        midiInput.NoteFullyEnded -= OnNoteFullyEnded;
        midiInput.SustainChanged -= OnSustainChanged;
    }

    private void OnDestroy()
    {
        Shutdown();
    }

    // Creates the native engine, uploads all samples from the bank, and starts audio.
    // Returns false if DLL missing, no samples, or engine fails to start.
    public bool Initialize()
    {
        initializationAttempted = true;

        if (initialized)
            return true;

        if (!pluginAvailable || !NativeAudioInterop.IsSupported)
        {
            Debug.LogWarning("Native audio backend is unavailable on this platform.");
            return false;
        }

        if (sampleBank == null)
        {
            Debug.LogError("NativePianoSampler requires a PianoSampleBank reference.");
            return false;
        }

        var config = new NativeAudioInterop.EngineConfig
        {
            sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000,
            channelCount = 2,
            maxVoices = Mathf.Max(1, maxVoices),
            backend = (int)backendKind,
            requestedBufferFrames = Mathf.Max(32, requestedBufferFrames),
            releaseFadeMs = Mathf.Clamp(releaseFadeMs, 20, 4000),
            preferredOutputDeviceName = string.IsNullOrWhiteSpace(preferredOutputDeviceName)
                ? string.Empty
                : preferredOutputDeviceName.Trim()
        };

        try
        {
            engineHandle = NativeAudioInterop.TryCreateEngine(ref config);
        }
        catch (DllNotFoundException ex)
        {
            pluginAvailable = false;
            Debug.LogWarning($"Native audio plugin DLL not found. Build NativePianoBackend first. {ex.Message}");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            pluginAvailable = false;
            Debug.LogWarning($"Native audio plugin exports are missing or stale. {ex.Message}");
            return false;
        }

        if (engineHandle == IntPtr.Zero)
        {
            Debug.LogError("Native audio engine creation failed.");
            return false;
        }

        if (!UploadSampleBank())
        {
            Shutdown();
            return false;
        }

        int startResult = NativeAudioInterop.TryStartEngine(engineHandle);
        if (startResult != 0)
        {
            Debug.LogError($"Native audio engine start failed: {NativeAudioInterop.TryGetLastError(engineHandle)}");
            Shutdown();
            return false;
        }

        initialized = true;

        if (logDiagnostics)
        {
            Debug.Log(
                $"Native audio backend initialized. backend={backendKind}, " +
                $"sampleRate={config.sampleRate}, maxVoices={config.maxVoices}, " +
                $"requestedBufferFrames={config.requestedBufferFrames}, releaseFadeMs={config.releaseFadeMs}, " +
                $"preferredOutputDeviceName={config.preferredOutputDeviceName}"
            );
        }

        return true;
    }

    // Stops the native engine and releases the handle.
    public void Shutdown()
    {
        initialized = false;

        if (engineHandle == IntPtr.Zero)
            return;

        NativeAudioInterop.TryStopEngine(engineHandle);
        NativeAudioInterop.TryDestroyEngine(engineHandle);
        engineHandle = IntPtr.Zero;
    }

    // Reads PCM data from each AudioClip and uploads to native engine via P/Invoke.
    private bool UploadSampleBank()
    {
        NativeAudioInterop.TryClearSamples(engineHandle);
        scratchBuffers.Clear();

        int registeredCount = 0;

        for (int i = 0; i < sampleBank.keySamples.Length; i++)
        {
            PianoKeySample keySample = sampleBank.keySamples[i];
            if (keySample == null || keySample.velocityLayers == null)
                continue;

            for (int layerIndex = 0; layerIndex < keySample.velocityLayers.Length; layerIndex++)
            {
                VelocityLayerClip velocityLayer = keySample.velocityLayers[layerIndex];
                if (velocityLayer == null || velocityLayer.clip == null)
                    continue;

                AudioClip clip = velocityLayer.clip;
                int sampleCount = clip.samples * clip.channels;
                float[] interleaved = new float[sampleCount];

                if (!clip.GetData(interleaved, 0))
                {
                    Debug.LogError($"Failed to read PCM data from clip {clip.name}.");
                    return false;
                }

                scratchBuffers.Add(interleaved);

                var descriptor = new NativeAudioInterop.SampleDescriptor
                {
                    rootMidiNote = keySample.midiNote,
                    minVelocity = velocityLayer.minVelocity,
                    maxVelocity = velocityLayer.maxVelocity,
                    channelCount = clip.channels,
                    sampleRate = clip.frequency,
                    frameCount = clip.samples
                };

                int result = NativeAudioInterop.TryRegisterSample(engineHandle, ref descriptor, interleaved);
                if (result != 0)
                {
                    Debug.LogError(
                        $"Failed to register sample {clip.name} for MIDI {keySample.midiNote}: " +
                        $"{NativeAudioInterop.TryGetLastError(engineHandle)}"
                    );
                    return false;
                }

                registeredCount++;
            }
        }

        if (registeredCount == 0)
        {
            Debug.LogError("No samples were uploaded to the native backend.");
            return false;
        }

        if (logDiagnostics)
            Debug.Log($"Uploaded {registeredCount} sample layers to the native backend.");

        return true;
    }

    // Forwards MIDI note-on to the native engine for audio playback.
    private void OnNotePressed(int keyIndex, int midiNote, float velocityNormalized)
    {
        if (!EnsureReady())
            return;

        int result = NativeAudioInterop.TryNoteOn(engineHandle, midiNote, Mathf.Clamp01(velocityNormalized));
        if (result != 0)
            Debug.LogWarning($"Native note-on failed for MIDI {midiNote}: {NativeAudioInterop.TryGetLastError(engineHandle)}");
    }

    private void OnNoteReleased(int keyIndex, int midiNote)
    {
        if (!sendNoteOffWhenReleased || !EnsureReady())
            return;

        NativeAudioInterop.TryNoteOff(engineHandle, midiNote);
    }

    private void OnNoteFullyEnded(int keyIndex, int midiNote)
    {
        if (!EnsureReady())
            return;

        NativeAudioInterop.TryNoteOff(engineHandle, midiNote);
    }

    private void OnSustainChanged(bool sustainOn, float rawValue)
    {
        if (!EnsureReady())
            return;

        NativeAudioInterop.TrySetSustain(engineHandle, sustainOn);
    }

    // Lazy initialization: tries to init on first use if not already done.
    private bool EnsureReady()
    {
        if (initialized)
            return true;

        if (initializationAttempted)
            return false;

        return Initialize();
    }
}
