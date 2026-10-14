/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System.Linq;
using Rql.Execution;
using UnityEngine.SceneManagement;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Scene (struct): a whitelist of safe members plus its root GameObjects.</summary>
    public sealed class UnityEngine_Scene_Adapter : RqlTypeAdapter<Scene>
    {
        protected override void Members(MemberTable<Scene> m)
        {
            m.Untyped.Only("name", "path", "buildIndex", "isLoaded", "isDirty", "rootCount");
            m.Add("roots", s => s.GetRootGameObjects().ToList(), description: "root GameObjects", owning: true);
        }
    }
}
