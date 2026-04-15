#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#include <sstream>

#include <juce_audio_basics/juce_audio_basics.h>
#include <juce_audio_devices/juce_audio_devices.h>
#include <juce_core/juce_core.h>
#include <juce_events/juce_events.h>

namespace
{
    // Audio backend type constants matching C# BackendKind enum
    constexpr int kBackendAuto = -1;
    constexpr int kBackendWasapiShared = 0;
    constexpr int kBackendWasapiExclusive = 1;
    constexpr int kBackendAsio = 2;
    // Minimum volume floor so quiet notes are still audible
    constexpr float kVelocityFloor = 0.2f;

    // Passed from C# via P/Invoke to configure the audio engine
    struct EngineConfig
    {
        int sampleRate;
        int channelCount;
        int maxVoices;
        int backend;              // BackendKind enum value
        int requestedBufferFrames;
        int releaseFadeMs;        // fade-out time when note is released
        char preferredOutputDeviceName[256];
    };

    // Metadata for a single audio sample uploaded from Unity
    struct SampleDescriptor
    {
        int rootMidiNote;   // which piano key this sample was recorded from
        int minVelocity;    // MIDI velocity range lower bound (1-127)
        int maxVelocity;    // MIDI velocity range upper bound (1-127)
        int channelCount;   // mono=1, stereo=2
        int sampleRate;     // sample rate of the audio data
        int frameCount;     // total audio frames in the sample
    };

    // Stored sample data with PCM buffer, one per velocity layer per key
    struct SampleLayer
    {
        int rootMidiNote = 60;
        int minVelocity = 1;
        int maxVelocity = 127;
        int channelCount = 2;
        int sampleRate = 48000;
        int frameCount = 0;
        std::vector<float> interleaved; // raw PCM data (interleaved channels)
    };

    // A single playing note in the voice pool
    struct Voice
    {
        bool active = false;          // currently producing audio
        bool releasing = false;       // fading out after note-off
        int targetMidiNote = -1;      // which MIDI note this voice plays
        uint64_t startCounter = 0;    // monotonic counter for voice-stealing priority
        const SampleLayer* sample = nullptr;
        double readPosition = 0.0;    // current playback position in frames
        double step = 1.0;            // playback speed (handles pitch shift + sample rate conversion)
        float gain = 1.0f;            // velocity-based volume
        float releaseGain = 1.0f;     // fades from 1 to 0 during release
        float releaseStep = 0.0f;     // how much releaseGain decreases per sample
    };

    // Main audio engine using JUCE for device management and audio output.
    // Implements JUCE's AudioIODeviceCallback to render audio in real time.
    // Manages a polyphonic voice pool, sample bank, and audio device lifecycle.
    class NativeEngine final : public juce::AudioIODeviceCallback
    {
    public:
        // Allocates the voice pool based on config. Does not open any audio device yet.
        explicit NativeEngine(const EngineConfig& config)
            : config_(config),
              juceInitialiser_(std::make_unique<juce::ScopedJuceInitialiser_GUI>())
        {
            voices_.resize(std::max(1, config_.maxVoices));
        }

        ~NativeEngine() override
        {
            Stop();
        }

        // Opens the audio device and starts the audio callback.
        // Tries the preferred backend/device first, falls back to defaults.
        // Returns 0 on success, -1 on failure (check GetLastError).
        int Start()
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            if (started_)
                return 0;

            juce::OwnedArray<juce::AudioIODeviceType> deviceTypes;
            deviceManager_.createAudioDeviceTypes(deviceTypes);

            juce::String preferredDeviceName;
            juce::AudioIODeviceType* chosenType = ChooseDeviceType(deviceTypes, preferredDeviceName);
            if (chosenType == nullptr)
            {
                SetError("No suitable JUCE audio device type was found. Available types: " + DescribeDeviceTypes(deviceTypes));
                return -1;
            }

            const juce::String chosenTypeName = chosenType->getTypeName();
            const int chosenTypeIndex = deviceTypes.indexOf(chosenType);
            if (chosenTypeIndex < 0)
            {
                SetError(("Chosen JUCE audio device type was not present in the enumerated list: " + chosenTypeName).toStdString());
                return -1;
            }

            deviceManager_.addAudioDeviceType(
                std::unique_ptr<juce::AudioIODeviceType>(deviceTypes.removeAndReturn(chosenTypeIndex))
            );
            deviceManager_.setCurrentAudioDeviceType(chosenTypeName, true);

            auto* currentType = deviceManager_.getCurrentDeviceTypeObject();
            if (currentType == nullptr)
            {
                SetError(
                    ("JUCE could not activate audio device type: " + chosenTypeName
                    + ". Available types: " + DescribeDeviceTypes(deviceTypes)).toStdString()
                );
                return -1;
            }

            currentType->scanForDevices();
            juce::StringArray devices = currentType->getDeviceNames(false);
            if (devices.isEmpty())
            {
                SetError(("No output devices were found for audio device type: " + chosenTypeName).toStdString());
                return -1;
            }

            juce::String preferredDevice = ChooseOutputDeviceName(devices, preferredDeviceName);
            juce::AudioDeviceManager::AudioDeviceSetup setup;
            setup.inputDeviceName = {};
            setup.outputDeviceName = preferredDevice;
            setup.useDefaultInputChannels = false;
            setup.useDefaultOutputChannels = false;
            setup.sampleRate = static_cast<double>(std::max(1, config_.sampleRate));
            setup.bufferSize = std::max(32, config_.requestedBufferFrames);
            setup.inputChannels.clear();
            setup.outputChannels.clear();
            setup.outputChannels.setRange(0, std::max(1, config_.channelCount), true);

            juce::String result = deviceManager_.initialise(
                0,
                std::max(1, config_.channelCount),
                nullptr,
                false,
                {},
                &setup
            );

            // First try the exact requested sample rate / buffer. If the device rejects that
            // combination, retry with JUCE-managed defaults so startup can still succeed.
            if (result.isNotEmpty())
            {
                juce::AudioDeviceManager::AudioDeviceSetup relaxedSetup = setup;
                relaxedSetup.sampleRate = 0.0;
                relaxedSetup.bufferSize = 0;

                result = deviceManager_.initialise(
                    0,
                    std::max(1, config_.channelCount),
                    nullptr,
                    true,
                    preferredDevice,
                    &relaxedSetup
                );
            }

            if (result.isNotEmpty())
            {
                SetError(
                    ("Failed to open output device type '" + chosenTypeName
                    + "' device '" + preferredDevice
                    + "': " + result).toStdString()
                );
                return -1;
            }

            auto* currentDevice = deviceManager_.getCurrentAudioDevice();
            if (currentDevice == nullptr)
            {
                SetError(
                    ("JUCE did not return an active audio device after setup. type='"
                    + chosenTypeName + "', requestedDevice='" + preferredDevice + "'").toStdString()
                );
                return -1;
            }

            actualSampleRate_ = currentDevice->getCurrentSampleRate() > 0.0
                ? currentDevice->getCurrentSampleRate()
                : static_cast<double>(config_.sampleRate);
            actualBufferSize_ = currentDevice->getCurrentBufferSizeSamples();

            outputMix_.resize(std::max(4096, actualBufferSize_) * std::max(1, config_.channelCount), 0.0f);

            deviceManager_.addAudioCallback(this);
            started_ = true;
            return 0;
        }

        // Stops audio output, removes callback, and closes the device.
        void Stop()
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            if (!started_)
                return;

            deviceManager_.removeAudioCallback(this);
            deviceManager_.closeAudioDevice();
            started_ = false;
        }

        // Removes all uploaded samples and resets all voices.
        void ClearSamples()
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            samples_.clear();
            for (auto& voice : voices_)
                voice = Voice{};
        }

        // Copies a PCM sample into the engine's sample bank.
        // Called once per velocity layer per key during initialization.
        int RegisterSample(const SampleDescriptor& descriptor, const float* interleaved, int sampleCount)
        {
            if (!interleaved || sampleCount <= 0 || descriptor.frameCount <= 0 || descriptor.channelCount <= 0)
            {
                SetError("RegisterSample received invalid PCM data.");
                return -1;
            }

            if (sampleCount != descriptor.frameCount * descriptor.channelCount)
            {
                SetError("RegisterSample sample count does not match descriptor.");
                return -1;
            }

            SampleLayer layer;
            layer.rootMidiNote = descriptor.rootMidiNote;
            layer.minVelocity = descriptor.minVelocity;
            layer.maxVelocity = descriptor.maxVelocity;
            layer.channelCount = descriptor.channelCount;
            layer.sampleRate = descriptor.sampleRate;
            layer.frameCount = descriptor.frameCount;
            layer.interleaved.assign(interleaved, interleaved + sampleCount);

            std::lock_guard<std::recursive_mutex> lock(mutex_);
            samples_.push_back(std::move(layer));
            return 0;
        }

        // Triggers a note. Finds the best matching sample, acquires a voice,
        // and sets up pitch shift + gain. Pitch = 2^(semitones/12) for resampling.
        int NoteOn(int midiNote, float velocityNormalized)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            const SampleLayer* sample = ChooseSample(midiNote, velocityNormalized);
            if (sample == nullptr)
            {
                SetError("No registered sample matched the requested note.");
                return -1;
            }

            Voice* voice = AcquireVoice();
            if (voice == nullptr)
            {
                SetError("No voice available for note-on.");
                return -1;
            }

            double deviceRate = actualSampleRate_ > 0.0 ? actualSampleRate_ : static_cast<double>(config_.sampleRate);
            // Pitch shift: if sample root differs from target note, resample by semitone ratio
            float pitch = std::pow(2.0f, static_cast<float>(midiNote - sample->rootMidiNote) / 12.0f);

            voice->active = true;
            voice->releasing = false;
            voice->targetMidiNote = midiNote;
            voice->startCounter = ++voiceCounter_;
            voice->sample = sample;
            voice->readPosition = 0.0;
            voice->step = static_cast<double>(sample->sampleRate) / deviceRate * pitch;
            voice->gain = kVelocityFloor + (1.0f - kVelocityFloor) * std::clamp(velocityNormalized, 0.0f, 1.0f);
            voice->releaseGain = 1.0f;
            voice->releaseStep = 0.0f;
            return 0;
        }

        // Begins fade-out for all voices playing this note.
        // Fade duration is set by config.releaseFadeMs (clamped 20-4000ms).
        void NoteOff(int midiNote)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            int fadeMs = std::clamp(config_.releaseFadeMs, 20, 4000);
            double deviceRate = actualSampleRate_ > 0.0 ? actualSampleRate_ : static_cast<double>(config_.sampleRate);
            int releaseSamples = std::max(1, static_cast<int>((deviceRate * fadeMs) / 1000.0));
            float releaseStep = 1.0f / static_cast<float>(releaseSamples);

            for (auto& voice : voices_)
            {
                if (!voice.active || voice.targetMidiNote != midiNote)
                    continue;

                voice.releasing = true;
                voice.releaseStep = releaseStep;
            }
        }

        // Sets sustain pedal state. When on, note-off is deferred.
        void SetSustain(bool sustainOn)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            sustainOn_ = sustainOn;
        }

        // Copies the last error message into the provided buffer. Returns bytes written.
        int GetLastError(char* buffer, int capacity)
        {
            if (!buffer || capacity <= 0)
                return 0;

            std::lock_guard<std::recursive_mutex> lock(mutex_);
            int copyLength = static_cast<int>(std::min<std::size_t>(lastError_.size(), static_cast<std::size_t>(capacity - 1)));
            std::memcpy(buffer, lastError_.data(), copyLength);
            buffer[copyLength] = '\0';
            return copyLength;
        }

        // JUCE audio callback, called by the audio thread to fill output buffers.
        void audioDeviceIOCallbackWithContext(const float* const*, int, float* const* outputChannelData, int numOutputChannels, int numSamples, const juce::AudioIODeviceCallbackContext&) override
        {
            RenderBlock(outputChannelData, numOutputChannels, numSamples);
        }

        // Called when device starts. Captures actual sample rate and buffer size.
        void audioDeviceAboutToStart(juce::AudioIODevice* device) override
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            actualSampleRate_ = device != nullptr ? device->getCurrentSampleRate() : static_cast<double>(config_.sampleRate);
            actualBufferSize_ = device != nullptr ? device->getCurrentBufferSizeSamples() : std::max(32, config_.requestedBufferFrames);
            outputMix_.resize(std::max(4096, actualBufferSize_) * std::max(1, config_.channelCount), 0.0f);
        }

        void audioDeviceStopped() override
        {
        }

    private:
        // Selects the best audio device type based on config.backend.
        // Auto mode: tries ASIO first, then Low Latency, Exclusive, Windows Audio.
        juce::AudioIODeviceType* ChooseDeviceType(juce::OwnedArray<juce::AudioIODeviceType>& deviceTypes, juce::String& preferredDeviceName) const
        {
            if (config_.backend == kBackendAuto)
            {
                if (auto* asioType = FindDeviceType(deviceTypes, "ASIO"))
                {
                    asioType->scanForDevices();
                    auto devices = asioType->getDeviceNames(false);
                    auto asioDevice = ChooseOutputDeviceName(devices, GetConfiguredDeviceName());
                    if (asioDevice.isNotEmpty())
                    {
                        preferredDeviceName = asioDevice;
                        return asioType;
                    }
                }

                if (auto* lowLatencyType = FindDeviceType(deviceTypes, "Low Latency"))
                    return lowLatencyType;

                if (auto* exclusiveType = FindDeviceType(deviceTypes, "Exclusive"))
                    return exclusiveType;

                if (auto* windowsAudioType = FindDeviceType(deviceTypes, "Windows Audio"))
                    return windowsAudioType;
            }

            const juce::String preferredType = config_.backend == kBackendAsio ? "ASIO" : juce::String();

            if (preferredType.isNotEmpty())
            {
                if (auto* type = FindDeviceType(deviceTypes, preferredType))
                    return type;
            }

            const char* fallbackNames[] = { "Windows Audio", "WASAPI", "DirectSound" };
            for (const auto* fallbackName : fallbackNames)
            {
                if (auto* type = FindDeviceType(deviceTypes, fallbackName))
                    return type;
            }

            return deviceTypes.isEmpty() ? nullptr : deviceTypes[0];
        }

        // Finds a device type whose name contains the given fragment (case insensitive).
        juce::AudioIODeviceType* FindDeviceType(juce::OwnedArray<juce::AudioIODeviceType>& deviceTypes, const juce::String& nameFragment) const
        {
            for (auto* type : deviceTypes)
            {
                if (type != nullptr && type->getTypeName().containsIgnoreCase(nameFragment))
                    return type;
            }

            return nullptr;
        }

        // Returns the preferred device name from config, trimmed.
        juce::String GetConfiguredDeviceName() const
        {
            return juce::String::fromUTF8(config_.preferredOutputDeviceName).trim();
        }

        // Builds a comma-separated list of device type names for error messages.
        std::string DescribeDeviceTypes(const juce::OwnedArray<juce::AudioIODeviceType>& deviceTypes) const
        {
            if (deviceTypes.isEmpty())
                return "<none>";

            std::ostringstream stream;

            for (int i = 0; i < deviceTypes.size(); ++i)
            {
                auto* type = deviceTypes[i];
                if (i > 0)
                    stream << ", ";

                stream << "'" << (type != nullptr ? type->getTypeName().toStdString() : std::string("<null>")) << "'";
            }

            return stream.str();
        }

        // Picks an output device by name. Tries exact match, then substring match,
        // then ASIO-specific heuristics (USB, Focusrite), then first available.
        juce::String ChooseOutputDeviceName(const juce::StringArray& devices, const juce::String& overrideName = {}) const
        {
            const juce::String configuredName = overrideName.isNotEmpty() ? overrideName : GetConfiguredDeviceName();
            if (configuredName.isNotEmpty())
            {
                for (const auto& device : devices)
                {
                    if (device.equalsIgnoreCase(configuredName))
                        return device;
                }

                for (const auto& device : devices)
                {
                    if (device.containsIgnoreCase(configuredName))
                        return device;
                }
            }

            if (config_.backend == kBackendAsio)
            {
                for (const auto& device : devices)
                {
                    if (device.containsIgnoreCase("USB"))
                        return device;
                }

                for (const auto& device : devices)
                {
                    if (device.containsIgnoreCase("Focusrite"))
                        return device;
                }
            }

            return devices[0];
        }

        // Mixes all active voices into the output buffer.
        // For each voice: interpolates between sample frames, applies gain and release fade,
        // then deinterleaves into per-channel output arrays. Output is clamped to [-1, 1].
        void RenderBlock(float* const* outputChannelData, int numOutputChannels, int numSamples)
        {
            const int outputChannels = std::max(1, numOutputChannels);
            const int sampleCount = numSamples * outputChannels;

            {
                std::lock_guard<std::recursive_mutex> lock(mutex_);

                if (static_cast<int>(outputMix_.size()) < sampleCount)
                    outputMix_.resize(sampleCount);

                std::fill(outputMix_.begin(), outputMix_.begin() + sampleCount, 0.0f);

                for (auto& voice : voices_)
                {
                    if (!voice.active || voice.sample == nullptr)
                        continue;

                    const SampleLayer& sample = *voice.sample;
                    const int inputChannels = std::max(1, sample.channelCount);

                    for (int frame = 0; frame < numSamples; ++frame)
                    {
                        int baseIndex = static_cast<int>(voice.readPosition);
                        int nextIndex = baseIndex + 1;

                        if (nextIndex >= sample.frameCount)
                        {
                            voice.active = false;
                            break;
                        }

                        float frac = static_cast<float>(voice.readPosition - static_cast<double>(baseIndex));

                        // We mix into a single interleaved scratch buffer first, then fan back out
                        // into JUCE's per-channel outputs at the end of the block.
                        for (int channel = 0; channel < outputChannels; ++channel)
                        {
                            int sampleChannel = inputChannels == 1 ? 0 : std::min(channel, inputChannels - 1);
                            float a = sample.interleaved[baseIndex * inputChannels + sampleChannel];
                            float b = sample.interleaved[nextIndex * inputChannels + sampleChannel];
                            float interpolated = a + (b - a) * frac;
                            float shapedReleaseGain = voice.releasing ? voice.releaseGain * voice.releaseGain : 1.0f;
                            float gain = voice.gain * shapedReleaseGain;
                            outputMix_[frame * outputChannels + channel] += interpolated * gain;
                        }

                        voice.readPosition += voice.step;

                        if (voice.releasing)
                        {
                            voice.releaseGain -= voice.releaseStep;
                            if (voice.releaseGain <= 0.0f)
                            {
                                voice.active = false;
                                break;
                            }
                        }

                        if (voice.readPosition >= static_cast<double>(sample.frameCount - 1))
                        {
                            voice.active = false;
                            break;
                        }
                    }
                }

                for (int channel = 0; channel < outputChannels; ++channel)
                {
                    float* output = outputChannelData[channel];
                    if (output == nullptr)
                        continue;

                    for (int frame = 0; frame < numSamples; ++frame)
                        output[frame] = std::clamp(outputMix_[frame * outputChannels + channel], -1.0f, 1.0f);
                }

                for (int channel = outputChannels; channel < numOutputChannels; ++channel)
                {
                    float* output = outputChannelData[channel];
                    if (output != nullptr)
                        juce::FloatVectorOperations::clear(output, numSamples);
                }
            }
        }

        // Finds the best sample for a given note and velocity.
        // Priority: exact root + exact velocity > exact root + nearest velocity > nearest root.
        const SampleLayer* ChooseSample(int midiNote, float velocityNormalized) const
        {
            if (samples_.empty())
                return nullptr;

            int velocity = static_cast<int>(std::round(std::clamp(velocityNormalized, 0.0f, 1.0f) * 126.0f)) + 1;

            const SampleLayer* best = nullptr;
            int bestDistance = std::numeric_limits<int>::max();
            bool exactRootFound = false;

            for (const auto& sample : samples_)
            {
                int distance = std::abs(sample.rootMidiNote - midiNote);
                bool exactRoot = sample.rootMidiNote == midiNote;
                bool velocityMatch = velocity >= sample.minVelocity && velocity <= sample.maxVelocity;

                if (exactRoot)
                {
                    if (!exactRootFound)
                    {
                        exactRootFound = true;
                        best = nullptr;
                        bestDistance = std::numeric_limits<int>::max();
                    }

                    if (velocityMatch)
                        return &sample;

                    int velocityDistance = VelocityDistance(sample, velocity);
                    if (best == nullptr || velocityDistance < bestDistance)
                    {
                        best = &sample;
                        bestDistance = velocityDistance;
                    }
                }
                else if (!exactRootFound)
                {
                    // Before we find an exact root note, prioritize pitch proximity. Velocity only
                    // acts as a tie-breaker because wrong pitch is much more obvious than a layer mismatch.
                    if (best == nullptr || distance < bestDistance || (distance == bestDistance && velocityMatch))
                    {
                        best = &sample;
                        bestDistance = distance;
                    }
                }
            }

            if (exactRootFound || best == nullptr)
                return best;

            const int bestRoot = best->rootMidiNote;
            const SampleLayer* bestLayer = nullptr;
            int bestVelocityDistance = std::numeric_limits<int>::max();

            for (const auto& sample : samples_)
            {
                if (sample.rootMidiNote != bestRoot)
                    continue;

                int velocityDistance = VelocityDistance(sample, velocity);
                if (bestLayer == nullptr || velocityDistance < bestVelocityDistance)
                {
                    bestLayer = &sample;
                    bestVelocityDistance = velocityDistance;
                }
            }

            return bestLayer != nullptr ? bestLayer : best;
        }

        // Returns how far the velocity is from the sample's velocity range. 0 = inside range.
        static int VelocityDistance(const SampleLayer& sample, int velocity)
        {
            if (velocity < sample.minVelocity)
                return sample.minVelocity - velocity;
            if (velocity > sample.maxVelocity)
                return velocity - sample.maxVelocity;
            return 0;
        }

        // Gets a free voice, or steals the oldest active voice if none available.
        Voice* AcquireVoice()
        {
            for (auto& voice : voices_)
            {
                if (!voice.active)
                    return &voice;
            }

            // Oldest-voice stealing is simple but predictable, which is good enough for piano-style
            // material where voices naturally decay and overlaps are short-lived.
            auto it = std::min_element(
                voices_.begin(),
                voices_.end(),
                [](const Voice& a, const Voice& b) { return a.startCounter < b.startCounter; }
            );

            return it != voices_.end() ? &(*it) : nullptr;
        }

        void SetError(std::string message)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            lastError_ = std::move(message);
        }

        EngineConfig config_;
        std::unique_ptr<juce::ScopedJuceInitialiser_GUI> juceInitialiser_;
        juce::AudioDeviceManager deviceManager_;

        mutable std::recursive_mutex mutex_;
        std::string lastError_;
        std::vector<SampleLayer> samples_;
        std::vector<Voice> voices_;
        std::vector<float> outputMix_;
        uint64_t voiceCounter_ = 0;
        bool sustainOn_ = false;
        bool started_ = false;
        double actualSampleRate_ = 0.0;
        int actualBufferSize_ = 0;
    };
}

// DLL exports called from C# via P/Invoke (NativeAudioInterop.cs).
// All functions take an opaque engine pointer returned by np_create_engine.
extern "C"
{
    // Creates a new engine instance. Returns null on failure.
    __declspec(dllexport) void* np_create_engine(const EngineConfig* config)
    {
        if (config == nullptr)
            return nullptr;

        try
        {
            return new NativeEngine(*config);
        }
        catch (...)
        {
            return nullptr;
        }
    }

    // Destroys the engine and frees all resources.
    __declspec(dllexport) void np_destroy_engine(void* engine)
    {
        delete static_cast<NativeEngine*>(engine);
    }

    // Opens the audio device and starts playback. Returns 0 on success.
    __declspec(dllexport) int np_start_engine(void* engine)
    {
        if (engine == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->Start();
    }

    // Stops audio output and closes the device.
    __declspec(dllexport) void np_stop_engine(void* engine)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->Stop();
    }

    // Removes all registered samples from the engine.
    __declspec(dllexport) void np_clear_samples(void* engine)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->ClearSamples();
    }

    // Uploads one sample (one velocity layer for one key) to the engine.
    __declspec(dllexport) int np_register_sample(void* engine, const SampleDescriptor* descriptor, const float* interleavedData, int sampleCount)
    {
        if (engine == nullptr || descriptor == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->RegisterSample(*descriptor, interleavedData, sampleCount);
    }

    // Triggers a note with the given velocity (0.0 to 1.0).
    __declspec(dllexport) int np_note_on(void* engine, int midiNote, float velocityNormalized)
    {
        if (engine == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->NoteOn(midiNote, velocityNormalized);
    }

    // Releases a note, starting the fade-out envelope.
    __declspec(dllexport) void np_note_off(void* engine, int midiNote)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->NoteOff(midiNote);
    }

    // Sets sustain pedal state (0 = off, nonzero = on).
    __declspec(dllexport) void np_set_sustain(void* engine, int sustainOn)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->SetSustain(sustainOn != 0);
    }

    // Retrieves the last error message string for debugging.
    __declspec(dllexport) int np_get_last_error(void* engine, char* buffer, int capacity)
    {
        if (engine == nullptr)
            return 0;
        return static_cast<NativeEngine*>(engine)->GetLastError(buffer, capacity);
    }
}

