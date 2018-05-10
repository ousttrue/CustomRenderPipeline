using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.LightweightPipeline;
using UnityEngine.Rendering;
using UnityEngine.XR;


namespace CustomRP
{
    public class CustomRenderPipeline : RenderPipeline
    {
        private readonly CustomRenderPipelineAsset m_Asset;

        private RenderTargetIdentifier m_ColorRT;
        private RenderTargetIdentifier m_CopyColorRT;
        private RenderTargetIdentifier m_DepthRT;
        private RenderTargetIdentifier m_CopyDepth;
        private RenderTargetIdentifier m_Color;

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

        private CameraComparer m_CameraComparer = new CameraComparer();

        private Material m_ErrorMaterial;

        private LightManager m_LightManager;
        private ShadowManager m_ShadowManager;
        private TextureUtil m_TextureUtil;
        private CullingUtil m_CullingUtil;

        public CustomRenderPipeline(CustomRenderPipelineAsset asset)
        {
            m_Asset = asset;

            SceneViewUtil.SetRenderingFeatures();

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

            // Let engine know we have MSAA on for cases where we support MSAA backbuffer
            if (QualitySettings.antiAliasing != m_Asset.MSAASampleCount)
                QualitySettings.antiAliasing = m_Asset.MSAASampleCount;

            Shader.globalRenderPipeline = "LightweightPipeline";

            m_ErrorMaterial = CoreUtils.CreateEngineMaterial("Hidden/InternalErrorShader");

            m_LightManager = new LightManager(asset);
            m_ShadowManager = new ShadowManager(asset);
            m_TextureUtil = new TextureUtil(asset);
            m_CullingUtil = new CullingUtil();
        }

        public override void Dispose()
        {
            base.Dispose();
            Shader.globalRenderPipeline = "";

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();

            CoreUtils.Destroy(m_ErrorMaterial);
            m_TextureUtil.Dispose();

#if UNITY_EDITOR
            SceneViewDrawMode.ResetDrawMode();
#endif
        }


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
                bool IsOffscreenCamera = camera.targetTexture != null && camera.cameraType != CameraType.SceneView;

                Render(ref context, camera, sceneViewCamera, stereoEnabled, IsOffscreenCamera);

                context.Submit();

                m_ShadowManager.ReleaseRenderTarget();
            }
        }

        void Render(ref ScriptableRenderContext context, Camera camera, bool sceneViewCamera, bool stereoEnabled, bool IsOffscreenCamera)
        {
            ///
            /// culling
            ///
            if (!m_CullingUtil.Cull(camera, ref context, sceneViewCamera, stereoEnabled, m_ShadowManager.MaxShadowDistance))
            {
                return;
            }
            var visibleLights = m_CullingUtil.CullResults.visibleLights;

            ///
            /// setup lights & shadows
            ///
            LightData lightData;
            m_LightManager.InitializeLightData(visibleLights, out lightData, camera);

            bool shadows = m_ShadowManager.ShadowPass(visibleLights, ref context, ref lightData,
                m_CullingUtil.CullResults, m_LightManager.GetLightUnsortedIndex(lightData.mainLightIndex), camera.backgroundColor);

            ///
            /// setup
            ///
            FrameRenderingConfiguration frameRenderingConfiguration;
            m_TextureUtil.SetupFrameRenderingConfiguration(out frameRenderingConfiguration, shadows, stereoEnabled, sceneViewCamera, camera, m_ShadowManager);
            m_TextureUtil.SetupIntermediateResources(frameRenderingConfiguration, ref context, camera, IsOffscreenCamera, m_ColorRT);

            // SetupCameraProperties does the following:
            // Setup Camera RenderTarget and Viewport
            // VR Camera Setup and SINGLE_PASS_STEREO props
            // Setup camera view, proj and their inv matrices.
            // Setup properties: _WorldSpaceCameraPos, _ProjectionParams, _ScreenParams, _ZBufferParams, unity_OrthoParams
            // Setup camera world clip planes props
            // setup HDR keyword
            // Setup global time properties (_Time, _SinTime, _CosTime)
            context.SetupCameraProperties(camera, stereoEnabled);

            if (LightweightUtils.HasFlag(frameRenderingConfiguration, FrameRenderingConfiguration.DepthPrePass))
                DepthPass(ref context, frameRenderingConfiguration, m_CullingUtil.CullResults.visibleRenderers, camera);

            if (shadows)
                m_ShadowManager.ShadowCollectPass(visibleLights, ref context, ref lightData, frameRenderingConfiguration, camera);
            else
                m_ShadowManager.SmallShadowBuffer(ref context);

            ///
            /// forward pass
            ///
            /// * Clear
            /// * Opaque
            /// * AfterOpaque
            /// * Transparent
            /// * AfterTransparent
            /// * PostEffect
            ///
            ForwardPass(visibleLights, frameRenderingConfiguration, ref context, ref lightData, stereoEnabled, m_CullingUtil.CullResults, camera, IsOffscreenCamera);

            var cmd = CommandBufferPool.Get("");
            cmd.name = "After Camera Render";
#if UNITY_EDITOR
            if (sceneViewCamera)
                m_TextureUtil.CopyTexture(cmd, CameraRenderTargetID.depth, BuiltinRenderTextureType.CameraTarget, true);
#endif
            cmd.ReleaseTemporaryRT(m_ShadowManager.ScreenSpaceShadowMapRTID);
            cmd.ReleaseTemporaryRT(CameraRenderTargetID.depthCopy);
            cmd.ReleaseTemporaryRT(CameraRenderTargetID.depth);
            cmd.ReleaseTemporaryRT(CameraRenderTargetID.color);
            cmd.ReleaseTemporaryRT(CameraRenderTargetID.copyColor);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void DepthPass(ref ScriptableRenderContext context, FrameRenderingConfiguration frameRenderingConfiguration, FilterResults visibleRenderers, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Depth Prepass");
            m_TextureUtil.SetRenderTarget(cmd, m_DepthRT, camera.backgroundColor, ClearFlag.Depth);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            var opaqueDrawSettings = new DrawRendererSettings(camera, m_DepthPrepass);
            opaqueDrawSettings.sorting.flags = SortFlags.CommonOpaque;

            var opaqueFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.opaque
            };

            StereoRendering.Start(ref context, frameRenderingConfiguration, camera);

            context.DrawRenderers(visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);

            StereoRendering.Stop(ref context, frameRenderingConfiguration, camera);
        }

        private void ForwardPass(List<VisibleLight> visibleLights, FrameRenderingConfiguration frameRenderingConfiguration, ref ScriptableRenderContext context, ref LightData lightData, bool stereoEnabled, CullResults cullResults, Camera camera, bool IsOffscreenCamera)
        {
            SetupShaderConstants(visibleLights, ref context, ref lightData, ref cullResults);

            RendererConfiguration rendererSettings = GetRendererSettings(ref lightData);

            BeginForwardRendering(ref context, frameRenderingConfiguration, camera, IsOffscreenCamera);
            RenderOpaques(ref context, rendererSettings, cullResults.visibleRenderers, camera);
            AfterOpaque(ref context, frameRenderingConfiguration, camera);
            RenderTransparents(ref context, rendererSettings, cullResults.visibleRenderers, camera);
            AfterTransparent(ref context, frameRenderingConfiguration, camera);
            EndForwardRendering(ref context, frameRenderingConfiguration, camera, IsOffscreenCamera);
        }

        private void RenderOpaques(ref ScriptableRenderContext context, RendererConfiguration settings, FilterResults visibleRenderers, Camera camera)
        {
            var opaqueDrawSettings = new DrawRendererSettings(camera, m_LitPassName);
            opaqueDrawSettings.SetShaderPassName(1, m_UnlitPassName);
            opaqueDrawSettings.sorting.flags = SortFlags.CommonOpaque;
            opaqueDrawSettings.rendererConfiguration = settings;

            var opaqueFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.opaque
            };

            context.DrawRenderers(visibleRenderers, ref opaqueDrawSettings, opaqueFilterSettings);

            // Render objects that did not match any shader pass with error shader
            RenderObjectsWithError(ref context, opaqueFilterSettings, SortFlags.None, visibleRenderers, camera);

            if (camera.clearFlags == CameraClearFlags.Skybox)
                context.DrawSkybox(camera);
        }

        private void AfterOpaque(ref ScriptableRenderContext context, FrameRenderingConfiguration config, Camera camera)
        {
            if (!m_TextureUtil.RequireDepthTexture)
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
                if (m_TextureUtil.RequireCopyColor)
                {
                    m_TextureUtil.RenderPostProcess(cmd, m_TextureUtil.CurrCameraColorRT, m_CopyColorRT, true, camera);
                    cmd.Blit(m_CopyColorRT, m_TextureUtil.CurrCameraColorRT);
                }
                else
                    m_TextureUtil.RenderPostProcess(cmd, m_TextureUtil.CurrCameraColorRT, m_TextureUtil.CurrCameraColorRT, true, camera);

                setRenderTarget = true;
                m_TextureUtil.SetRenderTarget(cmd, m_TextureUtil.CurrCameraColorRT, m_DepthRT, camera.backgroundColor);
            }

            if (LightweightUtils.HasFlag(config, FrameRenderingConfiguration.DepthCopy))
            {
                m_TextureUtil.CopyTexture(cmd, m_DepthRT, m_CopyDepth);
                depthRT = m_CopyDepth;
                setRenderTarget = true;
            }

            if (setRenderTarget)
                m_TextureUtil.SetRenderTarget(cmd, m_TextureUtil.CurrCameraColorRT, depthRT, camera.backgroundColor);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void RenderTransparents(ref ScriptableRenderContext context, RendererConfiguration config, FilterResults visibleRenderers, Camera camera)
        {
            var transparentSettings = new DrawRendererSettings(camera, m_LitPassName);
            transparentSettings.SetShaderPassName(1, m_UnlitPassName);
            transparentSettings.sorting.flags = SortFlags.CommonTransparent;
            transparentSettings.rendererConfiguration = config;

            var transparentFilterSettings = new FilterRenderersSettings(true)
            {
                renderQueueRange = RenderQueueRange.transparent
            };

            context.DrawRenderers(visibleRenderers, ref transparentSettings, transparentFilterSettings);

            // Render objects that did not match any shader pass with error shader
            RenderObjectsWithError(ref context, transparentFilterSettings, SortFlags.None, visibleRenderers, camera);
        }

        private void AfterTransparent(ref ScriptableRenderContext context, FrameRenderingConfiguration config, Camera camera)
        {
            if (!LightweightUtils.HasFlag(config, FrameRenderingConfiguration.PostProcess))
                return;

            CommandBuffer cmd = CommandBufferPool.Get("After Transparent");
            m_TextureUtil.RenderPostProcess(cmd, m_TextureUtil.CurrCameraColorRT, BuiltinRenderTextureType.CameraTarget, false, camera);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD"), System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void RenderObjectsWithError(ref ScriptableRenderContext context, FilterRenderersSettings filterSettings, SortFlags sortFlags, FilterResults visibleRenderers, Camera camera)
        {
            if (m_ErrorMaterial != null)
            {
                DrawRendererSettings errorSettings = new DrawRendererSettings(camera, s_LegacyPassNames[0]);
                for (int i = 1; i < s_LegacyPassNames.Length; ++i)
                    errorSettings.SetShaderPassName(i, s_LegacyPassNames[i]);

                errorSettings.sorting.flags = sortFlags;
                errorSettings.rendererConfiguration = RendererConfiguration.None;
                errorSettings.SetOverrideMaterial(m_ErrorMaterial, 0);
                context.DrawRenderers(visibleRenderers, ref errorSettings, filterSettings);
            }
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
            CoreUtils.SetKeyword(cmd, "SOFTPARTICLES_ON", m_TextureUtil.RequireDepthTexture && m_Asset.RequireSoftParticles);

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

        private void BeginForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig, Camera camera, bool IsOffscreenCamera)
        {
            RenderTargetIdentifier colorRT = BuiltinRenderTextureType.CameraTarget;
            RenderTargetIdentifier depthRT = BuiltinRenderTextureType.None;

            StereoRendering.Start(ref context, renderingConfig, camera);

            CommandBuffer cmd = CommandBufferPool.Get("SetCameraRenderTarget");
            bool intermediateTexture = LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.IntermediateTexture);
            if (intermediateTexture)
            {
                if (!IsOffscreenCamera)
                    colorRT = m_TextureUtil.CurrCameraColorRT;

                if (m_TextureUtil.RequireDepthTexture)
                    depthRT = m_DepthRT;
            }

            ClearFlag clearFlag = ClearFlag.None;
            CameraClearFlags cameraClearFlags = camera.clearFlags;
            if (cameraClearFlags != CameraClearFlags.Nothing)
            {
                clearFlag |= ClearFlag.Depth;
                if (cameraClearFlags == CameraClearFlags.Color || cameraClearFlags == CameraClearFlags.Skybox)
                    clearFlag |= ClearFlag.Color;
            }

            m_TextureUtil.SetRenderTarget(cmd, colorRT, depthRT, camera.backgroundColor, clearFlag);

            // If rendering to an intermediate RT we resolve viewport on blit due to offset not being supported
            // while rendering to a RT.
            if (!intermediateTexture && !LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.DefaultViewport))
                cmd.SetViewport(camera.pixelRect);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void EndForwardRendering(ref ScriptableRenderContext context, FrameRenderingConfiguration renderingConfig, Camera camera, bool IsOffscreenCamera)
        {
            // No additional rendering needs to be done if this is an off screen rendering camera
            if (IsOffscreenCamera)
                return;

            m_TextureUtil.Blit(context, m_TextureUtil.CurrCameraColorRT, renderingConfig, camera);

            if (LightweightUtils.HasFlag(renderingConfig, FrameRenderingConfiguration.Stereo))
            {
                context.StopMultiEye(camera);
                context.StereoEndRender(camera);
            }
        }

        RendererConfiguration GetRendererSettings(ref LightData lightData)
        {
            RendererConfiguration settings = RendererConfiguration.PerObjectReflectionProbes | RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe;
            if (lightData.totalAdditionalLightsCount > 0)
                settings |= RendererConfiguration.PerObjectLightIndices8;
            return settings;
        }
    }
}
