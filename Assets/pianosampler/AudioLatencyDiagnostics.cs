using UnityEngine;

public static class AudioLatencyDiagnostics
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Report()
    {
        AudioConfiguration config = AudioSettings.GetConfiguration();
        AudioSettings.GetDSPBufferSize(out int bufferLength, out int numBuffers);

        int sampleRate = config.sampleRate > 0 ? config.sampleRate : AudioSettings.outputSampleRate;
        double theoreticalOutputLatencyMs = sampleRate > 0
            ? (double)bufferLength * numBuffers * 1000.0 / sampleRate
            : 0.0;

        Debug.Log(
            $"Audio config: sampleRate={sampleRate}Hz, dspBuffer={bufferLength} x {numBuffers}, " +
            $"speakerMode={config.speakerMode}, realVoices={config.numRealVoices}, " +
            $"virtualVoices={config.numVirtualVoices}, theoreticalMixerLatency~={theoreticalOutputLatencyMs:0.0}ms"
        );
    }
}
