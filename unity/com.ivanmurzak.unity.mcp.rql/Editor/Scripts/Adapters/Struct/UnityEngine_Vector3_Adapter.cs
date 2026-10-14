/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Vector3: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Vector3_Adapter : FloatStructAdapter<Vector3>
    {
        public override string Description => "Vector3: write as \"(x, y, z)\", [x, y, z] or {x: 1, y: 2, z: 3}";
        protected override string Expected => "(x, y, z) or [x, y, z]";
        protected override float[] Parts(Vector3 v) => new[] { v.x, v.y, v.z };
        protected override Vector3? Make(float[] f) => f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : (Vector3?)null;
    }
}
