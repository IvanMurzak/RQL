#nullable enable
// UAC0005 (Unity 6.6 analyzer) suggests CurrentAssemblies APIs that older supported Unity versions lack; the scan only reads type metadata.
#pragma warning disable UAC0005
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace RqlTest.EditorTools
{
    /// <summary>
    /// Batch-mode helper: with <c>-rqlWaitReady</c> on the command line (and without <c>-quit</c>), keeps the Editor running until
    /// Unity-MCP's dependency resolver has restored its NuGet DLLs and the RQL tools assembly is compiled and loaded, then exits
    /// with 0. Exits 1 on compile errors, 2 on timeout. Survives domain reloads (state in SessionState).
    /// Usage: Unity -batchmode -nographics -projectPath RqlTestProject -rqlWaitReady -logFile -
    /// </summary>
    [InitializeOnLoad]
    internal static class RqlBatchReady
    {
        private const string StartKey = "RqlBatchReady.Start";
        private const string ErrorsKey = "RqlBatchReady.Errors";
        private const double TimeoutSeconds = 600;
        private const string ToolsAssembly = "com.IvanMurzak.Unity.MCP.Rql.Editor";

        static RqlBatchReady()
        {
            if (!Application.isBatchMode || !Environment.GetCommandLineArgs().Contains("-rqlWaitReady")) return;
            if (SessionState.GetFloat(StartKey, 0) == 0) SessionState.SetFloat(StartKey, (float)EditorApplication.timeSinceStartup);
            CompilationPipeline.assemblyCompilationFinished += (assembly, messages) =>
            {
                var errors = messages.Where(m => m.type == CompilerMessageType.Error).ToList();
                foreach (var e in errors) Debug.LogError($"[RQL] compile error {e.file}({e.line}): {e.message}");
                if (errors.Count > 0) SessionState.SetInt(ErrorsKey, SessionState.GetInt(ErrorsKey, 0) + errors.Count);
            };
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == ToolsAssembly);
            var elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(StartKey, 0);
            if (loaded)
            {
                Debug.Log($"[RQL] ready after {elapsed:0}s: {ToolsAssembly} loaded");
                EditorApplication.Exit(0);
            }
            else if (SessionState.GetInt(ErrorsKey, 0) > 0 && elapsed > 20)
            {
                Debug.LogError("[RQL] not ready: compile errors (see above)");
                EditorApplication.Exit(1);
            }
            else if (elapsed > TimeoutSeconds)
            {
                Debug.LogError($"[RQL] not ready after {elapsed:0}s (timeout)");
                EditorApplication.Exit(2);
            }
        }
    }
}
