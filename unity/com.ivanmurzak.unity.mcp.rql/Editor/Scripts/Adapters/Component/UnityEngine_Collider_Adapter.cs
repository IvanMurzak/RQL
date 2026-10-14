/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>
    /// Collider (physics module): material instantiates a copy — explicit-only. Targeted by name, so the adapter is inert when
    /// the physics module is disabled in the project.
    /// </summary>
    public sealed class UnityEngine_Collider_Adapter : RqlTypeAdapter
    {
        public UnityEngine_Collider_Adapter() : base("UnityEngine.Collider") { }

        public override void ConfigureMembers(MemberTable members) => members.ExplicitOnlyExisting("material");
    }
}
