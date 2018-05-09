using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.LightweightPipeline;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace CustomRP
{
    public class ShadowManager
    {
        private readonly CustomRenderPipelineAsset m_Asset;

        private const int kMaxCascades = 4;
        private int m_ShadowCasterCascadesCount;
        private int m_ShadowMapRTID;

        private int m_ScreenSpaceShadowMapRTID;
        public int ScreenSpaceShadowMapRTID
        {
            get
            {
                return m_ScreenSpaceShadowMapRTID;
            }
        }

        private Matrix4x4[] m_ShadowMatrices = new Matrix4x4[kMaxCascades + 1];

        private RenderTexture m_ShadowMapRT;
        private RenderTargetIdentifier m_ScreenSpaceShadowMapRT;

        private const int kShadowBufferBits = 16;
        private Vector4[] m_DirectionalShadowSplitDistances = new Vector4[kMaxCascades];
        private Vector4 m_DirectionalShadowSplitRadii;

        private UnityEngine.Experimental.Rendering.LightweightPipeline.ShadowSettings m_ShadowSettings = UnityEngine.Experimental.Rendering.LightweightPipeline.ShadowSettings.Default;
        public float MaxShadowDistance
        {
            get
            {
                return m_ShadowSettings.maxShadowDistance;
            }
        }
        public bool IsScreenSpace
        {
            get
            {
                return m_ShadowSettings.screenSpace;
            }
        }

        private ShadowSliceData[] m_ShadowSlices = new ShadowSliceData[kMaxCascades];

        private Material m_ScreenSpaceShadowsMaterial;

        public ShadowManager(CustomRenderPipelineAsset asset)
        {
            m_Asset = asset;
            m_ScreenSpaceShadowsMaterial = CoreUtils.CreateEngineMaterial(m_Asset.ScreenSpaceShadowShader);

            BuildShadowSettings();

            ShadowConstantBuffer._WorldToShadow = Shader.PropertyToID("_WorldToShadow");
            ShadowConstantBuffer._ShadowData = Shader.PropertyToID("_ShadowData");
            ShadowConstantBuffer._DirShadowSplitSpheres = Shader.PropertyToID("_DirShadowSplitSpheres");
            ShadowConstantBuffer._DirShadowSplitSphereRadii = Shader.PropertyToID("_DirShadowSplitSphereRadii");
            ShadowConstantBuffer._ShadowOffset0 = Shader.PropertyToID("_ShadowOffset0");
            ShadowConstantBuffer._ShadowOffset1 = Shader.PropertyToID("_ShadowOffset1");
            ShadowConstantBuffer._ShadowOffset2 = Shader.PropertyToID("_ShadowOffset2");
            ShadowConstantBuffer._ShadowOffset3 = Shader.PropertyToID("_ShadowOffset3");
            ShadowConstantBuffer._ShadowmapSize = Shader.PropertyToID("_ShadowmapSize");

            m_ShadowMapRTID = Shader.PropertyToID("_ShadowMap");
            m_ScreenSpaceShadowMapRTID = Shader.PropertyToID("_ScreenSpaceShadowMap");

            m_ScreenSpaceShadowMapRT = new RenderTargetIdentifier(m_ScreenSpaceShadowMapRTID);

            for (int i = 0; i < kMaxCascades; ++i)
                m_DirectionalShadowSplitDistances[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);
            m_DirectionalShadowSplitRadii = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);
        }

        private void BuildShadowSettings()
        {
            m_ShadowSettings = UnityEngine.Experimental.Rendering.LightweightPipeline.ShadowSettings.Default;
            m_ShadowSettings.screenSpace = SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2;
            m_ShadowSettings.directionalLightCascadeCount = (m_ShadowSettings.screenSpace) ? m_Asset.CascadeCount : 1;

            m_ShadowSettings.shadowAtlasWidth = m_Asset.ShadowAtlasResolution;
            m_ShadowSettings.shadowAtlasHeight = m_Asset.ShadowAtlasResolution;
            m_ShadowSettings.maxShadowDistance = m_Asset.ShadowDistance;
            m_ShadowSettings.shadowmapTextureFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Shadowmap)
                ? RenderTextureFormat.Shadowmap
                : RenderTextureFormat.Depth;

            m_ShadowSettings.screenspaceShadowmapTextureFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                    ? RenderTextureFormat.R8
                    : RenderTextureFormat.ARGB32;

            switch (m_ShadowSettings.directionalLightCascadeCount)
            {
                case 1:
                    m_ShadowSettings.directionalLightCascades = new Vector3(1.0f, 0.0f, 0.0f);
                    break;

                case 2:
                    m_ShadowSettings.directionalLightCascades = new Vector3(m_Asset.Cascade2Split, 1.0f, 0.0f);
                    break;

                default:
                    m_ShadowSettings.directionalLightCascades = m_Asset.Cascade4Split;
                    break;
            }
        }

        public bool ShadowPass(List<VisibleLight> visibleLights, ref ScriptableRenderContext context, ref LightData lightData, CullResults cullResults, int unsortedIndex, Color backgroundColor)
        {
            m_ShadowMapRT = null;
            if (m_Asset.AreShadowsEnabled() && lightData.mainLightIndex != -1)
            {
                VisibleLight mainLight = visibleLights[lightData.mainLightIndex];

                if (mainLight.light.shadows != LightShadows.None)
                {
                    if (!LightweightUtils.IsSupportedShadowType(mainLight.lightType))
                    {
                        Debug.LogWarning("Only directional and spot shadows are supported by LightweightPipeline.");
                        return false;
                    }

                    // There's no way to map shadow light indices. We need to pass in the original unsorted index.
                    // If no additional lights then no light sorting is performed and the indices match.
                    int shadowOriginalIndex = (lightData.totalAdditionalLightsCount > 0) ? unsortedIndex : lightData.mainLightIndex;
                    bool shadowsRendered = RenderShadows(ref cullResults, ref mainLight, shadowOriginalIndex, ref context, backgroundColor);
                    if (shadowsRendered)
                    {
                        lightData.shadowMapSampleType = (m_Asset.ShadowSetting != ShadowType.SOFT_SHADOWS) ? LightShadows.Hard : mainLight.light.shadows;

                        // In order to avoid shader variants explosion we only do hard shadows when sampling shadowmap in the lit pass.
                        // GLES2 platform is forced to hard single cascade shadows.
                        if (!m_ShadowSettings.screenSpace)
                            lightData.shadowMapSampleType = LightShadows.Hard;
                    }
                    else
                    {
                        lightData.shadowMapSampleType = LightShadows.None;
                    }

                    return shadowsRendered;
                }
            }

            return false;
        }

        private bool RenderShadows(ref CullResults cullResults, ref VisibleLight shadowLight, int shadowLightIndex, ref ScriptableRenderContext context, Color backgroundColor)
        {
            m_ShadowCasterCascadesCount = m_ShadowSettings.directionalLightCascadeCount;

            if (shadowLight.lightType == UnityEngine.LightType.Spot)
                m_ShadowCasterCascadesCount = 1;

            int shadowResolution = GetMaxTileResolutionInAtlas(m_ShadowSettings.shadowAtlasWidth, m_ShadowSettings.shadowAtlasHeight, m_ShadowCasterCascadesCount);

            Bounds bounds;
            if (!cullResults.GetShadowCasterBounds(shadowLightIndex, out bounds))
                return false;

            float shadowNearPlane = m_Asset.ShadowNearOffset;

            Matrix4x4 view, proj;
            var settings = new DrawShadowsSettings(cullResults, shadowLightIndex);
            bool success = false;

            var cmd = CommandBufferPool.Get("Prepare Shadowmap");
            RenderTextureDescriptor shadowmapDescriptor = new RenderTextureDescriptor(m_ShadowSettings.shadowAtlasWidth,
                m_ShadowSettings.shadowAtlasHeight, m_ShadowSettings.shadowmapTextureFormat, kShadowBufferBits);
            shadowmapDescriptor.shadowSamplingMode = ShadowSamplingMode.CompareDepths;
            m_ShadowMapRT = RenderTexture.GetTemporary(shadowmapDescriptor);
            m_ShadowMapRT.filterMode = FilterMode.Bilinear;
            m_ShadowMapRT.wrapMode = TextureWrapMode.Clamp;

            // LightweightPipeline.SetRenderTarget is meant to be used with camera targets, not shadowmaps
            CoreUtils.SetRenderTarget(cmd, m_ShadowMapRT, ClearFlag.Depth, CoreUtils.ConvertSRGBToActiveColorSpace(backgroundColor));

            if (shadowLight.lightType == UnityEngine.LightType.Spot)
            {
                success = cullResults.ComputeSpotShadowMatricesAndCullingPrimitives(shadowLightIndex, out view, out proj,
                        out settings.splitData);

                if (success)
                {
                    SetupShadowCasterConstants(cmd, ref shadowLight, proj, shadowResolution);
                    SetupShadowSliceTransform(0, shadowResolution, proj, view);
                    RenderShadowSlice(cmd, ref context, 0, proj, view, settings);
                }
            }
            else if (shadowLight.lightType == UnityEngine.LightType.Directional)
            {
                for (int cascadeIdx = 0; cascadeIdx < m_ShadowCasterCascadesCount; ++cascadeIdx)
                {
                    success = cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(shadowLightIndex,
                            cascadeIdx, m_ShadowCasterCascadesCount, m_ShadowSettings.directionalLightCascades, shadowResolution, shadowNearPlane, out view, out proj,
                            out settings.splitData);

                    float cullingSphereRadius = settings.splitData.cullingSphere.w;
                    m_DirectionalShadowSplitDistances[cascadeIdx] = settings.splitData.cullingSphere;
                    m_DirectionalShadowSplitRadii[cascadeIdx] = cullingSphereRadius * cullingSphereRadius;

                    if (!success)
                        break;

                    SetupShadowCasterConstants(cmd, ref shadowLight, proj, shadowResolution);
                    SetupShadowSliceTransform(cascadeIdx, shadowResolution, proj, view);
                    RenderShadowSlice(cmd, ref context, cascadeIdx, proj, view, settings);
                }
            }
            else
            {
                Debug.LogWarning("Only spot and directional shadow casters are supported in lightweight pipeline");
            }

            if (success)
                SetupShadowReceiverConstants(cmd, shadowLight, ref context);

            CommandBufferPool.Release(cmd);
            return success;
        }

        private void SetupShadowCasterConstants(CommandBuffer cmd, ref VisibleLight visibleLight, Matrix4x4 proj, float cascadeResolution)
        {
            Light light = visibleLight.light;
            float bias = 0.0f;
            float normalBias = 0.0f;

            // Use same kernel radius as built-in pipeline so we can achieve same bias results
            // with the default light bias parameters.
            const float kernelRadius = 3.65f;

            if (visibleLight.lightType == UnityEngine.LightType.Directional)
            {
                // Scale bias by cascade's world space depth range.
                // Directional shadow lights have orthogonal projection.
                // proj.m22 = -2 / (far - near) since the projection's depth range is [-1.0, 1.0]
                // In order to be correct we should multiply bias by 0.5 but this introducing aliasing along cascades more visible.
                float sign = (SystemInfo.usesReversedZBuffer) ? 1.0f : -1.0f;
                bias = light.shadowBias * proj.m22 * sign;

                // Currently only square POT cascades resolutions are used.
                // We scale normalBias
                double frustumWidth = 2.0 / (double)proj.m00;
                double frustumHeight = 2.0 / (double)proj.m11;
                float texelSizeX = (float)(frustumWidth / (double)cascadeResolution);
                float texelSizeY = (float)(frustumHeight / (double)cascadeResolution);
                float texelSize = Mathf.Max(texelSizeX, texelSizeY);

                // Since we are applying normal bias on caster side we want an inset normal offset
                // thus we use a negative normal bias.
                normalBias = -light.shadowNormalBias * texelSize * kernelRadius;
            }
            else if (visibleLight.lightType == UnityEngine.LightType.Spot)
            {
                float sign = (SystemInfo.usesReversedZBuffer) ? -1.0f : 1.0f;
                bias = light.shadowBias * sign;
                normalBias = 0.0f;
            }
            else
            {
                Debug.LogWarning("Only spot and directional shadow casters are supported in lightweight pipeline");
            }

            Vector3 lightDirection = -visibleLight.localToWorld.GetColumn(2);
            cmd.SetGlobalVector("_ShadowBias", new Vector4(bias, normalBias, 0.0f, 0.0f));
            cmd.SetGlobalVector("_LightDirection", new Vector4(lightDirection.x, lightDirection.y, lightDirection.z, 0.0f));
        }

        private int GetMaxTileResolutionInAtlas(int atlasWidth, int atlasHeight, int tileCount)
        {
            int resolution = Mathf.Min(atlasWidth, atlasHeight);
            if (tileCount > Mathf.Log(resolution))
            {
                Debug.LogError(
                    String.Format(
                        "Cannot fit {0} tiles into current shadowmap atlas of size ({1}, {2}). ShadowMap Resolution set to zero.",
                        tileCount, atlasWidth, atlasHeight));
                return 0;
            }

            int currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
            while (currentTileCount < tileCount)
            {
                resolution = resolution >> 1;
                currentTileCount = atlasWidth / resolution * atlasHeight / resolution;
            }
            return resolution;
        }

        private void SetupShadowSliceTransform(int cascadeIndex, int shadowResolution, Matrix4x4 proj, Matrix4x4 view)
        {
            if (cascadeIndex >= kMaxCascades)
            {
                Debug.LogError(String.Format("{0} is an invalid cascade index. Maximum of {1} cascades", cascadeIndex, kMaxCascades));
                return;
            }

            int atlasX = (cascadeIndex % 2) * shadowResolution;
            int atlasY = (cascadeIndex / 2) * shadowResolution;
            float atlasWidth = (float)m_ShadowSettings.shadowAtlasWidth;
            float atlasHeight = (float)m_ShadowSettings.shadowAtlasHeight;

            // Currently CullResults ComputeDirectionalShadowMatricesAndCullingPrimitives doesn't
            // apply z reversal to projection matrix. We need to do it manually here.
            if (SystemInfo.usesReversedZBuffer)
            {
                proj.m20 = -proj.m20;
                proj.m21 = -proj.m21;
                proj.m22 = -proj.m22;
                proj.m23 = -proj.m23;
            }

            Matrix4x4 worldToShadow = proj * view;

            var textureScaleAndBias = Matrix4x4.identity;
            textureScaleAndBias.m00 = 0.5f;
            textureScaleAndBias.m11 = 0.5f;
            textureScaleAndBias.m22 = 0.5f;
            textureScaleAndBias.m03 = 0.5f;
            textureScaleAndBias.m23 = 0.5f;
            textureScaleAndBias.m13 = 0.5f;

            // Apply texture scale and offset to save a MAD in shader.
            worldToShadow = textureScaleAndBias * worldToShadow;

            var cascadeAtlas = Matrix4x4.identity;
            cascadeAtlas.m00 = (float)shadowResolution / atlasWidth;
            cascadeAtlas.m11 = (float)shadowResolution / atlasHeight;
            cascadeAtlas.m03 = (float)atlasX / atlasWidth;
            cascadeAtlas.m13 = (float)atlasY / atlasHeight;

            // Apply cascade scale and offset
            worldToShadow = cascadeAtlas * worldToShadow;

            m_ShadowSlices[cascadeIndex].atlasX = atlasX;
            m_ShadowSlices[cascadeIndex].atlasY = atlasY;
            m_ShadowSlices[cascadeIndex].shadowResolution = shadowResolution;
            m_ShadowSlices[cascadeIndex].shadowTransform = worldToShadow;
        }

        private void RenderShadowSlice(CommandBuffer cmd, ref ScriptableRenderContext context, int cascadeIndex,
            Matrix4x4 proj, Matrix4x4 view, DrawShadowsSettings settings)
        {
            cmd.SetViewport(new Rect(m_ShadowSlices[cascadeIndex].atlasX, m_ShadowSlices[cascadeIndex].atlasY,
                    m_ShadowSlices[cascadeIndex].shadowResolution, m_ShadowSlices[cascadeIndex].shadowResolution));
            cmd.EnableScissorRect(new Rect(m_ShadowSlices[cascadeIndex].atlasX + 4, m_ShadowSlices[cascadeIndex].atlasY + 4,
                m_ShadowSlices[cascadeIndex].shadowResolution - 8, m_ShadowSlices[cascadeIndex].shadowResolution - 8));

            cmd.SetViewProjectionMatrices(view, proj);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            context.DrawShadows(ref settings);
            cmd.DisableScissorRect();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
        }

        public void ShadowCollectPass(List<VisibleLight> visibleLights, ref ScriptableRenderContext context, ref LightData lightData, FrameRenderingConfiguration frameRenderingConfiguration,
            Camera currentCamera)
        {
            if (!m_ShadowSettings.screenSpace)
                return;

            CommandBuffer cmd = CommandBufferPool.Get("Collect Shadows");

            SetShadowCollectPassKeywords(cmd, visibleLights[lightData.mainLightIndex], ref lightData);

            // TODO: Support RenderScale for the SSSM target.  Should probably move allocation elsewhere, or at
            // least propogate RenderTextureDescriptor generation
            if (LightweightUtils.HasFlag(frameRenderingConfiguration, FrameRenderingConfiguration.Stereo))
            {
                var desc = XRSettings.eyeTextureDesc;
                desc.depthBufferBits = 0;
                desc.colorFormat = m_ShadowSettings.screenspaceShadowmapTextureFormat;
                cmd.GetTemporaryRT(m_ScreenSpaceShadowMapRTID, desc, FilterMode.Bilinear);
            }
            else
            {
                cmd.GetTemporaryRT(m_ScreenSpaceShadowMapRTID, currentCamera.pixelWidth, currentCamera.pixelHeight, 0, FilterMode.Bilinear, m_ShadowSettings.screenspaceShadowmapTextureFormat);
            }

            // Note: The source isn't actually 'used', but there's an engine peculiarity (bug) that
            // doesn't like null sources when trying to determine a stereo-ized blit.  So for proper
            // stereo functionality, we use the screen-space shadow map as the source (until we have
            // a better solution).
            // An alternative would be DrawProcedural, but that would require further changes in the shader.
            cmd.Blit(m_ScreenSpaceShadowMapRT, m_ScreenSpaceShadowMapRT, m_ScreenSpaceShadowsMaterial);

            StereoRendering.Start(ref context, frameRenderingConfiguration, currentCamera);

            context.ExecuteCommandBuffer(cmd);

            StereoRendering.Stop(ref context, frameRenderingConfiguration, currentCamera);

            CommandBufferPool.Release(cmd);
        }

        private void SetShadowCollectPassKeywords(CommandBuffer cmd, VisibleLight shadowLight, ref LightData lightData)
        {
            bool cascadeShadows = shadowLight.lightType == UnityEngine.LightType.Directional && m_Asset.CascadeCount > 1;
            CoreUtils.SetKeyword(cmd, "_SHADOWS_SOFT", lightData.shadowMapSampleType == LightShadows.Soft);
            CoreUtils.SetKeyword(cmd, "_SHADOWS_CASCADE", cascadeShadows);
        }

        public void SmallShadowBuffer(ref ScriptableRenderContext context)
        {
            var setRT = CommandBufferPool.Get("Generate Small Shadow Buffer");
            if (m_ShadowSettings.screenSpace)
                setRT.GetTemporaryRT(m_ScreenSpaceShadowMapRTID, 4, 4, 0, FilterMode.Bilinear, m_ShadowSettings.screenspaceShadowmapTextureFormat);
            else
                setRT.GetTemporaryRT(m_ShadowMapRTID, 4, 4, 0, FilterMode.Bilinear, m_ShadowSettings.shadowmapTextureFormat);
            setRT.Blit(Texture2D.whiteTexture, m_ScreenSpaceShadowMapRT);
            context.ExecuteCommandBuffer(setRT);
        }

        public void ReleaseRenderTarget()
        {
            if (m_ShadowMapRT)
            {
                RenderTexture.ReleaseTemporary(m_ShadowMapRT);
                m_ShadowMapRT = null;
            }
        }

        private void SetupShadowReceiverConstants(CommandBuffer cmd, VisibleLight shadowLight, ref ScriptableRenderContext context)
        {
            Light light = shadowLight.light;

            int cascadeCount = m_ShadowCasterCascadesCount;
            for (int i = 0; i < kMaxCascades; ++i)
                m_ShadowMatrices[i] = (cascadeCount >= i) ? m_ShadowSlices[i].shadowTransform : Matrix4x4.identity;

            // We setup and additional a no-op WorldToShadow matrix in the last index
            // because the ComputeCascadeIndex function in Shadows.hlsl can return an index
            // out of bounds. (position not inside any cascade) and we want to avoid branching
            Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
            noOpShadowMatrix.m33 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
            m_ShadowMatrices[kMaxCascades] = noOpShadowMatrix;

            float invShadowResolution = 1.0f / m_Asset.ShadowAtlasResolution;
            float invHalfShadowResolution = 0.5f * invShadowResolution;
            cmd.Clear();
            cmd.SetGlobalTexture(m_ShadowMapRTID, m_ShadowMapRT);
            cmd.SetGlobalMatrixArray(ShadowConstantBuffer._WorldToShadow, m_ShadowMatrices);
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowData, new Vector4(light.shadowStrength, 0.0f, 0.0f, 0.0f));
            cmd.SetGlobalVectorArray(ShadowConstantBuffer._DirShadowSplitSpheres, m_DirectionalShadowSplitDistances);
            cmd.SetGlobalVector(ShadowConstantBuffer._DirShadowSplitSphereRadii, m_DirectionalShadowSplitRadii);
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowOffset0, new Vector4(-invHalfShadowResolution, -invHalfShadowResolution, 0.0f, 0.0f));
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowOffset1, new Vector4(invHalfShadowResolution, -invHalfShadowResolution, 0.0f, 0.0f));
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowOffset2, new Vector4(-invHalfShadowResolution, invHalfShadowResolution, 0.0f, 0.0f));
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowOffset3, new Vector4(invHalfShadowResolution, invHalfShadowResolution, 0.0f, 0.0f));
            cmd.SetGlobalVector(ShadowConstantBuffer._ShadowmapSize, new Vector4(invShadowResolution, invShadowResolution, m_Asset.ShadowAtlasResolution, m_Asset.ShadowAtlasResolution));
            context.ExecuteCommandBuffer(cmd);
        }

    }
}
