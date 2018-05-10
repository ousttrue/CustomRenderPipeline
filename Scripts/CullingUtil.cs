using UnityEngine;
using UnityEngine.Experimental.Rendering;


namespace CustomRP
{
    public class CullingUtil
    {
        ScriptableCullingParameters cullingParameters;

        CullResults m_CullResults;
        public CullResults CullResults
        {
            get { return m_CullResults; }
        }

        public bool Cull(Camera camera, ref ScriptableRenderContext context
            , bool sceneViewCamera, bool stereoEnabled
            , float maxShadowDistance)
        {
            if (!CullResults.GetCullingParameters(camera, stereoEnabled, out cullingParameters))
                return false;

            cullingParameters.shadowDistance = Mathf.Min(maxShadowDistance, camera.farClipPlane);

#if UNITY_EDITOR
            // Emit scene view UI
            if (sceneViewCamera)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

            CullResults.Cull(ref cullingParameters, context, ref m_CullResults);

            return true;
        }
    }
}
