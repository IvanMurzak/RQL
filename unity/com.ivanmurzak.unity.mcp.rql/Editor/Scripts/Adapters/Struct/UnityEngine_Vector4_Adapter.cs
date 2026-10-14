/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Vector4: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Vector4_Adapter : NumericTupleAdapter<Vector4>
    {
        public override string Description => "Vector4: write as \"(x, y, z, w)\" or [x, y, z, w]";
        protected override string Expected => "(x, y, z, w) or [x, y, z, w]";
        protected override int Width => 4;
        protected override float Part(Vector4 v, int i) => i switch { 0 => v.x, 1 => v.y, 2 => v.z, _ => v.w };
        protected override Vector4? Make(float[] f) => f.Length == 4 ? new Vector4(f[0], f[1], f[2], f[3]) : (Vector4?)null;
    }
}
