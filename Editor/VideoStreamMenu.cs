using UnityEditor;
using UnityEngine;

namespace VideoStream.Editor
{
    public static class VideoStreamMenu
    {
        [MenuItem("GameObject/Video Stream/Unity Video Streamer", false, 10)]
        static void CreateUnityVideoStreamer()
        {
            var existing = Object.FindObjectOfType<UnityVideoStreamer>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            var go = new GameObject("Unity Video Streamer");
            go.AddComponent<UnityVideoStreamer>().UseHevc = false;
            Undo.RegisterCreatedObjectUndo(go, "Create Unity Video Streamer");
            Selection.activeGameObject = go;
        }
    }
}
