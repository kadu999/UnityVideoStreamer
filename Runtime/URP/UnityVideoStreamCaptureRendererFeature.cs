#if VIDEOSTREAM_URP

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace VideoStream.URP
{
    public sealed class UnityVideoStreamCaptureRendererFeature : ScriptableRendererFeature
    {
        sealed class CapturePass : ScriptableRenderPass
        {
            RTHandle _destination;
            RenderTexture _registeredTarget;

            public CapturePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!ShouldCapture(frameData.Get<UniversalCameraData>()))
                    return;

                var target = UnityVideoStreamCaptureBridge.TargetTexture;
                if (target == null || !target.IsCreated())
                    return;

                UpdateDestination(target);

                var resourceData = frameData.Get<UniversalResourceData>();
                var source = resourceData.activeColorTexture;
                var destination = renderGraph.ImportTexture(_destination);
                if (!source.IsValid() || !destination.IsValid())
                    return;

                var blitParameters = new RenderGraphUtils.BlitMaterialParameters(
                    source,
                    destination,
                    Blitter.GetBlitMaterial(TextureDimension.Tex2D),
                    0);
                renderGraph.AddBlitPass(blitParameters, "VideoStream Camera Stack Capture");
            }

#if URP_COMPATIBILITY_MODE
#pragma warning disable 618, 672
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!ShouldCapture(renderingData.cameraData))
                    return;

                var target = UnityVideoStreamCaptureBridge.TargetTexture;
                if (target == null || !target.IsCreated())
                    return;

                UpdateDestination(target);

                var cmd = CommandBufferPool.Get("VideoStream Camera Stack Capture");
                Blitter.BlitCameraTexture(
                    cmd,
                    renderingData.cameraData.renderer.cameraColorTargetHandle,
                    _destination,
                    0f,
                    true);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore 618, 672
#endif

            static bool ShouldCapture(UniversalCameraData cameraData)
            {
                if (cameraData.cameraType != CameraType.Game)
                    return false;

                if (cameraData.renderType == CameraRenderType.Overlay)
                    return true;

                if (cameraData.renderType != CameraRenderType.Base)
                    return false;

                return !HasActiveOverlayCamera(cameraData.camera);
            }

            static bool HasActiveOverlayCamera(Camera camera)
            {
                if (camera == null || !camera.TryGetComponent<UniversalAdditionalCameraData>(out var data))
                    return false;

                foreach (var overlay in data.cameraStack)
                {
                    if (overlay != null && overlay.isActiveAndEnabled)
                        return true;
                }

                return false;
            }

            void UpdateDestination(RenderTexture target)
            {
                if (ReferenceEquals(_registeredTarget, target))
                    return;

                if (_destination != null)
                {
                    _destination.Release();
                    _destination = null;
                }

                _registeredTarget = target;
                _destination = RTHandles.Alloc(target);
            }

            public void ReleaseResources()
            {
                if (_destination != null)
                {
                    _destination.Release();
                    _destination = null;
                }

                _registeredTarget = null;
            }
        }

        CapturePass _capturePass;

        public override void Create()
        {
            _capturePass = new CapturePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (UnityVideoStreamCaptureBridge.TargetTexture == null)
                return;

            renderer.EnqueuePass(_capturePass);
        }

        protected override void Dispose(bool disposing)
        {
            if (_capturePass != null)
            {
                _capturePass.ReleaseResources();
                _capturePass = null;
            }
        }
    }
}

#endif
