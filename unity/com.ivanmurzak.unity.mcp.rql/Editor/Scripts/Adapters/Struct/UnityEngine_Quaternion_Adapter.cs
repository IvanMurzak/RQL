/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Quaternion: compact "(…)" output; "(…)" / [ … ] / object literal input.</summary>
    public sealed class UnityEngine_Quaternion_Adapter : FloatStructAdapter<Quaternion>
    {
        public override string Description => "Quaternion: (x, y, z, w); prefer eulerAngles for rotations, e.g. set transform.eulerAngles = \"(0, 90, 0)\"";
        protected override string Expected => "(x, y, z, w) or [x, y, z, w]";
        protected override float[] Parts(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        protected override Quaternion? Make(float[] f) => f.Length == 4 ? new Quaternion(f[0], f[1], f[2], f[3]) : (Quaternion?)null;
    }
}
