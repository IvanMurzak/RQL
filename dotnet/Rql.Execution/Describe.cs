using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    /// <summary>Type descriptors for `describe` and the host schema (capability-manifest.schema.json#typeDescriptor).</summary>
    internal static class Describe
    {
        public static QueryResult Run(Exec x, IEnumerable<object?> values)
        {
            var types = new List<Type>();
            foreach (var v in values)
            {
                if (v == null || V.IsAbsent(v)) continue;
                var t = v is RowValue ? typeof(RowValue) : v.GetType();
                if (!types.Contains(t)) types.Add(t);
                if (types.Count >= 8) break;
            }
            var arr = new JsonArray();
            foreach (var t in types) arr.Add(TypeDescriptor(x.Types, t, x.Engine.Mutations.Enabled));
            return new QueryResult { Ok = true, Kind = ResultKind.Schema, Types = arr };
        }

        public static JsonObject TypeDescriptor(TypeModel model, Type t, bool showWritable)
        {
            var info = model.Info(t);
            var o = new JsonObject { ["name"] = info.ShortName };
            var baseType = t.BaseType;
            if (baseType != null && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
                o["base"] = TypeModel.ShortName(baseType);
            o["kind"] = info.Kind switch
            {
                TypeKind.Enum => "enum",
                TypeKind.Struct => "struct",
                _ => "object",
            };
            if (info.Description != null) o["description"] = info.Description;
            if (info.Kind == TypeKind.Enum)
            {
                o["values"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                o["members"] = new JsonArray();
                return o;
            }
            var members = new JsonArray();
            foreach (var m in info.Members)
            {
                var mo = new JsonObject { ["name"] = m.Name, ["type"] = TypeModel.FriendlyName(m.Type) };
                if (showWritable && m.CanWrite) mo["writable"] = true;
                if (m.Description != null) mo["description"] = m.Description;
                members.Add(mo);
            }
            o["members"] = members;
            if (info.Callable.Count > 0)
                o["methods"] = new JsonArray(info.Callable.Select(mi => (JsonNode)JsonValue.Create(Signature(mi))!).ToArray());
            return o;
        }

        public static string Signature(MethodInfo m) =>
            m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => TypeModel.FriendlyName(p.ParameterType) + " " + p.Name)) + ")";

        /// <summary>
        /// All types statically reachable from the given roots through member types, element types and
        /// concrete subclasses defined in the same assembly (polymorphic collections).
        /// </summary>
        public static List<Type> Reachable(TypeModel model, IEnumerable<Type> roots, int max = 200)
        {
            var result = new List<Type>();
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>(roots);
            while (queue.Count > 0 && result.Count < max)
            {
                var t = queue.Dequeue();
                t = Nullable.GetUnderlyingType(t) ?? t;
                if (!seen.Add(t)) continue;
                var info = model.Info(t);
                if (info.Kind == TypeKind.List || info.Kind == TypeKind.Map)
                {
                    if (info.KeyType != null) queue.Enqueue(info.KeyType);
                    if (info.ElementType != null) queue.Enqueue(info.ElementType);
                    continue;
                }
                if (info.Kind == TypeKind.Scalar || t == typeof(object)) continue;
                result.Add(t);
                foreach (var m in info.Members) queue.Enqueue(m.Type);
                if (info.Kind == TypeKind.Object)
                {
                    Type[] defined;
                    try { defined = t.Assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { defined = ex.Types.Where(x => x != null).ToArray()!; }
                    foreach (var sub in defined)
                    {
                        if (sub != t && t.IsAssignableFrom(sub) && sub.IsPublic && !sub.IsGenericTypeDefinition) queue.Enqueue(sub);
                    }
                }
            }
            return result;
        }
    }
}
