/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Vector2: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Vector2_Adapter : FloatStructAdapter<Vector2>
    {
        public override string Description => "Vector2: write as \"(x, y)\", [x, y] or {x: 1, y: 2}";
        protected override string Expected => "(x, y) or [x, y]";
        protected override float[] Parts(Vector2 v) => new[] { v.x, v.y };
        protected override Vector2? Make(float[] f) => f.Length == 2 ? new Vector2(f[0], f[1]) : (Vector2?)null;
    }
}
