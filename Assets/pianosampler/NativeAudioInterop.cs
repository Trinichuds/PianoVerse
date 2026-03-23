using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeAudioInterop
{
    public enum BackendKind
    {
        WasapiShared = 0,
        WasapiExclusive = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineConfig
    {
        public int sampleRate;
        public int channelCount;
        public int maxVoices;
        public int backend;
        public int requestedBufferFrames;
        public int releaseFadeMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleDescriptor
    {
        public int rootMidiNote;
        public int minVelocity;
        public int maxVelocity;
        public int channelCount;
        public int sampleRate;
        public int frameCount;
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const string DllName = "NativePianoBackend";

    [DllImport(DllName, EntryPoint = "np_create_engine", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateEngine(ref EngineConfig config);

    [DllImport(DllName, EntryPoint = "np_destroy_engine", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyEngine(IntPtr engine);

    [DllImport(DllName, EntryPoint = "np_start_engine", CallingConvention = CallingConvention.Cdecl)]
    private static extern int StartEngine(IntPtr engine);

    [DllImport(DllName, EntryPoint = "np_stop_engine", CallingConvention = CallingConvention.Cdecl)]
    private static extern void StopEngine(IntPtr engine);

    [DllImport(DllName, EntryPoint = "np_clear_samples", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ClearSamples(IntPtr engine);

    [DllImport(DllName, EntryPoint = "np_register_sample", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RegisterSample(IntPtr engine, ref SampleDescriptor descriptor, float[] interleavedData, int sampleCount);

    [DllImport(DllName, EntryPoint = "np_note_on", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NoteOn(IntPtr engine, int midiNote, float velocityNormalized);

    [DllImport(DllName, EntryPoint = "np_note_off", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NoteOff(IntPtr engine, int midiNote);

    [DllImport(DllName, EntryPoint = "np_set_sustain", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetSustain(IntPtr engine, int sustainOn);

    [DllImport(DllName, EntryPoint = "np_get_last_error", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetLastErrorMessage(IntPtr engine, StringBuilder message, int capacity);
#endif

    public static bool IsSupported =>
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        true;
#else
        false;
#endif

    public static IntPtr TryCreateEngine(ref EngineConfig config)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return CreateEngine(ref config);
#else
        return IntPtr.Zero;
#endif
    }

    public static void TryDestroyEngine(IntPtr engine)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine != IntPtr.Zero)
            DestroyEngine(engine);
#endif
    }

    public static int TryStartEngine(IntPtr engine)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return StartEngine(engine);
#else
        return -1;
#endif
    }

    public static void TryStopEngine(IntPtr engine)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine != IntPtr.Zero)
            StopEngine(engine);
#endif
    }

    public static void TryClearSamples(IntPtr engine)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine != IntPtr.Zero)
            ClearSamples(engine);
#endif
    }

    public static int TryRegisterSample(IntPtr engine, ref SampleDescriptor descriptor, float[] interleavedData)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return RegisterSample(engine, ref descriptor, interleavedData, interleavedData?.Length ?? 0);
#else
        return -1;
#endif
    }

    public static int TryNoteOn(IntPtr engine, int midiNote, float velocityNormalized)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return NoteOn(engine, midiNote, velocityNormalized);
#else
        return -1;
#endif
    }

    public static void TryNoteOff(IntPtr engine, int midiNote)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine != IntPtr.Zero)
            NoteOff(engine, midiNote);
#endif
    }

    public static void TrySetSustain(IntPtr engine, bool sustainOn)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine != IntPtr.Zero)
            SetSustain(engine, sustainOn ? 1 : 0);
#endif
    }

    public static string TryGetLastError(IntPtr engine)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (engine == IntPtr.Zero)
            return "Engine handle is null.";

        var builder = new StringBuilder(1024);
        int written = GetLastErrorMessage(engine, builder, builder.Capacity);
        return written > 0 ? builder.ToString() : "Unknown native audio backend error.";
#else
        return "Native audio backend is only supported on Windows.";
#endif
    }
}
