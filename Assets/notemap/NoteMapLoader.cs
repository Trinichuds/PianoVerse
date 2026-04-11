using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// On startup, scans StreamingAssets/Maps for .mid files.
/// Any .mid without a matching .json gets parsed and cached.
/// All .json files are loaded into LoadedMaps.
///
/// Drop a new .mid into StreamingAssets/Maps and restart — it auto-converts.
/// </summary>
public class NoteMapLoader : MonoBehaviour
{
    public static NoteMapLoader Instance { get; private set; }

    /// <summary>All successfully loaded maps, sorted by title.</summary>
    public List<NoteMap> LoadedMaps { get; private set; } = new();

    /// <summary>True once the startup scan is complete.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Fired when all maps are loaded + delay has elapsed.</summary>
    public event Action<List<NoteMap>> OnMapsLoaded;

    private static string MapsDir =>
        Path.Combine(Application.streamingAssetsPath, "Maps");

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        StartCoroutine(LoadAll());
    }

    // Scans Maps folder, auto-converts any .mid without a .json, loads all .json maps.
    // Yields between files to avoid frame stalls.
    private IEnumerator LoadAll()
    {
        if (!Directory.Exists(MapsDir))
        {
            Directory.CreateDirectory(MapsDir);
            Debug.Log($"[NoteMapLoader] Created Maps folder at {MapsDir}");
        }

        string[] midFiles  = Directory.GetFiles(MapsDir, "*.mid");
        string[] midiFiles = Directory.GetFiles(MapsDir, "*.midi");

        var allMidPaths = new List<string>(midFiles);
        allMidPaths.AddRange(midiFiles);

        int converted = 0;
        int failed    = 0;

        foreach (string midPath in allMidPaths)
        {
            string jsonPath = Path.ChangeExtension(midPath, ".json");

            if (!File.Exists(jsonPath))
            {
                Debug.Log($"[NoteMapLoader] Converting: {Path.GetFileName(midPath)}");
                bool ok = TryConvert(midPath, jsonPath);
                if (ok) converted++;
                else    failed++;
            }

            yield return null; // spread across frames so we don't stall
        }

        // Load all json files
        LoadedMaps.Clear();
        foreach (string jsonPath in Directory.GetFiles(MapsDir, "*.json"))
        {
            NoteMap map = TryLoadJson(jsonPath);
            if (map != null)
                LoadedMaps.Add(map);
            yield return null;
        }

        LoadedMaps.Sort((a, b) => string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase));

        IsReady = true;
        Debug.Log($"[NoteMapLoader] Ready — {LoadedMaps.Count} maps loaded ({converted} converted, {failed} failed).");
        OnMapsLoaded?.Invoke(LoadedMaps);
    }

    // -------------------------------------------------------------------------

    // Reads a .mid file, parses it with MidiParser, serializes to JSON, writes to disk.
    private static bool TryConvert(string midPath, string jsonPath)
    {
        try
        {
            byte[]  bytes = File.ReadAllBytes(midPath);
            string  title = Path.GetFileNameWithoutExtension(midPath);
            NoteMap map   = MidiParser.Parse(bytes, title);

            string json = JsonUtility.ToJson(map, prettyPrint: true);
            File.WriteAllText(jsonPath, json);

            Debug.Log($"[NoteMapLoader] Converted '{title}' — " +
                      $"{map.notes.Length} notes, {map.durationSeconds:F1}s, {map.bpm:F0} BPM");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[NoteMapLoader] Failed to convert '{Path.GetFileName(midPath)}': {e.Message}");
            return false;
        }
    }

    // Deserializes a .json map file into a NoteMap object.
    private static NoteMap TryLoadJson(string jsonPath)
    {
        try
        {
            string  json = File.ReadAllText(jsonPath);
            NoteMap map  = JsonUtility.FromJson<NoteMap>(json);
            map.filePath = jsonPath;
            return map;
        }
        catch (Exception e)
        {
            Debug.LogError($"[NoteMapLoader] Failed to load '{Path.GetFileName(jsonPath)}': {e.Message}");
            return null;
        }
    }

    /// <summary>Force re-convert a specific .mid and reload it.</summary>
    public void Reconvert(string midPath)
    {
        string jsonPath = Path.ChangeExtension(midPath, ".json");
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
        StartCoroutine(LoadAll());
    }
}
