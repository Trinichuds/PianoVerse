using System;
using System.Collections.Generic;
using UnityEngine;

public enum PlayMode { RealTime, Practice }
public enum HitQuality { Perfect, Great, Good, Miss }

[Serializable]
public struct HitResult
{
    public int keyIndex;
    public HitQuality quality;
    public float timingError; // negative = early, positive = late
}

/// <summary>
/// Drives playback of a NoteMap in two modes:
///
/// RealTime — notes scroll at tempo, player is scored on timing accuracy.
/// Practice — playback freezes at each step until the correct notes are held,
///            then advances to the next step.
///
/// Fires events that the visual layer (lights, waterfall, score UI) can subscribe to.
/// </summary>
public class NoteMapPlayer : MonoBehaviour
{
    [Header("References")]
    public Midi88KeyInput midiInput;

    [Header("Mode")]
    public PlayMode mode = PlayMode.Practice;

    [Header("Timing Windows — Real-Time (seconds)")]
    public float perfectWindow = 0.050f;
    public float greatWindow   = 0.100f;
    public float goodWindow    = 0.150f;

    [Header("Step Grouping — Practice")]
    [Tooltip("Notes within this many seconds of each other form one step.")]
    public float stepTolerance = 0.030f;

    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    public bool    IsPlaying   { get; private set; }
    public bool    IsPaused    { get; private set; }
    public float   PlaybackTime => _playbackTime;
    public NoteMap CurrentMap  { get; private set; }

    // Score counters (real-time)
    public int PerfectCount { get; private set; }
    public int GreatCount   { get; private set; }
    public int GoodCount    { get; private set; }
    public int MissCount    { get; private set; }
    public int TotalNotes   { get; private set; }

    // Practice state
    public int   CurrentStepIndex => _currentStepIndex;
    public int   TotalSteps       => _steps?.Count ?? 0;
    public int[] CurrentStepKeys  => _steps != null && _currentStepIndex < _steps.Count
        ? _steps[_currentStepIndex].keys : Array.Empty<int>();

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>A guide note should be shown (key should be pressed now/soon).</summary>
    public event Action<int, float> GuideNoteOn;   // keyIndex, duration

    /// <summary>A guide note's duration has ended.</summary>
    public event Action<int> GuideNoteOff;          // keyIndex

    /// <summary>A note was judged (hit or miss) in real-time mode.</summary>
    public event Action<HitResult> NoteJudged;

    /// <summary>Practice mode advanced to a new step.</summary>
    public event Action<int, int[]> StepChanged;    // stepIndex, requiredKeys

    /// <summary>Player pressed a wrong key in practice mode.</summary>
    public event Action<int> WrongKeyPressed;       // keyIndex

    /// <summary>Player pressed a correct key in practice mode (but step not yet complete).</summary>
    public event Action<int> CorrectKeyPressed;     // keyIndex

    /// <summary>Song finished.</summary>
    public event Action SongFinished;

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private float _playbackTime;
    private NoteEvent[] _notes;

    // Guide ref-counting: fires On when 0→1, Off when 1→0
    private int _nextGuideIndex;
    private readonly List<(int noteIndex, float endTime)> _guideEnds = new();
    private readonly Dictionary<int, int> _guideRefCount = new();

    // Real-time hit detection
    private int _nextHitIndex;                              // next note to enter hittable window
    private readonly List<int> _hittableNoteIndices = new();// notes in the timing window
    private readonly HashSet<int> _consumed = new();        // already judged

    // Practice mode
    private List<NoteStep> _steps;
    private int _currentStepIndex;
    private readonly HashSet<int> _heldKeys = new();
    private bool _stepSatisfied; // all keys in current step are held

    private class NoteStep
    {
        public float time;
        public int[] keys;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void LoadMap(NoteMap map)
    {
        Stop();
        CurrentMap = map;
        _notes = map.notes;

        if (mode == PlayMode.Practice)
            _steps = BuildSteps(map);
    }

    public void Play()
    {
        if (CurrentMap == null || _notes == null || _notes.Length == 0) return;

        ResetState();
        IsPlaying = true;
        IsPaused  = false;

        if (mode == PlayMode.Practice)
        {
            if (_steps.Count > 0)
            {
                _playbackTime = _steps[0].time;
                FireStepChanged();
            }
        }
        else
        {
            _playbackTime = 0f;
        }

        Debug.Log($"[NoteMapPlayer] Playing '{CurrentMap.title}' in {mode} mode — " +
                  $"{_notes.Length} notes" +
                  (mode == PlayMode.Practice ? $", {_steps.Count} steps" : ""));
    }

    public void TogglePause()
    {
        if (!IsPlaying) return;
        IsPaused = !IsPaused;
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused  = false;
        ClearAllGuides();
        ResetState();
    }

    public void SetMode(PlayMode newMode)
    {
        bool wasPlaying = IsPlaying;
        Stop();
        mode = newMode;
        if (CurrentMap != null)
        {
            LoadMap(CurrentMap);
            if (wasPlaying) Play();
        }
    }

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (midiInput == null)
            midiInput = FindObjectOfType<Midi88KeyInput>();
        if (midiInput == null)
            Debug.LogError("[NoteMapPlayer] No Midi88KeyInput found! Assign it in the Inspector.");
    }

    private void OnEnable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed    += OnPlayerPressed;
        midiInput.NoteFullyEnded += OnPlayerReleased;
        Debug.Log("[NoteMapPlayer] Subscribed to MIDI input.");
    }

    private void OnDisable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed    -= OnPlayerPressed;
        midiInput.NoteFullyEnded -= OnPlayerReleased;
    }

    private void Update()
    {
        if (!IsPlaying || IsPaused || _notes == null) return;

        if (mode == PlayMode.RealTime)
            UpdateRealTime();
        else
            UpdatePractice();

        UpdateGuides();
    }

    // -------------------------------------------------------------------------
    // Player MIDI input
    // -------------------------------------------------------------------------

    private void OnPlayerPressed(int keyIndex, int midiNote, float velocity)
    {
        _heldKeys.Add(keyIndex);
        if (!IsPlaying || IsPaused) return;

        if (mode == PlayMode.Practice && _steps != null && _currentStepIndex < _steps.Count)
        {
            var step = _steps[_currentStepIndex];
            Debug.Log($"[NoteMapPlayer] Key {keyIndex} pressed. Step {_currentStepIndex} needs [{string.Join(",", step.keys)}]. Held: {_heldKeys.Count}");
        }

        if (mode == PlayMode.RealTime)
            TryMatchHit(keyIndex);
        else
            CheckPracticeInput(keyIndex);
    }

    private void OnPlayerReleased(int keyIndex, int midiNote)
    {
        // Only remove from held when note fully ends (not just physical release w/ sustain)
        _heldKeys.Remove(keyIndex);
    }

    /// <summary>Expose held keys so PianoKeyLights can check during song mode.</summary>
    public bool IsKeyHeld(int keyIndex) => _heldKeys.Contains(keyIndex);

    // -------------------------------------------------------------------------
    // Real-time mode
    // -------------------------------------------------------------------------

    private void UpdateRealTime()
    {
        _playbackTime += Time.deltaTime;

        // Bring notes into the hittable window
        while (_nextHitIndex < _notes.Length)
        {
            if (_notes[_nextHitIndex].start - goodWindow <= _playbackTime)
            {
                _hittableNoteIndices.Add(_nextHitIndex);
                _nextHitIndex++;
            }
            else break;
        }

        // Expire missed notes
        for (int i = _hittableNoteIndices.Count - 1; i >= 0; i--)
        {
            int ni = _hittableNoteIndices[i];
            if (_consumed.Contains(ni))
            {
                _hittableNoteIndices.RemoveAt(i);
                continue;
            }

            if (_playbackTime > _notes[ni].start + goodWindow)
            {
                _consumed.Add(ni);
                _hittableNoteIndices.RemoveAt(i);
                MissCount++;
                NoteJudged?.Invoke(new HitResult
                {
                    keyIndex    = _notes[ni].key,
                    quality     = HitQuality.Miss,
                    timingError = goodWindow
                });
            }
        }

        // Song finished?
        if (_nextHitIndex >= _notes.Length && _hittableNoteIndices.Count == 0 && _guideEnds.Count == 0)
        {
            IsPlaying = false;
            SongFinished?.Invoke();
            Debug.Log($"[NoteMapPlayer] Finished — Perfect:{PerfectCount} Great:{GreatCount} " +
                      $"Good:{GoodCount} Miss:{MissCount}");
        }
    }

    private void TryMatchHit(int keyIndex)
    {
        int   bestIndex = -1;
        float bestDist  = float.MaxValue;

        foreach (int ni in _hittableNoteIndices)
        {
            if (_consumed.Contains(ni)) continue;
            var note = _notes[ni];
            if (note.key != keyIndex) continue;

            float dist = Mathf.Abs(_playbackTime - note.start);
            if (dist < bestDist)
            {
                bestDist  = dist;
                bestIndex = ni;
            }
        }

        if (bestIndex < 0) return; // wrong key or no matching note

        _consumed.Add(bestIndex);

        HitQuality q;
        if      (bestDist <= perfectWindow) { q = HitQuality.Perfect; PerfectCount++; }
        else if (bestDist <= greatWindow)   { q = HitQuality.Great;   GreatCount++;   }
        else                                { q = HitQuality.Good;    GoodCount++;    }

        NoteJudged?.Invoke(new HitResult
        {
            keyIndex    = keyIndex,
            quality     = q,
            timingError = _playbackTime - _notes[bestIndex].start
        });
    }

    // -------------------------------------------------------------------------
    // Practice mode
    // -------------------------------------------------------------------------

    private void UpdatePractice()
    {
        if (_steps == null || _currentStepIndex >= _steps.Count) return;

        // Once step is satisfied, advance playbackTime toward next step
        if (_stepSatisfied)
        {
            _playbackTime += Time.deltaTime;

            float targetTime = _currentStepIndex + 1 < _steps.Count
                ? _steps[_currentStepIndex + 1].time
                : CurrentMap.durationSeconds;

            if (_playbackTime >= targetTime)
            {
                _playbackTime = targetTime;
                _currentStepIndex++;

                if (_currentStepIndex >= _steps.Count)
                {
                    IsPlaying = false;
                    SongFinished?.Invoke();
                    Debug.Log("[NoteMapPlayer] Practice complete!");
                    return;
                }

                _stepSatisfied = false;
                FireStepChanged();
            }
        }
        else
        {
            // Freeze — check if all required keys are held
            var step = _steps[_currentStepIndex];
            bool allHeld = true;
            foreach (int k in step.keys)
            {
                if (!_heldKeys.Contains(k))
                {
                    allHeld = false;
                    break;
                }
            }

            if (allHeld)
                _stepSatisfied = true;
        }
    }

    private void CheckPracticeInput(int keyIndex)
    {
        if (_steps == null || _currentStepIndex >= _steps.Count) return;

        var step = _steps[_currentStepIndex];
        bool isRequired = Array.IndexOf(step.keys, keyIndex) >= 0;

        if (isRequired)
            CorrectKeyPressed?.Invoke(keyIndex);
        else
            WrongKeyPressed?.Invoke(keyIndex);
    }

    // -------------------------------------------------------------------------
    // Guide notes (both modes)
    // -------------------------------------------------------------------------

    private void UpdateGuides()
    {
        // Start new guides
        while (_nextGuideIndex < _notes.Length)
        {
            var note = _notes[_nextGuideIndex];
            if (note.start <= _playbackTime)
            {
                int k = note.key;
                if (!_guideRefCount.TryGetValue(k, out int count) || count == 0)
                {
                    _guideRefCount[k] = 1;
                    GuideNoteOn?.Invoke(k, note.dur);
                }
                else
                {
                    _guideRefCount[k] = count + 1;
                }
                _guideEnds.Add((_nextGuideIndex, note.start + note.dur));
                _nextGuideIndex++;
            }
            else break;
        }

        // End expired guides
        for (int i = _guideEnds.Count - 1; i >= 0; i--)
        {
            var (ni, endTime) = _guideEnds[i];
            if (_playbackTime >= endTime)
            {
                int k = _notes[ni].key;
                _guideRefCount[k]--;
                if (_guideRefCount[k] <= 0)
                {
                    _guideRefCount.Remove(k);
                    GuideNoteOff?.Invoke(k);
                }
                _guideEnds.RemoveAt(i);
            }
        }
    }

    private void ClearAllGuides()
    {
        foreach (var kvp in _guideRefCount)
        {
            if (kvp.Value > 0)
                GuideNoteOff?.Invoke(kvp.Key);
        }
        _guideRefCount.Clear();
        _guideEnds.Clear();
    }

    // -------------------------------------------------------------------------
    // Step building
    // -------------------------------------------------------------------------

    private List<NoteStep> BuildSteps(NoteMap map)
    {
        var steps = new List<NoteStep>();
        if (map.notes == null || map.notes.Length == 0) return steps;

        var currentKeys = new List<int>();
        float currentTime = map.notes[0].start;

        foreach (var note in map.notes)
        {
            if (note.start - currentTime > stepTolerance && currentKeys.Count > 0)
            {
                steps.Add(new NoteStep { time = currentTime, keys = currentKeys.ToArray() });
                currentKeys.Clear();
                currentTime = note.start;
            }

            // Avoid duplicate keys in the same step
            if (!currentKeys.Contains(note.key))
                currentKeys.Add(note.key);
        }

        if (currentKeys.Count > 0)
            steps.Add(new NoteStep { time = currentTime, keys = currentKeys.ToArray() });

        Debug.Log($"[NoteMapPlayer] Built {steps.Count} practice steps from {map.notes.Length} notes.");
        return steps;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void ResetState()
    {
        _playbackTime = 0f;
        _nextGuideIndex = 0;
        _nextHitIndex   = 0;
        _currentStepIndex = 0;
        _stepSatisfied    = false;
        _hittableNoteIndices.Clear();
        _consumed.Clear();
        _guideEnds.Clear();
        _guideRefCount.Clear();
        _heldKeys.Clear();

        PerfectCount = 0;
        GreatCount   = 0;
        GoodCount    = 0;
        MissCount    = 0;
        TotalNotes   = _notes?.Length ?? 0;
    }

    private void FireStepChanged()
    {
        if (_steps == null || _currentStepIndex >= _steps.Count) return;
        var step = _steps[_currentStepIndex];
        StepChanged?.Invoke(_currentStepIndex, step.keys);
    }
}
