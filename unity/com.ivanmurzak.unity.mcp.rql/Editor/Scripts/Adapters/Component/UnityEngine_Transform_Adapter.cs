/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>
    /// Transform: parent / root are references; a GameObject value is accepted where a Transform is expected
    /// (set transform.parent = ref("GameObject#…")). It is an object although it enumerates its children (UnityCoreModule
    /// registers TreatAsObject).
    /// </summary>
    public sealed class UnityEngine_Transform_Adapter : RqlTypeAdapter<Transform>
    {
        protected override void Members(MemberTable<Transform> m) => m.Untyped.ReferenceExisting("parent", "root");

        public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            reason = null;
            result = value is GameObject go && target.IsAssignableFrom(typeof(Transform)) ? go.transform : null;
            return result != null ? AdapterOutcome.Ok : AdapterOutcome.NotHandled;
        }
    }
}
