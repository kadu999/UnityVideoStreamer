using UnityEditor;
using UnityEngine;

namespace VideoStream.Editor
{
    public static class VideoStreamMenu
    {
        [MenuItem("GameObject/Video Stream/UDP Streamer", false, 10)]
        static void CreateUdpStreamer()
        {
            var existing = Object.FindObjectOfType<UdpVideoStreamer>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            var go = new GameObject("UDP Video Streamer");
            go.AddComponent<UdpVideoStreamer>();
            Undo.RegisterCreatedObjectUndo(go, "Create UDP Video Streamer");
            Selection.activeGameObject = go;
        }
    }
}
