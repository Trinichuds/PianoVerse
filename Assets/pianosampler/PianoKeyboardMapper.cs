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
    [Range(0.4f, 0.8f)]
    public float blackKeyWidthRatio = 0.58f;
    public bool flipDepth = false;

    [Header("LED Strips")]
    public float stripeThickness = 0.004f;
    public float stripeHeight = 0.005f;
    public float stripeYOffset = 0.003f;
    public float blackCapHeight = 0.007f;
    [Range(0.15f, 0.5f)]
    public float blackStemWidthRatio = 0.3f;
    public float keyGap = 0.001f;

    [Header("Glow")]
    [Range(0f, 0.01f)]
    public float glowPadding = 0.003f;

    [Header("Top Line")]
    public float topLineThickness = 0.003f;
    public Color topLineColor = new(0.85f, 0.88f, 1f, 0.7f);
    public float glowLineThickness = 0.008f;
    public Color glowLineColor = new(0.4f, 0.5f, 1f, 0.25f);

    public bool IsCalibrated { get; private set; }

    /// <summary>World-space left end of the top line (above A0).</summary>
    public Vector3 TopLineLeft { get; private set; }
    /// <summary>World-space right end of the top line (above C8).</summary>
    public Vector3 TopLineRight { get; private set; }
    /// <summary>Forward direction (away from player, into the keyboard).</summary>
    public Vector3 TopLineForward { get; private set; }
    /// <summary>Y position of the top line.</summary>
    public float TopLineY { get; private set; }

    private readonly Vector3[] _keyPositions = new Vector3[88];
    private readonly bool[] _isBlack = new bool[88];
    private readonly MeshRenderer[] _strips = new MeshRenderer[88];
    private readonly MeshRenderer[] _glowStrips = new MeshRenderer[88];
    private GameObject _topLineObj;
    private GameObject _glowLineObj;
    private readonly Color[] _defaultStripColors = new Color[88];
    private readonly float[] _keyHalfWidths = new float[88];
    private MaterialPropertyBlock _propBlock;
    private MaterialPropertyBlock _glowPropBlock;
    private Material _whiteStripMaterial;
    private Material _blackStripMaterial;
    private Material _glowMaterial;

    private static readonly bool[] BlackInOctave =
        { false, true, false, true, false, false, true, false, true, false, true, false };

    private static readonly Color IdleWhite = new(0.55f, 0.58f, 0.7f, 0.18f);
    private static readonly Color IdleBlack = new(0.35f, 0.38f, 0.5f, 0.13f);

    private void Start()
    {
        if (leftAnchor != null && rightAnchor != null)
            Calibrate(leftAnchor.position, rightAnchor.position);
    }

    // Takes two world-space edge points and builds 88 key strips between them.
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

    public float GetKeyHalfWidth(int keyIndex)
    {
        if (!IsCalibrated || keyIndex is < 0 or >= 88) return 0.005f;
        return _keyHalfWidths[keyIndex];
    }

    public void SetKeyIndicatorColor(int keyIndex, Color color)
    {
        if (keyIndex is < 0 or >= 88 || _strips[keyIndex] == null)
            return;

        _propBlock ??= new MaterialPropertyBlock();
        _propBlock.SetColor("_Color", color);
        _strips[keyIndex].SetPropertyBlock(_propBlock);

        if (_glowStrips[keyIndex] != null)
        {
            _glowPropBlock ??= new MaterialPropertyBlock();
            Color glow = color;
            glow.a = 0.2f;
            _glowPropBlock.SetColor("_Color", glow);
            _glowStrips[keyIndex].SetPropertyBlock(_glowPropBlock);
            _glowStrips[keyIndex].enabled = true;
        }
    }

    public void ResetKeyIndicator(int keyIndex)
    {
        if (keyIndex is < 0 or >= 88)
            return;

        _propBlock ??= new MaterialPropertyBlock();
        _propBlock.SetColor("_Color", _defaultStripColors[keyIndex]);
        if (_strips[keyIndex] != null)
            _strips[keyIndex].SetPropertyBlock(_propBlock);

        if (_glowStrips[keyIndex] != null)
            _glowStrips[keyIndex].enabled = false;
    }

    // Calculates all 88 key positions, creates strip meshes, and builds the top line.
    // White keys get simple boxes, black keys get inverted-T shapes.
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
        float stemHalfWidth = blackKeyWidth * 0.5f * blackStemWidthRatio;
        float stripeStart = Mathf.Max(0f, whiteKeyDepth - stripeThickness);

        HideMainMesh();
        ClearExistingStrips();
        EnsureMaterials();

        for (int i = 0; i < 88; i++)
        {
            int midiNote = i + 21;
            bool black = NoteIsBlack(midiNote);
            _isBlack[i] = black;

            float center = NormalizedCenter(midiNote) * totalWidth;
            float halfWidth = (black ? blackKeyWidth : whiteKeyWidth) * 0.5f - keyGap * 0.5f;

            float totalHeight = black ? stripeHeight + blackCapHeight : stripeHeight;
            // Common top baseline: white tops at stripeYOffset + stripeHeight
            // Black hangs down from there, so its base is lower
            float topLine = stripeYOffset + stripeHeight;
            float yBase = black ? topLine - totalHeight : stripeYOffset;

            _keyPositions[i] = leftEdge
                + kRight * center
                + kForward * (stripeStart + stripeThickness * 0.5f)
                + kUp * (yBase + totalHeight * 0.5f);
            _keyHalfWidths[i] = halfWidth;

            // Black strips sit slightly closer to viewer so stem always renders on top
            float zNudge = black ? -0.0015f : 0f;

            if (black)
                CreateBlackStrip(i, leftEdge, kRight, kForward, kUp, center, halfWidth,
                    stemHalfWidth, stripeStart + zNudge, alpha);
            else
                CreateWhiteStrip(i, leftEdge, kRight, kForward, kUp, center, halfWidth,
                    stripeStart, alpha);
        }

        // Build top line hugging the top edge of the strip bar
        float tl = stripeYOffset + stripeHeight;
        TopLineLeft = leftEdge + kUp * tl + kForward * stripeStart;
        TopLineRight = rightEdge + kUp * tl + kForward * stripeStart;
        TopLineForward = kForward;
        TopLineY = tl;
        BuildTopLine(leftEdge, rightEdge, kRight, kForward, kUp, tl, stripeStart);

        return true;
    }

    // Creates the horizontal guide line at the top of the keyboard strip.
    // Two lines: a soft glow behind and a bright sharp line on top.
    private void BuildTopLine(Vector3 leftEdge, Vector3 rightEdge, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float y, float stripeStart)
    {
        if (_topLineObj != null) Destroy(_topLineObj);
        if (_glowLineObj != null) Destroy(_glowLineObj);

        Vector3 left = leftEdge + kUp * y + kForward * (stripeStart + stripeThickness * 0.5f);
        Vector3 right = rightEdge + kUp * y + kForward * (stripeStart + stripeThickness * 0.5f);

        // Glow line (wider, softer, behind main line)
        _glowLineObj = new GameObject("TopLineGlow");
        _glowLineObj.transform.SetParent(transform, false);
        var glowLr = _glowLineObj.AddComponent<LineRenderer>();
        glowLr.useWorldSpace = true;
        glowLr.positionCount = 2;
        glowLr.SetPosition(0, left);
        glowLr.SetPosition(1, right);
        glowLr.startWidth = glowLineThickness;
        glowLr.endWidth = glowLineThickness;
        var glowMat = new Material(Shader.Find("Sprites/Default"));
        glowMat.mainTexture = Texture2D.whiteTexture;
        glowMat.renderQueue = 2999;
        glowLr.material = glowMat;
        glowLr.startColor = glowLineColor;
        glowLr.endColor = glowLineColor;
        glowLr.allowOcclusionWhenDynamic = false;

        // Main line (sharp, bright)
        _topLineObj = new GameObject("TopLine");
        _topLineObj.transform.SetParent(transform, false);
        var lr = _topLineObj.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.SetPosition(0, left);
        lr.SetPosition(1, right);
        lr.startWidth = topLineThickness;
        lr.endWidth = topLineThickness;
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.mainTexture = Texture2D.whiteTexture;
        mat.renderQueue = 3003;
        lr.material = mat;
        lr.startColor = topLineColor;
        lr.endColor = topLineColor;
        lr.allowOcclusionWhenDynamic = false;
    }

    private void EnsureMaterials()
    {
        if (_whiteStripMaterial == null)
        {
            _whiteStripMaterial = new Material(Shader.Find("Sprites/Default"));
            _whiteStripMaterial.mainTexture = Texture2D.whiteTexture;
            _whiteStripMaterial.renderQueue = 3000;
        }

        if (_blackStripMaterial == null)
        {
            _blackStripMaterial = new Material(Shader.Find("Sprites/Default"));
            _blackStripMaterial.mainTexture = Texture2D.whiteTexture;
            _blackStripMaterial.renderQueue = 3002;
        }

        if (_glowMaterial == null)
        {
            _glowMaterial = new Material(Shader.Find("Sprites/Default"));
            _glowMaterial.mainTexture = Texture2D.whiteTexture;
            _glowMaterial.renderQueue = 2999;
        }
    }

    private void CreateWhiteStrip(int keyIndex, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float halfWidth, float stripeStart, float alpha)
    {
        _propBlock ??= new MaterialPropertyBlock();
        _glowPropBlock ??= new MaterialPropertyBlock();

        var go = new GameObject($"KeyStrip_{keyIndex}");
        go.transform.SetParent(transform, false);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        float topLine = stripeYOffset + stripeHeight;
        mf.sharedMesh = BuildBoxMesh($"KeyStripMesh_{keyIndex}",
            origin, kRight, kForward, kUp, center, halfWidth, stripeStart,
            topLine - stripeHeight, stripeThickness, stripeHeight);
        mr.sharedMaterial = _whiteStripMaterial;
        mr.allowOcclusionWhenDynamic = false;

        Color idle = IdleWhite;
        idle.a *= alpha;
        _defaultStripColors[keyIndex] = idle;
        _propBlock.SetColor("_Color", idle);
        mr.SetPropertyBlock(_propBlock);
        _strips[keyIndex] = mr;

        CreateGlow(keyIndex, origin, kRight, kForward, kUp, center, halfWidth,
            stripeStart, stripeYOffset, stripeHeight);
    }

    private void CreateBlackStrip(int keyIndex, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float capHalfWidth, float stemHalfWidth, float stripeStart, float alpha)
    {
        _propBlock ??= new MaterialPropertyBlock();
        _glowPropBlock ??= new MaterialPropertyBlock();

        var go = new GameObject($"KeyStrip_{keyIndex}");
        go.transform.SetParent(transform, false);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        // Inverted T: narrow stem at top (shares top line with white), wide cap hangs below
        float topLine = stripeYOffset + stripeHeight;
        float capBottom = topLine - stripeHeight - blackCapHeight;
        mf.sharedMesh = BuildInvertedTMesh($"KeyStripMesh_{keyIndex}",
            origin, kRight, kForward, kUp, center,
            stemHalfWidth, capHalfWidth,
            stripeStart, topLine,
            stripeThickness, stripeHeight, blackCapHeight);
        mr.sharedMaterial = _blackStripMaterial;
        mr.allowOcclusionWhenDynamic = false;

        Color idle = IdleBlack;
        idle.a *= alpha;
        _defaultStripColors[keyIndex] = idle;
        _propBlock.SetColor("_Color", idle);
        mr.SetPropertyBlock(_propBlock);
        _strips[keyIndex] = mr;

        float totalHeight = stripeHeight + blackCapHeight;
        CreateGlow(keyIndex, origin, kRight, kForward, kUp, center, capHalfWidth,
            stripeStart, capBottom, totalHeight);
    }

    private void CreateGlow(int keyIndex, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float halfWidth, float stripeStart, float yOffset, float height)
    {
        var glowGo = new GameObject($"KeyGlow_{keyIndex}");
        glowGo.transform.SetParent(transform, false);

        var glowMf = glowGo.AddComponent<MeshFilter>();
        var glowMr = glowGo.AddComponent<MeshRenderer>();

        float pad = glowPadding;
        glowMf.sharedMesh = BuildBoxMesh($"KeyGlowMesh_{keyIndex}",
            origin, kRight, kForward, kUp, center, halfWidth + pad, stripeStart - pad,
            yOffset - pad, stripeThickness + pad * 2f, height + pad * 2f);
        glowMr.sharedMaterial = _glowMaterial;
        glowMr.allowOcclusionWhenDynamic = false;
        glowMr.enabled = false;

        _glowStrips[keyIndex] = glowMr;
    }

    /// <summary>
    /// Inverted T-shape: narrow stem at top sharing the white key top line,
    /// wide cap hanging below.
    /// Stem: from (topLine - stemHeight) to topLine, width = stemHW*2
    /// Cap:  from (topLine - stemHeight - capHeight) to (topLine - stemHeight), width = capHW*2
    /// </summary>
    // Builds a T-shape mesh for black keys: narrow stem at top, wide cap hanging below.
    private Mesh BuildInvertedTMesh(string name, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float stemHW, float capHW,
        float stripeStart, float topLine,
        float thickness, float stemHeight, float capHeight)
    {
        float stemTop = topLine;
        float stemBot = topLine - stemHeight;
        float capBot = stemBot - capHeight;

        // Stem front
        Vector3 sFbl = origin + kRight * (center - stemHW) + kForward * stripeStart + kUp * stemBot;
        Vector3 sFbr = origin + kRight * (center + stemHW) + kForward * stripeStart + kUp * stemBot;
        Vector3 sFtl = origin + kRight * (center - stemHW) + kForward * stripeStart + kUp * stemTop;
        Vector3 sFtr = origin + kRight * (center + stemHW) + kForward * stripeStart + kUp * stemTop;
        // Stem back
        Vector3 sBbl = sFbl + kForward * thickness;
        Vector3 sBbr = sFbr + kForward * thickness;
        Vector3 sBtl = sFtl + kForward * thickness;
        Vector3 sBtr = sFtr + kForward * thickness;

        // Cap front
        Vector3 cFbl = origin + kRight * (center - capHW) + kForward * stripeStart + kUp * capBot;
        Vector3 cFbr = origin + kRight * (center + capHW) + kForward * stripeStart + kUp * capBot;
        Vector3 cFtl = origin + kRight * (center - capHW) + kForward * stripeStart + kUp * stemBot;
        Vector3 cFtr = origin + kRight * (center + capHW) + kForward * stripeStart + kUp * stemBot;
        // Cap back
        Vector3 cBbl = cFbl + kForward * thickness;
        Vector3 cBbr = cFbr + kForward * thickness;
        Vector3 cBtl = cFtl + kForward * thickness;
        Vector3 cBtr = cFtr + kForward * thickness;

        var mesh = new Mesh { name = name };
        mesh.vertices = new[]
        {
            // Stem: front bl(0), br(1), tr(2), tl(3) | back bl(4), br(5), tr(6), tl(7)
            transform.InverseTransformPoint(sFbl), transform.InverseTransformPoint(sFbr),
            transform.InverseTransformPoint(sFtr), transform.InverseTransformPoint(sFtl),
            transform.InverseTransformPoint(sBbl), transform.InverseTransformPoint(sBbr),
            transform.InverseTransformPoint(sBtr), transform.InverseTransformPoint(sBtl),
            // Cap: front bl(8), br(9), tr(10), tl(11) | back bl(12), br(13), tr(14), tl(15)
            transform.InverseTransformPoint(cFbl), transform.InverseTransformPoint(cFbr),
            transform.InverseTransformPoint(cFtr), transform.InverseTransformPoint(cFtl),
            transform.InverseTransformPoint(cBbl), transform.InverseTransformPoint(cBbr),
            transform.InverseTransformPoint(cBtr), transform.InverseTransformPoint(cBtl),
        };
        mesh.triangles = new[]
        {
            // Stem 6 faces
            0,2,1, 0,3,2,
            4,5,6, 4,6,7,
            0,1,5, 0,5,4,
            3,7,6, 3,6,2,
            1,2,6, 1,6,5,
            0,4,7, 0,7,3,
            // Cap 6 faces
            8,10,9, 8,11,10,
            12,13,14, 12,14,15,
            8,9,13, 8,13,12,
            11,15,14, 11,14,10,
            9,10,14, 9,14,13,
            8,12,15, 8,15,11,
        };
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
        return mesh;
    }

    // Builds a simple rectangular box mesh for white key strips.
    private Mesh BuildBoxMesh(string name, Vector3 origin, Vector3 kRight, Vector3 kForward, Vector3 kUp,
        float center, float halfWidth, float stripeStart, float yOffset, float thickness, float height)
    {
        Vector3 bl = origin + kRight * (center - halfWidth) + kForward * stripeStart + kUp * yOffset;
        Vector3 br = origin + kRight * (center + halfWidth) + kForward * stripeStart + kUp * yOffset;
        Vector3 bl2 = bl + kForward * thickness;
        Vector3 br2 = br + kForward * thickness;
        Vector3 tl = bl + kUp * height;
        Vector3 tr = br + kUp * height;
        Vector3 tl2 = bl2 + kUp * height;
        Vector3 tr2 = br2 + kUp * height;

        var mesh = new Mesh { name = name };
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
        return mesh;
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
            if (_strips[i] != null) Destroy(_strips[i].gameObject);
            if (_glowStrips[i] != null) Destroy(_glowStrips[i].gameObject);
            _strips[i] = null;
            _glowStrips[i] = null;
        }
        if (_topLineObj != null) { Destroy(_topLineObj); _topLineObj = null; }
        if (_glowLineObj != null) { Destroy(_glowLineObj); _glowLineObj = null; }
    }

    private int CountVisibleStrips()
    {
        int count = 0;
        for (int i = 0; i < _strips.Length; i++)
            if (_strips[i] != null) count++;
        return count;
    }

    // Returns the horizontal center of a key as a 0-1 fraction of keyboard width.
    // Black keys are centered between their adjacent white keys.
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
