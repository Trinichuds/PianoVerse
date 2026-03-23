using System;
using UnityEngine;

[Serializable]
public struct WaterfallNoteData
{
    [Range(21, 108)] public int midiNote;   // Piano range: A0 ~ C8
    public float startSec;                  // When the note should hit the line
    public float endSec;                    // Note release time
    [Range(1, 127)] public int velocity;    // Optional for later use

    public float DurationSec => Mathf.Max(0f, endSec - startSec);
}