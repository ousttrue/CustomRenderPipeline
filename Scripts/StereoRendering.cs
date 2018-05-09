using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.LightweightPipeline;


namespace CustomRP
{
    public static class StereoRendering
    {
        public static void Start(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfiguration, Camera currentCamera)
        {
            if (LightweightUtils.HasFlag(renderingConfiguration, FrameRenderingConfiguration.Stereo))
                context.StartMultiEye(currentCamera);
        }

        public static void Stop(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfiguration, Camera currentCamera)
        {
            if (LightweightUtils.HasFlag(renderingConfiguration, FrameRenderingConfiguration.Stereo))
                context.StopMultiEye(currentCamera);
        }
    }
}
