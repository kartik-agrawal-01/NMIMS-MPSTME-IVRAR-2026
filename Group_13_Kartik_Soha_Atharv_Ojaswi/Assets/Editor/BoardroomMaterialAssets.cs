using System.IO;
using UnityEditor;
using UnityEngine;

namespace BoardroomXR.EditorTools
{
    /// <summary>
    /// Creates the three material assets the procedural boardroom loads at runtime:
    ///
    ///   Assets/Resources/BoardroomXR/Lit.mat          (URP Lit)
    ///   Assets/Resources/BoardroomXR/LitEmissive.mat  (URP Lit with _EMISSION on)
    ///   Assets/Resources/BoardroomXR/Unlit.mat        (Sprites/Default)
    ///
    /// They live under Resources so the shader variants are pulled into the player
    /// build. Without them, a material created from Shader.Find at runtime can come
    /// up with a stripped variant on the Quest and render magenta.
    ///
    /// Runs on editor load and is a no-op once the assets exist.
    /// </summary>
    [InitializeOnLoad]
    public static class BoardroomMaterialAssets
    {
        private const string Folder = "Assets/Resources/BoardroomXR";

        static BoardroomMaterialAssets()
        {
            // Defer: asset creation during a domain reload / import is unsafe.
            EditorApplication.delayCall += () => EnsureAll(false);
        }

        [MenuItem("BoardroomXR/Create Boardroom Materials")]
        private static void EnsureAllFromMenu() => EnsureAll(true);

        /// <summary>
        /// Entry point for -executeMethod, and called by the build script: in batch mode
        /// the deferred editor-load hook does not necessarily run before Unity quits.
        /// </summary>
        public static void EnsureFromCommandLine() => EnsureAll(true);

        public static void EnsureAll(bool verbose)
        {
            try
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                    AssetDatabase.CreateFolder("Assets", "Resources");
                if (!AssetDatabase.IsValidFolder(Folder))
                    AssetDatabase.CreateFolder("Assets/Resources", "BoardroomXR");

                bool created = false;
                created |= EnsureLit("Lit.mat", false);
                created |= EnsureLit("LitEmissive.mat", true);
                created |= EnsureUnlit("Unlit.mat");

                if (created)
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[BoardroomXR] Boardroom material assets ensured in {Folder}.");
                }
                else if (verbose)
                {
                    Debug.Log($"[BoardroomXR] Boardroom material assets already present in {Folder}.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BoardroomXR] Could not create boardroom materials: {e.Message}");
            }
        }

        private static bool EnsureLit(string fileName, bool emissive)
        {
            string path = Path.Combine(Folder, fileName).Replace('\\', '/');
            if (File.Exists(path)) return false;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning("[BoardroomXR] No Lit shader found; skipping " + fileName);
                return false;
            }

            var m = new Material(shader) { name = Path.GetFileNameWithoutExtension(fileName) };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.6f, 0.6f, 0.6f));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.3f);
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
            }

            AssetDatabase.CreateAsset(m, path);
            return true;
        }

        private static bool EnsureUnlit(string fileName)
        {
            string path = Path.Combine(Folder, fileName).Replace('\\', '/');
            if (File.Exists(path)) return false;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader == null)
            {
                Debug.LogWarning("[BoardroomXR] No unlit shader found; skipping " + fileName);
                return false;
            }

            var m = new Material(shader) { name = Path.GetFileNameWithoutExtension(fileName), color = Color.white };
            AssetDatabase.CreateAsset(m, path);
            return true;
        }
    }
}
