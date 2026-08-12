using System;
using UnityEngine;

namespace VideoStream
{
    public sealed class UnityFrameCapture : IDisposable
    {
        readonly Camera _camera;
        readonly RenderTexture _target;
        readonly bool _flipY;
        readonly RenderTexture _previousTarget;

        bool _disposed;

        public RenderTexture TargetTexture => _target;

        public UnityFrameCapture(Camera camera, int width, int height, bool flipY)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            _camera = camera;
            _flipY = flipY;
            _previousTarget = camera.targetTexture;
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                useMipMap = false,
                filterMode = FilterMode.Bilinear
            };
            _target.Create();
            camera.targetTexture = _target;
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

            if (_camera != null && ReferenceEquals(_camera.targetTexture, _target))
            {
                _camera.targetTexture = _previousTarget;
            }

            _target.Release();
            UnityEngine.Object.Destroy(_target);
        }
    }
}
