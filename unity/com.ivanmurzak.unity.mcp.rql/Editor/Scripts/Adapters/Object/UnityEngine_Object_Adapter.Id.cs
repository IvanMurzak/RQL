/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
#if UNITY_6000_5_OR_NEWER
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    public sealed partial class UnityEngine_Object_Adapter
    {
        // Unity 6.5+ (current): the raw EntityId. EntityId.ToString() is "index:version" there and does not round-trip.
        private static string IdOf(Object o) => EntityId.ToULong(o.GetEntityId()).ToString(CultureInfo.InvariantCulture);
    }
}
#endif
