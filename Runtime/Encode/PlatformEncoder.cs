using UnityEngine;

namespace VideoStream
{
    static class PlatformEncoder
    {
        public static IUnityVideoEncoder Create(VideoStreamConfig config)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidMediaCodecEncoder();
#else
            Debug.Log("[VideoStream] Streaming requires an Android build; disabled in editor/desktop.");
            return null;
#endif
        }
    }
}
