using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders falling note bars above the piano keyboard strip using 3D cubes.
/// Bars fall vertically downward toward the top line, crossing it at the note's play time.
/// </summary>
public class WaterfallRenderer : MonoBehaviour
{
    [Header("References")]
    public PianoKeyboardMapper mapper;
    public NoteMapPlayer player;

    [Header("Appearance")]
    [Tooltip("Meters of fall distance per second of song time.")]
    public float fallSpeed = 0.3f;
    [Tooltip("How many seconds of upcoming notes to show.")]
    public float lookAhead = 4f;
    [Tooltip("How long past bars linger below the line.")]
    public float trailTime = 0.3f;

    [Header("Bar Style")]
    public float barThickness = 0.003f;
    public Color whiteNoteColor = new(0.25f, 0.95f, 0.45f, 0.35f);
    public Color blackNoteColor = new(1f, 0.5f, 0.1f, 0.35f);
    public Color whiteActiveColor = new(0.3f, 1f, 0.5f, 0.7f);
    public Color blackActiveColor = new(1f, 0.55f, 0.15f, 0.7f);

    private Material _whiteMat;
    private Material _blackMat;
    private Material _headMat;
    private Mesh _cubeMesh;

    private readonly List<BarInstance> _pool = new();
    private readonly Dictionary<int, BarInstance> _activeMap = new();
    private readonly List<int> _removeList = new();
    private int _nextNoteIndex;

    private Vector3 _kRight;
    private Vector3 _kUp;
    private Vector3 _topLineOrigin;

    private class BarInstance
    {
        public GameObject go;
        public MeshRenderer mr;
        public MaterialPropertyBlock pb;
        public int noteIndex;
        public bool inUse;
        // Note head (darker bottom cap)
        public GameObject headGo;
        public MeshRenderer headMr;
        public MaterialPropertyBlock headPb;
    }

    private void Start()
    {
        CreateMaterials();
        // Grab the cube mesh from a temp primitive
        var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(temp);

        if (player != null)
            player.SongFinished += OnSongFinished;
    }

    private void OnDestroy()
    {
        if (player != null)
            player.SongFinished -= OnSongFinished;
    }

    private void OnSongFinished()
    {
        Invoke(nameof(ResetBars), trailTime + 0.5f);
    }

    // Each frame: releases bars that passed the play line, activates new bars
    // entering the look-ahead window, and updates all bar positions/colors.
    private void LateUpdate()
    {
        if (mapper == null || !mapper.IsCalibrated) return;
        if (player == null || !player.IsPlaying || player.CurrentMap == null) return;

        var notes = player.CurrentMap.notes;
        if (notes == null || notes.Length == 0) return;

        float pt = player.PlaybackTime;

        _topLineOrigin = mapper.TopLineLeft;
        _kRight = (mapper.TopLineRight - mapper.TopLineLeft).normalized;
        _kUp = Vector3.up;

        // Bars are keyed by note index rather than piano key, so repeated notes on the same pitch
        // can overlap without fighting over a single visual instance.
        // Release expired bars
        _removeList.Clear();
        foreach (var kvp in _activeMap)
        {
            var note = notes[kvp.Key];
            float barTop = ((note.start + note.dur) - pt) * fallSpeed;
            if (barTop <= 0f)
            {
                ReleaseBar(kvp.Value);
                _removeList.Add(kvp.Key);
            }
        }
        foreach (int key in _removeList)
            _activeMap.Remove(key);

        // Activate notes only once they enter the look-ahead window. Pooling keeps this cheap even
        // when dense passages bring many bars on screen at the same time.
        // Activate new bars entering look-ahead
        while (_nextNoteIndex < notes.Length)
        {
            var note = notes[_nextNoteIndex];
            float barBottom = (note.start - pt) * fallSpeed;

            if (barBottom <= lookAhead * fallSpeed)
            {
                if (!_activeMap.ContainsKey(_nextNoteIndex))
                {
                    var bar = AcquireBar();
                    bar.noteIndex = _nextNoteIndex;
                    _activeMap[_nextNoteIndex] = bar;
                }
                _nextNoteIndex++;
            }
            else break;
        }

        // Update positions
        foreach (var kvp in _activeMap)
            UpdateBar(kvp.Value, notes[kvp.Key], pt);
    }

    private const float HeadHeight = 0.012f; // darker bottom cap height

    // Positions a bar based on note timing. Bar falls toward play line (y=0).
    // Green for white keys, orange for black. Brighter when crossing the play line.
    // Includes a darker "note head" cap at the bottom for depth cue.
    private void UpdateBar(BarInstance bar, NoteEvent note, float playbackTime)
    {
        bool isBlack = mapper.KeyIsBlack(note.key);
        float halfWidth = mapper.GetKeyHalfWidth(note.key);

        Vector3 keyPos = mapper.GetKeyPosition(note.key);
        float centerX = Vector3.Dot(keyPos - _topLineOrigin, _kRight);

        float barBottom = (note.start - playbackTime) * fallSpeed;
        float barTop = barBottom + Mathf.Max(note.dur * fallSpeed, 0.003f);

        // Clamp bottom to play line (0) — bar shrinks as it crosses, then disappears
        float clampedBottom = Mathf.Max(barBottom, 0f);
        float visibleHeight = barTop - clampedBottom;

        if (visibleHeight <= 0f)
        {
            bar.go.SetActive(false);
            if (bar.headGo != null) bar.headGo.SetActive(false);
            return;
        }

        bool active = barBottom <= 0f && barTop > 0f;
        Quaternion rot = Quaternion.LookRotation(mapper.TopLineForward, _kUp);

        // Main body
        Vector3 center = _topLineOrigin
            + _kRight * centerX
            + _kUp * (clampedBottom + visibleHeight * 0.5f)
            + mapper.TopLineForward * barThickness * 0.5f;

        bar.go.transform.position = center;
        bar.go.transform.rotation = rot;
        bar.go.transform.localScale = new Vector3(halfWidth * 2f, visibleHeight, barThickness);

        Color c = active
            ? (isBlack ? blackActiveColor : whiteActiveColor)
            : (isBlack ? blackNoteColor : whiteNoteColor);

        bar.pb.SetColor("_Color", c);
        bar.mr.SetPropertyBlock(bar.pb);
        bar.mr.sharedMaterial = isBlack ? _blackMat : _whiteMat;
        bar.go.SetActive(true);

        // The separate head makes the onset edge easier to read in VR. The body can shrink at the
        // play line while the head still marks the note's actual leading edge.
        // Note head — darker cap at the note's true bottom (unclamped)
        if (bar.headGo != null)
        {
            // Head stays at the original barBottom, clamped to play line
            float headBottom = Mathf.Max(barBottom, 0f);
            float headTop = Mathf.Min(barBottom + HeadHeight, barTop);
            float headH = headTop - headBottom;

            if (headH <= 0f)
            {
                bar.headGo.SetActive(false);
                return;
            }

            Vector3 headCenter = _topLineOrigin
                + _kRight * centerX
                + _kUp * (headBottom + headH * 0.5f)
                - mapper.TopLineForward * 0.001f; // nudge toward viewer so it's visible from above

            bar.headGo.transform.position = headCenter;
            bar.headGo.transform.rotation = rot;
            bar.headGo.transform.localScale = new Vector3(halfWidth * 2f + 0.001f, headH, barThickness + 0.002f);

            Color headColor = c * 0.25f; // very dark
            headColor.a = Mathf.Min(c.a + 0.45f, 1f); // very opaque
            bar.headPb.SetColor("_Color", headColor);
            bar.headMr.SetPropertyBlock(bar.headPb);
            bar.headMr.sharedMaterial = _headMat;
            bar.headGo.SetActive(true);
        }
    }

    // -------------------------------------------------------------------------
    // Pool
    // -------------------------------------------------------------------------

    // Gets a bar from the pool, or creates a new one with cube mesh and note head.
    private BarInstance AcquireBar()
    {
        foreach (var b in _pool)
        {
            if (!b.inUse)
            {
                b.inUse = true;
                b.go.SetActive(true);
                return b;
            }
        }

        var go = new GameObject("WaterfallBar");
        go.transform.SetParent(transform, false);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _cubeMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _whiteMat;
        mr.allowOcclusionWhenDynamic = false;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        // Remove collider if cube mesh added one
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        // The head is a separate object because it follows different visual rules from the body
        // once the bar reaches the play line.
        // Note head — separate object so it has independent transform
        var headGo = new GameObject("WaterfallHead");
        headGo.transform.SetParent(transform, false);
        var headMf = headGo.AddComponent<MeshFilter>();
        headMf.sharedMesh = _cubeMesh;
        var headMr = headGo.AddComponent<MeshRenderer>();
        headMr.sharedMaterial = _whiteMat;
        headMr.allowOcclusionWhenDynamic = false;
        headMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        headMr.receiveShadows = false;

        var bar = new BarInstance
        {
            go = go, mr = mr,
            pb = new MaterialPropertyBlock(),
            headGo = headGo, headMr = headMr,
            headPb = new MaterialPropertyBlock(),
            inUse = true
        };
        _pool.Add(bar);
        return bar;
    }

    private void ReleaseBar(BarInstance bar)
    {
        bar.inUse = false;
        bar.go.SetActive(false);
        if (bar.headGo != null) bar.headGo.SetActive(false);
    }

    private void CreateMaterials()
    {
        _whiteMat = new Material(Shader.Find("Sprites/Default"));
        _whiteMat.mainTexture = Texture2D.whiteTexture;
        _whiteMat.renderQueue = 2998;

        _blackMat = new Material(Shader.Find("Sprites/Default"));
        _blackMat.mainTexture = Texture2D.whiteTexture;
        _blackMat.renderQueue = 2997;

        _headMat = new Material(Shader.Find("Sprites/Default"));
        _headMat.mainTexture = Texture2D.whiteTexture;
        _headMat.renderQueue = 2999; // renders on top of bar body
    }

    public void ResetBars()
    {
        foreach (var bar in _pool)
            ReleaseBar(bar);
        _activeMap.Clear();
        _nextNoteIndex = 0;
    }

    private void OnDisable()
    {
        ResetBars();
    }
}

