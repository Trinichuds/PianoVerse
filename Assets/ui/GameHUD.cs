using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// In-game HUD anchored to top-left of player vision using World Space Canvas.
/// Shows song info, mode, progress/score.
/// Controls: B = back to menu, Y = pause, X = restart, Left grip = toggle mode.
/// </summary>
public class GameHUD : MonoBehaviour
{
    [Header("References")]
    public NoteMapPlayer player;
    public Transform centerEyeAnchor;
    public GameManager gameManager;

    private Canvas _canvas;
    private TextMeshProUGUI _hudText;

    private const float DistFromEye = 1.2f;
    private const float OffsetX = -0.38f;
    private const float OffsetY = 0.28f;
    private const float CanvasScale = 0.0008f;
    private const float PanelW = 400f;
    private const float PanelH = 140f;

    private bool _gripReset = true;

    private void OnEnable()
    {
        if (_canvas == null)
            Build();
    }

    private void Build()
    {
        // Canvas
        var canvasGo = new GameObject("HUDCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 110;

        var canvasRT = _canvas.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(PanelW, PanelH);
        canvasRT.localScale = Vector3.one * CanvasScale;
        canvasRT.localPosition = Vector3.zero;

        // Background
        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(canvasGo.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.04f, 0.06f, 0.12f, 0.5f);
        var bgRT = bgGo.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        // Text
        var textGo = new GameObject("HUDText");
        textGo.transform.SetParent(canvasGo.transform, false);
        _hudText = textGo.AddComponent<TextMeshProUGUI>();
        _hudText.fontSize = 24;
        _hudText.alignment = TextAlignmentOptions.TopLeft;
        _hudText.color = new Color(0.85f, 0.9f, 1f, 0.95f);
        _hudText.enableWordWrapping = true;
        _hudText.overflowMode = TextOverflowModes.Truncate;
        _hudText.richText = true;
        _hudText.raycastTarget = false;

        var textRT = textGo.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(12f, 8f);
        textRT.offsetMax = new Vector2(-12f, -8f);
    }

    private void Update()
    {
        FollowEye();
        UpdateText();
        HandleInput();
    }

    private void FollowEye()
    {
        if (centerEyeAnchor == null) return;

        Vector3 forward = centerEyeAnchor.forward;
        Vector3 right = centerEyeAnchor.right;
        Vector3 up = centerEyeAnchor.up;

        transform.position = centerEyeAnchor.position
            + forward * DistFromEye
            + right * OffsetX
            + up * OffsetY;

        transform.rotation = Quaternion.LookRotation(forward, up);
    }

    private void UpdateText()
    {
        if (_hudText == null || player == null) return;

        var map = player.CurrentMap;
        if (map == null)
        {
            _hudText.text = "";
            return;
        }

        string mode = player.mode switch
        {
            PlayMode.Practice => "Practice",
            PlayMode.RealTime => "Real-Time",
            PlayMode.Watch    => "Watch",
            _                 => player.mode.ToString()
        };
        string status;

        if (player.IsPaused)
        {
            status = "<color=#FFD700>PAUSED</color>";
        }
        else if (player.mode == PlayMode.Watch)
        {
            float t = Mathf.Max(0f, player.PlaybackTime);
            status = $"{t:F1}s / {map.durationSeconds:F1}s";
        }
        else if (player.mode == PlayMode.Practice)
        {
            status = $"Step {player.CurrentStepIndex + 1}/{player.TotalSteps}";
        }
        else
        {
            float t = Mathf.Max(0f, player.PlaybackTime);
            status = $"{t:F1}s / {map.durationSeconds:F1}s\n" +
                     $"<color=#FFD700>P:{player.PerfectCount}</color> " +
                     $"<color=#00FF88>G:{player.GreatCount}</color> " +
                     $"<color=#66AAFF>OK:{player.GoodCount}</color> " +
                     $"<color=#FF4444>X:{player.MissCount}</color>";
        }

        _hudText.text = $"<b>{map.title}</b>  {map.bpm:F0}BPM\n" +
                         $"{mode}  {status}";
    }

    private void HandleInput()
    {
        if (gameManager == null) return;

        // B = back to map select
        if (OVRInput.GetDown(OVRInput.RawButton.B))
            gameManager.StopSong();

        // Y = pause/resume
        if (OVRInput.GetDown(OVRInput.RawButton.Y))
            gameManager.PauseSong();

        // X = restart
        if (OVRInput.GetDown(OVRInput.RawButton.X))
            gameManager.RestartSong();

        // Left grip = toggle mode
        float grip = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);
        if (grip < 0.5f)
        {
            _gripReset = true;
        }
        else if (_gripReset)
        {
            _gripReset = false;
            gameManager.ToggleMode();
        }
    }
}
