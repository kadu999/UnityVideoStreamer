#include "IUnityInterface.h"

#include <android/native_window.h>
#include <android/log.h>
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#define VS_CODEC_LOG_TAG "VideoStreamNativeCodec"
#define VS_LOGE(...) __android_log_print(ANDROID_LOG_ERROR, VS_CODEC_LOG_TAG, __VA_ARGS__)

namespace
{
    struct EncodedFrame
    {
        std::vector<uint8_t> data;
        bool isConfig = false;
        bool isKeyFrame = false;
        int64_t ptsUs = 0;
    };

    AMediaCodec* gCodec = nullptr;
    ANativeWindow* gInputWindow = nullptr;
    std::thread gCodecThread;
    std::mutex gCodecMutex;
    std::condition_variable gCodecCv;
    std::deque<EncodedFrame> gEncodedFrames;
    std::atomic<bool> gCodecRunning{false};

    void CodecLoop()
    {
        while (gCodecRunning.load())
        {
            AMediaCodecBufferInfo info{};
            ssize_t index = AMediaCodec_dequeueOutputBuffer(gCodec, &info, 10000);
            if (index < 0)
            {
                continue;
            }

            if (index == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                continue;
            }

            size_t bufferSize = 0;
            uint8_t* buffer = AMediaCodec_getOutputBuffer(gCodec, index, &bufferSize);
            if (buffer != nullptr && info.size > 0)
            {
                EncodedFrame frame;
                frame.data.assign(
                    buffer + info.offset,
                    buffer + info.offset + info.size);
                frame.isConfig = (info.flags & AMEDIACODEC_BUFFER_FLAG_CODEC_CONFIG) != 0;
                frame.isKeyFrame = (info.flags & 1) != 0;
                frame.ptsUs = info.presentationTimeUs;

                {
                    std::lock_guard<std::mutex> lock(gCodecMutex);
                    gEncodedFrames.push_back(std::move(frame));
                }
                gCodecCv.notify_one();
            }

            AMediaCodec_releaseOutputBuffer(gCodec, index, false);
        }
    }
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_CodecStart(
    int width,
    int height,
    int bitrate,
    int frameRate,
    int iFrameIntervalSeconds,
    const char* mime)
{
    if (gCodecRunning.load())
    {
        return 0;
    }

    AMediaFormat* format = AMediaFormat_new();
    AMediaFormat_setString(format, AMEDIAFORMAT_KEY_MIME, mime);
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_WIDTH, width);
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_HEIGHT, height);
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_BIT_RATE, bitrate);
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_FRAME_RATE, frameRate);
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_I_FRAME_INTERVAL, iFrameIntervalSeconds);

    AMediaCodec* codec = AMediaCodec_createEncoderByType(mime);
    if (codec == nullptr)
    {
        VS_LOGE("AMediaCodec_createEncoderByType failed");
        AMediaFormat_delete(format);
        return 0;
    }

    media_status_t status = AMediaCodec_configure(
        codec,
        format,
        nullptr,
        nullptr,
        AMEDIACODEC_CONFIGURE_FLAG_ENCODE);
    AMediaFormat_delete(format);
    if (status != AMEDIA_OK)
    {
        VS_LOGE("AMediaCodec_configure failed: %d", status);
        AMediaCodec_delete(codec);
        return 0;
    }

    ANativeWindow* inputWindow = nullptr;
    status = AMediaCodec_createInputSurface(codec, &inputWindow);
    if (status != AMEDIA_OK || inputWindow == nullptr)
    {
        VS_LOGE("AMediaCodec_createInputSurface failed: %d", status);
        AMediaCodec_delete(codec);
        return 0;
    }

    status = AMediaCodec_start(codec);
    if (status != AMEDIA_OK)
    {
        VS_LOGE("AMediaCodec_start failed: %d", status);
        AMediaCodec_delete(codec);
        ANativeWindow_release(inputWindow);
        return 0;
    }

    gCodec = codec;
    gInputWindow = inputWindow;
    gCodecRunning.store(true);
    gCodecThread = std::thread(CodecLoop);
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_CodecStop()
{
    if (!gCodecRunning.exchange(false))
    {
        return 1;
    }

    if (gCodecThread.joinable())
    {
        gCodecThread.join();
    }

    if (gCodec != nullptr)
    {
        AMediaCodec_stop(gCodec);
        AMediaCodec_delete(gCodec);
        gCodec = nullptr;
    }

    if (gInputWindow != nullptr)
    {
        ANativeWindow_release(gInputWindow);
        gInputWindow = nullptr;
    }

    {
        std::lock_guard<std::mutex> lock(gCodecMutex);
        gEncodedFrames.clear();
    }
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT void* VSMedia_CodecGetInputSurface()
{
    return gInputWindow;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_CodecDequeueFrame(
    uint8_t* buffer,
    int capacity,
    int* size,
    bool* isConfig,
    bool* isKeyFrame,
    int64_t* ptsUs)
{
    EncodedFrame frame;
    {
        std::lock_guard<std::mutex> lock(gCodecMutex);
        if (gEncodedFrames.empty())
        {
            return 0;
        }
        frame = std::move(gEncodedFrames.front());
        gEncodedFrames.pop_front();
    }

    if (buffer == nullptr || capacity < static_cast<int>(frame.data.size()))
    {
        return 0;
    }

    std::memcpy(buffer, frame.data.data(), frame.data.size());
    if (size != nullptr) *size = static_cast<int>(frame.data.size());
    if (isConfig != nullptr) *isConfig = frame.isConfig;
    if (isKeyFrame != nullptr) *isKeyFrame = frame.isKeyFrame;
    if (ptsUs != nullptr) *ptsUs = frame.ptsUs;
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT void VSMedia_CodecRequestKeyFrame()
{
    if (gCodec == nullptr)
    {
        return;
    }

    AMediaFormat* params = AMediaFormat_new();
    AMediaFormat_setInt32(params, "request-sync-frame", 0);
    AMediaCodec_setParameters(gCodec, params);
    AMediaFormat_delete(params);
}
