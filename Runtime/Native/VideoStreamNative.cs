#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace VideoStream
{
    internal static class VideoStreamNative
    {
        const string Library = "unity-video-streamer-native";

        [DllImport(Library)]
        internal static extern IntPtr GetRenderEventFunc();

        [DllImport(Library)]
        internal static extern int GetRenderEventId();

        [DllImport(Library)]
        internal static extern int GetCameraUpdateEventId();

        [DllImport(Library)]
        internal static extern int VSMedia_CameraSetSurfaceTexture(IntPtr surfaceTexture);

        [DllImport(Library)]
        internal static extern int VSMedia_CameraGetExternalTexture();

        [DllImport(Library)]
        internal static extern void SetActive(int active);

        [DllImport(Library)]
        internal static extern void SetFrameInfo(IntPtr texture, int width, int height, int flipY);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpStart(int localPort);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpStop();

        [DllImport(Library)]
        internal static extern int VSMedia_UdpAddTarget(
            [MarshalAs(UnmanagedType.LPStr)] string ip,
            int port);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpSendFrame(
            int frameId,
            long ptsUs,
            [In] byte[] data,
            int size,
            [MarshalAs(UnmanagedType.I1)] bool isConfig,
            [MarshalAs(UnmanagedType.I1)] bool isKeyFrame,
            [MarshalAs(UnmanagedType.LPStr)] string mime,
            uint sequence);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpTakeIdrRequest();

        [DllImport(Library)]
        internal static extern int VSMedia_CodecStart(
            int width,
            int height,
            int bitrate,
            int frameRate,
            int iFrameIntervalSeconds,
            [MarshalAs(UnmanagedType.LPStr)] string mime);

        [DllImport(Library)]
        internal static extern int VSMedia_CodecStop();

        [DllImport(Library)]
        internal static extern IntPtr VSMedia_CodecGetInputSurface();

        [DllImport(Library)]
        internal static extern int VSMedia_CodecDequeueFrame(
            [Out] byte[] buffer,
            int capacity,
            out int size,
            [MarshalAs(UnmanagedType.I1)] out bool isConfig,
            [MarshalAs(UnmanagedType.I1)] out bool isKeyFrame,
            out long ptsUs);

        [DllImport(Library)]
        internal static extern void VSMedia_CodecRequestKeyFrame();

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderStart(
            [MarshalAs(UnmanagedType.LPStr)] string mime);

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderProcessPackets();

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderSetOutputSurface(IntPtr surface);

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderStop();

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderGetOutputWidth();

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderGetOutputHeight();
    }
}
#endif
