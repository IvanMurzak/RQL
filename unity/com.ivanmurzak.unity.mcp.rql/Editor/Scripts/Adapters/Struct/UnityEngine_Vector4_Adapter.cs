/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Vector4: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Vector4_Adapter : FloatStructAdapter<Vector4>
    {
        public override string Description => "Vector4: write as \"(x, y, z, w)\" or [x, y, z, w]";
        protected override string Expected => "(x, y, z, w) or [x, y, z, w]";
        protected override float[] Parts(Vector4 v) => new[] { v.x, v.y, v.z, v.w };
        protected override Vector4? Make(float[] f) => f.Length == 4 ? new Vector4(f[0], f[1], f[2], f[3]) : (Vector4?)null;
    }
}
