/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
// UAC0005 (Unity 6.6 analyzer) suggests CurrentAssemblies APIs that older supported Unity versions lack; the scan only reads type metadata.
#pragma warning disable UAC0005
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor
{
    /// <summary>
    /// Compact schema for agents. RQL's generic schema walks every reachable type and subclass, which for UnityEngine means
    /// hundreds of component types; here the overview lists what the loaded scenes actually contain, and a type name shows
    /// one type's members (any loaded type, present in the scene or not).
    /// </summary>
    public static class RqlUnitySchema
    {
        private static readonly ConcurrentDictionary<string, Type[]> ByShortName = new ConcurrentDictionary<string, Type[]>(StringComparer.OrdinalIgnoreCase);

        public static string Render(RqlEngine engine, string? typeName)
        {
            if (typeName != null) return RenderType(engine, typeName);

            var sb = new StringBuilder();
            sb.Append("sources:\n");
            foreach (var s in engine.Sources.Values)
                sb.Append("  ").Append(s.Name).Append(s.Description != null ? " — " + s.Description : "").Append('\n');
            sb.Append("functions: unity.assets(filter, folder?) — AssetDatabase search, e.g. \"t:Material\", \"t:Prefab Enemy\" (max ")
              .Append(RqlUnity.MaxAssets).Append("); unity.asset(path) — one asset\n");

            var counts = new Dictionary<Type, int>();
            foreach (var go in RqlUnity.AllGameObjects())
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    counts[t] = counts.TryGetValue(t, out var n) ? n + 1 : 1;
                }
            sb.Append("component types in the loaded scenes (rql-schema \"<type>\" for members): ");
            sb.Append(string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal)
                .Select(kv => kv.Key.Name + " ×" + kv.Value)));
            sb.Append('\n');
            AppendType(sb, engine, typeof(GameObject));
            AppendType(sb, engine, typeof(Transform), skipMembersOf: typeof(Component)); // name / tag / gameObject… are on GameObject above
            return sb.ToString().TrimEnd();
        }

        private static string RenderType(RqlEngine engine, string typeName)
        {
            var candidates = ByShortName.GetOrAdd(typeName, Find);
            if (candidates.Length == 0)
            {
                var known = KnownNames();
                var close = known.Where(k => k.StartsWith(typeName.Substring(0, Math.Min(3, typeName.Length)), StringComparison.OrdinalIgnoreCase))
                                 .Take(5).ToList();
                return $"error unknown-type: no type '{typeName}'" + (close.Count > 0 ? " — did you mean " + string.Join(" / ", close) + "?" : "");
            }
            var sb = new StringBuilder();
            foreach (var t in candidates)
            {
                // Components list their own members; the shared Object / Component / Behaviour ones are summarised once.
                if (typeof(Component).IsAssignableFrom(t) && t != typeof(Component))
                {
                    AppendType(sb, engine, t, skipMembersOf: typeof(MonoBehaviour));
                    sb.Append("  (+ inherited: name, tag, gameObject, transform").Append(typeof(Behaviour).IsAssignableFrom(t) ? ", enabled, isActiveAndEnabled" : "").Append(")\n");
                }
                else AppendType(sb, engine, t);
            }
            return sb.ToString().TrimEnd();
        }

        private static void AppendType(StringBuilder sb, RqlEngine engine, Type t, Type? skipMembersOf = null)
        {
            var info = engine.Types.Info(t);
            sb.Append(info.Kind == TypeKind.Struct ? "struct " : info.Kind == TypeKind.Enum ? "enum " : "").Append(TypeModel.FriendlyName(t));
            if (info.Kind == TypeKind.Enum)
            {
                sb.Append(": ").Append(string.Join(" ", Enum.GetNames(t))).Append('\n');
                return;
            }
            if (t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType))
                sb.Append(" : ").Append(t.BaseType.Name);
            sb.Append(" {");
            var parts = new List<string>();
            foreach (var m in info.Members)
            {
                if (skipMembersOf != null && m.DeclaringType.IsAssignableFrom(skipMembersOf)) continue;
                var part = m.Name + " " + TypeModel.FriendlyName(m.Type);
                if (!m.CanWrite) part += " ro";
                if (m.ExplicitOnly) part += " explicit-only";
                if (m.Description != null) part += " (" + m.Description + ")";
                parts.Add(part);
            }
            sb.Append(string.Join(", ", parts)).Append('}');
            if (info.Callable.Count > 0) sb.Append(" methods: ").Append(string.Join(" | ", info.Callable.Select(c => c.Signature)));
            if (info.Description != null) sb.Append(" — ").Append(info.Description);
            sb.Append('\n');
        }

        private static Type[] Find(string name)
        {
            var hits = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (!t.IsPublic || t.IsGenericTypeDefinition) continue;
                    if (!string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(t) || (t.IsValueType && t.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true) || t.IsEnum)
                        hits.Add(t);
                }
            }
            // Prefer UnityEngine types, then project scripts.
            return hits.OrderBy(t => t.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) == true ? 0 : 1).Take(3).ToArray();
        }

        private static IEnumerable<string> KnownNames() =>
            RqlUnity.AllGameObjects().SelectMany(g => g.GetComponents<Component>()).Where(c => c != null)
                .Select(c => c.GetType().Name).Distinct().OrderBy(n => n, StringComparer.Ordinal);
    }
}
