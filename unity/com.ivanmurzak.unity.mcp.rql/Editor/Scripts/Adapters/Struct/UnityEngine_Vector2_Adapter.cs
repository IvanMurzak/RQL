/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Vector2: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Vector2_Adapter : NumericTupleAdapter<Vector2>
    {
        public override string Description => "Vector2: write as \"(x, y)\", [x, y] or {x: 1, y: 2}";
        protected override string Expected => "(x, y) or [x, y]";
        protected override int Width => 2;
        protected override float Part(Vector2 v, int i) => i switch { 0 => v.x, _ => v.y };
        protected override Vector2? Make(float[] f) => f.Length == 2 ? new Vector2(f[0], f[1]) : (Vector2?)null;
    }
}
