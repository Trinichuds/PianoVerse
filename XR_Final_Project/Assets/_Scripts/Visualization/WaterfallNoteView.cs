using UnityEngine;

public class WaterfallNoteView : MonoBehaviour
{
    [SerializeField] private Renderer targetRenderer;

    private MaterialPropertyBlock propertyBlock;

    private void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        propertyBlock = new MaterialPropertyBlock();
    }

    public void SetColor(Color color)
    {
        if (targetRenderer == null)
            return;

        targetRenderer.GetPropertyBlock(propertyBlock);

        if (targetRenderer.sharedMaterial != null && targetRenderer.sharedMaterial.HasProperty("_BaseColor"))
        {
            propertyBlock.SetColor("_BaseColor", color);
        }
        else
        {
            propertyBlock.SetColor("_Color", color);
        }

        targetRenderer.SetPropertyBlock(propertyBlock);
    }
}