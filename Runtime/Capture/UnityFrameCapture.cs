using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VideoStream
{
    public sealed class UnityFrameCapture : IDisposable
    {
        readonly Camera _camera;
        readonly RenderTexture _target;
        readonly bool _flipY;
        readonly bool _useCameraTarget;
        readonly RenderTexture _previousTarget;

        bool _disposed;

        public RenderTexture TargetTexture => _target;

        public UnityFrameCapture(Camera camera, int width, int height, bool flipY)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            _camera = camera;
            _flipY = flipY;
            _useCameraTarget = GraphicsSettings.currentRenderPipeline == null;
            _previousTarget = camera.targetTexture;
            var descriptor = new RenderTextureDescriptor(
                width,
                height,
                GraphicsFormat.R8G8B8A8_SRGB,
                0)
            {
                msaaSamples = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = true
            };
            _target = new RenderTexture(descriptor);
            _target.name = "VideoStreamCapture";
            _target.filterMode = FilterMode.Bilinear;
            _target.wrapMode = TextureWrapMode.Clamp;
            _target.Create();

            if (_useCameraTarget)
            {
                camera.targetTexture = _target;
            }
            else
            {
                UnityVideoStreamCaptureBridge.TargetTexture = _target;
            }
        }

        public void RenderFrameToEncoder(IUnityVideoEncoder encoder)
        {
            if (_disposed || encoder == null) return;
            encoder.RenderFrame(_target.GetNativeTexturePtr(), _target.width, _target.height, _flipY);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_useCameraTarget && _camera != null && ReferenceEquals(_camera.targetTexture, _target))
            {
                _camera.targetTexture = _previousTarget;
            }

            if (!_useCameraTarget && ReferenceEquals(UnityVideoStreamCaptureBridge.TargetTexture, _target))
            {
                UnityVideoStreamCaptureBridge.TargetTexture = null;
            }

            _target.Release();
            UnityEngine.Object.Destroy(_target);
        }
    }
}
