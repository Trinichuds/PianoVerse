using UnityEngine;

/// <summary>
/// Procedural audio feedback for calibration events.
/// Attach to the same GameObject as PianoCalibrationInput, or any object with an AudioSource.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class CalibrationSFX : MonoBehaviour
{
    private AudioSource _source;
    private AudioClip _blip;
    private AudioClip _chime;

    private void Awake()
    {
        _source = GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;

        _blip = CreateTone("CalibBlip", 880f, 0.08f);
        _chime = CreateChime("CalibChime", new[] { 1046.5f, 1318.5f }, 0.18f);
    }

    public void PlayBlip()
    {
        _source.PlayOneShot(_blip, 0.5f);
    }

    public void PlayChime()
    {
        _source.PlayOneShot(_chime, 0.6f);
    }

    // Generates a sine wave AudioClip with quadratic fade-out envelope.
    private static AudioClip CreateTone(string name, float freq, float duration)
    {
        int sampleRate = 44100;
        int samples = Mathf.CeilToInt(sampleRate * duration);
        var data = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = 1f - (t / duration);
            envelope *= envelope;
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 0.6f;
        }

        var clip = AudioClip.Create(name, samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Generates a multi-frequency chime by summing sine waves with fade-out.
    private static AudioClip CreateChime(string name, float[] freqs, float duration)
    {
        int sampleRate = 44100;
        int samples = Mathf.CeilToInt(sampleRate * duration);
        var data = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = 1f - (t / duration);
            envelope *= envelope;

            float val = 0f;
            for (int f = 0; f < freqs.Length; f++)
                val += Mathf.Sin(2f * Mathf.PI * freqs[f] * t);
            val /= freqs.Length;

            data[i] = val * envelope * 0.5f;
        }

        var clip = AudioClip.Create(name, samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
