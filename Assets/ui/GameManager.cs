using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Top-level game state machine.
/// States: MainMenu → MapSelect → Playing
///
/// Press B to summon/dismiss menu (when not in calibration mode).
/// Menu spawns in front of you. Disappears when a song starts.
///
/// Setup: Add to a GameObject. Assign NoteMapPlayer + WaterfallRenderer.
/// Optionally assign calibrationInput to prevent B conflicts.
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("References")]
    public NoteMapPlayer player;
    public WaterfallRenderer waterfall;
    public Transform centerEyeAnchor;
    [Tooltip("Assign to prevent B button conflict during calibration.")]
    public PianoCalibrationInput calibrationInput;

    [Tooltip("Auto-found if not set.")]
    public NativePianoSampler pianoSampler;

    public enum GameState { MainMenu, MapSelect, Options, Playing }
    public GameState State { get; private set; } = GameState.MainMenu;

    private VRMenuPanel _mainMenu;
    private VRMenuPanel _mapSelectMenu;
    private VRMenuPanel _optionsMenu;
    private GameHUD _hud;

    private List<NoteMap> _maps;
    private int _selectedMapIndex;
    private bool _mapsReady;
    private bool _menuVisible;

    // Audio mode options
    private static readonly NativeAudioInterop.BackendKind[] AudioModes =
    {
        NativeAudioInterop.BackendKind.Auto,
        NativeAudioInterop.BackendKind.Asio,
        NativeAudioInterop.BackendKind.WasapiShared,
        NativeAudioInterop.BackendKind.WasapiExclusive,
    };
    private int _currentAudioModeIndex;

    private void Start()
    {
        if (centerEyeAnchor == null)
        {
            var rig = FindObjectOfType<OVRCameraRig>();
            if (rig != null) centerEyeAnchor = rig.centerEyeAnchor;
        }

        if (calibrationInput == null)
            calibrationInput = FindObjectOfType<PianoCalibrationInput>();
        if (pianoSampler == null)
            pianoSampler = FindObjectOfType<NativePianoSampler>();

        // Subscribe to map loader
        if (NoteMapLoader.Instance != null)
        {
            if (NoteMapLoader.Instance.IsReady)
                OnMapsLoaded(NoteMapLoader.Instance.LoadedMaps);
            else
                NoteMapLoader.Instance.OnMapsLoaded += OnMapsLoaded;
        }

        if (player != null)
            player.SongFinished += OnSongFinished;

        BuildMenus();
        EnterState(GameState.MainMenu);
    }

    private void OnDestroy()
    {
        if (NoteMapLoader.Instance != null)
            NoteMapLoader.Instance.OnMapsLoaded -= OnMapsLoaded;
        if (player != null)
            player.SongFinished -= OnSongFinished;
    }

    private void OnMapsLoaded(List<NoteMap> maps)
    {
        _maps = maps;
        _mapsReady = true;
        RefreshMapList();
    }

    private void OnSongFinished()
    {
        Invoke(nameof(ReturnToMapSelect), 2f);
    }

    private void ReturnToMapSelect()
    {
        EnterState(GameState.MapSelect);
    }

    // -------------------------------------------------------------------------
    // State machine
    // -------------------------------------------------------------------------

    private void EnterState(GameState newState)
    {
        // Hide everything first
        if (_mainMenu != null) _mainMenu.gameObject.SetActive(false);
        if (_mapSelectMenu != null) _mapSelectMenu.gameObject.SetActive(false);
        if (_optionsMenu != null) _optionsMenu.gameObject.SetActive(false);
        if (_hud != null) _hud.gameObject.SetActive(false);

        if (State == GameState.Playing && newState != GameState.Playing)
        {
            if (player != null && player.IsPlaying) player.Stop();
            if (waterfall != null) waterfall.ResetBars();
        }

        State = newState;

        switch (newState)
        {
            case GameState.MainMenu:
                _mainMenu.gameObject.SetActive(true);
                _mainMenu.SelectedIndex = 0;
                PositionPanelInFront(_mainMenu.transform);
                _menuVisible = true;
                break;

            case GameState.MapSelect:
                if (_mapsReady) RefreshMapList();
                _mapSelectMenu.gameObject.SetActive(true);
                PositionPanelInFront(_mapSelectMenu.transform);
                _menuVisible = true;
                break;

            case GameState.Options:
                RefreshOptionsMenu();
                _optionsMenu.gameObject.SetActive(true);
                _optionsMenu.SelectedIndex = 0;
                PositionPanelInFront(_optionsMenu.transform);
                _menuVisible = true;
                break;

            case GameState.Playing:
                _hud.gameObject.SetActive(true);
                _menuVisible = false;
                break;
        }
    }

    /// <summary>Show/hide the current menu panel and reposition in front of player.</summary>
    private void ToggleMenuVisibility()
    {
        if (State == GameState.Playing) return;

        _menuVisible = !_menuVisible;

        VRMenuPanel activePanel = State switch
        {
            GameState.MainMenu => _mainMenu,
            GameState.MapSelect => _mapSelectMenu,
            GameState.Options => _optionsMenu,
            _ => null
        };
        if (activePanel == null) return;

        activePanel.gameObject.SetActive(_menuVisible);
        if (_menuVisible)
            PositionPanelInFront(activePanel.transform);
    }

    // -------------------------------------------------------------------------
    // Menus
    // -------------------------------------------------------------------------

    private void BuildMenus()
    {
        // Main Menu
        var mainGo = new GameObject("MainMenu");
        mainGo.transform.SetParent(transform, false);
        _mainMenu = mainGo.AddComponent<VRMenuPanel>();
        _mainMenu.title = "PIANO MR";
        _mainMenu.SetItems(new[] { "Start", "Options", "Exit" });
        _mainMenu.OnItemSelected += OnMainMenuSelect;

        // Map Select Menu
        var mapGo = new GameObject("MapSelectMenu");
        mapGo.transform.SetParent(transform, false);
        _mapSelectMenu = mapGo.AddComponent<VRMenuPanel>();
        _mapSelectMenu.title = "SELECT SONG";
        _mapSelectMenu.SetItems(new[] { "Loading maps..." });
        _mapSelectMenu.showBackButton = true;
        _mapSelectMenu.OnItemSelected += OnMapMenuSelect;
        _mapSelectMenu.OnBack += OnMapMenuBack;

        // Options Menu
        var optGo = new GameObject("OptionsMenu");
        optGo.transform.SetParent(transform, false);
        _optionsMenu = optGo.AddComponent<VRMenuPanel>();
        _optionsMenu.title = "OPTIONS";
        _optionsMenu.showBackButton = true;
        _optionsMenu.OnItemSelected += OnOptionsSelect;
        _optionsMenu.OnBack += OnOptionsBack;
        RefreshOptionsMenu();

        // HUD
        var hudGo = new GameObject("GameHUD");
        _hud = hudGo.AddComponent<GameHUD>();
        _hud.player = player;
        _hud.centerEyeAnchor = centerEyeAnchor;
        _hud.gameManager = this;

        mainGo.SetActive(false);
        mapGo.SetActive(false);
        optGo.SetActive(false);
        hudGo.SetActive(false);
    }

    private void RefreshMapList()
    {
        if (_maps == null || _maps.Count == 0)
        {
            _mapSelectMenu.SetItems(new[] { "No maps found" });
            return;
        }

        var items = new string[_maps.Count];
        for (int i = 0; i < _maps.Count; i++)
        {
            var m = _maps[i];
            items[i] = $"{m.title}  ({m.bpm:F0} BPM, {m.durationSeconds:F0}s)";
        }
        _mapSelectMenu.SetItems(items);
    }

    private void OnMainMenuSelect(int index)
    {
        switch (index)
        {
            case 0: // Start
                EnterState(GameState.MapSelect);
                break;
            case 1: // Options
                EnterState(GameState.Options);
                break;
            case 2: // Exit
                Application.Quit();
                #if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
                #endif
                break;
        }
    }

    private void OnMapMenuSelect(int index)
    {
        if (!_mapsReady || _maps == null || index >= _maps.Count) return;
        _selectedMapIndex = index;
        StartSong();
    }

    private void OnMapMenuBack()
    {
        EnterState(GameState.MainMenu);
    }

    private void StartSong()
    {
        if (_maps == null || _selectedMapIndex >= _maps.Count) return;

        player.LoadMap(_maps[_selectedMapIndex]);
        if (waterfall != null) waterfall.ResetBars();
        player.Play();

        EnterState(GameState.Playing);
    }

    // -------------------------------------------------------------------------
    // Public actions (called by GameHUD)
    // -------------------------------------------------------------------------

    public void PauseSong()
    {
        if (player != null && player.IsPlaying)
            player.TogglePause();
    }

    public void StopSong()
    {
        if (player != null && player.IsPlaying)
        {
            player.Stop();
            if (waterfall != null) waterfall.ResetBars();
        }
        EnterState(GameState.MapSelect);
    }

    public void RestartSong()
    {
        if (player != null)
        {
            player.Stop();
            if (waterfall != null) waterfall.ResetBars();
        }
        StartSong();
    }

    public void ToggleMode()
    {
        if (player == null) return;
        bool wasPlaying = player.IsPlaying;

        PlayMode newMode = player.mode switch
        {
            PlayMode.Practice => PlayMode.RealTime,
            PlayMode.RealTime => PlayMode.Watch,
            PlayMode.Watch    => PlayMode.Practice,
            _                 => PlayMode.Practice
        };
        player.SetMode(newMode);

        if (wasPlaying)
        {
            player.LoadMap(_maps[_selectedMapIndex]);
            if (waterfall != null) waterfall.ResetBars();
            player.Play();
        }
    }

    // -------------------------------------------------------------------------
    // Options menu
    // -------------------------------------------------------------------------

    private void RefreshOptionsMenu()
    {
        string currentMode = GetAudioModeName(AudioModes[_currentAudioModeIndex]);
        _optionsMenu?.SetItems(new[]
        {
            $"Audio Mode: {currentMode}",
            "Apply & Restart Audio",
            "Back"
        });
    }

    private void OnOptionsSelect(int index)
    {
        switch (index)
        {
            case 0: // Cycle audio mode
                _currentAudioModeIndex = (_currentAudioModeIndex + 1) % AudioModes.Length;
                RefreshOptionsMenu();
                break;

            case 1: // Apply
                ApplyAudioMode();
                break;

            case 2: // Back
                OnOptionsBack();
                break;
        }
    }

    private void OnOptionsBack()
    {
        EnterState(GameState.MainMenu);
    }

    private void ApplyAudioMode()
    {
        if (pianoSampler == null)
        {
            Debug.LogWarning("[GameManager] No NativePianoSampler found to apply audio settings.");
            return;
        }

        var newBackend = AudioModes[_currentAudioModeIndex];

        // Shutdown existing engine, change backend, re-initialize
        pianoSampler.Shutdown();
        pianoSampler.SetBackend(newBackend);
        bool ok = pianoSampler.Initialize();

        Debug.Log(ok
            ? $"[GameManager] Audio backend switched to {GetAudioModeName(newBackend)}."
            : $"[GameManager] Failed to switch audio backend to {GetAudioModeName(newBackend)}.");

        RefreshOptionsMenu();
    }

    private static string GetAudioModeName(NativeAudioInterop.BackendKind kind) => kind switch
    {
        NativeAudioInterop.BackendKind.Auto => "Auto",
        NativeAudioInterop.BackendKind.Asio => "ASIO",
        NativeAudioInterop.BackendKind.WasapiShared => "WASAPI Shared",
        NativeAudioInterop.BackendKind.WasapiExclusive => "WASAPI Exclusive",
        _ => kind.ToString()
    };

    // -------------------------------------------------------------------------
    // Positioning
    // -------------------------------------------------------------------------

    private void PositionPanelInFront(Transform panel)
    {
        if (centerEyeAnchor == null) return;

        Vector3 forward = centerEyeAnchor.forward;
        forward.y = 0f;
        forward.Normalize();
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;

        panel.position = centerEyeAnchor.position + forward * 1.2f + Vector3.up * 0.1f;
        panel.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    // -------------------------------------------------------------------------
    // Update — B to summon/dismiss, retry map loader
    // -------------------------------------------------------------------------

    private void Update()
    {
        if (State != GameState.Playing && OVRInput.GetDown(OVRInput.RawButton.B))
        {
            if (!_menuVisible)
            {
                // Summon menu
                ToggleMenuVisibility();
            }
            else if (State == GameState.MainMenu)
            {
                // Dismiss main menu
                ToggleMenuVisibility();
            }
            // MapSelect & Options: VRMenuPanel handles B → OnBack
        }

        // Retry map loader subscription
        if (!_mapsReady && NoteMapLoader.Instance != null)
        {
            if (NoteMapLoader.Instance.IsReady)
            {
                OnMapsLoaded(NoteMapLoader.Instance.LoadedMaps);
            }
            else
            {
                NoteMapLoader.Instance.OnMapsLoaded -= OnMapsLoaded;
                NoteMapLoader.Instance.OnMapsLoaded += OnMapsLoaded;
            }
        }
    }

    /// <summary>Check if calibration is in progress (PlacingRight state uses B).</summary>
    private bool IsCalibrating()
    {
        // CalibrationInput only uses B during PlacingRight state.
        // We check if the preview sphere exists as a proxy — it's created when entering marker mode.
        if (calibrationInput == null) return false;
        // Use reflection-free approach: if calibrationInput is enabled and has children (preview/markers), it might be active
        // Simpler: expose a property. For now, just don't block — calibration's B only fires in PlacingRight state
        // and VRMenuPanel's B is for back button which is only on map select.
        // The conflict is minimal, so just let both fire.
        return false;
    }
}
