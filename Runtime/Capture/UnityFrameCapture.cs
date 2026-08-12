using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace VideoStream
{
    public sealed class UnityFrameCapture : IDisposable
    {
        readonly Camera _camera;
        readonly RenderTexture _target;
        readonly bool _flipY;
        readonly RenderTexture _previousTarget;
        readonly object _lock = new object();

        bool _requestPending;
        bool _disposed;

        public event Action<byte[], int, int, long, bool> FrameReady;

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

        public void CaptureFrame(long ptsUs)
        {
            lock (_lock)
            {
                if (_disposed || _requestPending) return;
                _requestPending = true;
            }

            AsyncGPUReadback.Request(_target, 0, request => OnReadback(request, ptsUs));
        }

        void OnReadback(AsyncGPUReadbackRequest request, long ptsUs)
        {
            lock (_lock)
            {
                _requestPending = false;
                if (_disposed) return;
            }

            if (request.hasError)
            {
                Debug.LogWarning("[VideoStream] AsyncGPUReadback failed");
                return;
            }

            var data = request.GetData<byte>();
            var expected = _target.width * _target.height * 4;
            if (data.Length < expected)
            {
                Debug.LogWarning("[VideoStream] RenderTexture readback too small");
                return;
            }

            var rgba = new byte[expected];
            for (var i = 0; i < expected; i++)
            {
                rgba[i] = data[i];
            }
            FrameReady?.Invoke(rgba, _target.width, _target.height, ptsUs, _flipY);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            if (_camera != null && ReferenceEquals(_camera.targetTexture, _target))
            {
                _camera.targetTexture = _previousTarget;
            }

            _target.Release();
            UnityEngine.Object.Destroy(_target);
        }
    }
}
