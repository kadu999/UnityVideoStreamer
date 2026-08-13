namespace VideoStream
{
    public static class UnityVideoStreamCaptureBridge
    {
        static UnityEngine.RenderTexture _targetTexture;

        public static UnityEngine.RenderTexture TargetTexture
        {
            get => _targetTexture;
            set => _targetTexture = value;
        }
    }
}
