using UnityEngine;

public class WaterfallPlayer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform waterfallRoot;

    [Header("Playback")]
    [SerializeField] private float fallSpeed = 0.4f;
    [SerializeField] private bool playOnStart = true;

    [Header("Cleanup / Visibility")]
    [SerializeField] private float hideBelowY = -0.5f;

    private float songTime = 0f;
    private bool isPlaying = false;
    private Vector3 initialRootLocalPosition;

    private void Start()
    {
        if (waterfallRoot == null)
        {
            Debug.LogError("WaterfallPlayer: waterfallRoot is missing.");
            return;
        }

        initialRootLocalPosition = waterfallRoot.localPosition;
        isPlaying = playOnStart;
    }

    private void Update()
    {
        if (waterfallRoot == null)
            return;

        if (Input.GetKeyDown(KeyCode.Space))
            isPlaying = !isPlaying;

        if (Input.GetKeyDown(KeyCode.R))
            ResetPlayback();

        if (isPlaying)
            songTime += Time.deltaTime;

        waterfallRoot.localPosition = initialRootLocalPosition + Vector3.down * (songTime * fallSpeed);

        UpdateNoteVisibility();
    }

    public void ResetPlayback()
    {
        songTime = 0f;
        waterfallRoot.localPosition = initialRootLocalPosition;

        for (int i = 0; i < waterfallRoot.childCount; i++)
        {
            waterfallRoot.GetChild(i).gameObject.SetActive(true);
        }
    }

    private void UpdateNoteVisibility()
    {
        for (int i = 0; i < waterfallRoot.childCount; i++)
        {
            Transform note = waterfallRoot.GetChild(i);

            bool shouldBeVisible = note.localPosition.y + waterfallRoot.localPosition.y > hideBelowY;
            if (note.gameObject.activeSelf != shouldBeVisible)
                note.gameObject.SetActive(shouldBeVisible);
        }
    }
}