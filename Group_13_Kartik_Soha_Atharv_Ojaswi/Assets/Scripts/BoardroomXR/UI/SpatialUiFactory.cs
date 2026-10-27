using UnityEngine;

namespace BoardroomXR.UI
{
    /// <summary>
    /// Builds the runtime world-space UI: unlit quads and TextMesh labels, created
    /// from built-in resources only, with <b>no colliders anywhere</b> so the XRI ray
    /// interactors can still reach the PIN keypad canvas and the OTP token through
    /// everything this file makes.
    ///
    /// Quads are built from a shared mesh rather than GameObject.CreatePrimitive, so
    /// no collider is ever created in the first place.
    ///
    /// Orientation convention: Unity TextMesh and quads read correctly when their
    /// local +Z points AWAY from the viewer, which is why every panel is rotated with
    /// <see cref="XrRigLocator.FacingUser"/> / FacingOrigin.
    /// </summary>
    public static class SpatialUiFactory
    {
        /// <summary>Nominal TextMesh point size; world height is set through characterSize.</summary>
        public const int FontSize = 64;

        private static Mesh _quad;
        private static Font _font;
        private static Shader _unlitShader;

        public static Font Font
        {
            get
            {
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _font;
            }
        }

        /// <summary>Sprites/Default: unlit, two-sided and alpha-blended, and already in Always Included Shaders.</summary>
        public static Shader UnlitShader
        {
            get
            {
                if (_unlitShader == null)
                    _unlitShader = Shader.Find("Sprites/Default")
                                ?? Shader.Find("UI/Default")
                                ?? Shader.Find("Universal Render Pipeline/Unlit")
                                ?? Shader.Find("Unlit/Color");
                return _unlitShader;
            }
        }

        public static Mesh QuadMesh
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "BoardroomXR_Quad" };
                _quad.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f),
                };
                _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                _quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                _quad.RecalculateBounds();
                return _quad;
            }
        }

        public static Material UnlitMaterial(Color color)
        {
            var m = new Material(UnlitShader) { color = color };
            m.renderQueue = 3000; // transparent, drawn after the room
            return m;
        }

        /// <summary>An unlit, collider-free quad of the given size in its parent's local space.</summary>
        public static MeshRenderer Quad(Transform parent, string name, Vector3 localPos, Vector2 size, Color color,
                                        out Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = QuadMesh;
            var mr = go.AddComponent<MeshRenderer>();
            material = UnlitMaterial(color);
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        public static MeshRenderer Quad(Transform parent, string name, Vector3 localPos, Vector2 size, Color color) =>
            Quad(parent, name, localPos, size, color, out _);

        /// <summary>
        /// A TextMesh label whose capital letters are about <paramref name="worldHeight"/>
        /// metres tall. anchor/alignment default to a centred line.
        /// </summary>
        public static TextMesh Text(Transform parent, string name, Vector3 localPos, float worldHeight, Color color,
                                    TextAnchor anchor = TextAnchor.MiddleCenter,
                                    TextAlignment alignment = TextAlignment.Center)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var tm = go.AddComponent<TextMesh>();
            tm.font = Font;
            tm.fontSize = FontSize;
            // TextMesh world line height is roughly characterSize * fontSize / 10.
            tm.characterSize = worldHeight * 10f / FontSize;
            tm.anchor = anchor;
            tm.alignment = alignment;
            tm.color = color;
            tm.richText = true;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = Font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return tm;
        }

        /// <summary>
        /// A built-in primitive with its collider removed and disabled in the same frame,
        /// so it can never block an XRI ray even before Destroy is processed.
        /// </summary>
        public static GameObject Primitive(PrimitiveType type, Transform parent, string name,
                                           Vector3 localPos, Vector3 localScale, Quaternion localRot, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) { col.enabled = false; Object.Destroy(col); }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;
            var mr = go.GetComponent<MeshRenderer>();
            if (material != null) mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        /// <summary>Strips colliders from a whole hierarchy (used after building props).</summary>
        public static void StripColliders(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                c.enabled = false;
                Object.Destroy(c);
            }
        }
    }
}
