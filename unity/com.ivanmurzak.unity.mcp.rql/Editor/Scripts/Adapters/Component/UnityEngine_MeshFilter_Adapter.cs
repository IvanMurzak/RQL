/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>MeshFilter: mesh instantiates a copy when read — explicit-only.</summary>
    public sealed class UnityEngine_MeshFilter_Adapter : RqlTypeAdapter<MeshFilter>
    {
        protected override void Members(MemberTable<MeshFilter> m) => m.Untyped.ExplicitOnly("mesh");
    }
}
