using UnityEngine;

/// <summary>
/// VR controller calibration for PianoKeyboardMapper.
///
/// Press A → place left anchor (left edge of A0)
/// Press B → place right anchor (right edge of C8) and calibrate
/// Press A again at any time to restart.
/// </summary>
public class PianoCalibrationInput : MonoBehaviour
{
    [Header("References")]
    public PianoKeyboardMapper mapper;
    [Tooltip("OVRCameraRig → TrackingSpace → RightControllerInHandAnchor")]
    public Transform rightControllerAnchor;

    [Header("Left Anchor Marker (optional small sphere prefab)")]
    public GameObject anchorMarkerPrefab;

    private enum State { WaitingForLeft, WaitingForRight }
    private State _state = State.WaitingForLeft;

    private Vector3 _leftEdge;
    private GameObject _leftMarker;

    private void Update()
    {
        if (rightControllerAnchor == null || mapper == null) return;

        Vector3 ctrlPos = rightControllerAnchor.position;

        // A button — always sets/resets the left anchor
        if (OVRInput.GetDown(OVRInput.RawButton.A))
        {
            _leftEdge = ctrlPos;
            MoveMarker(ctrlPos);
            _state = State.WaitingForRight;
            Debug.Log($"[Calibration] Left edge set at {ctrlPos}. State → WaitingForRight. Now press B.");
        }

        // B button — sets right anchor and calibrates (only after left is set)
        if (OVRInput.GetDown(OVRInput.RawButton.B))
        {
            Debug.Log($"[Calibration] B pressed. State={_state}  leftEdge={_leftEdge}  rightPos={ctrlPos}  dist={Vector3.Distance(_leftEdge, ctrlPos):F3}m");
            if (_state == State.WaitingForRight)
            {
                // Spawn debug sphere at right edge so we can confirm position is correct
                SpawnDebugSphere(ctrlPos, Color.red);

                mapper.Calibrate(_leftEdge, ctrlPos);
                _state = State.WaitingForLeft;
                Debug.Log("[Calibration] Done. Press A to redo.");
            }
            else
            {
                Debug.LogWarning("[Calibration] B pressed but left anchor not set yet — press A first.");
            }
        }
    }

    private void SpawnDebugSphere(Vector3 position, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.transform.position = position;
        go.transform.localScale = Vector3.one * 0.05f;
        go.GetComponent<Renderer>().material.color = color;
        Destroy(go.GetComponent<Collider>());
    }

    private void MoveMarker(Vector3 position)
    {
        if (anchorMarkerPrefab == null) return;
        if (_leftMarker == null)
        {
            _leftMarker = Instantiate(anchorMarkerPrefab, position, Quaternion.identity);
            var r = _leftMarker.GetComponent<Renderer>();
            if (r != null) r.material.color = Color.green;
        }
        else
        {
            _leftMarker.transform.position = position;
        }
    }
}
