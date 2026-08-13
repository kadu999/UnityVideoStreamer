#include "IUnityInterface.h"

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

namespace
{
    constexpr int kUdpFragmentHeaderSize = 10;
    constexpr int kUdpMaxPayload = 1400;
    constexpr int kFrameHeaderSize = 18;

    constexpr uint16_t kFlagIdr = 0x0001;
    constexpr uint16_t kFlagConfig = 0x0002;
    constexpr uint16_t kFlagCodecAvc = 0x0010;
    constexpr uint16_t kFlagCodecHevc = 0x0020;

    std::mutex gUdpMutex;
    std::vector<sockaddr_in> gUdpTargets;
    int gUdpSocket = -1;
    std::atomic<bool> gUdpRunning{false};

    void WriteU16(uint8_t* dst, uint16_t value)
    {
        dst[0] = static_cast<uint8_t>(value >> 8);
        dst[1] = static_cast<uint8_t>(value);
    }

    void WriteU32(uint8_t* dst, uint32_t value)
    {
        dst[0] = static_cast<uint8_t>(value >> 24);
        dst[1] = static_cast<uint8_t>(value >> 16);
        dst[2] = static_cast<uint8_t>(value >> 8);
        dst[3] = static_cast<uint8_t>(value);
    }

    void WriteU64(uint8_t* dst, uint64_t value)
    {
        WriteU32(dst, static_cast<uint32_t>(value >> 32));
        WriteU32(dst + 4, static_cast<uint32_t>(value));
    }

    std::vector<uint8_t> BuildFramePacket(
        int frameId,
        int64_t ptsUs,
        const uint8_t* data,
        int size,
        bool isConfig,
        bool isKeyFrame,
        const char* mime)
    {
        uint16_t flags = 0;
        if (isConfig) flags |= kFlagConfig;
        if (isKeyFrame) flags |= kFlagIdr;
        flags |= std::strstr(mime, "hevc") != nullptr ? kFlagCodecHevc : kFlagCodecAvc;

        std::vector<uint8_t> packet(static_cast<size_t>(kFrameHeaderSize + size));
        WriteU32(packet.data(), static_cast<uint32_t>(frameId));
        WriteU64(packet.data() + 4, static_cast<uint64_t>(ptsUs));
        WriteU32(packet.data() + 12, static_cast<uint32_t>(size));
        WriteU16(packet.data() + 16, flags);
        if (size > 0)
        {
            std::memcpy(packet.data() + kFrameHeaderSize, data, static_cast<size_t>(size));
        }
        return packet;
    }

    std::vector<std::vector<uint8_t>> Fragment(
        const std::vector<uint8_t>& packet,
        uint32_t sequence,
        bool isIdr)
    {
        std::vector<std::vector<uint8_t>> fragments;
        uint16_t flags = isIdr ? kFlagIdr : 0;

        if (static_cast<int>(packet.size()) <= kUdpMaxPayload)
        {
            std::vector<uint8_t> datagram(kUdpFragmentHeaderSize + packet.size());
            WriteU16(datagram.data(), flags);
            WriteU16(datagram.data() + 2, 0);
            WriteU16(datagram.data() + 4, 1);
            WriteU32(datagram.data() + 6, sequence);
            std::memcpy(datagram.data() + kUdpFragmentHeaderSize, packet.data(), packet.size());
            fragments.push_back(std::move(datagram));
            return fragments;
        }

        int count = (static_cast<int>(packet.size()) + kUdpMaxPayload - 1) / kUdpMaxPayload;
        for (int index = 0; index < count; ++index)
        {
            int start = index * kUdpMaxPayload;
            int end = std::min(start + kUdpMaxPayload, static_cast<int>(packet.size()));
            int payloadSize = end - start;
            std::vector<uint8_t> datagram(kUdpFragmentHeaderSize + payloadSize);
            WriteU16(datagram.data(), flags);
            WriteU16(datagram.data() + 2, static_cast<uint16_t>(index));
            WriteU16(datagram.data() + 4, static_cast<uint16_t>(count));
            WriteU32(datagram.data() + 6, sequence);
            std::memcpy(datagram.data() + kUdpFragmentHeaderSize, packet.data() + start, payloadSize);
            fragments.push_back(std::move(datagram));
        }
        return fragments;
    }
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_UdpStart(int localPort)
{
    std::lock_guard<std::mutex> lock(gUdpMutex);
    if (gUdpSocket >= 0)
    {
        return 1;
    }

    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0)
    {
        return 0;
    }

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(static_cast<uint16_t>(localPort));
    if (bind(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
    {
        close(fd);
        return 0;
    }

    int sendBuffer = 1024 * 1024;
    setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &sendBuffer, sizeof(sendBuffer));

    gUdpSocket = fd;
    gUdpRunning.store(true);
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_UdpStop()
{
    std::lock_guard<std::mutex> lock(gUdpMutex);
    gUdpRunning.store(false);
    if (gUdpSocket >= 0)
    {
        close(gUdpSocket);
        gUdpSocket = -1;
    }
    gUdpTargets.clear();
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_UdpAddTarget(const char* ip, int port)
{
    if (ip == nullptr || port <= 0 || port > 65535)
    {
        return 0;
    }

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<uint16_t>(port));
    if (inet_pton(AF_INET, ip, &addr.sin_addr) != 1)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(gUdpMutex);
    gUdpTargets.push_back(addr);
    return 1;
}

extern "C" UNITY_INTERFACE_EXPORT int VSMedia_UdpSendFrame(
    int frameId,
    int64_t ptsUs,
    const uint8_t* data,
    int size,
    bool isConfig,
    bool isKeyFrame,
    const char* mime,
    uint32_t sequence)
{
    if (data == nullptr || size <= 0 || mime == nullptr)
    {
        return 0;
    }

    auto packet = BuildFramePacket(frameId, ptsUs, data, size, isConfig, isKeyFrame, mime);
    auto fragments = Fragment(packet, sequence, isKeyFrame || isConfig);

    std::lock_guard<std::mutex> lock(gUdpMutex);
    if (gUdpSocket < 0 || gUdpTargets.empty())
    {
        return 0;
    }

    int sent = 0;
    for (const auto& datagram : fragments)
    {
        for (const auto& target : gUdpTargets)
        {
            ssize_t result = sendto(
                gUdpSocket,
                datagram.data(),
                datagram.size(),
                0,
                reinterpret_cast<const sockaddr*>(&target),
                sizeof(target));
            if (result == static_cast<ssize_t>(datagram.size()))
            {
                ++sent;
            }
        }
    }
    return sent;
}
