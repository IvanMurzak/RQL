/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Physics3D
{
    /// <summary>Collider: <c>material</c> instantiates a copy, so it is explicit-only (<c>sharedMaterial</c> is the asset).</summary>
    public sealed class UnityEngine_Collider_Adapter : RqlTypeAdapter<Collider>
    {
        protected override void Members(MemberTable<Collider> m) => m.Untyped.ExplicitOnly("material");
    }
}
