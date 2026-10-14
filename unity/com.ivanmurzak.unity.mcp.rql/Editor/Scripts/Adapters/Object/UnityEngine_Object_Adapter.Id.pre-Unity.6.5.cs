/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
#if !UNITY_6000_5_OR_NEWER
using System.Globalization;
using Object = UnityEngine.Object;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    public sealed partial class UnityEngine_Object_Adapter
    {
        // Unity before 6.5: the instance id.
        private static string IdOf(Object o) => o.GetInstanceID().ToString(CultureInfo.InvariantCulture);
    }
}
#endif
