using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

namespace RqlPlayer.EditorTools
{
    /// <summary>
    /// Batch build: Unity -batchmode -quit -projectPath P -executeMethod RqlPlayer.EditorTools.PlayerBuild.Build
    ///   -rqlTarget osx|win64|linux64|webgl -rqlBackend mono|il2cpp -rqlStripping Minimal|Low|Medium|High -rqlOut path
    ///   [-rqlDefines RQL_RUNTIME]
    /// </summary>
    public static class PlayerBuild
    {
        private static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        public static void Build()
        {
            const string scene = "Assets/Scenes/Main.unity";
            if (!File.Exists(scene))
            {
                Directory.CreateDirectory("Assets/Scenes");
                EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene), scene);
            }
            var (target, group) = Arg("-rqlTarget", "osx") switch
            {
                "win64" => (BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone),
                "linux64" => (BuildTarget.StandaloneLinux64, BuildTargetGroup.Standalone),
                "webgl" => (BuildTarget.WebGL, BuildTargetGroup.WebGL),
                _ => (BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone),
            };
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            var backend = Arg("-rqlBackend", "mono") == "il2cpp" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x;
            PlayerSettings.SetScriptingBackend(named, backend);
            PlayerSettings.SetManagedStrippingLevel(named, (ManagedStrippingLevel)Enum.Parse(typeof(ManagedStrippingLevel), Arg("-rqlStripping", "Minimal")));
            if (target == BuildTarget.WebGL) PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            // -rqlDefines RQL_RUNTIME opts the RQL runtime into the player (asmdef / plugin defineConstraints
            // "UNITY_EDITOR || RQL_RUNTIME"); without it the player contains no RQL code or DLLs.
            PlayerSettings.SetScriptingDefineSymbols(named, Arg("-rqlDefines", ""));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                target = target,
                targetGroup = group,
                locationPathName = Arg("-rqlOut", "Builds/player"),
                options = BuildOptions.None,
            });
            UnityEngine.Debug.Log($"[RQLBUILD] {report.summary.result} {target} {backend} errors={report.summary.totalErrors} size={report.summary.totalSize}");
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
