#include <Windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>

#include <algorithm>
#include <atomic>
#include <climits>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
    constexpr int kBackendWasapiShared = 0;
    constexpr int kBackendWasapiExclusive = 1;
    constexpr float kVelocityFloor = 0.2f;
    struct EngineConfig
    {
        int sampleRate;
        int channelCount;
        int maxVoices;
        int backend;
        int requestedBufferFrames;
        int releaseFadeMs;
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

    class NativeEngine
    {
    public:
        explicit NativeEngine(const EngineConfig& config)
            : config_(config)
        {
            voices_.resize(std::max(1, config_.maxVoices));
            outputMix_.resize(4096 * std::max(1, config_.channelCount), 0.0f);
        }

        ~NativeEngine()
        {
            Stop();
        }

        int Start()
        {
            {
                std::lock_guard<std::recursive_mutex> lock(mutex_);
                if (started_)
                    return 0;
            }

            HRESULT hr = InitializeWasapi();
            if (FAILED(hr))
            {
                SetError("InitializeWasapi failed.", hr);
                CleanupWasapi();
                return -1;
            }

            stopRequested_ = false;

            try
            {
                renderThread_ = std::thread([this]() { RenderLoop(); });
            }
            catch (...)
            {
                SetError("Failed to create render thread.", E_FAIL);
                CleanupWasapi();
                return -1;
            }

            hr = audioClient_->Start();
            if (FAILED(hr))
            {
                stopRequested_ = true;
                if (renderThread_.joinable())
                    renderThread_.join();
                SetError("IAudioClient::Start failed.", hr);
                CleanupWasapi();
                return -1;
            }

            started_ = true;
            return 0;
        }

        void Stop()
        {
            {
                std::lock_guard<std::recursive_mutex> lock(mutex_);
                if (!audioClient_ && !renderThread_.joinable())
                    return;
                stopRequested_ = true;
            }

            if (bufferEvent_)
                SetEvent(bufferEvent_);

            if (audioClient_)
                audioClient_->Stop();

            if (renderThread_.joinable())
                renderThread_.join();

            CleanupWasapi();
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
                SetError("RegisterSample received invalid PCM data.", E_INVALIDARG);
                return -1;
            }

            if (sampleCount != descriptor.frameCount * descriptor.channelCount)
            {
                SetError("RegisterSample sample count does not match descriptor.", E_INVALIDARG);
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
            if (!sample)
            {
                SetError("No registered sample matched the requested note.", E_FAIL);
                return -1;
            }

            Voice* voice = AcquireVoice();
            if (!voice)
            {
                SetError("No voice available for note-on.", E_FAIL);
                return -1;
            }

            float pitch = std::pow(2.0f, static_cast<float>(midiNote - sample->rootMidiNote) / 12.0f);
            voice->active = true;
            voice->releasing = false;
            voice->targetMidiNote = midiNote;
            voice->startCounter = ++voiceCounter_;
            voice->sample = sample;
            voice->readPosition = 0.0;
            voice->step = static_cast<double>(sample->sampleRate) / static_cast<double>(config_.sampleRate) * pitch;
            voice->gain = kVelocityFloor + (1.0f - kVelocityFloor) * std::clamp(velocityNormalized, 0.0f, 1.0f);
            voice->releaseGain = 1.0f;
            voice->releaseStep = 0.0f;
            return 0;
        }

        void NoteOff(int midiNote)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            int fadeMs = std::clamp(config_.releaseFadeMs, 20, 4000);
            int releaseSamples = std::max(1, static_cast<int>((config_.sampleRate * fadeMs) / 1000.0f));
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
            int copyLength = static_cast<int>(std::min<std::size_t>(lastError_.size(), capacity - 1));
            std::memcpy(buffer, lastError_.data(), copyLength);
            buffer[copyLength] = '\0';
            return copyLength;
        }

    private:
        HRESULT InitializeWasapi()
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
                return hr;

            IMMDeviceEnumerator* enumerator = nullptr;
            IMMDevice* device = nullptr;

            hr = CoCreateInstance(
                __uuidof(MMDeviceEnumerator),
                nullptr,
                CLSCTX_ALL,
                __uuidof(IMMDeviceEnumerator),
                reinterpret_cast<void**>(&enumerator)
            );
            if (FAILED(hr))
                return hr;

            hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
            if (FAILED(hr))
            {
                enumerator->Release();
                return hr;
            }

            hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&audioClient_));
            device->Release();
            enumerator->Release();
            if (FAILED(hr))
                return hr;

            WAVEFORMATEXTENSIBLE format = {};
            format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
            format.Format.nChannels = static_cast<WORD>(std::clamp(config_.channelCount, 1, 2));
            format.Format.nSamplesPerSec = static_cast<DWORD>(std::max(1, config_.sampleRate));
            format.Format.wBitsPerSample = 32;
            format.Format.nBlockAlign = static_cast<WORD>(format.Format.nChannels * (format.Format.wBitsPerSample / 8));
            format.Format.nAvgBytesPerSec = format.Format.nSamplesPerSec * format.Format.nBlockAlign;
            format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
            format.Samples.wValidBitsPerSample = 32;
            format.dwChannelMask = format.Format.nChannels == 1 ? SPEAKER_FRONT_CENTER : (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT);
            format.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

            AUDCLNT_SHAREMODE shareMode = config_.backend == kBackendWasapiExclusive
                ? AUDCLNT_SHAREMODE_EXCLUSIVE
                : AUDCLNT_SHAREMODE_SHARED;

            REFERENCE_TIME requestedDuration = static_cast<REFERENCE_TIME>(
                (10000000.0 * std::max(32, config_.requestedBufferFrames)) / static_cast<double>(format.Format.nSamplesPerSec)
            );

            DWORD streamFlags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            hr = audioClient_->Initialize(
                shareMode,
                streamFlags,
                shareMode == AUDCLNT_SHAREMODE_SHARED ? requestedDuration : requestedDuration,
                shareMode == AUDCLNT_SHAREMODE_EXCLUSIVE ? requestedDuration : 0,
                reinterpret_cast<WAVEFORMATEX*>(&format),
                nullptr
            );

            if (FAILED(hr) && shareMode == AUDCLNT_SHAREMODE_EXCLUSIVE)
            {
                config_.backend = kBackendWasapiShared;
                shareMode = AUDCLNT_SHAREMODE_SHARED;
                hr = audioClient_->Initialize(
                    shareMode,
                    streamFlags,
                    requestedDuration,
                    0,
                    reinterpret_cast<WAVEFORMATEX*>(&format),
                    nullptr
                );
            }

            if (FAILED(hr))
                return hr;

            hr = audioClient_->GetBufferSize(&bufferFrameCount_);
            if (FAILED(hr))
                return hr;

            bufferEvent_ = CreateEvent(nullptr, FALSE, FALSE, nullptr);
            if (!bufferEvent_)
                return HRESULT_FROM_WIN32(::GetLastError());

            hr = audioClient_->SetEventHandle(bufferEvent_);
            if (FAILED(hr))
                return hr;

            hr = audioClient_->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(&renderClient_));
            if (FAILED(hr))
                return hr;

            BYTE* buffer = nullptr;
            hr = renderClient_->GetBuffer(bufferFrameCount_, &buffer);
            if (FAILED(hr))
                return hr;

            std::memset(buffer, 0, bufferFrameCount_ * format.Format.nBlockAlign);
            hr = renderClient_->ReleaseBuffer(bufferFrameCount_, 0);
            if (FAILED(hr))
                return hr;

            return S_OK;
        }

        void CleanupWasapi()
        {
            if (renderClient_)
            {
                renderClient_->Release();
                renderClient_ = nullptr;
            }

            if (audioClient_)
            {
                audioClient_->Release();
                audioClient_ = nullptr;
            }

            if (bufferEvent_)
            {
                CloseHandle(bufferEvent_);
                bufferEvent_ = nullptr;
            }

            bufferFrameCount_ = 0;
        }

        void RenderLoop()
        {
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);

            while (!stopRequested_)
            {
                DWORD waitResult = WaitForSingleObject(bufferEvent_, 1000);
                if (stopRequested_)
                    break;

                if (waitResult != WAIT_OBJECT_0)
                    continue;

                UINT32 padding = 0;
                HRESULT hr = audioClient_->GetCurrentPadding(&padding);
                if (FAILED(hr))
                {
                    SetError("GetCurrentPadding failed.", hr);
                    continue;
                }

                UINT32 framesToWrite = bufferFrameCount_ > padding ? bufferFrameCount_ - padding : 0;
                if (framesToWrite == 0)
                    continue;

                BYTE* rawBuffer = nullptr;
                hr = renderClient_->GetBuffer(framesToWrite, &rawBuffer);
                if (FAILED(hr))
                {
                    SetError("GetBuffer failed.", hr);
                    continue;
                }

                Render(reinterpret_cast<float*>(rawBuffer), static_cast<int>(framesToWrite));

                hr = renderClient_->ReleaseBuffer(framesToWrite, 0);
                if (FAILED(hr))
                    SetError("ReleaseBuffer failed.", hr);
            }

            CoUninitialize();
        }

        void Render(float* output, int frameCount)
        {
            const int outputChannels = std::max(1, config_.channelCount);
            const int sampleCount = frameCount * outputChannels;

            if (static_cast<int>(outputMix_.size()) < sampleCount)
                outputMix_.resize(sampleCount);

            std::fill(outputMix_.begin(), outputMix_.begin() + sampleCount, 0.0f);

            std::lock_guard<std::recursive_mutex> lock(mutex_);

            for (auto& voice : voices_)
            {
                if (!voice.active || !voice.sample)
                    continue;

                const SampleLayer& sample = *voice.sample;
                const int inputChannels = std::max(1, sample.channelCount);

                for (int frame = 0; frame < frameCount; ++frame)
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
                        float shapedReleaseGain = voice.releasing
                            ? voice.releaseGain * voice.releaseGain
                            : 1.0f;
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

            for (int i = 0; i < sampleCount; ++i)
                output[i] = std::clamp(outputMix_[i], -1.0f, 1.0f);
        }

        const SampleLayer* ChooseSample(int midiNote, float velocityNormalized) const
        {
            if (samples_.empty())
                return nullptr;

            int velocity = static_cast<int>(std::round(std::clamp(velocityNormalized, 0.0f, 1.0f) * 126.0f)) + 1;

            const SampleLayer* best = nullptr;
            int bestDistance = INT_MAX;
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
                        bestDistance = INT_MAX;
                    }

                    if (velocityMatch)
                        return &sample;

                    int velocityDistance = VelocityDistance(sample, velocity);
                    if (!best || velocityDistance < bestDistance)
                    {
                        best = &sample;
                        bestDistance = velocityDistance;
                    }
                }
                else if (!exactRootFound)
                {
                    if (!best || distance < bestDistance || (distance == bestDistance && velocityMatch))
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
            int bestVelocityDistance = INT_MAX;

            for (const auto& sample : samples_)
            {
                if (sample.rootMidiNote != bestRoot)
                    continue;

                int velocityDistance = VelocityDistance(sample, velocity);
                if (!bestLayer || velocityDistance < bestVelocityDistance)
                {
                    bestLayer = &sample;
                    bestVelocityDistance = velocityDistance;
                }
            }

            return bestLayer ? bestLayer : best;
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

        void SetError(const char* message, HRESULT hr = S_OK)
        {
            std::lock_guard<std::recursive_mutex> lock(mutex_);
            lastError_ = message;

            if (FAILED(hr))
            {
                char hex[16];
                std::snprintf(hex, sizeof(hex), " HRESULT=0x%08X", static_cast<unsigned int>(hr));
                lastError_ += hex;
            }
        }

        EngineConfig config_;
        mutable std::recursive_mutex mutex_;
        std::string lastError_;
        std::vector<SampleLayer> samples_;
        std::vector<Voice> voices_;
        std::vector<float> outputMix_;
        uint64_t voiceCounter_ = 0;
        bool sustainOn_ = false;
        bool started_ = false;

        IAudioClient* audioClient_ = nullptr;
        IAudioRenderClient* renderClient_ = nullptr;
        HANDLE bufferEvent_ = nullptr;
        UINT32 bufferFrameCount_ = 0;
        std::thread renderThread_;
        std::atomic<bool> stopRequested_ = false;
    };
}

extern "C"
{
    __declspec(dllexport) void* np_create_engine(const EngineConfig* config)
    {
        if (!config)
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
        if (!engine)
            return -1;
        return static_cast<NativeEngine*>(engine)->Start();
    }

    __declspec(dllexport) void np_stop_engine(void* engine)
    {
        if (!engine)
            return;
        static_cast<NativeEngine*>(engine)->Stop();
    }

    __declspec(dllexport) void np_clear_samples(void* engine)
    {
        if (!engine)
            return;
        static_cast<NativeEngine*>(engine)->ClearSamples();
    }

    __declspec(dllexport) int np_register_sample(
        void* engine,
        const SampleDescriptor* descriptor,
        const float* interleavedData,
        int sampleCount)
    {
        if (!engine || !descriptor)
            return -1;
        return static_cast<NativeEngine*>(engine)->RegisterSample(*descriptor, interleavedData, sampleCount);
    }

    __declspec(dllexport) int np_note_on(void* engine, int midiNote, float velocityNormalized)
    {
        if (!engine)
            return -1;
        return static_cast<NativeEngine*>(engine)->NoteOn(midiNote, velocityNormalized);
    }

    __declspec(dllexport) void np_note_off(void* engine, int midiNote)
    {
        if (!engine)
            return;
        static_cast<NativeEngine*>(engine)->NoteOff(midiNote);
    }

    __declspec(dllexport) void np_set_sustain(void* engine, int sustainOn)
    {
        if (!engine)
            return;
        static_cast<NativeEngine*>(engine)->SetSustain(sustainOn != 0);
    }

    __declspec(dllexport) int np_get_last_error(void* engine, char* buffer, int capacity)
    {
        if (!engine)
            return 0;
        return static_cast<NativeEngine*>(engine)->GetLastError(buffer, capacity);
    }
}
