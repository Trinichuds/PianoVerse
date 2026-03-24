using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

public class MidiImporter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private WaterfallBuilder waterfallBuilder;

    [Header("MIDI File")]
    [SerializeField] private string midiFileName = "Super Mario 64 - Medley.mid";
    [SerializeField] private string midiFolder = "MIDI";

    [Header("Options")]
    [SerializeField] private bool buildOnStart = true;
    [SerializeField] private bool logLoadedNotes = true;

    private void Start()
    {
        if (buildOnStart)
            StartCoroutine(BuildFromMidiCoroutine());
    }

    [ContextMenu("Build From MIDI")]
    public void BuildFromMidi()
    {
        StartCoroutine(BuildFromMidiCoroutine());
    }

    private IEnumerator BuildFromMidiCoroutine()
    {
        if (waterfallBuilder == null)
        {
            Debug.LogError("MidiImporter: waterfallBuilder is missing.");
            yield break;
        }

        string fullPath = Path.Combine(Application.streamingAssetsPath, midiFolder, midiFileName);
        byte[] midiBytes = null;

#if UNITY_ANDROID && !UNITY_EDITOR
        using (UnityWebRequest request = UnityWebRequest.Get(fullPath))
        {
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                Debug.LogError($"MidiImporter: failed to load MIDI file.\nPath: {fullPath}\nError: {request.error}");
                yield break;
            }

            midiBytes = request.downloadHandler.data;
        }
#else
        if (!File.Exists(fullPath))
        {
            Debug.LogError($"MidiImporter: MIDI file not found.\nPath: {fullPath}");
            yield break;
        }

        midiBytes = File.ReadAllBytes(fullPath);
#endif

        List<WaterfallNoteData> notes = LoadNotesFromBytes(midiBytes);
        waterfallBuilder.Build(notes);

        if (logLoadedNotes)
            Debug.Log($"MidiImporter: loaded {notes.Count} notes from {midiFileName}");
    }

    private List<WaterfallNoteData> LoadNotesFromBytes(byte[] midiBytes)
    {
        var result = new List<WaterfallNoteData>();

        using (var stream = new MemoryStream(midiBytes))
        {
            MidiFile midiFile = MidiFile.Read(stream);
            TempoMap tempoMap = midiFile.GetTempoMap();

            foreach (var note in midiFile.GetNotes())
            {
                int midiNote = note.NoteNumber;

                if (midiNote < 21 || midiNote > 108)
                    continue;

                MetricTimeSpan startMetric =
                    TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempoMap);

                MetricTimeSpan lengthMetric =
                    LengthConverter.ConvertTo<MetricTimeSpan>(note.Length, note.Time, tempoMap);

                float startSec = MetricToSeconds(startMetric);
                float durationSec = Mathf.Max(0.01f, MetricToSeconds(lengthMetric));
                float endSec = startSec + durationSec;

                result.Add(new WaterfallNoteData
                {
                    midiNote = midiNote,
                    startSec = startSec,
                    endSec = endSec,
                    velocity = note.Velocity
                });
            }
        }

        result.Sort((a, b) => a.startSec.CompareTo(b.startSec));
        return result;
    }

    private float MetricToSeconds(MetricTimeSpan time)
    {
        return time.Hours * 3600f +
               time.Minutes * 60f +
               time.Seconds +
               time.Milliseconds / 1000f;
    }
}