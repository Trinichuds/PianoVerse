using UnityEngine;

public class PianoKeyLights : MonoBehaviour
{
    public PianoKeyboardMapper mapper;
    public Midi88KeyInput midiInput;

    private static readonly Color WhitePressed = Color.green;
    private static readonly Color BlackPressed = new(1f, 0.5f, 0f, 1f);

    private void OnEnable()
    {
        if (midiInput == null)
            return;

        midiInput.NotePressed += OnNotePressed;
        midiInput.NoteFullyEnded += OnNoteEnded;
    }

    private void OnDisable()
    {
        if (midiInput == null)
            return;

        midiInput.NotePressed -= OnNotePressed;
        midiInput.NoteFullyEnded -= OnNoteEnded;
    }

    private void OnNotePressed(int keyIndex, int midiNote, float velocity)
    {
        if (!mapper.IsCalibrated)
            return;

        mapper.SetKeyIndicatorColor(keyIndex, mapper.KeyIsBlack(keyIndex) ? BlackPressed : WhitePressed);
    }

    private void OnNoteEnded(int keyIndex, int midiNote)
    {
        if (!mapper.IsCalibrated)
            return;

        mapper.ResetKeyIndicator(keyIndex);
    }
}
