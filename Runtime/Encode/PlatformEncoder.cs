using UnityEngine;

namespace VideoStream
{
    static class PlatformEncoder
    {
        public static IUnityVideoEncoder Create(VideoStreamConfig config)
        {
#if UNITY_ANDROID
            return new AndroidMediaCodecEncoder();
#else
            Debug.LogWarning("[VideoStream] UDP streamer currently requires an Android build. Editor/desktop play mode is not encoded.");
            return null;
#endif
        }
    }
}
