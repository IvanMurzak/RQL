/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Component types by short or full name (Unity's TypeCache: engine, packages and project scripts).</summary>
    public static class ComponentTypes
    {
        private static readonly ConcurrentDictionary<string, Type?> Cache = new ConcurrentDictionary<string, Type?>(StringComparer.OrdinalIgnoreCase);

        public static Type? Find(string name) => Cache.GetOrAdd(name, n =>
        {
            var candidates = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition
                            && (string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase) || string.Equals(t.FullName, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            // Prefer an exact-case match, then UnityEngine types, then project scripts.
            return candidates.OrderBy(t => t.Name == n || t.FullName == n ? 0 : 1)
                             .ThenBy(t => t.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true ? 0 : 1)
                             .FirstOrDefault();
        });
    }
}
