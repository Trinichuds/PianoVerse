using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// VR song selector using left controller.
///
/// Controls:
///   Left Thumbstick Left/Right  → browse songs
///   Left Trigger (Index)        → play / stop
///   Left Grip (Hand)            → toggle Practice / Real-Time mode
///   Y button                    → pause / resume
///   X button                    → restart current song
///
/// Requires NoteMapLoader to be in the scene (auto-finds via singleton).
/// </summary>
public class SongSelector : MonoBehaviour
{
    [Header("References")]
    public NoteMapPlayer player;
    public WaterfallRenderer waterfall;

    [Header("HUD")]
    [Tooltip("World Space TMP text to show song info. Optional.")]
    public TMP_Text hudText;

    private List<NoteMap> _maps;
    private int _selectedIndex;
    private bool _thumbstickReset = true;
    private bool _triggerReset = true;
    private bool _gripReset = true;
    private bool _ready;

    private bool _subscribed;

    private void Start()
    {
        TrySubscribe();
        UpdateHud();
    }

    private void TrySubscribe()
    {
        if (_subscribed || NoteMapLoader.Instance == null) return;
        _subscribed = true;

        if (NoteMapLoader.Instance.IsReady)
            OnMapsLoaded(NoteMapLoader.Instance.LoadedMaps);
        else
            NoteMapLoader.Instance.OnMapsLoaded += OnMapsLoaded;
    }

    private void OnDestroy()
    {
        if (NoteMapLoader.Instance != null)
            NoteMapLoader.Instance.OnMapsLoaded -= OnMapsLoaded;
    }

    private void OnMapsLoaded(List<NoteMap> maps)
    {
        _maps = maps;
        _selectedIndex = 0;
        _ready = true;
        Debug.Log($"[SongSelector] {maps.Count} songs available.");
        UpdateHud();
    }

    private void Update()
    {
        if (!_subscribed) TrySubscribe();

        if (!_ready || _maps == null || _maps.Count == 0)
        {
            UpdateHud();
            return;
        }

        HandleThumbstick();
        HandleTrigger();
        HandleGrip();
        HandleButtons();
        UpdateHud();
    }

    // -------------------------------------------------------------------------
    // Input
    // -------------------------------------------------------------------------

    // Left thumbstick left/right to browse songs. Stops playback when switching.
    private void HandleThumbstick()
    {
        Vector2 stick = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick);

        if (Mathf.Abs(stick.x) < 0.4f)
        {
            _thumbstickReset = true;
            return;
        }

        if (!_thumbstickReset) return;
        _thumbstickReset = false;

        if (stick.x > 0.4f)
        {
            _selectedIndex = (_selectedIndex + 1) % _maps.Count;
            StopIfPlaying();
        }
        else if (stick.x < -0.4f)
        {
            _selectedIndex = (_selectedIndex - 1 + _maps.Count) % _maps.Count;
            StopIfPlaying();
        }
    }

    // Left index trigger toggles play/stop for the selected song.
    private void HandleTrigger()
    {
        float trigger = OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger);

        if (trigger < 0.5f)
        {
            _triggerReset = true;
            return;
        }

        if (!_triggerReset) return;
        _triggerReset = false;

        if (player.IsPlaying)
        {
            player.Stop();
            if (waterfall != null) waterfall.ResetBars();
        }
        else
        {
            player.LoadMap(_maps[_selectedIndex]);
            if (waterfall != null) waterfall.ResetBars();
            player.Play();
        }
    }

    // Left grip toggles between Practice and RealTime mode. Restarts if playing.
    private void HandleGrip()
    {
        float grip = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);

        if (grip < 0.5f)
        {
            _gripReset = true;
            return;
        }

        if (!_gripReset) return;
        _gripReset = false;

        bool wasPlaying = player.IsPlaying;
        PlayMode newMode = player.mode == PlayMode.Practice ? PlayMode.RealTime : PlayMode.Practice;
        player.SetMode(newMode);

        if (wasPlaying)
        {
            player.LoadMap(_maps[_selectedIndex]);
            if (waterfall != null) waterfall.ResetBars();
            player.Play();
        }
    }

    private void HandleButtons()
    {
        // Y = pause/resume
        if (OVRInput.GetDown(OVRInput.RawButton.Y))
        {
            if (player.IsPlaying)
                player.TogglePause();
        }

        // X = restart
        if (OVRInput.GetDown(OVRInput.RawButton.X))
        {
            if (player.IsPlaying)
            {
                player.Stop();
                if (waterfall != null) waterfall.ResetBars();
            }
            player.LoadMap(_maps[_selectedIndex]);
            if (waterfall != null) waterfall.ResetBars();
            player.Play();
        }
    }

    private void StopIfPlaying()
    {
        if (player.IsPlaying)
        {
            player.Stop();
            if (waterfall != null) waterfall.ResetBars();
        }
    }

    // -------------------------------------------------------------------------
    // HUD
    // -------------------------------------------------------------------------

    private void UpdateHud()
    {
        if (hudText == null) return;

        if (!_ready)
        {
            hudText.text = NoteMapLoader.Instance != null
                ? "Loading maps..."
                : "NoteMapLoader not found!\nAdd it to a GameObject.";
            return;
        }

        if (_maps == null || _maps.Count == 0)
        {
            hudText.text = "No songs found.\nDrop .mid files in\nStreamingAssets/Maps/";
            return;
        }

        var map = _maps[_selectedIndex];
        string status;

        if (player.IsPlaying)
        {
            if (player.IsPaused)
                status = "PAUSED";
            else if (player.mode == PlayMode.Practice)
                status = $"PRACTICE  Step {player.CurrentStepIndex + 1}/{player.TotalSteps}";
            else
                status = $"PLAYING  {player.PlaybackTime:F1}s / {map.durationSeconds:F1}s\n" +
                         $"P:{player.PerfectCount} G:{player.GreatCount} OK:{player.GoodCount} X:{player.MissCount}";
        }
        else
        {
            status = "STOPPED";
        }

        hudText.text = $"{map.title}  ({map.bpm:F0} BPM, {map.durationSeconds:F0}s)\n" +
                       $"[{_selectedIndex + 1}/{_maps.Count}]  Mode: {player.mode}\n" +
                       $"{status}";
    }
}
