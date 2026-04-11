using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using Minis;

public class Midi88KeyInput : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Text outputText;
    [SerializeField] private bool refreshUiEveryFrame = true;

    [Header("Keyboard Range")]
    [SerializeField] private int minMidiNote = 21;   // A0
    [SerializeField] private int maxMidiNote = 108;  // C8

    [Header("Pedal")]
    [SerializeField] private int sustainCc = 64;

    private readonly List<MidiDevice> connectedDevices = new();
    private readonly HashSet<int> physicallyHeldNotes = new();
    private readonly HashSet<int> sustainedOnlyNotes = new();
    private readonly Dictionary<int, float> latestVelocity = new();
    private readonly StringBuilder sb = new();

    private float sustainValue;
    private bool uiDirty;

    public bool SustainOn => sustainValue >= 0.5f;

    // Events for other game systems
    public event Action<int, int, float> NotePressed;       // keyIndex0to87, midiNote, velocity0to1
    public event Action<int, int> NoteReleased;             // keyIndex0to87, midiNote
    public event Action<int, int> NoteBecameSustained;      // keyIndex0to87, midiNote
    public event Action<int, int> NoteFullyEnded;           // keyIndex0to87, midiNote
    public event Action<bool, float> SustainChanged;        // on/off, raw 0to1

    private void OnEnable()
    {
        foreach (var device in InputSystem.devices)
        {
            if (device is MidiDevice midi)
                RegisterDevice(midi);
        }

        InputSystem.onDeviceChange += OnDeviceChange;
        MarkUiDirty();
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;

        for (int i = 0; i < connectedDevices.Count; i++)
            UnregisterDevice(connectedDevices[i]);

        connectedDevices.Clear();
        physicallyHeldNotes.Clear();
        sustainedOnlyNotes.Clear();
        latestVelocity.Clear();

        sustainValue = 0f;
        MarkUiDirty();
    }

    private void LateUpdate()
    {
        if (refreshUiEveryFrame && uiDirty)
            RefreshOutput();
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (device is not MidiDevice midi) return;

        switch (change)
        {
            case InputDeviceChange.Added:
            case InputDeviceChange.Reconnected:
                RegisterDevice(midi);
                break;

            case InputDeviceChange.Removed:
            case InputDeviceChange.Disconnected:
                UnregisterDevice(midi);
                break;
        }
    }

    private void RegisterDevice(MidiDevice midi)
    {
        if (connectedDevices.Contains(midi)) return;

        connectedDevices.Add(midi);
        midi.onWillNoteOn += HandleNoteOn;
        midi.onWillNoteOff += HandleNoteOff;
        midi.onWillControlChange += HandleControlChange;
    }

    private void UnregisterDevice(MidiDevice midi)
    {
        if (!connectedDevices.Contains(midi)) return;

        midi.onWillNoteOn -= HandleNoteOn;
        midi.onWillNoteOff -= HandleNoteOff;
        midi.onWillControlChange -= HandleControlChange;

        connectedDevices.Remove(midi);
    }

    // Called when a MIDI key is pressed. Adds to held set and fires NotePressed event.
    private void HandleNoteOn(MidiNoteControl noteControl, float velocity)
    {
        int midiNote = noteControl.noteNumber;
        if (!IsIn88KeyRange(midiNote)) return;

        int keyIndex = ToKeyIndex(midiNote);

        physicallyHeldNotes.Add(midiNote);
        sustainedOnlyNotes.Remove(midiNote);
        latestVelocity[midiNote] = velocity;

        NotePressed?.Invoke(keyIndex, midiNote, velocity);
        MarkUiDirty();
    }

    // Called when a MIDI key is released. If sustain is on, note becomes sustained.
    // Otherwise fires NoteFullyEnded.
    private void HandleNoteOff(MidiNoteControl noteControl)
    {
        int midiNote = noteControl.noteNumber;
        if (!IsIn88KeyRange(midiNote)) return;

        int keyIndex = ToKeyIndex(midiNote);

        physicallyHeldNotes.Remove(midiNote);
        NoteReleased?.Invoke(keyIndex, midiNote);

        if (SustainOn)
        {
            sustainedOnlyNotes.Add(midiNote);
            NoteBecameSustained?.Invoke(keyIndex, midiNote);
        }
        else
        {
            sustainedOnlyNotes.Remove(midiNote);
            latestVelocity.Remove(midiNote);
            NoteFullyEnded?.Invoke(keyIndex, midiNote);
        }

        MarkUiDirty();
    }

    // Handles sustain pedal (CC#64). When released, all sustained-only notes end.
    private void HandleControlChange(MidiValueControl cc, float value)
    {
        if (cc.controlNumber != sustainCc) return;

        bool oldState = SustainOn;
        sustainValue = value;
        bool newState = SustainOn;

        if (oldState != newState)
            SustainChanged?.Invoke(newState, value);

        // Sustain released: all sustained-only notes truly end
        if (oldState && !newState)
        {
            List<int> notesToEnd = new();

            foreach (int midiNote in sustainedOnlyNotes)
            {
                if (!physicallyHeldNotes.Contains(midiNote))
                    notesToEnd.Add(midiNote);
            }

            for (int i = 0; i < notesToEnd.Count; i++)
            {
                int midiNote = notesToEnd[i];
                int keyIndex = ToKeyIndex(midiNote);

                sustainedOnlyNotes.Remove(midiNote);
                latestVelocity.Remove(midiNote);
                NoteFullyEnded?.Invoke(keyIndex, midiNote);
            }
        }

        MarkUiDirty();
    }

    public bool IsKeyPhysicallyHeld(int keyIndex)
    {
        int midiNote = keyIndex + minMidiNote;
        return physicallyHeldNotes.Contains(midiNote);
    }

    public bool IsKeySustainedOnly(int keyIndex)
    {
        int midiNote = keyIndex + minMidiNote;
        return sustainedOnlyNotes.Contains(midiNote);
    }

    public bool IsKeyAudiblyActive(int keyIndex)
    {
        int midiNote = keyIndex + minMidiNote;
        return physicallyHeldNotes.Contains(midiNote) || sustainedOnlyNotes.Contains(midiNote);
    }

    public float GetLatestVelocity(int keyIndex)
    {
        int midiNote = keyIndex + minMidiNote;
        return latestVelocity.TryGetValue(midiNote, out float v) ? v : 0f;
    }

    public int ToKeyIndex(int midiNote) => midiNote - minMidiNote;

    public int ToMidiNote(int keyIndex) => keyIndex + minMidiNote;

    private bool IsIn88KeyRange(int midiNote)
    {
        return midiNote >= minMidiNote && midiNote <= maxMidiNote;
    }

    private void MarkUiDirty()
    {
        if (outputText == null) return;

        if (refreshUiEveryFrame)
        {
            uiDirty = true;
            return;
        }

        RefreshOutput();
    }

    private void RefreshOutput()
    {
        if (outputText == null) return;

        uiDirty = false;
        sb.Clear();
        sb.Append("Sustain: ").Append(SustainOn ? "ON" : "OFF");
        sb.Append(" (").Append(Mathf.RoundToInt(sustainValue * 127f)).AppendLine("/127)");
        sb.AppendLine();
        sb.AppendLine("Active notes:");

        bool any = false;

        for (int midiNote = minMidiNote; midiNote <= maxMidiNote; midiNote++)
        {
            bool held = physicallyHeldNotes.Contains(midiNote);
            bool sustained = sustainedOnlyNotes.Contains(midiNote);

            if (!held && !sustained) continue;

            any = true;
            int keyIndex = ToKeyIndex(midiNote);
            float velocity = latestVelocity.TryGetValue(midiNote, out float v) ? v : 0f;

            sb.Append("Key ").Append(keyIndex)
              .Append(" (MIDI ").Append(midiNote).Append(")")
              .Append(" Vel ").Append(Mathf.RoundToInt(velocity * 127f))
              .Append(" State: ");

            if (held) sb.Append("HELD");
            else if (sustained) sb.Append("SUSTAINED");

            sb.AppendLine();
        }

        if (!any)
            sb.AppendLine("None");

        outputText.text = sb.ToString();
    }
}
