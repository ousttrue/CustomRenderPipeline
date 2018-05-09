using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.LightweightPipeline;
using UnityEngine.Rendering;

namespace CustomRP
{
    public class LightManager
    {
        private readonly CustomRenderPipelineAsset m_Asset;
        public LightManager(CustomRenderPipelineAsset asset)
        {
            m_Asset = asset;
        }

        // Maximum amount of visible lights the shader can process. This controls the constant global light buffer size.
        // It must match the MAX_VISIBLE_LIGHTS in LightweightInput.cginc
        private static readonly int kMaxVisibleLights = 16;

        // Lights are culled per-object. This holds the maximum amount of lights that can be shaded per-object.
        // The engine fills in the lights indices per-object in unity4_LightIndices0 and unity_4LightIndices1
        private static readonly int kMaxPerObjectLights = 8;

        private static readonly int kMaxVertexLights = 4;

        private Vector4 kDefaultLightPosition = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
        private Vector4 kDefaultLightColor = Color.black;
        private Vector4 kDefaultLightAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        private Vector4 kDefaultLightSpotDirection = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
        private Vector4 kDefaultLightSpotAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);

        private Vector4[] m_LightPositions = new Vector4[kMaxVisibleLights];
        private Vector4[] m_LightColors = new Vector4[kMaxVisibleLights];
        private Vector4[] m_LightDistanceAttenuations = new Vector4[kMaxVisibleLights];
        private Vector4[] m_LightSpotDirections = new Vector4[kMaxVisibleLights];
        private Vector4[] m_LightSpotAttenuations = new Vector4[kMaxVisibleLights];

        private MixedLightingSetup m_MixedLightingSetup;
        public MixedLightingSetup MixedLightingSetup
        {
            get { return m_MixedLightingSetup; }
        }

        private LightComparer m_LightComparer = new LightComparer();
        private Dictionary<VisibleLight, int> m_VisibleLightsIDMap = new Dictionary<VisibleLight, int>(new LightEqualityComparer());

        // Maps from sorted light indices to original unsorted. We need this for shadow rendering
        // and per-object light lists.
        private List<int> m_SortedLightIndexMap = new List<int>();

        public int GetLightUnsortedIndex(int index)
        {
            return (index < m_SortedLightIndexMap.Count) ? m_SortedLightIndexMap[index] : index;
        }

        public void InitializeLightData(List<VisibleLight> visibleLights, out LightData lightData, Camera currentCamera)
        {
            int visibleLightsCount = Math.Min(visibleLights.Count, m_Asset.MaxPixelLights);
            m_SortedLightIndexMap.Clear();

            lightData.shadowMapSampleType = LightShadows.None;

            if (visibleLightsCount <= 1)
                lightData.mainLightIndex = GetMainLight(visibleLights);
            else
                lightData.mainLightIndex = SortLights(visibleLights, currentCamera);

            // If we have a main light we don't shade it in the per-object light loop. We also remove it from the per-object cull list
            int mainLightPresent = (lightData.mainLightIndex >= 0) ? 1 : 0;
            int additionalPixelLightsCount = visibleLightsCount - mainLightPresent;
            int vertexLightCount = (m_Asset.SupportsVertexLight) ? Math.Min(visibleLights.Count, kMaxPerObjectLights) - additionalPixelLightsCount - mainLightPresent : 0;
            vertexLightCount = Math.Min(vertexLightCount, kMaxVertexLights);

            lightData.pixelAdditionalLightsCount = additionalPixelLightsCount;
            lightData.totalAdditionalLightsCount = additionalPixelLightsCount + vertexLightCount;

            m_MixedLightingSetup = MixedLightingSetup.None;
        }
        // How main light is decided:
        // If shadows enabled, main light is always a shadow casting light. Directional has priority over local lights.
        // Otherwise directional lights have priority based on cookie support and intensity
        private int GetMainLight(List<VisibleLight> visibleLights)
        {
            int totalVisibleLights = visibleLights.Count;
            bool shadowsEnabled = m_Asset.AreShadowsEnabled();

            if (totalVisibleLights == 0 || m_Asset.MaxPixelLights == 0)
                return -1;

            int brighestDirectionalIndex = -1;
            for (int i = 0; i < totalVisibleLights; ++i)
            {
                VisibleLight currLight = visibleLights[i];

                // Particle system lights have the light property as null. We sort lights so all particles lights
                // come last. Therefore, if first light is particle light then all lights are particle lights.
                // In this case we either have no main light or already found it.
                if (currLight.light == null)
                    break;

                // Shadow lights are sorted by type (directional > puctual) and intensity
                // The first shadow light we find in the list is the main light
                if (shadowsEnabled && currLight.light.shadows != LightShadows.None && LightweightUtils.IsSupportedShadowType(currLight.lightType))
                    return i;

                // In case no shadow light is present we will return the brightest directional light
                if (currLight.lightType == UnityEngine.LightType.Directional && brighestDirectionalIndex == -1)
                    brighestDirectionalIndex = i;
            }

            return brighestDirectionalIndex;
        }

        private int SortLights(List<VisibleLight> visibleLights, Camera currentCamera)
        {
            int totalVisibleLights = visibleLights.Count;

            m_VisibleLightsIDMap.Clear();
            for (int i = 0; i < totalVisibleLights; ++i)
                m_VisibleLightsIDMap.Add(visibleLights[i], i);

            // Sorts light so we have all directionals first, then local lights.
            // Directionals are sorted further by shadow, cookie and intensity
            // Locals are sorted further by shadow, cookie and distance to camera
            m_LightComparer.CurrCamera = currentCamera;
            visibleLights.Sort(m_LightComparer);

            for (int i = 0; i < totalVisibleLights; ++i)
                m_SortedLightIndexMap.Add(m_VisibleLightsIDMap[visibleLights[i]]);

            return GetMainLight(visibleLights);
        }

        private void InitializeLightConstants(List<VisibleLight> lights, int lightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightDistanceAttenuation, out Vector4 lightSpotDir,
            out Vector4 lightSpotAttenuation)
        {
            lightPos = kDefaultLightPosition;
            lightColor = kDefaultLightColor;
            lightDistanceAttenuation = kDefaultLightSpotAttenuation;
            lightSpotDir = kDefaultLightSpotDirection;
            lightSpotAttenuation = kDefaultLightAttenuation;

            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            VisibleLight lightData = lights[lightIndex];
            if (lightData.lightType == UnityEngine.LightType.Directional)
            {
                Vector4 dir = -lightData.localToWorld.GetColumn(2);
                lightPos = new Vector4(dir.x, dir.y, dir.z, 0.0f);
            }
            else
            {
                Vector4 pos = lightData.localToWorld.GetColumn(3);
                lightPos = new Vector4(pos.x, pos.y, pos.z, 1.0f);
            }

            // VisibleLight.finalColor already returns color in active color space
            lightColor = lightData.finalColor;

            // Directional Light attenuation is initialize so distance attenuation always be 1.0
            if (lightData.lightType != UnityEngine.LightType.Directional)
            {
                // Light attenuation in lightweight matches the unity vanilla one.
                // attenuation = 1.0 / 1.0 + distanceToLightSqr * quadraticAttenuation
                // then a smooth factor is applied to linearly fade attenuation to light range
                // the attenuation smooth factor starts having effect at 80% of light range
                // smoothFactor = (lightRangeSqr - distanceToLightSqr) / (lightRangeSqr - fadeStartDistanceSqr)
                // We rewrite smoothFactor to be able to pre compute the constant terms below and apply the smooth factor
                // with one MAD instruction
                // smoothFactor =  distanceSqr * (1.0 / (fadeDistanceSqr - lightRangeSqr)) + (-lightRangeSqr / (fadeDistanceSqr - lightRangeSqr)
                //                 distanceSqr *           oneOverFadeRangeSqr             +              lightRangeSqrOverFadeRangeSqr
                float lightRangeSqr = lightData.range * lightData.range;
                float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
                float fadeRangeSqr = (fadeStartDistanceSqr - lightRangeSqr);
                float oneOverFadeRangeSqr = 1.0f / fadeRangeSqr;
                float lightRangeSqrOverFadeRangeSqr = -lightRangeSqr / fadeRangeSqr;
                float quadAtten = 25.0f / lightRangeSqr;
                lightDistanceAttenuation = new Vector4(quadAtten, oneOverFadeRangeSqr, lightRangeSqrOverFadeRangeSqr, 1.0f);
            }

            if (lightData.lightType == UnityEngine.LightType.Spot)
            {
                Vector4 dir = lightData.localToWorld.GetColumn(2);
                lightSpotDir = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);

                // Spot Attenuation with a linear falloff can be defined as
                // (SdotL - cosOuterAngle) / (cosInnerAngle - cosOuterAngle)
                // This can be rewritten as
                // invAngleRange = 1.0 / (cosInnerAngle - cosOuterAngle)
                // SdotL * invAngleRange + (-cosOuterAngle * invAngleRange)
                // If we precompute the terms in a MAD instruction
                float cosOuterAngle = Mathf.Cos(Mathf.Deg2Rad * lightData.spotAngle * 0.5f);
                // We neeed to do a null check for particle lights
                // This should be changed in the future
                // Particle lights will use an inline function
                float cosInnerAngle;
                if (lightData.light != null)
                    cosInnerAngle = Mathf.Cos(LightmapperUtils.ExtractInnerCone(lightData.light) * 0.5f);
                else
                    cosInnerAngle = Mathf.Cos((2.0f * Mathf.Atan(Mathf.Tan(lightData.spotAngle * 0.5f * Mathf.Deg2Rad) * (64.0f - 18.0f) / 64.0f)) * 0.5f);
                float smoothAngleRange = Mathf.Max(0.001f, cosInnerAngle - cosOuterAngle);
                float invAngleRange = 1.0f / smoothAngleRange;
                float add = -cosOuterAngle * invAngleRange;
                lightSpotAttenuation = new Vector4(invAngleRange, add, 0.0f);
            }

            Light light = lightData.light;

            // TODO: Add support to shadow mask
            if (light != null && light.bakingOutput.mixedLightingMode == MixedLightingMode.Subtractive && light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
            {
                if (m_MixedLightingSetup == MixedLightingSetup.None && lightData.light.shadows != LightShadows.None)
                {
                    m_MixedLightingSetup = MixedLightingSetup.Subtractive;
                    lightDistanceAttenuation.w = 0.0f;
                }
            }
        }

        public void SetupShaderLightConstants(CommandBuffer cmd, List<VisibleLight> lights, ref LightData lightData, ref CullResults cullResults)
        {
            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Lightweight pipeline also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, lights, lightData.mainLightIndex);
            SetupAdditionalListConstants(cmd, lights, ref lightData, ref cullResults);
        }

        private void SetupMainLightConstants(CommandBuffer cmd, List<VisibleLight> lights, int lightIndex)
        {
            Vector4 lightPos, lightColor, lightDistanceAttenuation, lightSpotDir, lightSpotAttenuation;
            InitializeLightConstants(lights, lightIndex, out lightPos, out lightColor, out lightDistanceAttenuation, out lightSpotDir, out lightSpotAttenuation);

            if (lightIndex >= 0)
            {
                UnityEngine.LightType mainLightType = lights[lightIndex].lightType;
                Light mainLight = lights[lightIndex].light;

                if (LightweightUtils.IsSupportedCookieType(mainLightType) && mainLight.cookie != null)
                {
                    Matrix4x4 lightCookieMatrix;
                    LightweightUtils.GetLightCookieMatrix(lights[lightIndex], out lightCookieMatrix);
                    cmd.SetGlobalTexture(PerCameraBuffer._MainLightCookie, mainLight.cookie);
                    cmd.SetGlobalMatrix(PerCameraBuffer._WorldToLight, lightCookieMatrix);
                }
            }

            cmd.SetGlobalVector(PerCameraBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightColor, lightColor);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightDistanceAttenuation, lightDistanceAttenuation);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightSpotDir, lightSpotDir);
            cmd.SetGlobalVector(PerCameraBuffer._MainLightSpotAttenuation, lightSpotAttenuation);
        }

        private void SetupAdditionalListConstants(CommandBuffer cmd, List<VisibleLight> lights, ref LightData lightData, ref CullResults cullResults)
        {
            int additionalLightIndex = 0;

            if (lightData.totalAdditionalLightsCount > 0)
            {
                // We need to update per-object light list with the proper map to our global additional light buffer
                // First we initialize all lights in the map to -1 to tell the system to discard main light index and
                // remaining lights in the scene that don't fit the max additional light buffer (kMaxVisibileAdditionalLights)
                int[] perObjectLightIndexMap = cullResults.GetLightIndexMap();
                for (int i = 0; i < lights.Count; ++i)
                    perObjectLightIndexMap[i] = -1;

                for (int i = 0; i < lights.Count && additionalLightIndex < kMaxVisibleLights; ++i)
                {
                    if (i != lightData.mainLightIndex)
                    {
                        // The engine performs per-object light culling and initialize 8 light indices into two vec4 constants unity_4LightIndices0 and unity_4LightIndices1.
                        // In the shader we iterate over each visible light using the indices provided in these constants to index our global light buffer
                        // ex: first light position would be m_LightPosisitions[unity_4LightIndices[0]];

                        // However since we sorted the lights we need to tell the engine how to map the original/unsorted indices to our global buffer
                        // We do it by settings the perObjectLightIndexMap to the appropriate additionalLightIndex.
                        perObjectLightIndexMap[GetLightUnsortedIndex(i)] = additionalLightIndex;
                        InitializeLightConstants(lights, i, out m_LightPositions[additionalLightIndex],
                            out m_LightColors[additionalLightIndex],
                            out m_LightDistanceAttenuations[additionalLightIndex],
                            out m_LightSpotDirections[additionalLightIndex],
                            out m_LightSpotAttenuations[additionalLightIndex]);
                        additionalLightIndex++;
                    }
                }
                cullResults.SetLightIndexMap(perObjectLightIndexMap);

                cmd.SetGlobalVector(PerCameraBuffer._AdditionalLightCount, new Vector4(lightData.pixelAdditionalLightsCount,
                        lightData.totalAdditionalLightsCount, 0.0f, 0.0f));
            }
            else
            {
                cmd.SetGlobalVector(PerCameraBuffer._AdditionalLightCount, Vector4.zero);

                // Clear to default all light cosntant data
                for (int i = 0; i < kMaxVisibleLights; ++i)
                    InitializeLightConstants(lights, -1, out m_LightPositions[additionalLightIndex],
                            out m_LightColors[additionalLightIndex],
                            out m_LightDistanceAttenuations[additionalLightIndex],
                            out m_LightSpotDirections[additionalLightIndex],
                            out m_LightSpotAttenuations[additionalLightIndex]);
            }

            cmd.SetGlobalVectorArray(PerCameraBuffer._AdditionalLightPosition, m_LightPositions);
            cmd.SetGlobalVectorArray(PerCameraBuffer._AdditionalLightColor, m_LightColors);
            cmd.SetGlobalVectorArray(PerCameraBuffer._AdditionalLightDistanceAttenuation, m_LightDistanceAttenuations);
            cmd.SetGlobalVectorArray(PerCameraBuffer._AdditionalLightSpotDir, m_LightSpotDirections);
            cmd.SetGlobalVectorArray(PerCameraBuffer._AdditionalLightSpotAttenuation, m_LightSpotAttenuations);
        }

    }
}