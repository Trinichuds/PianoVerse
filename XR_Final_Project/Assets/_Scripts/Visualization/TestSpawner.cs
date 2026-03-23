using UnityEngine;

public class TestSpawner : MonoBehaviour
{
    public GameObject notePrefab;
    public Transform parent;

    void Start()
    {
        SpawnNote(new Vector3(-1, 0, 0), Color.red);
        SpawnNote(new Vector3( 0, 0, 0), Color.green);
        SpawnNote(new Vector3( 1, 0, 0), Color.blue);
    }

    void SpawnNote(Vector3 pos, Color color)
    {
        GameObject go = Instantiate(notePrefab, pos, Quaternion.identity, parent);

        WaterfallNoteView view = go.GetComponent<WaterfallNoteView>();
        if (view != null)
            view.SetColor(color);
    }
}