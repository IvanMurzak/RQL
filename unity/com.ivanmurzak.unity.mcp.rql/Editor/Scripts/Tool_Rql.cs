/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using Rql;
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor
{
    /// <summary>
    /// The RQL MCP tools: discovered by Unity-MCP's assembly scan like any extension tool, so the AI Game Dev App picks them
    /// up through its live tool list without changes. Results are the compact table format the RQL evaluation selected.
    /// </summary>
    [AiToolType]
    public partial class Tool_Rql
    {
        public const string QueryToolId = "rql-query";
        public const string MutateToolId = "rql-mutate";
        public const string SchemaToolId = "rql-schema";

        [AiTool(QueryToolId, Title = "RQL / Query", Enabled = true, ReadOnlyHint = true, OpenWorldHint = false)]
        [AiSkillDescription(RqlGuide.QueryBlurb)]
        [AiSkillBody(RqlGuide.QueryDescription)]
        [Description(RqlGuide.QueryDescription)]
        public ResponseCallTool Query
        (
            [Description("RQL query, e.g. objects | where $ is ... | select $ref name")]
            string query
        )
        {
            return Run(() => RqlUnity.Engine.Execute(query));
        }

        [AiTool(MutateToolId, Title = "RQL / Mutate", Enabled = true, DestructiveHint = true, OpenWorldHint = false)]
        [AiSkillDescription(RqlGuide.MutateBlurb)]
        [AiSkillBody(RqlGuide.MutateDescription)]
        [Description(RqlGuide.MutateDescription)]
        public ResponseCallTool Mutate
        (
            [Description("RQL query ending with a set or call stage, e.g. objects | where name == \"Player\" | set active = false")]
            string query,
            [Description("true = preview the receipt without changing anything")]
            bool dry_run = false
        )
        {
            return Run(() => RqlUnity.Engine.Execute(query, null, new QueryOptions { AllowMutation = true, DryRun = dry_run }));
        }

        [AiTool(SchemaToolId, Title = "RQL / Schema", Enabled = true, ReadOnlyHint = true, OpenWorldHint = false)]
        [AiSkillDescription(RqlGuide.SchemaBlurb)]
        [AiSkillBody(RqlGuide.SchemaDescription)]
        [Description(RqlGuide.SchemaDescription)]
        public ResponseCallTool Schema
        (
            [Description("Optional type name, e.g. \"Light\", \"Rigidbody\", \"Enemy\", \"Vector3\"")]
            string? type = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                try { return ResponseCallTool.Success(RqlUnitySchema.Render(RqlUnity.Engine, string.IsNullOrWhiteSpace(type) ? null : type!.Trim())); }
                catch (Exception ex) { return ResponseCallTool.Error($"error internal-error: {ex.GetType().Name}: {ex.Message}"); }
            });
        }

        private static ResponseCallTool Run(Func<QueryResult> execute) => MainThread.Instance.Run(() =>
        {
            var result = execute();
            var text = AgentFormat.Render(result, OutputFormat.Table);
            return result.Ok ? ResponseCallTool.Success(text) : ResponseCallTool.Error(text);
        });
    }
}
