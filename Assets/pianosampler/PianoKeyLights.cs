using System.Collections.Generic;
using UnityEngine;

public class PianoKeyLights : MonoBehaviour
{
    public PianoKeyboardMapper mapper;
    public Midi88KeyInput midiInput;

    [Header("Optional — Guide/Hit Feedback")]
    public NoteMapPlayer noteMapPlayer;

    [Header("Sparkle")]
    [Range(1, 10)]
    public int burstCount = 3;
    [Range(0, 3)]
    public int streamRate = 1;

    private static readonly Color WhitePressed = new(0.2f, 1f, 0.4f, 0.92f);
    private static readonly Color BlackPressed = new(1f, 0.45f, 0.05f, 0.92f);
    private static readonly Color GuideWhite = new(0.3f, 0.5f, 1f, 0.5f);
    private static readonly Color GuideBlack = new(0.6f, 0.35f, 0.9f, 0.5f);
    private static readonly Color PerfectColor = new(1f, 1f, 0.3f, 0.95f);
    private static readonly Color GreatColor   = new(0.2f, 1f, 0.4f, 0.92f);
    private static readonly Color GoodColor    = new(0.4f, 0.7f, 1f, 0.8f);
    private static readonly Color MissColor    = new(1f, 0.15f, 0.1f, 0.9f);
    private static readonly Color WrongColor   = new(1f, 0f, 0f, 0.7f);
    private static readonly Color CorrectColor = new(0.2f, 1f, 0.5f, 0.9f);
    private static readonly Color SustainColor = new(0.6f, 0.3f, 1f, 0.75f);

    private ParticleSystem _sparklePS;
    private readonly HashSet<int> _activeKeys = new();
    private readonly Dictionary<int, Color> _activeColors = new();
    private readonly HashSet<int> _guideKeys = new();
    private readonly Dictionary<int, float> _flashTimers = new();
    private readonly List<int> _expiredFlashes = new();

    private const float FlashDuration = 0.25f;

    private bool SongActive => noteMapPlayer != null && noteMapPlayer.IsPlaying;

    private void OnEnable()
    {
        if (midiInput != null)
        {
            midiInput.NotePressed        += OnNotePressed;
            midiInput.NoteReleased       += OnNoteReleased;
            midiInput.NoteBecameSustained += OnNoteSustained;
            midiInput.NoteFullyEnded     += OnNoteEnded;
        }

        if (noteMapPlayer != null)
        {
            noteMapPlayer.GuideNoteOn       += OnGuideOn;
            noteMapPlayer.GuideNoteOff      += OnGuideOff;
            noteMapPlayer.NoteJudged        += OnNoteJudged;
            noteMapPlayer.WrongKeyPressed   += OnWrongKey;
            noteMapPlayer.CorrectKeyPressed += OnCorrectKey;
        }
    }

    private void OnDisable()
    {
        if (midiInput != null)
        {
            midiInput.NotePressed        -= OnNotePressed;
            midiInput.NoteReleased       -= OnNoteReleased;
            midiInput.NoteBecameSustained -= OnNoteSustained;
            midiInput.NoteFullyEnded     -= OnNoteEnded;
        }

        if (noteMapPlayer != null)
        {
            noteMapPlayer.GuideNoteOn       -= OnGuideOn;
            noteMapPlayer.GuideNoteOff      -= OnGuideOff;
            noteMapPlayer.NoteJudged        -= OnNoteJudged;
            noteMapPlayer.WrongKeyPressed   -= OnWrongKey;
            noteMapPlayer.CorrectKeyPressed -= OnCorrectKey;
        }
    }

    private void Start()
    {
        _sparklePS = CreateSparkleSystem();
    }

    private void Update()
    {
        if (!mapper.IsCalibrated) return;

        // Sparkle stream for held keys
        if (_sparklePS != null && streamRate > 0)
        {
            foreach (int key in _activeKeys)
            {
                Vector3 pos = mapper.GetKeyPosition(key);
                float hw = mapper.GetKeyHalfWidth(key);
                Color col = _activeColors.TryGetValue(key, out var c) ? c : Color.white;
                EmitSparkles(pos, col, streamRate, hw);
            }
        }

        // Fade out flash timers
        _expiredFlashes.Clear();
        var keys = new List<int>(_flashTimers.Keys);
        foreach (int k in keys)
        {
            float t = _flashTimers[k] - Time.deltaTime;
            if (t <= 0f)
            {
                _expiredFlashes.Add(k);
            }
            else
            {
                _flashTimers[k] = t;
            }
        }
        foreach (int k in _expiredFlashes)
        {
            _flashTimers.Remove(k);
            if (_guideKeys.Contains(k))
                SetGuideColor(k);
            else if (!_activeKeys.Contains(k))
                mapper.ResetKeyIndicator(k);
        }
    }

    // -------------------------------------------------------------------------
    // MIDI input — always fires, free-play AND song mode
    // -------------------------------------------------------------------------

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

    private void OnNoteReleased(int keyIndex, int midiNote)
    {
        // Physical key released — if sustain is holding it, transition to purple
        if (!mapper.IsCalibrated) return;
        if (midiInput != null && midiInput.IsKeySustainedOnly(keyIndex))
            return; // OnNoteSustained will handle the color change
    }

    private void OnNoteSustained(int keyIndex, int midiNote)
    {
        if (!mapper.IsCalibrated) return;

        mapper.SetKeyIndicatorColor(keyIndex, SustainColor);
        _activeColors[keyIndex] = SustainColor;
    }

    private void OnNoteEnded(int keyIndex, int midiNote)
    {
        if (!mapper.IsCalibrated) return;

        _activeKeys.Remove(keyIndex);
        _activeColors.Remove(keyIndex);

        // Restore to guide color if song is playing, otherwise idle
        if (SongActive && _guideKeys.Contains(keyIndex))
            SetGuideColor(keyIndex);
        else
            mapper.ResetKeyIndicator(keyIndex);
    }

    // -------------------------------------------------------------------------
    // Guide notes
    // -------------------------------------------------------------------------

    private void OnGuideOn(int keyIndex, float duration)
    {
        if (!mapper.IsCalibrated) return;
        _guideKeys.Add(keyIndex);
        SetGuideColor(keyIndex);
    }

    private void OnGuideOff(int keyIndex)
    {
        _guideKeys.Remove(keyIndex);
        if (!_activeKeys.Contains(keyIndex) && !_flashTimers.ContainsKey(keyIndex))
            mapper.ResetKeyIndicator(keyIndex);
    }

    private void SetGuideColor(int keyIndex)
    {
        Color c = mapper.KeyIsBlack(keyIndex) ? GuideBlack : GuideWhite;
        mapper.SetKeyIndicatorColor(keyIndex, c);
    }

    // -------------------------------------------------------------------------
    // Hit quality feedback
    // -------------------------------------------------------------------------

    private void OnNoteJudged(HitResult result)
    {
        if (!mapper.IsCalibrated) return;

        Color c = result.quality switch
        {
            HitQuality.Perfect => PerfectColor,
            HitQuality.Great   => GreatColor,
            HitQuality.Good    => GoodColor,
            _                  => MissColor
        };

        mapper.SetKeyIndicatorColor(result.keyIndex, c);
        _flashTimers[result.keyIndex] = FlashDuration;

        if (result.quality != HitQuality.Miss && _sparklePS != null)
        {
            int count = result.quality == HitQuality.Perfect ? burstCount * 2 : burstCount;
            EmitSparkles(mapper.GetKeyPosition(result.keyIndex), c, count,
                mapper.GetKeyHalfWidth(result.keyIndex));
        }
    }

    private void OnWrongKey(int keyIndex)
    {
        if (!mapper.IsCalibrated) return;
        mapper.SetKeyIndicatorColor(keyIndex, WrongColor);
        _flashTimers[keyIndex] = FlashDuration;
    }

    private void OnCorrectKey(int keyIndex)
    {
        if (!mapper.IsCalibrated) return;
        mapper.SetKeyIndicatorColor(keyIndex, CorrectColor);
        _activeKeys.Add(keyIndex);
        _activeColors[keyIndex] = CorrectColor;

        if (_sparklePS != null)
            EmitSparkles(mapper.GetKeyPosition(keyIndex), CorrectColor, burstCount,
                mapper.GetKeyHalfWidth(keyIndex));
    }

    // -------------------------------------------------------------------------
    // Sparkle system
    // -------------------------------------------------------------------------

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
