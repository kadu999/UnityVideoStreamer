using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VideoStream.URP;

namespace VideoStream.Editor.URP
{
    [InitializeOnLoad]
    public static class VideoStreamURPRendererSetup
    {
        static VideoStreamURPRendererSetup()
        {
            EditorApplication.delayCall += AddCaptureFeatureToRenderers;
        }

        [MenuItem("Assets/Video Stream/Setup URP Camera Capture Renderer Feature")]
        static void SetupFromMenu()
        {
            AddCaptureFeatureToRenderers();
        }

        static void AddCaptureFeatureToRenderers()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += AddCaptureFeatureToRenderers;
                return;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Packages/") || path.StartsWith("Library/"))
                    continue;

                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (renderer == null || HasCaptureFeature(renderer))
                    continue;

                AddCaptureFeature(renderer, path);
            }
        }

        static bool HasCaptureFeature(UniversalRendererData renderer)
        {
            foreach (var feature in renderer.rendererFeatures)
            {
                if (feature != null && feature.GetType() == typeof(UnityVideoStreamCaptureRendererFeature))
                    return true;
            }

            return false;
        }

        static void AddCaptureFeature(UniversalRendererData renderer, string path)
        {
            var feature = ScriptableObject.CreateInstance<UnityVideoStreamCaptureRendererFeature>();
            feature.name = "VideoStream Camera Capture";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var serializedObject = new SerializedObject(renderer);
            var features = serializedObject.FindProperty("m_RendererFeatures");
            var featureMap = serializedObject.FindProperty("m_RendererFeatureMap");

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            featureMap.arraySize++;
            featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
            Debug.Log("[VideoStream] Added URP camera capture renderer feature to " + path, renderer);
        }
    }
}
