using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class SalamanderSampleBankBuilder
{
    private static readonly Regex FilePattern =
        new Regex(@"^([A-G])(#?)(-?\d+)v(\d+)$", RegexOptions.IgnoreCase);

    // Editor tool: picks a folder of Salamander wav files, parses their names
    // to extract MIDI note and velocity layer, then creates a PianoSampleBank asset.
    [MenuItem("Tools/Piano/Build Salamander Sample Bank")]
    public static void BuildSampleBank()
    {
        string samplesFolder = EditorUtility.OpenFolderPanel(
            "Select Salamander sample folder",
            Application.dataPath,
            ""
        );

        if (string.IsNullOrEmpty(samplesFolder))
            return;

        if (!samplesFolder.StartsWith(Application.dataPath))
        {
            Debug.LogError("Selected folder must be inside this project's Assets folder.");
            return;
        }

        string assetFolder = "Assets" + samplesFolder.Substring(Application.dataPath.Length);

        string savePath = EditorUtility.SaveFilePanelInProject(
            "Save Piano Sample Bank",
            "SalamanderSampleBank",
            "asset",
            "Choose where to save the generated bank"
        );

        if (string.IsNullOrEmpty(savePath))
            return;

        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { assetFolder });

        var noteToLayers = new Dictionary<int, List<(int layer, AudioClip clip)>>();

        foreach (string guid in guids)
        {
            string clipPath = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileNameWithoutExtension(clipPath);

            if (!TryParseFileName(fileName, out int midiNote, out int layerIndex))
            {
                Debug.LogWarning($"Skipped file with unrecognized name: {fileName}");
                continue;
            }

            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip == null)
                continue;

            if (!noteToLayers.ContainsKey(midiNote))
                noteToLayers[midiNote] = new List<(int layer, AudioClip clip)>();

            noteToLayers[midiNote].Add((layerIndex, clip));
        }

        var bank = ScriptableObject.CreateInstance<PianoSampleBank>();
        bank.keySamples = new PianoKeySample[88];

        for (int midiNote = 21; midiNote <= 108; midiNote++)
        {
            int keyIndex = midiNote - 21;

            var keySample = new PianoKeySample();
            keySample.midiNote = midiNote;

            if (noteToLayers.TryGetValue(midiNote, out var layers))
            {
                layers.Sort((a, b) => a.layer.CompareTo(b.layer));

                int layerCount = layers.Count;
                var velocityLayers = new VelocityLayerClip[layerCount];

                for (int i = 0; i < layerCount; i++)
                {
                    int minVelocity = Mathf.FloorToInt((i * 127f) / layerCount) + 1;
                    int maxVelocity = Mathf.FloorToInt(((i + 1) * 127f) / layerCount);

                    if (i == layerCount - 1)
                        maxVelocity = 127;

                    velocityLayers[i] = new VelocityLayerClip
                    {
                        minVelocity = minVelocity,
                        maxVelocity = maxVelocity,
                        clip = layers[i].clip
                    };
                }

                keySample.velocityLayers = velocityLayers;
            }
            else
            {
                keySample.velocityLayers = Array.Empty<VelocityLayerClip>();
                Debug.LogWarning($"No samples found for MIDI note {midiNote}");
            }

            bank.keySamples[keyIndex] = keySample;
        }

        AssetDatabase.CreateAsset(bank, savePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Built PianoSampleBank: {savePath}");
    }

    // Parses filenames like "A0v1", "C#4v3" into MIDI note number and velocity layer.
    private static bool TryParseFileName(string fileName, out int midiNote, out int layerIndex)
    {
        midiNote = -1;
        layerIndex = -1;

        Match match = FilePattern.Match(fileName);
        if (!match.Success)
            return false;

        string noteLetter = match.Groups[1].Value.ToUpperInvariant();
        bool sharp = !string.IsNullOrEmpty(match.Groups[2].Value);
        int octave = int.Parse(match.Groups[3].Value);
        layerIndex = int.Parse(match.Groups[4].Value);

        string noteName = noteLetter + (sharp ? "#" : "");
        midiNote = NoteNameToMidi(noteName, octave);

        return midiNote >= 21 && midiNote <= 108;
    }

    private static int NoteNameToMidi(string noteName, int octave)
    {
        int semitone = noteName switch
        {
            "C" => 0,
            "C#" => 1,
            "D" => 2,
            "D#" => 3,
            "E" => 4,
            "F" => 5,
            "F#" => 6,
            "G" => 7,
            "G#" => 8,
            "A" => 9,
            "A#" => 10,
            "B" => 11,
            _ => -1
        };

        if (semitone < 0)
            return -1;

        return (octave + 1) * 12 + semitone;
    }
}