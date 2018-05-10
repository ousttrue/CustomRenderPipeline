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

        public bool Cull(ref ScriptableRenderContext context
            , CameraContext cameraContext
            , float maxShadowDistance)
        {
            if (!CullResults.GetCullingParameters(cameraContext.Camera, cameraContext.StereoEnabled, out cullingParameters))
                return false;

            cullingParameters.shadowDistance = Mathf.Min(maxShadowDistance, cameraContext.Camera.farClipPlane);

#if UNITY_EDITOR
            // Emit scene view UI
            if (cameraContext.SceneViewCamera)
                ScriptableRenderContext.EmitWorldGeometryForSceneView(cameraContext.Camera);
#endif

            CullResults.Cull(ref cullingParameters, context, ref m_CullResults);

            return true;
        }
    }
}
