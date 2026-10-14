/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Color: "(r, g, b, a)" output; "(r, g, b[, a])", [r, g, b[, a]], "#RRGGBB[AA]" or {r, g, b, a} input.</summary>
    public sealed class UnityEngine_Color_Adapter : FloatStructAdapter<Color>
    {
        public override string Description => "Color: write as \"(r, g, b, a)\" with 0..1 components, [r, g, b] or \"#RRGGBB\"";
        protected override string Expected => "(r, g, b, a), [r, g, b] or #RRGGBB";
        protected override float[] Parts(Color c) => new[] { c.r, c.g, c.b, c.a };
        protected override Color? Make(float[] f) => f.Length == 4 ? new Color(f[0], f[1], f[2], f[3]) : f.Length == 3 ? new Color(f[0], f[1], f[2]) : (Color?)null;
        protected override Color? ParseExtra(string text) =>
            text.TrimStart().StartsWith("#", System.StringComparison.Ordinal) && ColorUtility.TryParseHtmlString(text.Trim(), out var c) ? c : (Color?)null;
    }
}
