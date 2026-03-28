using System.Collections.Generic;
using UnityEngine;

public class PianoKeyLights : MonoBehaviour
{
    public PianoKeyboardMapper mapper;
    public Midi88KeyInput midiInput;

    [Header("Sparkle")]
    [Range(1, 10)]
    public int burstCount = 3;
    [Range(0, 3)]
    public int streamRate = 1;

    private static readonly Color WhitePressed = new(0.2f, 1f, 0.4f, 0.92f);
    private static readonly Color BlackPressed = new(1f, 0.45f, 0.05f, 0.92f);

    private ParticleSystem _sparklePS;
    private readonly HashSet<int> _activeKeys = new();
    private readonly Dictionary<int, Color> _activeColors = new();

    private void OnEnable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed += OnNotePressed;
        midiInput.NoteFullyEnded += OnNoteEnded;
    }

    private void OnDisable()
    {
        if (midiInput == null) return;
        midiInput.NotePressed -= OnNotePressed;
        midiInput.NoteFullyEnded -= OnNoteEnded;
    }

    private void Start()
    {
        _sparklePS = CreateSparkleSystem();
    }

    private void Update()
    {
        if (_sparklePS == null || !mapper.IsCalibrated || streamRate <= 0) return;

        foreach (int key in _activeKeys)
        {
            Vector3 pos = mapper.GetKeyPosition(key);
            float hw = mapper.GetKeyHalfWidth(key);
            Color col = _activeColors.TryGetValue(key, out var c) ? c : Color.white;
            EmitSparkles(pos, col, streamRate, hw);
        }
    }

    private void OnNotePressed(int keyIndex, int midiNote, float velocity)
    {
        if (!mapper.IsCalibrated) return;

        bool black = mapper.KeyIsBlack(keyIndex);
        Color baseColor = black ? BlackPressed : WhitePressed;
        Color boosted = Color.Lerp(baseColor, Color.white, velocity * 0.25f);
        boosted.a = baseColor.a;
        mapper.SetKeyIndicatorColor(keyIndex, boosted);

        _activeKeys.Add(keyIndex);
        _activeColors[keyIndex] = boosted;

        if (_sparklePS != null)
            EmitSparkles(mapper.GetKeyPosition(keyIndex), boosted, burstCount, mapper.GetKeyHalfWidth(keyIndex));
    }

    private void OnNoteEnded(int keyIndex, int midiNote)
    {
        if (!mapper.IsCalibrated) return;

        mapper.ResetKeyIndicator(keyIndex);
        _activeKeys.Remove(keyIndex);
        _activeColors.Remove(keyIndex);
    }

    private void EmitSparkles(Vector3 center, Color color, int count, float halfWidth)
    {
        var ep = new ParticleSystem.EmitParams();

        for (int i = 0; i < count; i++)
        {
            ep.position = center + new Vector3(
                Random.Range(-halfWidth, halfWidth),
                Random.Range(0f, 0.002f),
                Random.Range(-0.003f, 0.003f));
            ep.velocity = new Vector3(
                Random.Range(-0.06f, 0.06f),
                Random.Range(0.12f, 0.3f),
                Random.Range(-0.06f, 0.06f));
            // Bright white-hot core with a hint of the key color
            ep.startColor = Color.Lerp(Color.white, color, Random.Range(0.05f, 0.2f));
            ep.startSize = Random.Range(0.001f, 0.002f);
            ep.startLifetime = Random.Range(0.08f, 0.18f);
            _sparklePS.Emit(ep, 1);
        }
    }

    private ParticleSystem CreateSparkleSystem()
    {
        var go = new GameObject("KeySparkles");
        go.transform.SetParent(transform, false);

        var ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 0.18f;
        main.startSpeed = 0.25f;
        main.startSize = 0.0018f;
        main.gravityModifier = 0f;
        main.maxParticles = 400;

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.enabled = false;

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
        col.color = grad;

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.5f, 0.7f), new Keyframe(1f, 0f)));

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.mainTexture = Texture2D.whiteTexture;
        renderer.material = mat;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.allowOcclusionWhenDynamic = false;

        return ps;
    }
}
