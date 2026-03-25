using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PianoKeyboardMapper : MonoBehaviour
{
    [Header("Calibration Anchors")]
    [Tooltip("Left edge of A0")]
    public Transform leftAnchor;
    [Tooltip("Right edge of C8")]
    public Transform rightAnchor;

    [Header("Keyboard Depth Reference")]
    public float whiteKeyDepth = 0.15f;
    public float blackKeyDepth = 0.09f;
    [Range(0.4f, 0.8f)]
    public float blackKeyWidthRatio = 0.58f;
    public bool flipDepth = false;

    [Header("LED Strips")]
    public float stripeThickness = 0.01f;
    public float stripeHeight = 0.01f;
    public float stripeYOffset = 0.003f;
    public float blackStripeExtraYOffset = 0.006f;
    public float keyGap = 0.001f;

    public bool IsCalibrated { get; private set; }

    private readonly Vector3[] _keyPositions = new Vector3[88];
    private readonly bool[] _isBlack = new bool[88];
    private readonly MeshRenderer[] _strips = new MeshRenderer[88];
    private readonly Color[] _defaultStripColors = new Color[88];
    private MaterialPropertyBlock _propBlock;
    private Material _stripMaterial;

    private static readonly bool[] BlackInOctave =
        { false, true, false, true, false, false, true, false, true, false, true, false };

    private static readonly Color IdleStrip = new(0.22f, 0.22f, 0.22f, 1f);

    private void Start()
    {
        if (leftAnchor != null && rightAnchor != null)
            Calibrate(leftAnchor.position, rightAnchor.position);
    }

    public void Calibrate(Vector3 leftEdge, Vector3 rightEdge)
    {
        if (!TryBuildStrips(leftEdge, rightEdge, 1f, out float totalWidth))
            return;

        IsCalibrated = true;
        Debug.Log($"[PianoKeyboardMapper] Calibrated strips - width:{totalWidth * 100f:F1}cm  stripCount:{CountVisibleStrips()}");
    }

    public void Preview(Vector3 leftEdge, Vector3 rightEdge)
    {
        TryBuildStrips(leftEdge, rightEdge, 0.35f, out _);
    }

    public Vector3 GetKeyPosition(int keyIndex)
    {
        if (!IsCalibrated && leftAnchor != null && rightAnchor != null)
            Calibrate(leftAnchor.position, rightAnchor.position);

        return keyIndex is >= 0 and < 88 ? _keyPositions[keyIndex] : Vector3.zero;
    }

    public bool KeyIsBlack(int keyIndex) => keyIndex is >= 0 and < 88 && _isBlack[keyIndex];

    public void SetKeyIndicatorColor(int keyIndex, Color color)
    {
        if (keyIndex is < 0 or >= 88 || _strips[keyIndex] == null)
            return;

        _propBlock ??= new MaterialPropertyBlock();
        _propBlock.SetColor("_Color", color);
        _strips[keyIndex].SetPropertyBlock(_propBlock);
    }

    public void ResetKeyIndicator(int keyIndex)
    {
        if (keyIndex is < 0 or >= 88)
            return;

        SetKeyIndicatorColor(keyIndex, _defaultStripColors[keyIndex]);
    }

    private bool TryBuildStrips(Vector3 leftEdge, Vector3 rightEdge, float alpha, out float totalWidth)
    {
        totalWidth = Vector3.Distance(leftEdge, rightEdge);
        if (totalWidth < 0.01f)
        {
            Debug.LogWarning("[PianoKeyboardMapper] Anchors too close together.");
            return false;
        }

        var kRight = (rightEdge - leftEdge).normalized;
        var kUp = Vector3.up;
        var kForward = Vector3.Cross(kRight, kUp).normalized;
        if (flipDepth)
            kForward = -kForward;

        float whiteKeyWidth = totalWidth / 52f;
        float blackKeyWidth = whiteKeyWidth * blackKeyWidthRatio;
        float whiteStripeStart = Mathf.Max(0f, whiteKeyDepth - stripeThickness);
        float blackStripeStart = Mathf.Max(0f, blackKeyDepth - stripeThickness);

        HideMainMesh();
        ClearExistingStrips();

        for (int i = 0; i < 88; i++)
        {
            int midiNote = i + 21;
            bool black = NoteIsBlack(midiNote);
            _isBlack[i] = black;

            float center = NormalizedCenter(midiNote) * totalWidth;
            float halfWidth = (black ? blackKeyWidth : whiteKeyWidth) * 0.5f - keyGap * 0.5f;
            float stripeStart = black ? blackStripeStart : whiteStripeStart;
            float yOffset = stripeYOffset + (black ? blackStripeExtraYOffset : 0f);

            _keyPositions[i] = leftEdge
                + kRight * center
                + kForward * (stripeStart + stripeThickness * 0.5f)
                + kUp * (yOffset + stripeHeight * 0.5f);

            CreateStrip(i, leftEdge, kRight, kForward, kUp, center, halfWidth, stripeStart, yOffset, alpha);
        }

        return true;
    }

    private void CreateStrip(int keyIndex, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float halfWidth, float stripeStart, float yOffset, float alpha)
    {
        _propBlock ??= new MaterialPropertyBlock();
        _stripMaterial ??= new Material(Shader.Find("Unlit/Color"));

        var go = new GameObject($"KeyStrip_{keyIndex}");
        go.transform.SetParent(transform, false);

        var meshFilter = go.AddComponent<MeshFilter>();
        var meshRenderer = go.AddComponent<MeshRenderer>();

        Vector3 bl = origin + kRight * (center - halfWidth) + kForward * stripeStart + kUp * yOffset;
        Vector3 br = origin + kRight * (center + halfWidth) + kForward * stripeStart + kUp * yOffset;
        Vector3 bl2 = bl + kForward * stripeThickness;
        Vector3 br2 = br + kForward * stripeThickness;
        Vector3 tl = bl + kUp * stripeHeight;
        Vector3 tr = br + kUp * stripeHeight;
        Vector3 tl2 = bl2 + kUp * stripeHeight;
        Vector3 tr2 = br2 + kUp * stripeHeight;

        var mesh = new Mesh { name = $"KeyStripMesh_{keyIndex}" };
        mesh.vertices = new[]
        {
            transform.InverseTransformPoint(bl),
            transform.InverseTransformPoint(br),
            transform.InverseTransformPoint(tr),
            transform.InverseTransformPoint(tl),
            transform.InverseTransformPoint(bl2),
            transform.InverseTransformPoint(br2),
            transform.InverseTransformPoint(tr2),
            transform.InverseTransformPoint(tl2)
        };
        mesh.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3
        };
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);

        meshFilter.sharedMesh = mesh;
        meshRenderer.sharedMaterial = _stripMaterial;
        meshRenderer.allowOcclusionWhenDynamic = false;

        Color idle = IdleStrip;
        idle.a = alpha;
        _defaultStripColors[keyIndex] = idle;
        _propBlock.SetColor("_Color", idle);
        meshRenderer.SetPropertyBlock(_propBlock);

        _strips[keyIndex] = meshRenderer;
    }

    private void HideMainMesh()
    {
        GetComponent<MeshFilter>().sharedMesh = null;
        var renderer = GetComponent<MeshRenderer>();
        renderer.sharedMaterials = System.Array.Empty<Material>();
        renderer.enabled = false;
    }

    private void ClearExistingStrips()
    {
        for (int i = 0; i < _strips.Length; i++)
        {
            if (_strips[i] != null)
                Destroy(_strips[i].gameObject);
            _strips[i] = null;
        }
    }

    private int CountVisibleStrips()
    {
        int count = 0;
        for (int i = 0; i < _strips.Length; i++)
            if (_strips[i] != null)
                count++;
        return count;
    }

    private static float NormalizedCenter(int midiNote)
    {
        if (NoteIsBlack(midiNote))
        {
            float left = WhiteKeyCenterNormalized(midiNote - 1);
            float right = WhiteKeyCenterNormalized(midiNote + 1);
            return (left + right) * 0.5f;
        }

        return WhiteKeyCenterNormalized(midiNote);
    }

    private static float WhiteKeyCenterNormalized(int midiNote)
    {
        int whiteIndex = 0;
        for (int note = 21; note < midiNote; note++)
        {
            if (!NoteIsBlack(note))
                whiteIndex++;
        }

        return (whiteIndex + 0.5f) / 52f;
    }

    private static bool NoteIsBlack(int midiNote) => BlackInOctave[midiNote % 12];
}
