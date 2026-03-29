using System;

/// <summary>
/// A single note event: which key, when it starts, how long it lasts.
/// keyIndex 0 = A0, 87 = C8. start and dur are in seconds.
/// </summary>
[Serializable]
public class NoteEvent
{
    public int   key;   // 0–87
    public float start; // seconds from song start
    public float dur;   // seconds held
}

/// <summary>
/// A complete song notemap, serialized to/from JSON.
/// </summary>
[Serializable]
public class NoteMap
{
    public string      title;
    public float       bpm;
    public float       durationSeconds;
    public NoteEvent[] notes;

    // Runtime-only: source file path (not serialized)
    [NonSerialized] public string filePath;
}
