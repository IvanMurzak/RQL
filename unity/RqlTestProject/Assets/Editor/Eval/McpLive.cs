#nullable enable
using System;
using System.IO;
using System.Linq;
using RqlTest.EditorTools;
using UnityEditor;
using UnityEngine;

namespace RqlTest.Eval
{
    /// <summary>
    /// Live MCP session for an external agent harness (evaluation/agent-sdk): builds the test scene plus the escaping-probe
    /// object, connects Unity-MCP to the MCP server given by UNITY_MCP_HOST (the tool presentation comes from
    /// UserSettings/AI-Game-Developer-Config.json, as a host app would write it) and keeps the Editor running until the file
    /// given by -liveStopFile exists. Usage (no -quit):
    ///   UNITY_MCP_HOST=http://localhost:58090 UNITY_MCP_KEEP_CONNECTED=true Unity -batchmode -nographics -projectPath P \
    ///     -executeMethod RqlTest.Eval.McpLive.Run -liveStopFile /path/stop -logFile -
    /// Writes "[LIVE] connected" to the log once the plugin is connected.
    /// </summary>
    public static class McpLive
    {
        public const string ProbeName = "Crate #7 <A+B> — café \"x\"";
        private static string _stopFile = "";
        private static bool _announced;

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-liveStopFile");
            _stopFile = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
            RqlTestSceneBuilder.Build();
            var level = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().First(g => g.name == "Level");
            new GameObject(ProbeName).transform.SetParent(level.transform, false);
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.InitSingletonIfNeeded();
            _ = com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.Connect();
            Debug.Log($"[LIVE] presentation={com.IvanMurzak.McpPlugin.ToolPresentation.Current?.GetType().Name ?? "json"} escaping={com.IvanMurzak.McpPlugin.ToolPresentation.Escaping}");
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!_announced && com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.IsConnected.CurrentValue)
            {
                _announced = true;
                Debug.Log("[LIVE] connected");
            }
            if (_stopFile.Length > 0 && File.Exists(_stopFile))
            {
                EditorApplication.update -= Tick;
                Debug.Log("[LIVE] stop");
                EditorApplication.Exit(0);
            }
        }
    }
}
