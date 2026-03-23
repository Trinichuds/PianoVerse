using UnityEngine;

[System.Serializable]
public class VelocityLayerClip
{
    [Range(1, 127)] public int minVelocity = 1;
    [Range(1, 127)] public int maxVelocity = 127;
    public AudioClip clip;
}

[System.Serializable]
public class PianoKeySample
{
    [Range(21, 108)] public int midiNote;
    public VelocityLayerClip[] velocityLayers;
}

[CreateAssetMenu(fileName = "PianoSampleBank", menuName = "Audio/Piano Sample Bank")]
public class PianoSampleBank : ScriptableObject
{
    public PianoKeySample[] keySamples = new PianoKeySample[88];

    public bool HasAnyLayers(int midiNote)
    {
        var key = GetKeySample(midiNote);
        return key != null && key.velocityLayers != null && key.velocityLayers.Length > 0;
    }

    public PianoKeySample GetKeySample(int midiNote)
    {
        for (int i = 0; i < keySamples.Length; i++)
        {
            if (keySamples[i] != null && keySamples[i].midiNote == midiNote)
                return keySamples[i];
        }

        return null;
    }

    public AudioClip GetClipExact(int midiNote, int velocity)
    {
        var key = GetKeySample(midiNote);
        if (key == null || key.velocityLayers == null || key.velocityLayers.Length == 0)
            return null;

        for (int i = 0; i < key.velocityLayers.Length; i++)
        {
            var layer = key.velocityLayers[i];
            if (velocity >= layer.minVelocity && velocity <= layer.maxVelocity)
                return layer.clip;
        }

        return key.velocityLayers[key.velocityLayers.Length - 1].clip;
    }

    public int FindNearestSampledMidiNote(int targetMidiNote)
    {
        int bestMidiNote = -1;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < keySamples.Length; i++)
        {
            var key = keySamples[i];
            if (key == null || key.velocityLayers == null || key.velocityLayers.Length == 0)
                continue;

            int distance = Mathf.Abs(key.midiNote - targetMidiNote);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMidiNote = key.midiNote;
            }
        }

        return bestMidiNote;
    }

    public AudioClip GetNearestClip(int targetMidiNote, int velocity, out int sampledMidiNote)
    {
        sampledMidiNote = FindNearestSampledMidiNote(targetMidiNote);
        if (sampledMidiNote < 0)
            return null;

        return GetClipExact(sampledMidiNote, velocity);
    }
}