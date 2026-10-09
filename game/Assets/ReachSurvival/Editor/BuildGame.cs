using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ReachSurvival.EditorTools
{
    /// <summary>Batch build: Unity -batchmode -executeMethod ReachSurvival.EditorTools.BuildGame.Build -quit</summary>
    public static class BuildGame
    {
        const string ScenePath = "Assets/ReachSurvival/Main.unity";

        [MenuItem("Reach Survival/Build Windows")]
        public static void Build()
        {
            if (!File.Exists(ScenePath))
            {
                // Everything is spawned at runtime by Boot, so the scene only needs a camera for the loading screen.
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var cam = new GameObject("Loading Camera").AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "build", "ReachSurvival"));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, "ReachSurvival.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[build] {report.summary.result} {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB -> {outDir}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
