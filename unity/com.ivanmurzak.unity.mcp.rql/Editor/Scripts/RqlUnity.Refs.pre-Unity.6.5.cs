/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
#if !UNITY_6000_5_OR_NEWER
using System.Globalization;
using UnityEditor;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor
{
    public static partial class RqlUnity
    {
        /// <summary>Unity before 6.5: <c>Type#id</c> where id is the instance id; the engine checks the type.</summary>
        private static object? ResolveStableRef(string type, string id)
        {
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId)) return null;
            var o = EditorUtility.InstanceIDToObject(instanceId);
            return o == null ? null : o;
        }
    }
}
#endif
