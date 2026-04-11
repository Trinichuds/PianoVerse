using System.Collections.Generic;
using UnityEngine;

public class MidiPianoSampler : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Midi88KeyInput midiInput;
    [SerializeField] private PianoSampleBank sampleBank;

    [Header("Voice Settings")]
    [SerializeField] private int voicePoolSize = 48;
    [SerializeField] private float minVolume = 0.2f;
    [SerializeField] private float maxVolume = 1.0f;

    [Header("Release")]
    [SerializeField] private bool stopOnNoteOff = false;
    [SerializeField] private float releaseFadeTime = 0.15f;

    [Header("Pitch Shift")]
    [SerializeField] private bool usePitchShiftForMissingNotes = true;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = false;

    private readonly List<PianoVoice> voicePool = new();
    private readonly Dictionary<int, List<PianoVoice>> activeVoicesByMidiNote = new();

    private void Awake()
    {
        CreateVoicePool();
    }

    private void OnEnable()
    {
        if (midiInput == null) return;

        midiInput.NotePressed += OnNotePressed;
        midiInput.NoteReleased += OnNoteReleased;
        midiInput.NoteFullyEnded += OnNoteFullyEnded;
        midiInput.SustainChanged += OnSustainChanged;
    }

    private void OnDisable()
    {
        if (midiInput == null) return;

        midiInput.NotePressed -= OnNotePressed;
        midiInput.NoteReleased -= OnNoteReleased;
        midiInput.NoteFullyEnded -= OnNoteFullyEnded;
        midiInput.SustainChanged -= OnSustainChanged;
    }

    // Creates AudioSource GameObjects for polyphonic playback.
    private void CreateVoicePool()
    {
        for (int i = 0; i < voicePoolSize; i++)
        {
            GameObject voiceObject = new GameObject($"PianoVoice_{i}");
            voiceObject.transform.SetParent(transform);

            AudioSource source = voiceObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.priority = 0;
            source.dopplerLevel = 0f;
            source.reverbZoneMix = 0f;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;

            PianoVoice voice = voiceObject.AddComponent<PianoVoice>();
            voice.Initialize(source);

            voicePool.Add(voice);
        }
    }

    // Finds the matching sample (or nearest with pitch shift), gets a voice, and plays.
    private void OnNotePressed(int keyIndex, int midiNote, float velocityNormalized)
    {
        if (sampleBank == null) return;

        int velocity127 = Mathf.Clamp(Mathf.RoundToInt(velocityNormalized * 127f), 1, 127);

        int sampledMidiNote = midiNote;
        AudioClip clip = null;

        if (sampleBank.HasAnyLayers(midiNote))
        {
            clip = sampleBank.GetClipExact(midiNote, velocity127);
        }
        else if (usePitchShiftForMissingNotes)
        {
            clip = sampleBank.GetNearestClip(midiNote, velocity127, out sampledMidiNote);
        }

        if (clip == null)
        {
            Debug.LogWarning($"No sample found for MIDI note {midiNote}, velocity {velocity127}");
            return;
        }

        PianoVoice voice = GetFreeVoice();
        if (voice == null)
        {
            Debug.LogWarning("No available piano voice in pool.");
            return;
        }

        float volume = Mathf.Lerp(minVolume, maxVolume, velocityNormalized);
        float pitch = GetPitchShift(sampledMidiNote, midiNote);

        voice.Play(midiNote, clip, volume, pitch);

        if (!activeVoicesByMidiNote.ContainsKey(midiNote))
            activeVoicesByMidiNote[midiNote] = new List<PianoVoice>();

        activeVoicesByMidiNote[midiNote].Add(voice);

        if (verboseLogging)
            Debug.Log($"Played MIDI {midiNote} using sample {sampledMidiNote}, pitch {pitch:0.000}");
    }

    private void OnNoteReleased(int keyIndex, int midiNote)
    {
        if (midiInput != null && midiInput.SustainOn)
            return;

        if (stopOnNoteOff)
            StopVoicesForNote(midiNote);
    }

    private void OnNoteFullyEnded(int keyIndex, int midiNote)
    {
        StopVoicesForNote(midiNote);
    }

    private void OnSustainChanged(bool isOn, float rawValue)
    {
        if (isOn) return;

        List<int> notesToCheck = new(activeVoicesByMidiNote.Keys);

        for (int i = 0; i < notesToCheck.Count; i++)
        {
            int midiNote = notesToCheck[i];
            int keyIndex = midiInput.ToKeyIndex(midiNote);

            if (!midiInput.IsKeyAudiblyActive(keyIndex))
                StopVoicesForNote(midiNote);
        }
    }

    private void StopVoicesForNote(int midiNote)
    {
        if (!activeVoicesByMidiNote.TryGetValue(midiNote, out List<PianoVoice> voices))
            return;

        for (int i = voices.Count - 1; i >= 0; i--)
        {
            PianoVoice voice = voices[i];
            if (voice == null) continue;

            if (releaseFadeTime > 0f)
                voice.StopWithFade(releaseFadeTime);
            else
                voice.StopImmediate();
        }

        voices.Clear();
        activeVoicesByMidiNote.Remove(midiNote);
    }

    // Returns an idle voice, or steals the oldest playing voice if all busy.
    private PianoVoice GetFreeVoice()
    {
        for (int i = 0; i < voicePool.Count; i++)
        {
            if (!voicePool[i].IsBusy)
                return voicePool[i];
        }

        PianoVoice oldest = null;
        float oldestTime = float.MaxValue;

        for (int i = 0; i < voicePool.Count; i++)
        {
            if (voicePool[i].StartTime < oldestTime)
            {
                oldestTime = voicePool[i].StartTime;
                oldest = voicePool[i];
            }
        }

        if (oldest != null)
        {
            RemoveVoiceFromTracking(oldest);
            oldest.StopImmediate();
        }

        return oldest;
    }

    private void RemoveVoiceFromTracking(PianoVoice voice)
    {
        List<int> keys = new(activeVoicesByMidiNote.Keys);

        for (int i = 0; i < keys.Count; i++)
        {
            List<PianoVoice> voices = activeVoicesByMidiNote[keys[i]];
            if (voices.Remove(voice) && voices.Count == 0)
                activeVoicesByMidiNote.Remove(keys[i]);
        }
    }

    // Calculates pitch multiplier: 2^(semitones/12) for resampling.
    private float GetPitchShift(int sampledMidiNote, int targetMidiNote)
    {
        int semitoneOffset = targetMidiNote - sampledMidiNote;
        return Mathf.Pow(2f, semitoneOffset / 12f);
    }
}
