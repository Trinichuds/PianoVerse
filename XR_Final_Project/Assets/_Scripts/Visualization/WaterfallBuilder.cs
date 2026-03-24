using System;
using System.Collections.Generic;
using UnityEngine;

public class WaterfallBuilder : MonoBehaviour
{
    public float HitLineY => hitLineY;
    public float KeyboardWidth => keyboardWidth;
    public float WhiteLaneZ => whiteLaneZ;
    public float BlackLaneZ => blackLaneZ;

    [Header("References")]
    [SerializeField] private GameObject notePrefab;
    [SerializeField] private Transform waterfallRoot;

    [Header("Timing / Geometry")]
    [SerializeField] private float fallSpeed = 0.4f;     // meters per second
    [SerializeField] private float hitLineY = 0f;
    [SerializeField] private float minNoteHeight = 0.02f;
    [SerializeField] private float noteDepth = 0.03f;

    [Header("Temporary Keyboard Mapping")]
    [SerializeField] private float keyboardWidth = 1.22f; // temporary total width
    [SerializeField] private float whiteNoteWidth = 0.022f;
    [SerializeField] private float blackNoteWidth = 0.014f;
    [SerializeField] private float whiteLaneZ = 0f;
    [SerializeField] private float blackLaneZ = -0.015f;

    [Header("Debug / Test Data")]
    [SerializeField] private List<WaterfallNoteData> testNotes = new List<WaterfallNoteData>();

    private static readonly HashSet<int> BlackPitchClasses = new HashSet<int> { 1, 3, 6, 8, 10 };

    [ContextMenu("Build Test Waterfall")]
    public void BuildTestWaterfall()
    {
        Build(testNotes);
    }

    [ContextMenu("Clear Spawned Notes")]
    public void ClearSpawnedNotes()
    {
        if (waterfallRoot == null) return;

        for (int i = waterfallRoot.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(waterfallRoot.GetChild(i).gameObject);
        }
    }

    private void Start()
    {
        //BuildTestWaterfall();
    }


    public void Build(List<WaterfallNoteData> notes)
    {
        if (notePrefab == null)
        {
            Debug.LogError("WaterfallBuilder: notePrefab is missing.");
            return;
        }

        if (waterfallRoot == null)
        {
            Debug.LogError("WaterfallBuilder: waterfallRoot is missing.");
            return;
        }

        ClearSpawnedNotes();

        if (notes == null || notes.Count == 0)
        {
            Debug.LogWarning("WaterfallBuilder: no notes to build.");
            return;
        }

        foreach (var note in notes)
        {
            if (note.midiNote < 21 || note.midiNote > 108)
                continue;

            bool isBlack = IsBlackKey(note.midiNote);

            float width = isBlack ? blackNoteWidth : whiteNoteWidth;
            float height = Mathf.Max(minNoteHeight, note.DurationSec * fallSpeed);
            float x = GetXForMidiNote(note.midiNote);
            float z = isBlack ? blackLaneZ : whiteLaneZ;

            float centerY = hitLineY + note.startSec * fallSpeed + height * 0.5f;

            GameObject go = Instantiate(notePrefab, waterfallRoot);
            go.name = $"Note_{note.midiNote}_{note.startSec:F2}";

            go.transform.localPosition = new Vector3(x, centerY, z);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(width, height, noteDepth);

            WaterfallNoteView view = go.GetComponent<WaterfallNoteView>();
            if (view != null)
            {
                Color color = isBlack
                    ? new Color(0.30f, 0.60f, 1.00f)
                    : new Color(1.00f, 0.70f, 0.25f);

                view.SetColor(color);
            }
        }
    }

    private bool IsBlackKey(int midiNote)
    {
        int pitchClass = midiNote % 12;
        return BlackPitchClasses.Contains(pitchClass);
    }

    private float GetXForMidiNote(int midiNote)
    {
        // Temporary mapping: spread 88 notes evenly across keyboardWidth.
        // Later we will replace this with PianoKeyLayout88 for more accurate real-piano proportions.
        int keyIndex = midiNote - 21; // 0 ~ 87
        float step = keyboardWidth / 88f;
        float left = -keyboardWidth * 0.5f;

        return left + (keyIndex + 0.5f) * step;
    }
}