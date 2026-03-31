using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating glass menu panel for VR using World Space Canvas.
/// Right controller: thumbstick up/down to navigate, trigger to select, B to go back.
/// All visuals created procedurally.
/// </summary>
public class VRMenuPanel : MonoBehaviour
{
    [HideInInspector] public string title = "MENU";
    [HideInInspector] public bool showBackButton;

    public event Action<int> OnItemSelected;
    public event Action OnBack;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = Mathf.Clamp(value, 0, Mathf.Max(0, _items.Length - 1));
    }

    private string[] _items = { };
    private int _selectedIndex;

    // Canvas UI refs
    private Canvas _canvas;
    private RectTransform _canvasRT;
    private Image _bgImage;
    private TextMeshProUGUI _titleText;
    private TextMeshProUGUI[] _itemTexts;
    private Image[] _itemBGs;
    private TextMeshProUGUI _hintText;

    // Input
    private bool _stickReset = true;
    private bool _triggerReset = true;

    // Layout (in canvas units — 1 unit = 1 pixel at reference)
    private const float CanvasScale = 0.001f; // 1 pixel = 1mm in world
    private const float PanelW = 600f;
    private const float TitleH = 80f;
    private const float ItemH = 60f;
    private const float HintH = 40f;
    private const float Padding = 20f;

    public void SetItems(string[] items)
    {
        _items = items ?? Array.Empty<string>();
        _selectedIndex = Mathf.Clamp(_selectedIndex, 0, Mathf.Max(0, _items.Length - 1));
        Rebuild();
    }

    private void OnEnable()
    {
        if (_canvas == null && _items.Length > 0)
            Rebuild();
        // Reset input so a held trigger from a previous menu doesn't fire immediately
        _triggerReset = false;
        _stickReset = false;
        UpdateVisuals();
    }

    private void Rebuild()
    {
        // Destroy old canvas
        foreach (Transform child in transform)
            Destroy(child.gameObject);

        float panelH = Padding * 2f + TitleH + _items.Length * ItemH + HintH;

        // Canvas
        var canvasGo = new GameObject("MenuCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 100;

        _canvasRT = _canvas.GetComponent<RectTransform>();
        _canvasRT.sizeDelta = new Vector2(PanelW, panelH);
        _canvasRT.localScale = Vector3.one * CanvasScale;
        _canvasRT.localPosition = Vector3.zero;

        // Canvas scaler not needed for world space, but add raycaster-free setup
        canvasGo.AddComponent<CanvasRenderer>();

        // Background panel
        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(canvasGo.transform, false);
        _bgImage = bgGo.AddComponent<Image>();
        _bgImage.color = new Color(0.06f, 0.08f, 0.16f, 0.72f);
        var bgRT = bgGo.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        float yPos = panelH * 0.5f - Padding;

        // Title
        _titleText = CreateUIText(canvasGo.transform, "Title", title, 42, FontStyles.Bold, TextAlignmentOptions.Center);
        SetRect(_titleText.rectTransform, 0f, yPos - TitleH, PanelW, TitleH, Padding);
        yPos -= TitleH;

        // Separator line
        var sepGo = new GameObject("Sep");
        sepGo.transform.SetParent(canvasGo.transform, false);
        var sepImg = sepGo.AddComponent<Image>();
        sepImg.color = new Color(0.4f, 0.5f, 0.8f, 0.3f);
        var sepRT = sepGo.GetComponent<RectTransform>();
        sepRT.anchorMin = new Vector2(0.5f, 0.5f);
        sepRT.anchorMax = new Vector2(0.5f, 0.5f);
        sepRT.sizeDelta = new Vector2(PanelW - Padding * 4f, 2f);
        sepRT.anchoredPosition = new Vector2(0f, yPos);

        // Items
        _itemTexts = new TextMeshProUGUI[_items.Length];
        _itemBGs = new Image[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            float itemY = yPos - i * ItemH;

            // Highlight BG
            var hlGo = new GameObject($"ItemBG_{i}");
            hlGo.transform.SetParent(canvasGo.transform, false);
            _itemBGs[i] = hlGo.AddComponent<Image>();
            _itemBGs[i].color = Color.clear;
            var hlRT = hlGo.GetComponent<RectTransform>();
            hlRT.anchorMin = new Vector2(0.5f, 0.5f);
            hlRT.anchorMax = new Vector2(0.5f, 0.5f);
            hlRT.sizeDelta = new Vector2(PanelW - Padding * 2f, ItemH - 4f);
            hlRT.anchoredPosition = new Vector2(0f, itemY - ItemH * 0.5f);

            // Text
            _itemTexts[i] = CreateUIText(canvasGo.transform, $"Item_{i}", _items[i], 32, FontStyles.Normal, TextAlignmentOptions.Left);
            SetRect(_itemTexts[i].rectTransform, 0f, itemY - ItemH, PanelW, ItemH, Padding * 2f);
        }
        yPos -= _items.Length * ItemH;

        // Hint
        string hint = showBackButton
            ? "Stick Up/Down: Navigate  |  Trigger: Select  |  B: Back"
            : "Stick Up/Down: Navigate  |  Trigger: Select";
        _hintText = CreateUIText(canvasGo.transform, "Hint", hint, 20, FontStyles.Italic, TextAlignmentOptions.Center);
        _hintText.color = new Color(0.55f, 0.6f, 0.75f, 0.6f);
        SetRect(_hintText.rectTransform, 0f, yPos - HintH, PanelW, HintH, Padding);

        UpdateVisuals();
    }

    private void Update()
    {
        HandleStick();
        HandleTrigger();
        HandleBack();
        UpdateVisuals();
    }

    private void HandleStick()
    {
        Vector2 stick = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);

        if (Mathf.Abs(stick.y) < 0.4f)
        {
            _stickReset = true;
            return;
        }

        if (!_stickReset) return;
        _stickReset = false;

        if (stick.y > 0.4f)
            _selectedIndex = Mathf.Max(0, _selectedIndex - 1);
        else if (stick.y < -0.4f)
            _selectedIndex = Mathf.Min(_items.Length - 1, _selectedIndex + 1);
    }

    private void HandleTrigger()
    {
        float trigger = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger);

        if (trigger < 0.5f)
        {
            _triggerReset = true;
            return;
        }

        if (!_triggerReset) return;
        _triggerReset = false;

        OnItemSelected?.Invoke(_selectedIndex);
    }

    private void HandleBack()
    {
        if (!showBackButton) return;
        if (OVRInput.GetDown(OVRInput.RawButton.B))
            OnBack?.Invoke();
    }

    private void UpdateVisuals()
    {
        if (_itemTexts == null) return;

        for (int i = 0; i < _itemTexts.Length; i++)
        {
            bool selected = i == _selectedIndex;
            _itemTexts[i].text = (selected ? "> " : "   ") + _items[i];
            _itemTexts[i].color = selected ? Color.white : new Color(0.7f, 0.75f, 0.85f, 0.85f);
            _itemBGs[i].color = selected
                ? new Color(0.25f, 0.4f, 1f, 0.25f)
                : Color.clear;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static TextMeshProUGUI CreateUIText(Transform parent, string name, string text,
        float fontSize, FontStyles style, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.alignment = align;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;

        return tmp;
    }

    private static void SetRect(RectTransform rt, float x, float yBottom, float w, float h, float xPad)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w - xPad * 2f, h);
        rt.anchoredPosition = new Vector2(0f, yBottom + h * 0.5f);
    }
}
