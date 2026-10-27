using System.Collections.Generic;
using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Runtime material source for the procedural boardroom, with the three-step
    /// fallback the rest of the project uses for shaders:
    ///
    ///   1. Resources.Load&lt;Material&gt;("BoardroomXR/...") - created by the editor
    ///      script, which guarantees the shader variant is in the build,
    ///   2. Shader.Find("Universal Render Pipeline/Lit"),
    ///   3. the shader of any MeshRenderer already in the scene - whatever the
    ///      template environment uses must be a variant that shipped.
    ///
    /// Every material handed out is a fresh instance, so tinting one prop cannot
    /// recolour the whole room. Callers own them and destroy them.
    /// </summary>
    public static class BoardroomMaterials
    {
        private static Material _litTemplate, _emissiveTemplate, _unlitTemplate;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _litTemplate = Resources.Load<Material>("BoardroomXR/Lit");
            _emissiveTemplate = Resources.Load<Material>("BoardroomXR/LitEmissive");
            _unlitTemplate = Resources.Load<Material>("BoardroomXR/Unlit");

            if (_litTemplate == null)
            {
                Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? SceneShader();
                if (s != null) _litTemplate = new Material(s) { name = "BoardroomLit(runtime)" };
            }
            if (_emissiveTemplate == null && _litTemplate != null)
                _emissiveTemplate = _litTemplate;

            if (_unlitTemplate == null)
            {
                Shader s = SpatialUiFactory.UnlitShader;
                if (s != null) _unlitTemplate = new Material(s) { name = "BoardroomUnlit(runtime)" };
            }

            Debug.Log($"[BoardroomXR] Materials resolved: lit={(_litTemplate != null ? _litTemplate.shader.name : "none")}, " +
                      $"emissive={(_emissiveTemplate != null ? _emissiveTemplate.shader.name : "none")}, " +
                      $"unlit={(_unlitTemplate != null ? _unlitTemplate.shader.name : "none")}");
        }

        /// <summary>Any shader already rendering in this scene is guaranteed to exist in the build.</summary>
        private static Shader SceneShader()
        {
            foreach (var mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
            {
                var m = mr.sharedMaterial;
                if (m != null && m.shader != null) return m.shader;
            }
            return null;
        }

        /// <summary>An opaque surface: walls, table, chairs, carpet.</summary>
        public static Material Lit(Color color, float smoothness = 0.25f, float metallic = 0f)
        {
            Resolve();
            var m = _litTemplate != null ? new Material(_litTemplate) : SpatialUiFactory.UnlitMaterial(color);
            SetColor(m, color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            return m;
        }

        /// <summary>A glowing surface: ceiling light panels, door status strips.</summary>
        public static Material Emissive(Color color, float intensity = 1.4f)
        {
            Resolve();
            if (_emissiveTemplate == null) return SpatialUiFactory.UnlitMaterial(color);

            var m = new Material(_emissiveTemplate);
            SetColor(m, color);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", color * intensity);
            return m;
        }

        /// <summary>
        /// Translucent / unlit things: glass partitions, the sliding doors, carpet
        /// overlays. Uses Sprites/Default, which is two-sided and already in the
        /// project's Always Included Shaders.
        /// </summary>
        public static Material Translucent(Color color)
        {
            Resolve();
            var m = _unlitTemplate != null ? new Material(_unlitTemplate) : SpatialUiFactory.UnlitMaterial(color);
            m.color = color;
            m.renderQueue = 3000;
            return m;
        }

        private static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        /// <summary>Destroys a list of runtime material instances (component teardown).</summary>
        public static void DestroyAll(List<Material> materials)
        {
            if (materials == null) return;
            foreach (var m in materials) if (m != null) Object.Destroy(m);
            materials.Clear();
        }
    }
}
