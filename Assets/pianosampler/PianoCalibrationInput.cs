using UnityEngine;

/// <summary>
/// VR controller calibration for PianoKeyboardMapper.
///
/// Press A once  → enter marker mode (preview ball appears)
/// Press A again → place left anchor (green)
/// Press B       → place right anchor (red) + calibrate + exit marker mode
/// Press A any time after calibration → re-enter marker mode
/// </summary>
public class PianoCalibrationInput : MonoBehaviour
{
    [Header("References")]
    public PianoKeyboardMapper mapper;
    [Tooltip("OVRCameraRig → TrackingSpace → RightControllerInHandAnchor")]
    public Transform rightControllerAnchor;

    [Header("Audio Feedback")]
    public CalibrationSFX sfx;

    private enum State { Idle, PlacingLeft, PlacingRight }
    private State _state = State.Idle;

    private Vector3 _leftEdge;
    private GameObject _leftMarker;
    private GameObject _rightMarker;
    private GameObject _preview;
    private Material _previewMat;

    private const float MarkerSize = 0.02f;
    private const float ForwardOffset = 0.05f;

    private void Awake()
    {
        if (sfx == null) sfx = GetComponentInChildren<CalibrationSFX>();
        if (sfx == null) sfx = FindObjectOfType<CalibrationSFX>();
    }

    private void Update()
    {
        if (rightControllerAnchor == null || mapper == null) return;

        Vector3 ctrlPos = rightControllerAnchor.position + rightControllerAnchor.forward * ForwardOffset;

        // Preview follows controller only when in marker mode
        if (_preview != null)
            _preview.transform.position = ctrlPos;

        if (OVRInput.GetDown(OVRInput.RawButton.A))
        {
            switch (_state)
            {
                case State.Idle:
                    // Enter marker mode
                    DestroyMarkers();
                    CreatePreview(Color.green);
                    _state = State.PlacingLeft;
                    if (sfx != null) sfx.PlayBlip();
                    Debug.Log("[Calibration] Marker mode ON. Press A to place left anchor.");
                    break;

                case State.PlacingLeft:
                    // Place left anchor
                    _leftEdge = ctrlPos;
                    _leftMarker = SpawnMarker(ctrlPos, Color.green);
                    SetPreviewColor(Color.red);
                    _state = State.PlacingRight;
                    if (sfx != null) sfx.PlayBlip();
                    Debug.Log($"[Calibration] Left edge set at {ctrlPos}. Now press B.");
                    break;

                case State.PlacingRight:
                    // Restart — re-place left
                    DestroyMarkers();
                    _leftEdge = ctrlPos;
                    _leftMarker = SpawnMarker(ctrlPos, Color.green);
                    if (sfx != null) sfx.PlayBlip();
                    Debug.Log($"[Calibration] Left edge re-set at {ctrlPos}. Now press B.");
                    break;
            }
        }

        if (OVRInput.GetDown(OVRInput.RawButton.B) && _state == State.PlacingRight)
        {
            _rightMarker = SpawnMarker(ctrlPos, Color.red);
            mapper.Calibrate(_leftEdge, ctrlPos);
            if (sfx != null) sfx.PlayChime();
            Debug.Log($"[Calibration] Done. dist={Vector3.Distance(_leftEdge, ctrlPos):F3}m. Press A to redo.");

            // Exit marker mode
            DestroyPreview();
            _state = State.Idle;
            Invoke(nameof(DestroyMarkers), 0.6f);
        }
    }

    private void CreatePreview(Color color)
    {
        DestroyPreview();
        _preview = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _preview.transform.localScale = Vector3.one * MarkerSize;
        Destroy(_preview.GetComponent<Collider>());
        _previewMat = _preview.GetComponent<Renderer>().material;
        _previewMat.shader = Shader.Find("Sprites/Default");
        _previewMat.mainTexture = Texture2D.whiteTexture;
        _previewMat.renderQueue = 3000;
        SetPreviewColor(color);
    }

    private void DestroyPreview()
    {
        if (_preview != null) { Destroy(_preview); _preview = null; _previewMat = null; }
    }

    private void SetPreviewColor(Color color)
    {
        if (_previewMat == null) return;
        color.a = 0.35f;
        _previewMat.color = color;
    }

    private GameObject SpawnMarker(Vector3 position, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.transform.position = position;
        go.transform.localScale = Vector3.one * MarkerSize;
        go.GetComponent<Renderer>().material.color = color;
        Destroy(go.GetComponent<Collider>());
        return go;
    }

    private void DestroyMarkers()
    {
        if (_leftMarker != null) { Destroy(_leftMarker); _leftMarker = null; }
        if (_rightMarker != null) { Destroy(_rightMarker); _rightMarker = null; }
    }
}
