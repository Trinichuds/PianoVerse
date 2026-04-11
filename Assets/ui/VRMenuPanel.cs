using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating glass menu panel for VR using World Space Canvas.
/// Right controller: thumbstick up/down to navigate, trigger to select, B to go back.
/// All visuals created procedurally.
/// Shows a scrollable window of items (max 8 visible at once).
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
    private int _scrollOffset; // first visible item index

    // Canvas UI refs
    private Canvas _canvas;
    private RectTransform _canvasRT;
    private Image _bgImage;
    private TextMeshProUGUI _titleText;
    private TextMeshProUGUI _countText; // "3/40" indicator
    private TextMeshProUGUI[] _slotTexts;
    private Image[] _slotBGs;
    private TextMeshProUGUI _hintText;
    private Image _scrollUpArrow;
    private Image _scrollDownArrow;

    // Input
    private bool _stickReset = true;
    private bool _triggerReset = true;

    // Layout (in canvas units — 1 unit = 1 pixel at reference)
    private const float CanvasScale = 0.001f; // 1 pixel = 1mm in world
    private const float PanelW = 600f;
    private const float TitleH = 80f;
    private const float ItemH = 60f;
    private const float HintH = 40f;
    private const float ArrowH = 30f;
    private const float Padding = 20f;
    private const int MaxVisible = 8;

    // Updates the list of menu items and rebuilds the UI.
    public void SetItems(string[] items)
    {
        _items = items ?? Array.Empty<string>();
        _selectedIndex = Mathf.Clamp(_selectedIndex, 0, Mathf.Max(0, _items.Length - 1));
        _scrollOffset = 0;
        Rebuild();
    }

    private void OnEnable()
    {
        if (_canvas == null && _items.Length > 0)
            Rebuild();
        _triggerReset = false;
        _stickReset = false;
        UpdateVisuals();
    }

    // Destroys existing UI and rebuilds the entire panel from scratch:
    // canvas, background, title, scroll arrows, item slots, and hint text.
    private void Rebuild()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);

        int visibleCount = Mathf.Min(_items.Length, MaxVisible);
        bool scrollable = _items.Length > MaxVisible;

        float itemsBlockH = visibleCount * ItemH;
        float arrowsH = scrollable ? ArrowH * 2f : 0f;
        float panelH = Padding * 2f + TitleH + itemsBlockH + arrowsH + HintH;

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

        canvasGo.AddComponent<CanvasRenderer>();

        // Background
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

        // Count indicator (right-aligned next to title)
        if (_items.Length > MaxVisible)
        {
            _countText = CreateUIText(canvasGo.transform, "Count", "", 22, FontStyles.Normal, TextAlignmentOptions.Right);
            _countText.color = new Color(0.5f, 0.55f, 0.7f, 0.6f);
            var countRT = _countText.rectTransform;
            countRT.anchorMin = new Vector2(0.5f, 0.5f);
            countRT.anchorMax = new Vector2(0.5f, 0.5f);
            countRT.sizeDelta = new Vector2(PanelW - Padding * 4f, 30f);
            countRT.anchoredPosition = new Vector2(0f, yPos + 5f);
        }

        // Separator
        var sepGo = new GameObject("Sep");
        sepGo.transform.SetParent(canvasGo.transform, false);
        var sepImg = sepGo.AddComponent<Image>();
        sepImg.color = new Color(0.4f, 0.5f, 0.8f, 0.3f);
        var sepRT = sepGo.GetComponent<RectTransform>();
        sepRT.anchorMin = new Vector2(0.5f, 0.5f);
        sepRT.anchorMax = new Vector2(0.5f, 0.5f);
        sepRT.sizeDelta = new Vector2(PanelW - Padding * 4f, 2f);
        sepRT.anchoredPosition = new Vector2(0f, yPos);

        // Scroll up arrow
        if (scrollable)
        {
            _scrollUpArrow = CreateArrow(canvasGo.transform, "ScrollUp", "\u25B2"); // ▲
            var upRT = _scrollUpArrow.rectTransform;
            upRT.anchorMin = new Vector2(0.5f, 0.5f);
            upRT.anchorMax = new Vector2(0.5f, 0.5f);
            upRT.sizeDelta = new Vector2(PanelW - Padding * 2f, ArrowH);
            upRT.anchoredPosition = new Vector2(0f, yPos - ArrowH * 0.5f);
            yPos -= ArrowH;
        }

        // Item slots (fixed number)
        _slotTexts = new TextMeshProUGUI[visibleCount];
        _slotBGs = new Image[visibleCount];
        for (int i = 0; i < visibleCount; i++)
        {
            float itemY = yPos - i * ItemH;

            var hlGo = new GameObject($"SlotBG_{i}");
            hlGo.transform.SetParent(canvasGo.transform, false);
            _slotBGs[i] = hlGo.AddComponent<Image>();
            _slotBGs[i].color = Color.clear;
            var hlRT = hlGo.GetComponent<RectTransform>();
            hlRT.anchorMin = new Vector2(0.5f, 0.5f);
            hlRT.anchorMax = new Vector2(0.5f, 0.5f);
            hlRT.sizeDelta = new Vector2(PanelW - Padding * 2f, ItemH - 4f);
            hlRT.anchoredPosition = new Vector2(0f, itemY - ItemH * 0.5f);

            _slotTexts[i] = CreateUIText(canvasGo.transform, $"Slot_{i}", "", 32, FontStyles.Normal, TextAlignmentOptions.Left);
            SetRect(_slotTexts[i].rectTransform, 0f, itemY - ItemH, PanelW, ItemH, Padding * 2f);
        }
        yPos -= visibleCount * ItemH;

        // Scroll down arrow
        if (scrollable)
        {
            _scrollDownArrow = CreateArrow(canvasGo.transform, "ScrollDown", "\u25BC"); // ▼
            var dnRT = _scrollDownArrow.rectTransform;
            dnRT.anchorMin = new Vector2(0.5f, 0.5f);
            dnRT.anchorMax = new Vector2(0.5f, 0.5f);
            dnRT.sizeDelta = new Vector2(PanelW - Padding * 2f, ArrowH);
            dnRT.anchoredPosition = new Vector2(0f, yPos - ArrowH * 0.5f);
            yPos -= ArrowH;
        }

        // Hint
        string hint = showBackButton
            ? "Stick: Navigate  |  Trigger: Select  |  B: Back"
            : "Stick: Navigate  |  Trigger: Select";
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

    // Right thumbstick up/down to move selection. Uses dead zone and edge trigger.
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

        // Keep selection within visible window
        EnsureSelectionVisible();
    }

    // Adjusts scroll offset so the selected item is always within the visible window.
    private void EnsureSelectionVisible()
    {
        int visible = _slotTexts != null ? _slotTexts.Length : MaxVisible;
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + visible)
            _scrollOffset = _selectedIndex - visible + 1;
    }

    // Right index trigger to confirm selection. Edge-triggered at 50% threshold.
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

    // Refreshes slot text, highlight colors, scroll arrow visibility, and count indicator.
    private void UpdateVisuals()
    {
        if (_slotTexts == null) return;

        int visibleCount = _slotTexts.Length;

        for (int slot = 0; slot < visibleCount; slot++)
        {
            int itemIndex = _scrollOffset + slot;

            if (itemIndex >= _items.Length)
            {
                _slotTexts[slot].text = "";
                _slotBGs[slot].color = Color.clear;
                continue;
            }

            bool selected = itemIndex == _selectedIndex;
            _slotTexts[slot].text = (selected ? "> " : "   ") + _items[itemIndex];
            _slotTexts[slot].color = selected ? Color.white : new Color(0.7f, 0.75f, 0.85f, 0.85f);
            _slotBGs[slot].color = selected
                ? new Color(0.25f, 0.4f, 1f, 0.25f)
                : Color.clear;
        }

        // Scroll arrows visibility
        if (_scrollUpArrow != null)
            _scrollUpArrow.color = _scrollOffset > 0
                ? new Color(0.6f, 0.7f, 1f, 0.5f)
                : new Color(0.3f, 0.3f, 0.4f, 0.15f);

        if (_scrollDownArrow != null)
            _scrollDownArrow.color = _scrollOffset + visibleCount < _items.Length
                ? new Color(0.6f, 0.7f, 1f, 0.5f)
                : new Color(0.3f, 0.3f, 0.4f, 0.15f);

        // Count text
        if (_countText != null)
            _countText.text = $"{_selectedIndex + 1} / {_items.Length}";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static TextMeshProUGUI CreateUIText(Transform parent, string name, string text,
        float fontSize, FontStyles style, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        go.AddComponent<RectTransform>();
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

    private static Image CreateArrow(Transform parent, string name, string symbol)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = Color.clear; // used as background tint

        // Arrow text child
        var textGo = new GameObject("ArrowText");
        textGo.transform.SetParent(go.transform, false);
        var rt = textGo.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text = symbol;
        tmp.fontSize = 20;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = new Color(0.6f, 0.7f, 1f, 0.5f);
        tmp.raycastTarget = false;

        return img;
    }

    private static void SetRect(RectTransform rt, float x, float yBottom, float w, float h, float xPad)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w - xPad * 2f, h);
        rt.anchoredPosition = new Vector2(0f, yBottom + h * 0.5f);
    }
}
