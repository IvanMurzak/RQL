/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Renderer: material(s) instantiate copies when read — explicit-only; sharedMaterials returns a copy — written back.</summary>
    public sealed class UnityEngine_Renderer_Adapter : RqlTypeAdapter<Renderer>
    {
        protected override void Members(MemberTable<Renderer> m) => m.Untyped.ExplicitOnly("material", "materials").CopyOnRead("sharedMaterials");
    }
}
