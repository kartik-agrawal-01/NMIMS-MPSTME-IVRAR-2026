using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BoardroomXR.EditorTools
{
    /// <summary>
    /// One-click Quest build to C:\Users\HP\BoardroomXR_v5.apk (keeps v1-v4 untouched).
    ///
    /// Batch mode safe, so the expo build can be produced with Unity closed:
    ///   Unity.exe -batchmode -quit -projectPath . -buildTarget Android \
    ///             -executeMethod BoardroomXR.EditorTools.BoardroomBuild.BuildApk
    /// </summary>
    public static class BoardroomBuild
    {
        private const string ApkName = "BoardroomXR_v5.apk";

        [MenuItem("BoardroomXR/Build Quest APK (v5)")]
        public static void BuildApk()
        {
            // The procedural boardroom loads its materials from Resources, which is also
            // what pulls the shader variants into the player. Make sure they exist first.
            BoardroomMaterialAssets.EnsureAll(false);

            string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, ApkName);

            long freeGb = new DriveInfo(Path.GetPathRoot(home)).AvailableFreeSpace / (1024L * 1024 * 1024);
            Debug.Log($"[Build] Free space on {Path.GetPathRoot(home)}: {freeGb} GB");

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            string msg = $"Result: {s.result}\nOutput: {path}\nSize: {s.totalSize / (1024f * 1024f):F1} MB\n" +
                         $"Errors: {s.totalErrors}, warnings: {s.totalWarnings}\nTime: {s.totalTime}\nScenes: {string.Join(", ", scenes)}";
            Debug.Log("[Build] " + msg);

            // A modal dialog in batch mode would hang the build forever.
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Quest APK build", msg, "OK");

            if (s.result != BuildResult.Succeeded && Application.isBatchMode)
            {
                // Make the failure visible to the shell that launched this.
                Debug.LogError($"[Build] FAILED: {s.result} ({s.totalErrors} errors)");
                EditorApplication.Exit(1);
            }
        }
    }
}
