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
    constexpr int kBackendAuto = -1;
    constexpr int kBackendWasapiShared = 0;
    constexpr int kBackendWasapiExclusive = 1;
    constexpr int kBackendAsio = 2;
    constexpr float kVelocityFloor = 0.2f;

    struct EngineConfig
    {
        int sampleRate;
        int channelCount;
        int maxVoices;
        int backend;
        int requestedBufferFrames;
        int releaseFadeMs;
        char preferredOutputDeviceName[256];
    };

    struct SampleDescriptor
    {
        int rootMidiNote;
        int minVelocity;
        int maxVelocity;
        int channelCount;
        int sampleRate;
        int frameCount;
    };

    struct SampleLayer
    {
        int rootMidiNote = 60;
        int minVelocity = 1;
        int maxVelocity = 127;
        int channelCount = 2;
        int sampleRate = 48000;
        int frameCount = 0;
        std::vector<float> interleaved;
    };

    struct Voice
    {
        bool active = false;
        bool releasing = false;
        int targetMidiNote = -1;
        uint64_t startCounter = 0;
        const SampleLayer* sample = nullptr;
        double readPosition = 0.0;
        double step = 1.0;
        float gain = 1.0f;
        float releaseGain = 1.0f;
        float releaseStep = 0.0f;
    };

    class NativeEngine final : public juce::AudioIODeviceCallback
    {
    public:
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

        void Stop()
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            if (!started_)
                return;

            deviceManager_.removeAudioCallback(this);
            deviceManager_.closeAudioDevice();
            started_ = false;
        }

        void ClearSamples()
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            samples_.clear();
            for (auto& voice : voices_)
                voice = Voice{};
        }

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

        void SetSustain(bool sustainOn)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            sustainOn_ = sustainOn;
        }

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

        void audioDeviceIOCallbackWithContext(const float* const*, int, float* const* outputChannelData, int numOutputChannels, int numSamples, const juce::AudioIODeviceCallbackContext&) override
        {
            RenderBlock(outputChannelData, numOutputChannels, numSamples);
        }

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

        juce::AudioIODeviceType* FindDeviceType(juce::OwnedArray<juce::AudioIODeviceType>& deviceTypes, const juce::String& nameFragment) const
        {
            for (auto* type : deviceTypes)
            {
                if (type != nullptr && type->getTypeName().containsIgnoreCase(nameFragment))
                    return type;
            }

            return nullptr;
        }

        juce::String GetConfiguredDeviceName() const
        {
            return juce::String::fromUTF8(config_.preferredOutputDeviceName).trim();
        }

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

        static int VelocityDistance(const SampleLayer& sample, int velocity)
        {
            if (velocity < sample.minVelocity)
                return sample.minVelocity - velocity;
            if (velocity > sample.maxVelocity)
                return velocity - sample.maxVelocity;
            return 0;
        }

        Voice* AcquireVoice()
        {
            for (auto& voice : voices_)
            {
                if (!voice.active)
                    return &voice;
            }

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

extern "C"
{
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

    __declspec(dllexport) void np_destroy_engine(void* engine)
    {
        delete static_cast<NativeEngine*>(engine);
    }

    __declspec(dllexport) int np_start_engine(void* engine)
    {
        if (engine == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->Start();
    }

    __declspec(dllexport) void np_stop_engine(void* engine)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->Stop();
    }

    __declspec(dllexport) void np_clear_samples(void* engine)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->ClearSamples();
    }

    __declspec(dllexport) int np_register_sample(void* engine, const SampleDescriptor* descriptor, const float* interleavedData, int sampleCount)
    {
        if (engine == nullptr || descriptor == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->RegisterSample(*descriptor, interleavedData, sampleCount);
    }

    __declspec(dllexport) int np_note_on(void* engine, int midiNote, float velocityNormalized)
    {
        if (engine == nullptr)
            return -1;
        return static_cast<NativeEngine*>(engine)->NoteOn(midiNote, velocityNormalized);
    }

    __declspec(dllexport) void np_note_off(void* engine, int midiNote)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->NoteOff(midiNote);
    }

    __declspec(dllexport) void np_set_sustain(void* engine, int sustainOn)
    {
        if (engine == nullptr)
            return;
        static_cast<NativeEngine*>(engine)->SetSustain(sustainOn != 0);
    }

    __declspec(dllexport) int np_get_last_error(void* engine, char* buffer, int capacity)
    {
        if (engine == nullptr)
            return 0;
        return static_cast<NativeEngine*>(engine)->GetLastError(buffer, capacity);
    }
}
