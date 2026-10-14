using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;

namespace RqlPlayer.EditorTools
{
    /// <summary>
    /// Editor Play Mode without the RQL_RUNTIME define: the RQL code and DLLs must still work there (defineConstraints
    /// "UNITY_EDITOR || RQL_RUNTIME"). Step 1 clears the defines (-quit); step 2 enters Play Mode, waits for the smoke report
    /// at -rqlOut and exits 0. Unity -batchmode -projectPath P -executeMethod RqlPlayer.EditorTools.PlayModeProbe.ClearDefines -quit
    ///        Unity -batchmode -projectPath P -executeMethod RqlPlayer.EditorTools.PlayModeProbe.Run -rqlOut report.txt
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeProbe
    {
        private const string ReportKey = "RqlPlayer.PlayModeProbe.Report";

        // Entering Play Mode reloads the domain: resume waiting for the report afterwards.
        static PlayModeProbe()
        {
            _report = SessionState.GetString(ReportKey, "");
            if (_report.Length == 0) return;
            _deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Wait;
        }

        public static void ClearDefines()
        {
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, "");
            UnityEngine.Debug.Log("[PROBE] defines cleared: '" + PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) + "'");
        }

        private static string _report = "";
        private static double _deadline;

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-rqlOut");
            _report = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
            if (File.Exists(_report)) File.Delete(_report);
            SessionState.SetString(ReportKey, _report);
            UnityEngine.Debug.Log("[PROBE] defines: '" + PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) + "', entering Play Mode");
            _deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Wait;
            EditorApplication.EnterPlaymode();
        }

        private static void Wait()
        {
            if (File.Exists(_report))
            {
                EditorApplication.update -= Wait;
                SessionState.EraseString(ReportKey);
                UnityEngine.Debug.Log("[PROBE] report written in Play Mode");
                EditorApplication.Exit(File.ReadAllText(_report).Contains("failures=0") ? 0 : 1);
            }
            else if (EditorApplication.timeSinceStartup > _deadline)
            {
                SessionState.EraseString(ReportKey);
                UnityEngine.Debug.LogError("[PROBE] no report");
                EditorApplication.Exit(2);
            }
        }
    }
}
