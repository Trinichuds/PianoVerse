using UnityEngine;

public class HitLineVisualizer : MonoBehaviour
{
    [SerializeField] private WaterfallBuilder waterfallBuilder;

    [Header("Hit Line Appearance")]
    [SerializeField] private float lineThickness = 0.005f;
    [SerializeField] private float lineDepth = 0.06f;

    private void Start()
    {
        Apply();
    }

    private void OnValidate()
    {
        Apply();
    }

    [ContextMenu("Apply Hit Line")]
    public void Apply()
    {
        if (waterfallBuilder == null)
            return;

        transform.localPosition = new Vector3(
            0f,
            waterfallBuilder.HitLineY,
            (waterfallBuilder.WhiteLaneZ + waterfallBuilder.BlackLaneZ) * 0.5f
        );

        transform.localScale = new Vector3(
            waterfallBuilder.KeyboardWidth,
            lineThickness,
            lineDepth
        );
    }
}