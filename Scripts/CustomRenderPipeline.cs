using UnityEngine;
using UnityEngine.Experimental.Rendering;
using System.Collections;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.Experimental.Rendering.LightweightPipeline;
using System;
using UnityEngine.XR;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif


namespace CustomRP
{
    internal static class SceneViewDrawMode
    {
        static bool RejectDrawMode(SceneView.CameraMode cameraMode)
        {
            if (cameraMode.drawMode == DrawCameraMode.TexturedWire ||
                cameraMode.drawMode == DrawCameraMode.ShadowCascades ||
                cameraMode.drawMode == DrawCameraMode.RenderPaths ||
                cameraMode.drawMode == DrawCameraMode.AlphaChannel ||
                cameraMode.drawMode == DrawCameraMode.Overdraw ||
                cameraMode.drawMode == DrawCameraMode.Mipmaps ||
                cameraMode.drawMode == DrawCameraMode.SpriteMask ||
                cameraMode.drawMode == DrawCameraMode.DeferredDiffuse ||
                cameraMode.drawMode == DrawCameraMode.DeferredSpecular ||
                cameraMode.drawMode == DrawCameraMode.DeferredSmoothness ||
                cameraMode.drawMode == DrawCameraMode.DeferredNormal ||
                cameraMode.drawMode == DrawCameraMode.ValidateAlbedo ||
                cameraMode.drawMode == DrawCameraMode.ValidateMetalSpecular ||
                cameraMode.drawMode == DrawCameraMode.ShadowMasks ||
                cameraMode.drawMode == DrawCameraMode.LightOverlap
            )
                return false;

            return true;
        }

        public static void SetupDrawMode()
        {
            ArrayList sceneViewArray = SceneView.sceneViews;
            foreach (SceneView sceneView in sceneViewArray)
                sceneView.onValidateCameraMode += RejectDrawMode;
        }

        public static void ResetDrawMode()
        {
            ArrayList sceneViewArray = SceneView.sceneViews;
            foreach (SceneView sceneView in sceneViewArray)
                sceneView.onValidateCameraMode -= RejectDrawMode;
        }
    }

    public class CustomRenderPipeline : RenderPipeline
    {
        private readonly CustomRenderPipelineAsset m_Asset;


        private bool m_IsOffscreenCamera;

        private Camera m_CurrCamera;

        private RenderTargetIdentifier m_CurrCameraColorRT;
        private RenderTargetIdentifier m_ColorRT;
        private RenderTargetIdentifier m_CopyColorRT;
        private RenderTargetIdentifier m_DepthRT;
        private RenderTargetIdentifier m_CopyDepth;
        private RenderTargetIdentifier m_Color;

        private bool m_IntermediateTextureArray;
        private bool m_RequireDepthTexture;
        private bool m_RequireCopyColor;
        private bool m_DepthRenderBuffer;

        private const int kDepthStencilBufferBits = 32;

        // Pipeline pass names
        private static readonly ShaderPassName m_DepthPrepass = new ShaderPassName("DepthOnly");
        private static readonly ShaderPassName m_LitPassName = new ShaderPassName("LightweightForward");
        private static readonly ShaderPassName m_UnlitPassName = new ShaderPassName("SRPDefaultUnlit"); // Renders all shaders without a lightmode tag

        // Legacy pass names
        public static readonly ShaderPassName s_AlwaysName = new ShaderPassName("Always");
        public static readonly ShaderPassName s_ForwardBaseName = new ShaderPassName("ForwardBase");
        public static readonly ShaderPassName s_PrepassBaseName = new ShaderPassName("PrepassBase");
        public static readonly ShaderPassName s_VertexName = new ShaderPassName("Vertex");
        public static readonly ShaderPassName s_VertexLMRGBMName = new ShaderPassName("VertexLMRGBM");
        public static readonly ShaderPassName s_VertexLMName = new ShaderPassName("VertexLM");
        public static readonly ShaderPassName[] s_LegacyPassNames =
        {
            s_AlwaysName, s_ForwardBaseName, s_PrepassBaseName, s_VertexName, s_VertexLMRGBMName, s_VertexLMName
        };

        private RenderTextureFormat m_ColorFormat;
        private PostProcessRenderContext m_PostProcessRenderContext;
        private PostProcessLayer m_CameraPostProcessLayer;

        private CameraComparer m_CameraComparer = new CameraComparer();


        private Mesh m_BlitQuad;
        private Material m_BlitMaterial;
        private Material m_CopyDepthMaterial;
        private Material m_ErrorMaterial;
        private int m_BlitTexID = Shader.PropertyToID("_BlitTex");

        private CopyTextureSupport m_CopyTextureSupport;

        private LightManager m_LightManager;
        private ShadowManager m_ShadowMapManager;

        public CustomRenderPipeline(CustomRenderPipelineAsset asset)
        {
            m_Asset = asset;

            SetRenderingFeatures();

            PerFrameBuffer._GlossyEnvironmentColor = Shader.PropertyToID("_GlossyEnvironmentColor");
            PerFrameBuffer._SubtractiveShadowColor = Shader.PropertyToID("_SubtractiveShadowColor");

            // Lights are culled per-camera. Therefore we need to reset light buffers on each camera render
            PerCameraBuffer._MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            PerCameraBuffer._MainLightColor = Shader.PropertyToID("_MainLightColor");
            PerCameraBuffer._MainLightDistanceAttenuation = Shader.PropertyToID("_MainLightDistanceAttenuation");
            PerCameraBuffer._MainLightSpotDir = Shader.PropertyToID("_MainLightSpotDir");
            PerCameraBuffer._MainLightSpotAttenuation = Shader.PropertyToID("_MainLightSpotAttenuation");
            PerCameraBuffer._MainLightCookie = Shader.PropertyToID("_MainLightCookie");
            PerCameraBuffer._WorldToLight = Shader.PropertyToID("_WorldToLight");
            PerCameraBuffer._AdditionalLightCount = Shader.PropertyToID("_AdditionalLightCount");
            PerCameraBuffer._AdditionalLightPosition = Shader.PropertyToID("_AdditionalLightPosition");
            PerCameraBuffer._AdditionalLightColor = Shader.PropertyToID("_AdditionalLightColor");
            PerCameraBuffer._AdditionalLightDistanceAttenuation = Shader.PropertyToID("_AdditionalLightDistanceAttenuation");
            PerCameraBuffer._AdditionalLightSpotDir = Shader.PropertyToID("_AdditionalLightSpotDir");
            PerCameraBuffer._AdditionalLightSpotAttenuation = Shader.PropertyToID("_AdditionalLightSpotAttenuation");

            CameraRenderTargetID.color = Shader.PropertyToID("_CameraColorRT");
            CameraRenderTargetID.copyColor = Shader.PropertyToID("_CameraCopyColorRT");
            CameraRenderTargetID.depth = Shader.PropertyToID("_CameraDepthTexture");
            CameraRenderTargetID.depthCopy = Shader.PropertyToID("_CameraCopyDepthTexture");

            m_ColorRT = new RenderTargetIdentifier(CameraRenderTargetID.color);
            m_CopyColorRT = new RenderTargetIdentifier(CameraRenderTargetID.copyColor);
            m_DepthRT = new RenderTargetIdentifier(CameraRenderTargetID.depth);
            m_CopyDepth = new RenderTargetIdentifier(CameraRenderTargetID.depthCopy);
            m_PostProcessRenderContext = new PostProcessRenderContext();

            m_CopyTextureSupport = SystemInfo.copyTextureSupport;

            // Let engine know we have MSAA on for cases where we support MSAA backbuffer
            if (QualitySettings.antiAliasing != m_Asset.MSAASampleCount)
                QualitySettings.antiAliasing = m_Asset.MSAASampleCount;

            Shader.globalRenderPipeline = "LightweightPipeline";

            m_BlitQuad = LightweightUtils.CreateQuadMesh(false);
            m_BlitMaterial = CoreUtils.CreateEngineMaterial(m_Asset.BlitShader);
            m_CopyDepthMaterial = CoreUtils.CreateEngineMaterial(m_Asset.CopyDepthShader);
            m_ErrorMaterial = CoreUtils.CreateEngineMaterial("Hidden/InternalErrorShader");

            m_LightManager = new LightManager(asset);
            m_ShadowMapManager = new ShadowManager(asset);
            m_RendererPerCamera = new RendererPerCamera();
        }

        public override void Dispose()
        {
            base.Dispose();
            Shader.globalRenderPipeline = "";

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();

            CoreUtils.Destroy(m_ErrorMaterial);
            CoreUtils.Destroy(m_CopyDepthMaterial);
            CoreUtils.Destroy(m_BlitMaterial);

#if UNITY_EDITOR
            SceneViewDrawMode.ResetDrawMode();
#endif
        }

        private void SetRenderingFeatures()
        {
#if UNITY_EDITOR
            SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
            {
                reflectionProbeSupportFlags = SupportedRenderingFeatures.ReflectionProbeSupportFlags.None,
                defaultMixedLightingMode = SupportedRenderingFeatures.LightmapMixedBakeMode.Subtractive,
                supportedMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeMode.Subtractive,
                supportedLightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed,
                supportedLightmapsModes = LightmapsMode.CombinedDirectional | LightmapsMode.NonDirectional,
                rendererSupportsLightProbeProxyVolumes = false,
                rendererSupportsMotionVectors = false,
                rendererSupportsReceiveShadows = true,
                rendererSupportsReflectionProbes = true
            };
            SceneViewDrawMode.SetupDrawMode();
#endif
        }

        CullResults m_CullResults;
        public override void Render(ScriptableRenderContext context, Camera[] cameras)
        {
            base.Render(context, cameras);
            RenderPipeline.BeginFrameRendering(cameras);

            GraphicsSettings.lightsUseLinearIntensity = true;
            SetupPerFrameShaderConstants();

            // Sort cameras array by camera depth
            Array.Sort(cameras, m_CameraComparer);
            foreach (Camera camera in cameras)
            {
                RenderPipeline.BeginCameraRendering(camera);

                bool sceneViewCamera = camera.cameraType == CameraType.SceneView;
                bool stereoEnabled = XRSettings.isDeviceActive && !sceneViewCamera && (camera.stereoTargetEye == StereoTargetEyeMask.Both);
                m_CurrCamera = camera;
                m_IsOffscreenCamera = m_CurrCamera.targetTexture != null && m_CurrCamera.cameraType != CameraType.SceneView;


                ScriptableCullingParameters cullingParameters;
                if (!CullResults.GetCullingParameters(m_CurrCamera, stereoEnabled, out cullingParameters))
                    continue;

                var cmd = CommandBufferPool.Get("");
                cullingParameters.shadowDistance = Mathf.Min(m_ShadowMapManager.MaxShadowDistance,
                        m_CurrCamera.farClipPlane);

#if UNITY_EDITOR
                // Emit scene view UI
                if (sceneViewCamera)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

                CullResults.Cull(ref cullingParameters, context, ref m_CullResults);
                List<VisibleLight> visibleLights = m_CullResults.visibleLights;

                LightData lightData;
                m_LightManager.InitializeLightData(visibleLights, out lightData, m_CurrCamera);

                bool shadows = m_ShadowMapManager.ShadowPass(visibleLights, ref context, ref lightData,
                    m_CullResults, m_LightManager.GetLightUnsortedIndex(lightData.mainLightIndex), m_CurrCamera.backgroundColor);

                FrameRenderingConfiguration frameRenderingConfiguration;
                SetupFrameRenderingConfiguration(out frameRenderingConfiguration, shadows, stereoEnabled, sceneViewCamera);
                SetupIntermediateResources(frameRenderingConfiguration, ref context);

                // SetupCameraProperties does the following:
                // Setup Camera RenderTarget and Viewport
                // VR Camera Setup and SINGLE_PASS_STEREO props
                // Setup camera view, proj and their inv matrices.
                // Setup properties: _WorldSpaceCameraPos, _ProjectionParams, _ScreenParams, _ZBufferParams, unity_OrthoParams
                // Setup camera world clip planes props
                // setup HDR keyword
                // Setup global time properties (_Time, _SinTime, _CosTime)
                context.SetupCameraProperties(m_CurrCamera, stereoEnabled);

                if (LightweightUtils.HasFlag(frameRenderingConfiguration, FrameRenderingConfiguration.DepthPrePass))
                    DepthPass(ref context, frameRenderingConfiguration, m_CullResults.visibleRenderers);

                if (shadows)
                    m_ShadowMapManager.ShadowCollectPass(visibleLights, ref context, ref lightData, frameRenderingConfiguration, m_CurrCamera);
                else
                    m_ShadowMapManager.SmallShadowBuffer(ref context);


                ForwardPass(visibleLights, frameRenderingConfiguration, ref context, ref lightData, stereoEnabled, ref m_CullResults);


                cmd.name = "After Camera Render";
#if UNITY_EDITOR
                if (sceneViewCamera)
                    CopyTexture(cmd, CameraRenderTargetID.depth, BuiltinRenderTextureType.CameraTarget, m_CopyDepthMaterial, m_CopyTextureSupport, true);
#endif
                cmd.ReleaseTemporaryRT(m_ShadowMapManager.ScreenSpaceShadowMapRTID);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.depthCopy);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.depth);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.color);
                cmd.ReleaseTemporaryRT(CameraRenderTargetID.copyColor);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                context.Submit();

                m_ShadowMapManager.ReleaseRenderTarget();
            }
        }

        private void DepthPass(ref ScriptableRenderContext context, FrameRenderingConfiguration frameRenderingConfiguration, FilterResults visibleRenderers)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Depth Prepass");
            SetRenderTarget(cmd, m_DepthRT, ClearFlag.Depth);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            var opaqueDrawSettings = new DrawRendererSettings(m_CurrCamera, m_DepthPrepass);
            opaqueDrawSettings.sorting.flags = SortFlags.CommonOpaque;

            var opaqueFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.opaque
            };

            StereoRendering.Start(ref context, frameRenderingConfiguration, m_CurrCamera);

            context.DrawRenderers(visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);

            StereoRendering.Stop(ref context, frameRenderingConfiguration, m_CurrCamera);
        }

        private void ForwardPass(List<VisibleLight> visibleLights, FrameRenderingConfiguration frameRenderingConfiguration, ref ScriptableRenderContext context, ref LightData lightData, bool stereoEnabled, ref CullResults cullResults)
        {
            SetupShaderConstants(visibleLights, ref context, ref lightData, ref cullResults);

            RendererConfiguration rendererSettings = GetRendererSettings(ref lightData);

            BeginForwardRendering(ref context, frameRenderingConfiguration);
            RenderOpaques(ref context, rendererSettings, cullResults.visibleRenderers);
            AfterOpaque(ref context, frameRenderingConfiguration);
            RenderTransparents(ref context, rendererSettings, cullResults.visibleRenderers);
            AfterTransparent(ref context, frameRenderingConfiguration);
            EndForwardRendering(ref context, frameRenderingConfiguration);
        }

        private void RenderOpaques(ref ScriptableRenderContext context, RendererConfiguration settings, FilterResults visibleRenderers)
        {
            var opaqueDrawSettings = new DrawRendererSettings(m_CurrCamera, m_LitPassName);
            opaqueDrawSettings.SetShaderPassName(1, m_UnlitPassName);
            opaqueDrawSettings.sorting.flags = SortFlags.CommonOpaque;
            opaqueDrawSettings.rendererConfiguration = settings;

            var opaqueFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.opaque
            };

            context.DrawRenderers(visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);

            // Render objects that did not match any shader pass with error shader
            RenderObjectsWithError(ref context, opaqueFilterSettings, SortFlags.None, visibleRenderers);

            if (m_CurrCamera.clearFlags == CameraClearFlags.Skybox)
                context.DrawSkybox(m_CurrCamera);
        }

        private void AfterOpaque(ref ScriptableRenderContext context, FrameRenderingConfiguration config)
        {
            if (!m_RequireDepthTexture)
                return;

            CommandBuffer cmd = CommandBufferPool.Get("After Opaque");
            cmd.SetGlobalTexture(CameraRenderTargetID.depth, m_DepthRT);

            bool setRenderTarget = false;
            RenderTargetIdentifier depthRT = m_DepthRT;

            // TODO: There's currently an issue in the PostFX stack that has a one frame delay when an effect is enabled/disabled
            // when an effect is disabled, HasOpaqueOnlyEffects returns true in the first frame, however inside render the effect
            // state is update, causing RenderPostProcess here to not blit to FinalColorRT. Until the next frame the RT will have garbage.
            if (LightweightUtils.HasFlag(config, FrameRenderingConfiguration.BeforeTransparentPostProcess))
            {
                // When only have one effect in the stack we blit to a work RT then blit it back to active color RT.
                // This seems like an extra blit but it saves us a depth copy/blit which has some corner cases like msaa depth resolve.
                if (m_RequireCopyColor)
                {
                    RenderPostProcess(cmd, m_CurrCameraColorRT, m_CopyColorRT, true);
                    cmd.Blit(m_CopyColorRT, m_CurrCameraColorRT);
                }
                else
                    RenderPostProcess(cmd, m_CurrCameraColorRT, m_CurrCameraColorRT, true);

                setRenderTarget = true;
                SetRenderTarget(cmd, m_CurrCameraColorRT, m_DepthRT);
            }

            if (LightweightUtils.HasFlag(config, FrameRenderingConfiguration.DepthCopy))
            {
                CopyTexture(cmd, m_DepthRT, m_CopyDepth, m_CopyDepthMaterial, m_CopyTextureSupport);
                depthRT = m_CopyDepth;
                setRenderTarget = true;
            }

            if (setRenderTarget)
                SetRenderTarget(cmd, m_CurrCameraColorRT, depthRT);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void RenderTransparents(ref ScriptableRenderContext context, RendererConfiguration config, FilterResults visibleRenderers)
        {
            var transparentSettings = new DrawRendererSettings(m_CurrCamera, m_LitPassName);
            transparentSettings.SetShaderPassName(1, m_UnlitPassName);
            transparentSettings.sorting.flags = SortFlags.CommonTransparent;
            transparentSettings.rendererConfiguration = config;

            var transparentFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.transparent
            };

            context.DrawRenderers(visibleRenderers, ref transparentSettings, transparentFilterSettings);

            // Render objects that did not match any shader pass with error shader
            RenderObjectsWithError(ref context, transparentFilterSettings, SortFlags.None, visibleRenderers);
        }

        private void AfterTransparent(ref ScriptableRenderContext context, FrameRenderingConfiguration config)
        {
            if (!LightweightUtils.HasFlag(config, FrameRenderingConfiguration.PostProcess))
                return;

            CommandBuffer cmd = CommandBufferPool.Get("After Transparent");
            RenderPostProcess(cmd, m_CurrCameraColorRT, BuiltinRenderTextureType.CameraTarget, false);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD"), System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void RenderObjectsWithError(ref ScriptableRenderContext context, FilterRenderersSettings filterSettings, SortFlags sortFlags, FilterResults visibleRenderers)
        {
            if (m_ErrorMaterial != null)
            {
                DrawRendererSettings errorSettings = new DrawRendererSettings(m_CurrCamera, s_LegacyPassNames[0]);
                for (int i = 1; i < s_LegacyPassNames.Length; ++i)
                    errorSettings.SetShaderPassName(i, s_LegacyPassNames[i]);

                errorSettings.sorting.flags = sortFlags;
                errorSettings.rendererConfiguration = RendererConfiguration.None;
                errorSettings.SetOverrideMaterial(m_ErrorMaterial, 0);
                context.DrawRenderers(visibleRenderers, ref errorSettings, filterSettings);
            }
        }

        private void SetupFrameRenderingConfiguration(out FrameRenderingConfiguration configuration, bool shadows, bool stereoEnabled, bool sceneViewCamera)
        {
            configuration = (stereoEnabled) ? FrameRenderingConfiguration.Stereo : FrameRenderingConfiguration.None;
            if (stereoEnabled && XRSettings.eyeTextureDesc.dimension == TextureDimension.Tex2DArray)
                m_IntermediateTextureArray = true;
            else
                m_IntermediateTextureArray = false;

            bool hdrEnabled = m_Asset.SupportsHDR && m_CurrCamera.allowHDR;
            bool intermediateTexture = m_CurrCamera.targetTexture != null || m_CurrCamera.cameraType == CameraType.SceneView ||
                m_Asset.RenderScale < 1.0f || hdrEnabled;

            m_ColorFormat = hdrEnabled ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            m_RequireCopyColor = false;
            m_DepthRenderBuffer = false;
            m_CameraPostProcessLayer = m_CurrCamera.GetComponent<PostProcessLayer>();

            bool msaaEnabled = m_CurrCamera.allowMSAA && m_Asset.MSAASampleCount > 1 && (m_CurrCamera.targetTexture == null || m_CurrCamera.targetTexture.antiAliasing > 1);

            // TODO: PostProcessing and SoftParticles are currently not support for VR
            bool postProcessEnabled = m_CameraPostProcessLayer != null && m_CameraPostProcessLayer.enabled && !stereoEnabled;
            m_RequireDepthTexture = m_Asset.RequireDepthTexture && !stereoEnabled;
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

            if (sceneViewCamera)
                m_RequireDepthTexture = true;

            if (shadows)
            {
                m_RequireDepthTexture = m_ShadowMapManager.IsScreenSpace;

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

            Rect cameraRect = m_CurrCamera.rect;
            if (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f || Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f))
                configuration |= FrameRenderingConfiguration.DefaultViewport;
            else
                intermediateTexture = true;

            if (intermediateTexture)
                configuration |= FrameRenderingConfiguration.IntermediateTexture;
        }

        private void SetupIntermediateResources(FrameRenderingConfiguration renderingConfig, ref ScriptableRenderContext context)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Setup Intermediate Resources");

            int msaaSamples = (m_IsOffscreenCamera) ? Math.Min(m_CurrCamera.targetTexture.antiAliasing, m_Asset.MSAASampleCount) : m_Asset.MSAASampleCount;
            msaaSamples = (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Msaa)) ? msaaSamples : 1;
            m_CurrCameraColorRT = BuiltinRenderTextureType.CameraTarget;

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture) || m_RequireDepthTexture)
                SetupIntermediateRenderTextures(cmd, renderingConfig, msaaSamples);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void SetupIntermediateRenderTextures(CommandBuffer cmd, FrameRenderingConfiguration renderingConfig, int msaaSamples)
        {
            RenderTextureDescriptor baseDesc;
            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                baseDesc = XRSettings.eyeTextureDesc;
            else
                baseDesc = new RenderTextureDescriptor(m_CurrCamera.pixelWidth, m_CurrCamera.pixelHeight);

            float renderScale = (m_CurrCamera.cameraType == CameraType.Game) ? m_Asset.RenderScale : 1.0f;
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
            if (!m_IsOffscreenCamera)
            {
                cmd.GetTemporaryRT(CameraRenderTargetID.color, colorRTDesc, FilterMode.Bilinear);
                m_CurrCameraColorRT = m_ColorRT;
            }

            // When BeforeTransparent PostFX is enabled and only one effect is in the stack we need to create a temp
            // color RT to blit the effect.
            if (m_RequireCopyColor)
                cmd.GetTemporaryRT(CameraRenderTargetID.copyColor, colorRTDesc, FilterMode.Point);
        }

        private void SetupShaderConstants(List<VisibleLight> visibleLights, ref ScriptableRenderContext context, ref LightData lightData, ref CullResults cullResults)
        {
            CommandBuffer cmd = CommandBufferPool.Get("SetupShaderConstants");
            m_LightManager.SetupShaderLightConstants(cmd, visibleLights, ref lightData, ref cullResults);
            SetShaderKeywords(cmd, ref lightData, visibleLights);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void SetupPerFrameShaderConstants()
        {
            // When glossy reflections are OFF in the shader we set a constant color to use as indirect specular
            SphericalHarmonicsL2 ambientSH = RenderSettings.ambientProbe;
            Color linearGlossyEnvColor = new Color(ambientSH[0, 0], ambientSH[1, 0], ambientSH[2, 0]) * RenderSettings.reflectionIntensity;
            Color glossyEnvColor = CoreUtils.ConvertLinearToActiveColorSpace(linearGlossyEnvColor);
            Shader.SetGlobalVector(PerFrameBuffer._GlossyEnvironmentColor, glossyEnvColor);

            // Used when subtractive mode is selected
            Shader.SetGlobalVector(PerFrameBuffer._SubtractiveShadowColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.subtractiveShadowColor));
        }

        private void SetShaderKeywords(CommandBuffer cmd, ref LightData lightData, List<VisibleLight> visibleLights)
        {
            int vertexLightsCount = lightData.totalAdditionalLightsCount - lightData.pixelAdditionalLightsCount;

            int mainLightIndex = lightData.mainLightIndex;
            //TIM: Not used in shader for V1 to reduce keywords
            CoreUtils.SetKeyword(cmd, "_MAIN_LIGHT_DIRECTIONAL", mainLightIndex == -1 || visibleLights[mainLightIndex].lightType == UnityEngine.LightType.Directional);
            //TIM: Not used in shader for V1 to reduce keywords
            CoreUtils.SetKeyword(cmd, "_MAIN_LIGHT_SPOT", mainLightIndex != -1 && visibleLights[mainLightIndex].lightType == UnityEngine.LightType.Spot);

            //TIM: Not used in shader for V1 to reduce keywords
            CoreUtils.SetKeyword(cmd, "_SHADOWS_ENABLED", lightData.shadowMapSampleType != LightShadows.None);

            //TIM: Not used in shader for V1 to reduce keywords
            CoreUtils.SetKeyword(cmd, "_MAIN_LIGHT_COOKIE", mainLightIndex != -1 && LightweightUtils.IsSupportedCookieType(visibleLights[mainLightIndex].lightType) && visibleLights[mainLightIndex].light.cookie != null);

            CoreUtils.SetKeyword(cmd, "_ADDITIONAL_LIGHTS", lightData.totalAdditionalLightsCount > 0);
            CoreUtils.SetKeyword(cmd, "_MIXED_LIGHTING_SUBTRACTIVE", m_LightManager.MixedLightingSetup == MixedLightingSetup.Subtractive);
            CoreUtils.SetKeyword(cmd, "_VERTEX_LIGHTS", vertexLightsCount > 0);
            CoreUtils.SetKeyword(cmd, "SOFTPARTICLES_ON", m_RequireDepthTexture && m_Asset.RequireSoftParticles);

            bool linearFogModeEnabled = false;
            bool exponentialFogModeEnabled = false;
            if (RenderSettings.fog)
            {
                if (RenderSettings.fogMode == FogMode.Linear)
                    linearFogModeEnabled = true;
                else
                    exponentialFogModeEnabled = true;
            }

            CoreUtils.SetKeyword(cmd, "FOG_LINEAR", linearFogModeEnabled);
            CoreUtils.SetKeyword(cmd, "FOG_EXP2", exponentialFogModeEnabled);
        }

        private void BeginForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig)
        {
            RenderTargetIdentifier colorRT = BuiltinRenderTextureType.CameraTarget;
            RenderTargetIdentifier depthRT = BuiltinRenderTextureType.None;

            StereoRendering.Start(ref context, renderingConfig, m_CurrCamera);

            CommandBuffer cmd = CommandBufferPool.Get("SetCameraRenderTarget");
            bool intermediateTexture = LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture);
            if (intermediateTexture)
            {
                if (!m_IsOffscreenCamera)
                    colorRT = m_CurrCameraColorRT;

                if (m_RequireDepthTexture)
                    depthRT = m_DepthRT;
            }

            ClearFlag clearFlag = ClearFlag.None;
            CameraClearFlags cameraClearFlags = m_CurrCamera.clearFlags;
            if (cameraClearFlags != CameraClearFlags.Nothing)
            {
                clearFlag |= ClearFlag.Depth;
                if (cameraClearFlags == CameraClearFlags.Color || cameraClearFlags == CameraClearFlags.Skybox)
                    clearFlag |= ClearFlag.Color;
            }

            SetRenderTarget(cmd, colorRT, depthRT, clearFlag);

            // If rendering to an intermediate RT we resolve viewport on blit due to offset not being supported
            // while rendering to a RT.
            if (!intermediateTexture && !LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.DefaultViewport))
                cmd.SetViewport(m_CurrCamera.pixelRect);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void EndForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig)
        {
            // No additional rendering needs to be done if this is an off screen rendering camera
            if (m_IsOffscreenCamera)
                return;

            var cmd = CommandBufferPool.Get("Blit");
            if (m_IntermediateTextureArray)
            {
                cmd.Blit(m_CurrCameraColorRT, BuiltinRenderTextureType.CameraTarget);
            }
            else if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture))
            {
                Material blitMaterial = m_BlitMaterial;
                if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
                    blitMaterial = null;

                // If PostProcessing is enabled, it is already blit to CameraTarget.
                if (!LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.PostProcess))
                    Blit(cmd, renderingConfig, m_CurrCameraColorRT, BuiltinRenderTextureType.CameraTarget, blitMaterial);
            }

            SetRenderTarget(cmd, BuiltinRenderTextureType.CameraTarget);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
            {
                context.StopMultiEye(m_CurrCamera);
                context.StereoEndRender(m_CurrCamera);
            }
        }

        RendererConfiguration GetRendererSettings(ref LightData lightData)
        {
            RendererConfiguration settings = RendererConfiguration.PerObjectReflectionProbes | RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe;
            if (lightData.totalAdditionalLightsCount > 0)
                settings |= RendererConfiguration.PerObjectLightIndices8;
            return settings;
        }

        private void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorRT, ClearFlag clearFlag = ClearFlag.None)
        {
            int depthSlice = (m_IntermediateTextureArray) ? -1 : 0;
            CoreUtils.SetRenderTarget(cmd, colorRT, clearFlag, CoreUtils.ConvertSRGBToActiveColorSpace(m_CurrCamera.backgroundColor), 0, CubemapFace.Unknown, depthSlice);
        }

        private void SetRenderTarget(CommandBuffer cmd, RenderTargetIdentifier colorRT, RenderTargetIdentifier depthRT, ClearFlag clearFlag = ClearFlag.None)
        {
            if (depthRT == BuiltinRenderTextureType.None || !m_DepthRenderBuffer)
            {
                SetRenderTarget(cmd, colorRT, clearFlag);
                return;
            }

            int depthSlice = (m_IntermediateTextureArray) ? -1 : 0;
            CoreUtils.SetRenderTarget(cmd, colorRT, depthRT, clearFlag, CoreUtils.ConvertSRGBToActiveColorSpace(m_CurrCamera.backgroundColor), 0, CubemapFace.Unknown, depthSlice);
        }

        private void RenderPostProcess(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier dest, bool opaqueOnly)
        {
            m_PostProcessRenderContext.Reset();
            m_PostProcessRenderContext.camera = m_CurrCamera;
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

        private void Blit(CommandBuffer cmd, FrameRenderingConfiguration renderingConfig, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, Material material = null)
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

                SetRenderTarget(cmd, destRT);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.SetViewport(m_CurrCamera.pixelRect);
                cmd.DrawMesh(m_BlitQuad, Matrix4x4.identity, material);
            }
        }

        private static void CopyTexture(CommandBuffer cmd, RenderTargetIdentifier sourceRT, RenderTargetIdentifier destRT, Material copyMaterial, CopyTextureSupport copyTextureSupport, bool forceBlit = false)
        {
            if (copyTextureSupport != CopyTextureSupport.None && !forceBlit)
                cmd.CopyTexture(sourceRT, destRT);
            else
                cmd.Blit(sourceRT, destRT, copyMaterial);
        }
    }
}
