using System.Collections;
using UnityEngine;

public class PianoVoice : MonoBehaviour
{
    private AudioSource audioSource;
    private Coroutine fadeCoroutine;

    public bool IsBusy => audioSource != null && audioSource.isPlaying;
    public float StartTime { get; private set; }
    public int MidiNote { get; private set; }

    public void Initialize(AudioSource source)
    {
        audioSource = source;
    }

    public void Play(int midiNote, AudioClip clip, float volume, float pitch)
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        MidiNote = midiNote;
        StartTime = Time.time;

        audioSource.clip = clip;
        audioSource.volume = volume;
        audioSource.pitch = pitch;
        audioSource.Play();
    }

    public void StopImmediate()
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        audioSource.Stop();
        audioSource.clip = null;
        audioSource.pitch = 1f;
    }

    public void StopWithFade(float fadeTime)
    {
        if (!audioSource.isPlaying)
        {
            StopImmediate();
            return;
        }

        if (fadeCoroutine != null)
            StopCoroutine(fadeCoroutine);

        fadeCoroutine = StartCoroutine(FadeOutRoutine(fadeTime));
    }

    private IEnumerator FadeOutRoutine(float fadeTime)
    {
        float startVolume = audioSource.volume;
        float startPitch = audioSource.pitch;
        float timer = 0f;

        while (timer < fadeTime && audioSource.isPlaying)
        {
            timer += Time.deltaTime;
            float t = timer / fadeTime;
            audioSource.volume = Mathf.Lerp(startVolume, 0f, t);
            yield return null;
        }

        audioSource.Stop();
        audioSource.clip = null;
        audioSource.volume = startVolume;
        audioSource.pitch = startPitch;
        fadeCoroutine = null;
    }
}