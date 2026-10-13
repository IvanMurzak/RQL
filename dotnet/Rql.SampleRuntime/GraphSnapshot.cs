using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Execution;

namespace Rql.Sample
{
    /// <summary>
    /// Canonical flat dump of every scalar reachable through owning members: path → value text.
    /// Non-owning references are recorded as their $ref. Used by the hidden grader to verify final
    /// state exactly and to count unintended changes.
    /// </summary>
    public static class GraphSnapshot
    {
        public static Dictionary<string, string> Capture(RqlEngine engine, object root, string rootName)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Walk(engine, root, rootName, result, visited, 0);
            return result;
        }

        public static Dictionary<string, string> Capture(SampleHost host)
        {
            var w = Capture(host.Engine, host.Sandbox.World, "world");
            foreach (var kv in Capture(host.Engine, host.Sandbox.App, "app")) w[kv.Key] = kv.Value;
            return w;
        }

        /// <summary>Paths whose value differs, appeared or disappeared.</summary>
        public static List<string> Diff(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var keys = new SortedSet<string>(a.Keys, StringComparer.Ordinal);
            keys.UnionWith(b.Keys);
            return keys.Where(k => !a.TryGetValue(k, out var va) || !b.TryGetValue(k, out var vb) || va != vb).ToList();
        }

        private static void Walk(RqlEngine engine, object? v, string path, Dictionary<string, string> into, HashSet<object> visited, int depth)
        {
            if (depth > 64) return;
            if (v == null) { into[path] = "null"; return; }
            var t = v.GetType();
            if (TypeModel.IsScalarType(t)) { into[path] = Scalar(v); return; }
            var info = engine.Types.Info(t);
            if (!t.IsValueType && !visited.Add(v)) { into[path] = "→" + StableRef(engine, v); return; }
            if (info.IsMap)
            {
                into[path] = "map[" + (V2.Count(v)) + "]";
                foreach (var e in ((IEnumerable)v).Cast<object>().Select(Entry).OrderBy(e => Scalar(e.key), StringComparer.Ordinal))
                    Walk(engine, e.value, path + "[" + Scalar(e.key) + "]", into, visited, depth + 1);
                return;
            }
            if (info.IsList)
            {
                int i = 0;
                var items = ((IEnumerable)v).Cast<object?>().ToList();
                if (v.GetType().IsGenericType && v.GetType().GetGenericTypeDefinition() == typeof(HashSet<>))
                    items = items.OrderBy(x => x == null ? "" : Scalar(x), StringComparer.Ordinal).ToList();
                into[path] = "list[" + items.Count + "]";
                foreach (var el in items) Walk(engine, el, path + "[" + i++ + "]", into, visited, depth + 1);
                return;
            }
            into[path] = info.IsObject ? "obj " + info.ShortName : "struct " + info.ShortName;
            foreach (var m in info.Members)
            {
                object? mv;
                try { mv = m.Getter(v); } catch (Exception ex) { into[path + "." + m.Name] = "throws " + ex.GetType().Name; continue; }
                if (!m.Owning)
                {
                    if (mv == null) into[path + "." + m.Name] = "null";
                    else if (TypeModel.IsScalarType(mv.GetType())) into[path + "." + m.Name] = Scalar(mv);
                    else
                    {
                        var mi = engine.Types.Info(mv.GetType());
                        into[path + "." + m.Name] = mi.IsList || mi.IsMap
                            ? "refs[" + string.Join(",", ((IEnumerable)mv).Cast<object?>().Select(x => x == null ? "null" : StableRef(engine, x is DictionaryEntry de ? de.Value! : x))) + "]"
                            : "→" + StableRef(engine, mv);
                    }
                    continue;
                }
                Walk(engine, mv, path + "." + m.Name, into, visited, depth + 1);
            }
        }

        /// <summary>Session handles depend on query history, so identity-less objects are recorded by type only.</summary>
        private static string StableRef(RqlEngine engine, object o) =>
            engine.Types.Info(o.GetType()).IdMember != null ? engine.RefOf(o) : engine.Types.Info(o.GetType()).ShortName;

        private static (object? key, object? value) Entry(object kv)
        {
            if (kv is DictionaryEntry de) return (de.Key, de.Value);
            var t = kv.GetType();
            return (t.GetProperty("Key")!.GetValue(kv), t.GetProperty("Value")!.GetValue(kv));
        }

        private static string Scalar(object? v) => v switch
        {
            null => "null",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            IFormattable fo => fo.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };

        private static class V2
        {
            public static int Count(object v) => v is ICollection c ? c.Count : ((IEnumerable)v).Cast<object>().Count();
        }
    }
}
