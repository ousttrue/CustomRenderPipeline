using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.LightweightPipeline;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;

namespace CustomRP
{
    public class TextureUtil : IDisposable
    {
        private readonly CustomRenderPipelineAsset m_Asset;

        private const int kDepthStencilBufferBits = 32;

        private RenderTargetIdentifier m_CurrCameraColorRT;
        public RenderTargetIdentifier CurrCameraColorRT
        {
            get { return m_CurrCameraColorRT; }
        }

        private bool m_IntermediateTextureArray;
        private bool m_DepthRenderBuffer;
        private RenderTextureFormat m_ColorFormat;

        private bool m_RequireDepthTexture;
        public bool RequireDepthTexture
        {
            get { return m_RequireDepthTexture; }
        }

        private bool m_RequireCopyColor;
        public bool RequireCopyColor
        {
            get { return m_RequireCopyColor; }
        }

        private PostProcessLayer m_CameraPostProcessLayer;
        private PostProcessRenderContext m_PostProcessRenderContext;

        private Mesh m_BlitQuad;
        private Material m_BlitMaterial;
        private Material m_CopyDepthMaterial;
        private int m_BlitTexID = Shader.PropertyToID("_BlitTex");

        private CopyTextureSupport m_CopyTextureSupport;

        public TextureUtil(CustomRenderPipelineAsset asset)
        {
            m_Asset = asset;

            m_CopyTextureSupport = SystemInfo.copyTextureSupport;
            m_BlitQuad = LightweightUtils.CreateQuadMesh(false);
            m_BlitMaterial = CoreUtils.CreateEngineMaterial(asset.BlitShader);
            m_CopyDepthMaterial = CoreUtils.CreateEngineMaterial(asset.CopyDepthShader);
            m_PostProcessRenderContext = new PostProcessRenderContext();
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_CopyDepthMaterial);
            CoreUtils.Destroy(m_BlitMaterial);
        }

        public void SetupFrameRenderingConfiguration(out FrameRenderingConfiguration configuration, CameraContext cameraContext, bool shadows, ShadowManager shadowManager)
        {
            configuration = (cameraContext.StereoEnabled) ? FrameRenderingConfiguration.Stereo : FrameRenderingConfiguration.None;
            if (cameraContext.StereoEnabled && XRSettings.eyeTextureDesc.dimension == TextureDimension.Tex2DArray)
                m_IntermediateTextureArray = true;
            else
                m_IntermediateTextureArray = false;

            var camera = cameraContext.Camera;
            bool hdrEnabled = m_Asset.SupportsHDR && camera.allowHDR;
            bool intermediateTexture = camera.targetTexture != null || camera.cameraType == CameraType.SceneView ||
                m_Asset.RenderScale < 1.0f || hdrEnabled;

            m_ColorFormat = hdrEnabled ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            m_RequireCopyColor = false;
            m_DepthRenderBuffer = false;
            m_CameraPostProcessLayer = camera.GetComponent<PostProcessLayer>();

            bool msaaEnabled = camera.allowMSAA && m_Asset.MSAASampleCount > 1 && (camera.targetTexture == null || camera.targetTexture.antiAliasing > 1);

            // TODO: PostProcessing and SoftParticles are currently not support for VR
            bool postProcessEnabled = m_CameraPostProcessLayer != null && m_CameraPostProcessLayer.enabled && !cameraContext.StereoEnabled;
            m_RequireDepthTexture = m_Asset.RequireDepthTexture && !cameraContext.StereoEnabled;
            if (postProcessEnabled)
            {
                m_RequireDepthTexture = true;
                intermediateTexture = true;

                configuration |= FrameRenderingConfiguration.PostProcess;
                if (m_CameraPostProcessLayer.HasOpaqueOnlyEffects(m_PostProcessRenderContext))
                {
                    configuration |= FrameRenderingConfiguration.BeforeTransparentPostProcess;
                    if (m_CameraPostProcessLayer.sortedBundles[PostProcessEvent.BeforeTransparent].Count == 1)
                        m_RequireCopyColor = true;
                }
            }

            if (cameraContext.SceneViewCamera)
                m_RequireDepthTexture = true;

            if (shadows)
            {
                m_RequireDepthTexture = shadowManager.IsScreenSpace;

                if (!msaaEnabled)
                    intermediateTexture = true;
            }

            if (msaaEnabled)
            {
                configuration |= FrameRenderingConfiguration.Msaa;
                intermediateTexture = intermediateTexture || !LightweightUtils.PlatformSupportsMSAABackBuffer();
            }

            if (m_RequireDepthTexture)
            {
                // If msaa is enabled we don't use a depth renderbuffer as we might not have support to Texture2DMS to resolve depth.
                // Instead we use a depth prepass and whenever depth is needed we use the 1 sample depth from prepass.
                // Screen space shadows require depth before opaque shading.
                if (!msaaEnabled && !shadows)
                {
                    bool supportsDepthCopy = m_CopyTextureSupport != CopyTextureSupport.None && m_Asset.CopyDepthShader.isSupported;
                    m_DepthRenderBuffer = true;
                    intermediateTexture = true;

                    // If requiring a camera depth texture we need separate depth as it reads/write to depth at same time
                    // Post process doesn't need the copy
                    if (!m_Asset.RequireDepthTexture && postProcessEnabled)
                        configuration |= (supportsDepthCopy) ? FrameRenderingConfiguration.DepthCopy : FrameRenderingConfiguration.DepthPrePass;
                }
                else
                {
                    configuration |= FrameRenderingConfiguration.DepthPrePass;
                }
            }

            Rect cameraRect = camera.rect;
            if (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f || Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f))
                configuration |= FrameRenderingConfiguration.DefaultViewport;
            else
                intermediateTexture = true;

            if (intermediateTexture)
                configuration |= FrameRenderingConfiguration.IntermediateTexture;
        }

        void Blit(CommandBuffer cmd, FrameRenderingConfiguration renderingConfig, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, Camera camera, Material material = null)
        {
            cmd.SetGlobalTexture(m_BlitTexID, sourceRT);
            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.DefaultViewport))
            {
                cmd.Blit(sourceRT, destRT, material);
            }
            else
            {
                if (m_BlitQuad == null)
                    m_BlitQuad = LightweightUtils.CreateQuadMesh(false);

                SetRenderTarget(cmd, destRT, camera.backgroundColor);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.SetViewport(camera.pixelRect);
                cmd.DrawMesh(m_BlitQuad, Matrix4x4.identity, material);
            }
        }

        public void Blit(ScriptableRenderContext context, RenderTargetIdentifier currCameraColorRT, FrameRenderingConfiguration renderingConfig, Camera camera)
        {
            var cmd = CommandBufferPool.Get("Blit");
            if (m_IntermediateTextureArray)
            {
                cmd.Blit(currCameraColorRT, BuiltinRenderTextureType.CameraTarget);
            }
            else if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture))
            {
                Material blitMaterial = m_BlitMaterial;
                if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                    blitMaterial = null;

                // If PostProcessing is enabled, it is already blit to CameraTarget.
                if (!LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    Blit(cmd, renderingConfig, currCameraColorRT, BuiltinRenderTextureType.CameraTarget, camera, blitMaterial);
            }

            SetRenderTarget(cmd, BuiltinRenderTextureType.CameraTarget, camera.backgroundColor);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void CopyTexture(CommandBuffer cmd, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, bool forceBlit = false)
        {

            if (m_CopyTextureSupport != CopyTextureSupport.None && !forceBlit)
                cmd.CopyTexture(sourceRT, destRT);
            else
                cmd.Blit(sourceRT, destRT, m_CopyDepthMaterial);
        }

        public void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorRT, Color backgroundColor, ClearFlag clearFlag = ClearFlag.None)
        {
            int depthSlice = (m_IntermediateTextureArray) ? -1 : 0;
            CoreUtils.SetRenderTarget(cmd, colorRT, clearFlag, CoreUtils.ConvertSRGBToActiveColorSpace(backgroundColor), 0, CubemapFace.Unknown, depthSlice);
        }

        public void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorRT, RenderTargetIdentifier depthRT, Color backgroundColor, ClearFlag clearFlag = ClearFlag.None)
        {
            if (depthRT == BuiltinRenderTextureType.None || !m_DepthRenderBuffer)
            {
                SetRenderTarget(cmd, colorRT, backgroundColor, clearFlag);
                return;
            }

            int depthSlice = (m_IntermediateTextureArray) ? -1 : 0;
            CoreUtils.SetRenderTarget(cmd, colorRT, depthRT, clearFlag, CoreUtils.ConvertSRGBToActiveColorSpace(backgroundColor), 0, CubemapFace.Unknown, depthSlice);
        }

        public void SetupIntermediateResources(FrameRenderingConfiguration renderingConfig, ref ScriptableRenderContext context, CameraContext cameraContext, RenderTargetIdentifier colorRT)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Setup Intermediate Resources");

            int msaaSamples = (cameraContext.IsOffscreenCamera) ? Math.Min(cameraContext.Camera.targetTexture.antiAliasing, m_Asset.MSAASampleCount) : m_Asset.MSAASampleCount;
            msaaSamples = (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Msaa)) ? msaaSamples : 1;
            m_CurrCameraColorRT = BuiltinRenderTextureType.CameraTarget;

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture) || m_RequireDepthTexture)
                SetupIntermediateRenderTextures(cmd, renderingConfig, msaaSamples, cameraContext, colorRT);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void SetupIntermediateRenderTextures(CommandBuffer cmd, FrameRenderingConfiguration renderingConfig, int msaaSamples, CameraContext cameraContext, RenderTargetIdentifier ColorRT)
        {
            var camera = cameraContext.Camera;
            RenderTextureDescriptor baseDesc;
            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                baseDesc = XRSettings.eyeTextureDesc;
            else
                baseDesc = new RenderTextureDescriptor(camera.pixelWidth, camera.pixelHeight);

            float renderScale = (camera.cameraType == CameraType.Game) ? m_Asset.RenderScale : 1.0f;
            baseDesc.width = (int)((float)baseDesc.width * renderScale);
            baseDesc.height = (int)((float)baseDesc.height * renderScale);

            // TODO: Might be worth caching baseDesc for allocation of other targets (Screen-space Shadow Map?)

            if (m_RequireDepthTexture)
            {
                var depthRTDesc = baseDesc;
                depthRTDesc.colorFormat = RenderTextureFormat.Depth;
                depthRTDesc.depthBufferBits = kDepthStencilBufferBits;

                cmd.GetTemporaryRT(CameraRenderTargetID.depth, depthRTDesc, FilterMode.Bilinear);

                if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.DepthCopy))
                    cmd.GetTemporaryRT(CameraRenderTargetID.depthCopy, depthRTDesc, FilterMode.Bilinear);
            }

            var colorRTDesc = baseDesc;
            colorRTDesc.colorFormat = m_ColorFormat;
            colorRTDesc.depthBufferBits = kDepthStencilBufferBits; // TODO: does the color RT always need depth?
            colorRTDesc.sRGB = true;
            colorRTDesc.msaaSamples = msaaSamples;
            colorRTDesc.enableRandomWrite = false;

            // When offscreen camera current rendertarget is CameraTarget
            if (!cameraContext.IsOffscreenCamera)
            {
                cmd.GetTemporaryRT(CameraRenderTargetID.color, colorRTDesc, FilterMode.Bilinear);
                m_CurrCameraColorRT = ColorRT;
            }

            // When BeforeTransparent PostFX is enabled and only one effect is in the stack we need to create a temp
            // color RT to blit the effect.
            if (m_RequireCopyColor)
                cmd.GetTemporaryRT(CameraRenderTargetID.copyColor, colorRTDesc, FilterMode.Point);
        }

        public void RenderPostProcess(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier dest, bool opaqueOnly, Camera camera)
        {
            m_PostProcessRenderContext.Reset();
            m_PostProcessRenderContext.camera = camera;
            m_PostProcessRenderContext.source = source;
            m_PostProcessRenderContext.sourceFormat = m_ColorFormat;
            m_PostProcessRenderContext.destination = dest;
            m_PostProcessRenderContext.command = cmd;
            m_PostProcessRenderContext.flip = true;

            if (opaqueOnly)
            {
                m_CameraPostProcessLayer.RenderOpaqueOnly(m_PostProcessRenderContext);
            }
            else
                m_CameraPostProcessLayer.Render(m_PostProcessRenderContext);
        }
    }
}
