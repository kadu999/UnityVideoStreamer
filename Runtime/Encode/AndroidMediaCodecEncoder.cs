#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace VideoStream
{
    public sealed class AndroidMediaCodecEncoder : IUnityVideoEncoder
    {
        readonly object _lock = new object();
        AndroidJavaObject _javaEncoder;
        JavaCallbackProxy _proxy;
        volatile bool _running;

        public event Action<EncodedFrame> FrameEncoded;
        public event Action<string> Error;

        public bool IsRunning => _running;

        public bool Start(VideoStreamConfig config)
        {
            lock (_lock)
            {
                if (_running) return true;

                try
                {
                    _proxy = new JavaCallbackProxy(this);
                    _javaEncoder = new AndroidJavaObject("com.videostream.stream.VideoStreamEncoder");
                    _javaEncoder.Call("setCallback", _proxy);
                    _javaEncoder.Call("setFlipY", config.FlipY);

                    var ok = _javaEncoder.Call<bool>(
                        "open",
                        config.Width,
                        config.Height,
                        config.Bitrate,
                        config.FrameRate,
                        config.KeyFrameIntervalSeconds,
                        config.MimeType
                    );

                    if (!ok)
                    {
                        DisposeJava();
                        return false;
                    }

                    _running = true;
                    Debug.Log($"[VideoStream] Android encoder started: {config.Width}x{config.Height} {config.FrameRate}fps {config.MimeType}");
                    return true;
                }
                catch (Exception ex)
                {
                    Error?.Invoke("Android encoder start failed: " + ex.Message);
                    DisposeJava();
                    return false;
                }
            }
        }

        public void PushFrame(byte[] rgba, int width, int height, long ptsUs)
        {
            var encoder = _javaEncoder;
            if (!_running || encoder == null) return;

            try
            {
                encoder.Call("pushFrame", rgba, width, height, ptsUs);
            }
            catch (Exception ex)
            {
                Error?.Invoke("Push frame failed: " + ex.Message);
            }
        }

        public void RequestKeyFrame()
        {
            try { _javaEncoder?.Call("requestKeyFrame"); }
            catch (Exception ex)
            {
                Error?.Invoke("Request key frame failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running && _javaEncoder == null) return;
                _running = false;
                DisposeJava();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        void DisposeJava()
        {
            try { _javaEncoder?.Call("close"); } catch { }
            try { _javaEncoder?.Dispose(); } catch { }
            _javaEncoder = null;
            _proxy = null;
        }

        internal void RaiseFrameEncoded(
            byte[] data,
            bool isConfig,
            bool isKeyFrame,
            string mimeType,
            long ptsUs
        )
        {
            FrameEncoded?.Invoke(new EncodedFrame(data, isConfig, isKeyFrame, mimeType, ptsUs));
        }

        internal void RaiseError(string message)
        {
            Error?.Invoke(message);
        }
    }

    sealed class JavaCallbackProxy : AndroidJavaProxy
    {
        readonly AndroidMediaCodecEncoder _owner;

        public JavaCallbackProxy(AndroidMediaCodecEncoder owner)
            : base("com.videostream.stream.VideoStreamCallback")
        {
            _owner = owner;
        }

        void onEncodedFrame(
            sbyte[] data,
            int offset,
            int length,
            long ptsUs,
            bool config,
            bool keyFrame,
            string mime
        )
        {
            if (data == null || length <= 0 || offset < 0 || offset + length > data.Length) return;

            var bytes = new byte[length];
            Buffer.BlockCopy(data, offset, bytes, 0, length);
            _owner.RaiseFrameEncoded(bytes, config, keyFrame, mime, ptsUs);
        }

        void onError(string message)
        {
            _owner.RaiseError(message);
        }
    }
}
#endif
