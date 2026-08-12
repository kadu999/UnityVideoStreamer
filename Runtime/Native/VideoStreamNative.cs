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
        internal static extern void SetActive(int active);

        [DllImport(Library)]
        internal static extern void SetFrameInfo(IntPtr texture, int width, int height, int flipY);
    }
}
#endif
