#include "IUnityInterface.h"

#include <android/log.h>
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#define VS_DECODER_LOG_TAG "VideoStreamNativeDecoder"
#define VS_DLOGE(...) __android_log_print(ANDROID_LOG_ERROR, VS_DECODER_LOG_TAG, __VA_ARGS__)

namespace
{
    struct DecodedFrame
    {
        std::vector<uint8_t> data;
        int width = 0;
        int height = 0;
        int64_t ptsUs = 0;
    };

    AMediaCodec* gDecoder = nullptr;
    std::thread gDecoderThread;
    std::mutex gDecoderMutex;
    std::deque<DecodedFrame> gDecodedFrames;
    std::atomic<bool> gDecoderRunning{false};
    std::atomic<int> gDecodedWidth{0};
    std::atomic<int> gDecodedHeight{0};

    void DecoderLoop()
    {
        while (gDecoderRunning.load())
        {
            AMediaCodecBufferInfo info{};
            ssize_t index = AMediaCodec_dequeueOutputBuffer(gDecoder, &info, 10000);
            if (index < 0)
            {
                continue;
            }

            if (index == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                AMediaFormat* format = AMediaCodec_getOutputFormat(gDecoder);
                if (format != nullptr)
                {
                    int32_t width = 0;
                    int32_t height = 0;
                    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_WIDTH, &width);
                    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_HEIGHT, &height);
                    gDecodedWidth.store(width);
                    gDecodedHeight.store(height);
                    AMediaFormat_delete(format);
                }
                continue;
            }

            size_t bufferSize = 0;
            uint8_t* buffer = AMediaCodec_getOutputBuffer(gDecoder, index, &bufferSize);
            if (buffer != nullptr && info.size > 0)
            {
                DecodedFrame frame;
                frame.data.assign(buffer + info.offset, buffer + info.offset + info.size);
                frame.width = gDecodedWidth.load();
                frame.height = gDecodedHeight.load();
                frame.ptsUs = info.presentationTimeUs;

                {
                    std::lock_guard<std::mutex> lock(gDecoderMutex);
                    gDecodedFrames.push_back(std::move(frame));
                }
            }

            AMediaCodec_releaseOutputBuffer(gDecoder, index, false);
        }
    }
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_DecoderStart(const char* mime)
{
    if (gDecoderRunning.load())
    {
        return 0;
    }

    AMediaCodec* decoder = AMediaCodec_createDecoderByType(mime);
    if (decoder == nullptr)
    {
        VS_DLOGE("AMediaCodec_createDecoderByType failed");
        return 0;
    }

    AMediaFormat* format = AMediaFormat_new();
    AMediaFormat_setString(format, AMEDIAFORMAT_KEY_MIME, mime);
    media_status_t status = AMediaCodec_configure(decoder, format, nullptr, nullptr, 0);
    AMediaFormat_delete(format);
    if (status != AMEDIA_OK)
    {
        VS_DLOGE("decoder configure failed: %d", status);
        AMediaCodec_delete(decoder);
        return 0;
    }

    status = AMediaCodec_start(decoder);
    if (status != AMEDIA_OK)
    {
        VS_DLOGE("decoder start failed: %d", status);
        AMediaCodec_delete(decoder);
        return 0;
    }

    gDecoder = decoder;
    gDecoderRunning.store(true);
    gDecoderThread = std::thread(DecoderLoop);
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_DecoderFeed(
    const uint8_t* data,
    int size,
    int64_t ptsUs)
{
    if (gDecoder == nullptr || data == nullptr || size <= 0)
    {
        return 0;
    }

    ssize_t index = AMediaCodec_dequeueInputBuffer(gDecoder, 10000);
    if (index < 0)
    {
        return 0;
    }

    size_t bufferSize = 0;
    uint8_t* buffer = AMediaCodec_getInputBuffer(gDecoder, index, &bufferSize);
    if (buffer == nullptr || bufferSize < static_cast<size_t>(size))
    {
        return 0;
    }

    std::memcpy(buffer, data, static_cast<size_t>(size));
    media_status_t status = AMediaCodec_queueInputBuffer(
        gDecoder,
        index,
        0,
        static_cast<size_t>(size),
        static_cast<uint64_t>(ptsUs),
        0);
    return status == AMEDIA_OK ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_DecoderDequeueFrame(
    uint8_t* buffer,
    int capacity,
    int* size,
    int* width,
    int* height,
    int64_t* ptsUs)
{
    DecodedFrame frame;
    {
        std::lock_guard<std::mutex> lock(gDecoderMutex);
        if (gDecodedFrames.empty())
        {
            return 0;
        }
        frame = std::move(gDecodedFrames.front());
        gDecodedFrames.pop_front();
    }

    if (buffer == nullptr || capacity < static_cast<int>(frame.data.size()))
    {
        return 0;
    }

    std::memcpy(buffer, frame.data.data(), frame.data.size());
    if (size != nullptr) *size = static_cast<int>(frame.data.size());
    if (width != nullptr) *width = frame.width;
    if (height != nullptr) *height = frame.height;
    if (ptsUs != nullptr) *ptsUs = frame.ptsUs;
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT void VSMedia_DecoderConvertNv12ToRgba(
    const uint8_t* yuv,
    int width,
    int height,
    uint8_t* rgba)
{
    if (yuv == nullptr || rgba == nullptr || width <= 0 || height <= 0)
    {
        return;
    }

    int frameSize = width * height;
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            int yIndex = y * width + x;
            int uvIndex = frameSize + (y / 2) * width + (x & ~1);

            int yy = static_cast<int>(yuv[yIndex]) - 16;
            int uu = static_cast<int>(yuv[uvIndex]) - 128;
            int vv = static_cast<int>(yuv[uvIndex + 1]) - 128;

            yy = yy < 0 ? 0 : yy;
            int r = (298 * yy + 409 * vv + 128) >> 8;
            int g = (298 * yy - 100 * uu - 208 * vv + 128) >> 8;
            int b = (298 * yy + 516 * uu + 128) >> 8;

            r = r < 0 ? 0 : (r > 255 ? 255 : r);
            g = g < 0 ? 0 : (g > 255 ? 255 : g);
            b = b < 0 ? 0 : (b > 255 ? 255 : b);

            int rgbaIndex = yIndex * 4;
            rgba[rgbaIndex] = static_cast<uint8_t>(r);
            rgba[rgbaIndex + 1] = static_cast<uint8_t>(g);
            rgba[rgbaIndex + 2] = static_cast<uint8_t>(b);
            rgba[rgbaIndex + 3] = 255;
        }
    }
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_DecoderStop()
{
    if (!gDecoderRunning.exchange(false))
    {
        return 1;
    }

    if (gDecoderThread.joinable())
    {
        gDecoderThread.join();
    }

    if (gDecoder != nullptr)
    {
        AMediaCodec_stop(gDecoder);
        AMediaCodec_delete(gDecoder);
        gDecoder = nullptr;
    }

    {
        std::lock_guard<std::mutex> lock(gDecoderMutex);
        gDecodedFrames.clear();
    }
    return 1;
}
