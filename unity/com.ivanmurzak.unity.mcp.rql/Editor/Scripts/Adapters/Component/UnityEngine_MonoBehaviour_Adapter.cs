/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>MonoBehaviour: hide engine bookkeeping members so scripts show their own fields.</summary>
    public sealed class UnityEngine_MonoBehaviour_Adapter : RqlTypeAdapter<MonoBehaviour>
    {
        protected override void Members(MemberTable<MonoBehaviour> m) => m.Untyped.HideExisting("destroyCancellationToken", "useGUILayout", "runInEditMode", "didStart", "didAwake");
    }
}
