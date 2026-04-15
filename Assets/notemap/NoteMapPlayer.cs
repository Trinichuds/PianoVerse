using System;
using System.Collections.Generic;
using UnityEngine;

public enum PlayMode { RealTime, Practice, Watch }
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
    [Tooltip("For Watch mode audio playback. Auto-found if not set.")]
    public NativePianoSampler pianoSampler;

    [Header("Mode")]
    public PlayMode mode = PlayMode.Practice;

    [Header("Timing Windows — Real-Time (seconds)")]
    public float perfectWindow = 0.050f;
    public float greatWindow   = 0.100f;
    public float goodWindow    = 0.150f;

    [Header("Real-Time Countdown")]
    [Tooltip("Seconds of lead-in before real-time playback begins (waterfall scrolls in).")]
    public float realTimeCountdown = 3f;

    [Header("Step Grouping — Practice")]
    [Tooltip("Notes within this many seconds of each other form one step.")]
    public float stepTolerance = 0.080f;
    [Tooltip("How far ahead (in seconds) a key press can register for an upcoming step.")]
    public float earlyDetectionWindow = 0.200f;

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
    // Fresh presses belong to the currently active practice step only. They expire quickly so
    // held or stale keys cannot accidentally satisfy a later step.
    private readonly Dictionary<int, float> _freshPresses = new(); // key → Time.time when pressed
    // Early presses buffer slightly-ahead input for upcoming steps. We promote them only when
    // that exact step activates, which keeps fast players from feeling "dropped" by practice mode.
    private readonly Dictionary<int, (HashSet<int> keys, float time)> _earlyPresses = new(); // stepIndex → (keys, Time.time when first buffered)
    private bool _stepSatisfied;
    private float _stepStartRealTime; // Time.time when current step became active

    private class NoteStep
    {
        public float time;
        public int[] keys;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    // Stores the map and builds practice steps if in Practice mode.
    public void LoadMap(NoteMap map)
    {
        Stop();
        CurrentMap = map;
        _notes = map.notes;

        if (mode == PlayMode.Practice)
            _steps = BuildSteps(map);
    }

    // Starts playback. Practice starts at step 0, RealTime/Watch starts with countdown.
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
            _playbackTime = -realTimeCountdown;
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
        if (pianoSampler == null)
            pianoSampler = FindObjectOfType<NativePianoSampler>();
    }

    private void OnEnable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed  += OnPlayerPressed;
        midiInput.NoteReleased += OnPlayerReleased;
        Debug.Log("[NoteMapPlayer] Subscribed to MIDI input.");
    }

    private void OnDisable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed  -= OnPlayerPressed;
        midiInput.NoteReleased -= OnPlayerReleased;
    }

    private void Update()
    {
        if (!IsPlaying || IsPaused || _notes == null) return;

        if (mode == PlayMode.Watch)
            UpdateWatch();
        else if (mode == PlayMode.RealTime)
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

    // Advances time, brings notes into the hit window, and expires missed notes.
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

    // Finds the closest hittable note matching this key and judges timing accuracy.
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
    // Watch mode — just plays through, no input, no scoring
    // -------------------------------------------------------------------------

    private void UpdateWatch()
    {
        _playbackTime += Time.deltaTime;

        // Song finished when all guides have ended
        if (_playbackTime >= CurrentMap.durationSeconds && _guideEnds.Count == 0)
        {
            IsPlaying = false;
            SongFinished?.Invoke();
            Debug.Log("[NoteMapPlayer] Watch playback complete.");
        }
    }

    // -------------------------------------------------------------------------
    // Practice mode
    // -------------------------------------------------------------------------

    private readonly List<int> _expiredKeys = new(); // temp list for expired key removal

    private void UpdatePractice()
    {
        if (_steps == null || _currentStepIndex >= _steps.Count) return;

        float now = Time.time;

        // Expire stale fresh presses — only when step is NOT yet satisfied
        if (!_stepSatisfied)
        {
            _expiredKeys.Clear();
            foreach (var kvp in _freshPresses)
            {
                if (now - kvp.Value > earlyDetectionWindow)
                    _expiredKeys.Add(kvp.Key);
            }
            foreach (int k in _expiredKeys)
                _freshPresses.Remove(k);
        }

        // Expire stale early presses for future steps
        _expiredKeys.Clear();
        foreach (var kvp in _earlyPresses)
        {
            if (now - kvp.Value.time > earlyDetectionWindow)
                _expiredKeys.Add(kvp.Key);
        }
        foreach (int k in _expiredKeys)
            _earlyPresses.Remove(k);

        if (_stepSatisfied)
        {
            _playbackTime += Time.deltaTime;

            float targetTime = _currentStepIndex + 1 < _steps.Count
                ? _steps[_currentStepIndex + 1].time
                : CurrentMap.durationSeconds;

            // Once the current step is satisfied we let playback run forward until the next step
            // boundary, instead of staying frozen through empty time between chords.
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
                _freshPresses.Clear();

                // Carry only explicitly buffered input into the new step. Reusing raw held keys
                // here would make repeated notes/chords auto-complete too easily.
                // Apply only explicitly buffered early presses for this step (if not expired)
                if (_earlyPresses.TryGetValue(_currentStepIndex, out var early))
                {
                    if (now - early.time <= earlyDetectionWindow)
                    {
                        foreach (int k in early.keys)
                            _freshPresses[k] = now;
                    }
                    _earlyPresses.Remove(_currentStepIndex);
                }

                FireStepChanged();
            }
        }
        else
        {
            // Practice mode requires recent presses for every key in the step. That keeps the mode
            // intentional: the player must actively play the chord instead of parking fingers down.
            // Freeze — check if all required keys were freshly pressed (and not expired)
            var step = _steps[_currentStepIndex];
            bool allPressed = true;
            foreach (int k in step.keys)
            {
                if (!_freshPresses.ContainsKey(k))
                {
                    allPressed = false;
                    break;
                }
            }

            if (allPressed)
                _stepSatisfied = true;
        }
    }

    // Checks pressed key against current step and upcoming steps.
    // - Current step: registers as fresh press with timestamp
    // - Upcoming steps within earlyDetectionWindow: buffered for when that step activates
    // - Also works when current step is already satisfied (player rushing ahead)
    private void CheckPracticeInput(int keyIndex)
    {
        if (_steps == null || _currentStepIndex >= _steps.Count) return;

        float now = Time.time;

        // Check current step
        var step = _steps[_currentStepIndex];
        if (Array.IndexOf(step.keys, keyIndex) >= 0)
        {
            _freshPresses[keyIndex] = now;
            CorrectKeyPressed?.Invoke(keyIndex);
            return;
        }

        // Look ahead a few steps so slightly early input still feels responsive. We cap the scan
        // to avoid matching a repeated pitch far later in the song by accident.
        // Look ahead: check upcoming steps within earlyDetectionWindow (real-time based)
        // Only look ahead a few steps — no point scanning 100 steps
        int maxLookAhead = Mathf.Min(_currentStepIndex + 4, _steps.Count);
        for (int i = _currentStepIndex + 1; i < maxLookAhead; i++)
        {
            if (Array.IndexOf(_steps[i].keys, keyIndex) >= 0)
            {
                if (!_earlyPresses.TryGetValue(i, out var entry))
                {
                    entry = (new HashSet<int>(), now);
                    _earlyPresses[i] = entry;
                }
                entry.keys.Add(keyIndex);
                CorrectKeyPressed?.Invoke(keyIndex);
                return;
            }
        }

        WrongKeyPressed?.Invoke(keyIndex);
    }

    // -------------------------------------------------------------------------
    // Guide notes (both modes)
    // -------------------------------------------------------------------------

    // Fires GuideNoteOn/Off events as notes enter/exit playback time.
    // Uses ref-counting so overlapping notes on the same key work correctly.
    // In Watch mode, also triggers audio playback for each note.
    private void UpdateGuides()
    {
        bool watchAudio = mode == PlayMode.Watch && pianoSampler != null && pianoSampler.IsReady;
        int midiBase = 21; // A0

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

                // Watch mode: play the note audio
                if (watchAudio)
                    WatchNoteOn(k + midiBase, 0.8f);

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

                    // Watch mode: release the note audio
                    if (watchAudio)
                        WatchNoteOff(k + midiBase);
                }
                _guideEnds.RemoveAt(i);
            }
        }
    }

    private void WatchNoteOn(int midiNote, float velocity)
    {
        pianoSampler.PlayNote(midiNote, velocity);
    }

    private void WatchNoteOff(int midiNote)
    {
        pianoSampler.StopNote(midiNote);
    }

    private void ClearAllGuides()
    {
        foreach (var kvp in _guideRefCount)
        {
            if (kvp.Value > 0)
                GuideNoteOff?.Invoke(kvp.Key);
        }
        // ResetState handles the playback cursor; this method is only responsible for clearing
        // whatever guide output is currently live when playback is interrupted or switched.
        _guideRefCount.Clear();
        _guideEnds.Clear();
    }

    // -------------------------------------------------------------------------
    // Step building
    // -------------------------------------------------------------------------

    // Groups notes into practice steps. Notes within stepTolerance seconds
    // of each other become one step that must be played simultaneously.
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
        _freshPresses.Clear();
        _earlyPresses.Clear();
        _expiredKeys.Clear();

        PerfectCount = 0;
        GreatCount   = 0;
        GoodCount    = 0;
        MissCount    = 0;
        TotalNotes   = _notes?.Length ?? 0;
    }

    private void FireStepChanged()
    {
        _stepStartRealTime = Time.time;
        if (_steps == null || _currentStepIndex >= _steps.Count) return;
        var step = _steps[_currentStepIndex];
        StepChanged?.Invoke(_currentStepIndex, step.keys);
    }
}

