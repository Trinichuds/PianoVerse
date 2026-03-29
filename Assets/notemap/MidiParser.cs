using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Parses standard MIDI files (format 0 and format 1) into a NoteMap.
/// Handles tempo changes, running status, and Note On with velocity=0 as Note Off.
/// </summary>
public static class MidiParser
{
    private struct RawNote
    {
        public long tick;
        public byte midiNote;
        public byte channel;
        public bool isOn;
    }

    private struct TempoChange
    {
        public long tick;
        public int  microsPerBeat;
    }

    public static NoteMap Parse(byte[] data, string title = "")
    {
        int pos = 0;
        int len = data.Length;

        if (!MatchTag(data, ref pos, len, "MThd"))
            throw new InvalidDataException("Not a valid MIDI file — missing MThd header.");

        ReadInt32(data, ref pos, len); // header length, always 6
        int format    = ReadInt16(data, ref pos, len);
        int numTracks = ReadInt16(data, ref pos, len);
        int ppq       = ReadInt16(data, ref pos, len);

        if ((ppq & 0x8000) != 0)
            throw new NotSupportedException("SMPTE time division not supported.");

        if (format > 1)
            throw new NotSupportedException($"MIDI format {format} not supported (only 0 and 1).");

        var rawNotes     = new List<RawNote>();
        var tempoChanges = new List<TempoChange>();

        for (int t = 0; t < numTracks; t++)
        {
            if (pos >= len) break;

            if (!MatchTag(data, ref pos, len, "MTrk"))
            {
                // Unknown chunk — skip it
                int skip = ReadInt32(data, ref pos, len);
                pos += skip;
                continue;
            }

            int trackLen = ReadInt32(data, ref pos, len);
            int trackEnd = Math.Min(pos + trackLen, len);
            ParseTrack(data, ref pos, trackEnd, rawNotes, tempoChanges);
            pos = trackEnd; // guarantee alignment even if parser drifts
        }

        // Ensure there's always a default tempo at tick 0
        bool hasTick0Tempo = false;
        foreach (var tc in tempoChanges)
        {
            if (tc.tick == 0) { hasTick0Tempo = true; break; }
        }
        if (!hasTick0Tempo)
            tempoChanges.Insert(0, new TempoChange { tick = 0, microsPerBeat = 500000 });

        tempoChanges.Sort((a, b) => a.tick.CompareTo(b.tick));
        rawNotes.Sort((a, b) => a.tick.CompareTo(b.tick));

        // Match Note On / Off pairs
        // pending[(midiNote, channel)] = startTick
        var pending    = new Dictionary<(byte, byte), long>();
        var noteEvents = new List<NoteEvent>();

        foreach (var rn in rawNotes)
        {
            var key = (rn.midiNote, rn.channel);
            if (rn.isOn)
            {
                // If same key already pending, flush it as a completed note first
                if (pending.TryGetValue(key, out long prevStart))
                {
                    FlushNote(prevStart, rn.tick, rn.midiNote, tempoChanges, ppq, noteEvents);
                }
                pending[key] = rn.tick;
            }
            else
            {
                if (pending.TryGetValue(key, out long startTick))
                {
                    pending.Remove(key);
                    FlushNote(startTick, rn.tick, rn.midiNote, tempoChanges, ppq, noteEvents);
                }
            }
        }

        noteEvents.Sort((a, b) => a.start.CompareTo(b.start));

        float duration = noteEvents.Count > 0
            ? noteEvents[noteEvents.Count - 1].start + noteEvents[noteEvents.Count - 1].dur
            : 0f;

        // BPM from the first tempo event (tick 0)
        float bpm = 60_000_000f / tempoChanges[0].microsPerBeat;

        return new NoteMap
        {
            title           = title,
            bpm             = bpm,
            durationSeconds = duration,
            notes           = noteEvents.ToArray()
        };
    }

    private static void FlushNote(long startTick, long endTick, byte midiNote,
        List<TempoChange> tempos, int ppq, List<NoteEvent> noteEvents)
    {
        if (midiNote < 21 || midiNote > 108) return;

        float start = TicksToSeconds(startTick, tempos, ppq);
        float end   = TicksToSeconds(endTick, tempos, ppq);
        float dur   = end - start;
        if (dur <= 0f) return;

        noteEvents.Add(new NoteEvent
        {
            key   = midiNote - 21,
            start = start,
            dur   = dur
        });
    }

    // -------------------------------------------------------------------------
    // Track parsing
    // -------------------------------------------------------------------------

    private static void ParseTrack(byte[] data, ref int pos, int trackEnd,
        List<RawNote> rawNotes, List<TempoChange> tempoChanges)
    {
        long absoluteTick  = 0;
        byte runningStatus = 0;

        while (pos < trackEnd)
        {
            long delta = ReadVLQ(data, ref pos, trackEnd);
            absoluteTick += delta;

            if (pos >= trackEnd) break;

            byte b = data[pos];

            if (b == 0xFF) // Meta event
            {
                pos++;
                if (pos >= trackEnd) break;
                byte metaType = data[pos++];
                int  metaLen  = ReadVLQ(data, ref pos, trackEnd);

                if (metaType == 0x51 && metaLen >= 3 && pos + 3 <= trackEnd) // Set Tempo
                {
                    int micros = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
                    if (micros > 0)
                        tempoChanges.Add(new TempoChange { tick = absoluteTick, microsPerBeat = micros });
                }

                pos += metaLen;
                runningStatus = 0;
            }
            else if (b == 0xF0 || b == 0xF7) // SysEx
            {
                pos++;
                int sysexLen = ReadVLQ(data, ref pos, trackEnd);
                pos += sysexLen;
                runningStatus = 0;
            }
            else if ((b & 0xF0) == 0xF0) // System real-time / common — skip
            {
                pos++;
                runningStatus = 0;
            }
            else
            {
                byte status;
                if ((b & 0x80) != 0)
                {
                    status = b;
                    pos++;
                    runningStatus = status;
                }
                else
                {
                    status = runningStatus;
                    if (status == 0) { pos++; continue; } // no running status yet, skip
                }

                byte eventType = (byte)(status & 0xF0);
                byte channel   = (byte)(status & 0x0F);
                int  dataBytes = DataBytesForEvent(eventType);

                if (pos + dataBytes > trackEnd) break;

                if ((eventType == 0x90 || eventType == 0x80) && dataBytes == 2)
                {
                    byte note     = data[pos++];
                    byte velocity = data[pos++];

                    bool isOn = eventType == 0x90 && velocity > 0;
                    rawNotes.Add(new RawNote
                    {
                        tick     = absoluteTick,
                        midiNote = note,
                        channel  = channel,
                        isOn     = isOn
                    });
                }
                else
                {
                    pos += dataBytes;
                }
            }
        }
    }

    private static int DataBytesForEvent(byte eventType) => eventType switch
    {
        0xC0 or 0xD0 => 1,
        0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 2,
        _ => 0
    };

    // -------------------------------------------------------------------------
    // Tick → seconds
    // -------------------------------------------------------------------------

    private static float TicksToSeconds(long tick, List<TempoChange> tempos, int ppq)
    {
        double seconds   = 0;
        long   prevTick  = 0;
        int    curMicros = 500000;

        foreach (var tc in tempos)
        {
            if (tc.tick >= tick) break;
            seconds  += (tc.tick - prevTick) * curMicros / (double)(ppq * 1_000_000L);
            prevTick  = tc.tick;
            curMicros = tc.microsPerBeat;
        }

        seconds += (tick - prevTick) * curMicros / (double)(ppq * 1_000_000L);
        return (float)seconds;
    }

    // -------------------------------------------------------------------------
    // Binary helpers (big-endian, bounds-checked)
    // -------------------------------------------------------------------------

    private static bool MatchTag(byte[] data, ref int pos, int len, string tag)
    {
        if (pos + tag.Length > len) return false;
        for (int i = 0; i < tag.Length; i++)
            if (data[pos + i] != (byte)tag[i]) return false;
        pos += tag.Length;
        return true;
    }

    private static int ReadInt32(byte[] data, ref int pos, int len)
    {
        if (pos + 4 > len) throw new InvalidDataException("Unexpected end of MIDI data (reading int32).");
        int v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
        pos += 4;
        return v;
    }

    private static int ReadInt16(byte[] data, ref int pos, int len)
    {
        if (pos + 2 > len) throw new InvalidDataException("Unexpected end of MIDI data (reading int16).");
        int v = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        return v;
    }

    private static int ReadVLQ(byte[] data, ref int pos, int limit)
    {
        int  v = 0;
        byte b;
        int  maxBytes = 4; // VLQ is at most 4 bytes in MIDI
        do
        {
            if (pos >= limit || maxBytes-- <= 0) break;
            b = data[pos++];
            v = (v << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);
        return v;
    }
}
