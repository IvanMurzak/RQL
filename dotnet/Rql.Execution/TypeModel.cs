using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Rql.Execution
{
    public enum AccessorMode
    {
        /// <summary>Compile getters to delegates with System.Linq.Expressions (fast; needs JIT).</summary>
        Compiled,
        /// <summary>Plain PropertyInfo/FieldInfo.GetValue (AOT/IL2CPP safe).</summary>
        Reflection,
    }

    public enum TypeKind { Scalar, Enum, Object, Struct, List, Map }

    public sealed class MemberAccessor
    {
        internal MemberAccessor(MemberInfo member, Type type, Func<object, object?> getter, bool canWrite, bool owning, int order)
        {
            Member = member; Name = member.Name; Type = type; Getter = getter; CanWrite = canWrite; Owning = owning; Order = order;
            Description = member.GetCustomAttribute<RqlDescriptionAttribute>()?.Text;
        }

        public string Name { get; }
        public MemberInfo Member { get; }
        public Type Type { get; }
        public Func<object, object?> Getter { get; }
        public bool CanWrite { get; }
        /// <summary>False for [RqlReference] members — not followed by generic recurse.</summary>
        public bool Owning { get; }
        public int Order { get; }
        public string? Description { get; }

        public void SetValue(object target, object? value)
        {
            try
            {
                if (Member is PropertyInfo p) p.SetValue(target, value);
                else ((FieldInfo)Member).SetValue(target, value);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    public sealed class TypeInfo
    {
        internal TypeInfo(Type type) { Type = type; }

        public Type Type { get; }
        public TypeKind Kind { get; internal set; }
        public string ShortName { get; internal set; } = "";
        public string FriendlyName { get; internal set; } = "";
        public string? Description { get; internal set; }
        public IReadOnlyList<MemberAccessor> Members { get; internal set; } = Array.Empty<MemberAccessor>();
        internal Dictionary<string, MemberAccessor> ByName { get; set; } = new Dictionary<string, MemberAccessor>();
        internal Dictionary<string, MemberAccessor?> ByNameCI { get; set; } = new Dictionary<string, MemberAccessor?>(StringComparer.OrdinalIgnoreCase);
        public MemberAccessor? IdMember { get; internal set; }
        public MemberAccessor? DisplayMember { get; internal set; }
        public IReadOnlyCollection<string> TypeNames { get; internal set; } = Array.Empty<string>();
        public Type? ElementType { get; internal set; }
        public Type? KeyType { get; internal set; }
        internal Func<object, int>? CountGetter { get; set; }
        public IReadOnlyList<MethodInfo> Callable { get; internal set; } = Array.Empty<MethodInfo>();

        public bool IsList => Kind == TypeKind.List;
        public bool IsMap => Kind == TypeKind.Map;
        public bool IsStruct => Kind == TypeKind.Struct;
        public bool IsObject => Kind == TypeKind.Object;

        /// <summary>Exact ordinal match, then unique case-insensitive match (spec §3).</summary>
        public MemberAccessor? Find(string name)
        {
            if (ByName.TryGetValue(name, out var m)) return m;
            return ByNameCI.TryGetValue(name, out var ci) ? ci : null;
        }

        public bool IsA(string typeName) => TypeNames.Contains(typeName);
    }

    /// <summary>Cached, policy-filtered reflection metadata per CLR type.</summary>
    public sealed class TypeModel
    {
        public static readonly TypeModel Shared = new TypeModel(AccessorMode.Compiled);

        private readonly ConcurrentDictionary<Type, TypeInfo> _cache = new ConcurrentDictionary<Type, TypeInfo>();

        public TypeModel(AccessorMode mode) { Mode = mode; }

        public AccessorMode Mode { get; }

        public static TypeInfo Get(Type t) => Shared.Info(t);

        public TypeInfo Info(Type t) => _cache.GetOrAdd(t, Build);

        private static readonly HashSet<Type> ScalarTypes = new HashSet<Type>
        {
            typeof(string), typeof(char), typeof(bool), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset),
            typeof(TimeSpan), typeof(Guid), typeof(Uri),
        };

        public static bool IsScalarType(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || ScalarTypes.Contains(u);
        }

        private TypeInfo Build(Type t)
        {
            var info = new TypeInfo(t)
            {
                ShortName = ShortName(t),
                FriendlyName = FriendlyName(t),
                Description = t.GetCustomAttribute<RqlDescriptionAttribute>()?.Text,
            };

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var b = t; b != null && b != typeof(object); b = b.BaseType) names.Add(ShortName(b));
            foreach (var i in t.GetInterfaces()) names.Add(ShortName(i));
            info.TypeNames = names;

            if (t.IsEnum) { info.Kind = TypeKind.Enum; return info; }
            if (IsScalarType(t)) { info.Kind = TypeKind.Scalar; return info; }

            var dict = FindGeneric(t, typeof(IDictionary<,>)) ?? FindGeneric(t, typeof(IReadOnlyDictionary<,>));
            if (dict != null || typeof(IDictionary).IsAssignableFrom(t))
            {
                info.Kind = TypeKind.Map;
                info.KeyType = dict?.GetGenericArguments()[0];
                info.ElementType = dict?.GetGenericArguments()[1];
            }
            else if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                info.Kind = TypeKind.List;
                info.ElementType = t.IsArray ? t.GetElementType() : FindGeneric(t, typeof(IEnumerable<>))?.GetGenericArguments()[0];
            }
            else
            {
                info.Kind = t.IsValueType ? TypeKind.Struct : TypeKind.Object;
            }

            if (info.Kind == TypeKind.List || info.Kind == TypeKind.Map)
            {
                var countProp = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)
                                ?? FindGeneric(t, typeof(IReadOnlyCollection<>))?.GetProperty("Count")
                                ?? FindGeneric(t, typeof(ICollection<>))?.GetProperty("Count");
                if (countProp != null && countProp.PropertyType == typeof(int) && countProp.GetIndexParameters().Length == 0)
                {
                    var g = MakeGetter(countProp);
                    info.CountGetter = o => (int)g(o)!;
                }
                // Collections expose no members: .Count/.Length are handled by the evaluator (spec §3).
                return info;
            }

            BuildMembers(info, t);
            return info;
        }

        private void BuildMembers(TypeInfo info, Type t)
        {
            var chain = new List<Type>();
            for (var b = t; b != null && b != typeof(object) && b != typeof(ValueType); b = b.BaseType) chain.Add(b);
            chain.Reverse();

            var members = new List<MemberAccessor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int order = 0;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var decl in chain)
            {
                foreach (var f in decl.GetFields(flags).OrderBy(f => f.MetadataToken))
                {
                    if (f.IsDefined(typeof(RqlIgnoreAttribute)) || f.FieldType.IsByRefLike || IsDelegate(f.FieldType)) continue;
                    if (!seen.Add(f.Name)) continue;
                    bool canWrite = !f.IsInitOnly && !f.IsLiteral && !f.IsDefined(typeof(RqlReadOnlyAttribute));
                    members.Add(new MemberAccessor(f, f.FieldType, MakeGetter(f), canWrite, !f.IsDefined(typeof(RqlReferenceAttribute)), order++));
                }
                foreach (var p in decl.GetProperties(flags).OrderBy(p => p.MetadataToken))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetMethod == null || !p.GetMethod.IsPublic) continue;
                    if (p.IsDefined(typeof(RqlIgnoreAttribute)) || p.PropertyType.IsByRefLike || IsDelegate(p.PropertyType)) continue;
                    // Overridden/new properties: keep the most derived accessor, but the base declaration order.
                    var most = t.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance) ?? p;
                    if (!seen.Add(p.Name)) continue;
                    bool canWrite = most.SetMethod != null && most.SetMethod.IsPublic && !most.IsDefined(typeof(RqlReadOnlyAttribute));
                    members.Add(new MemberAccessor(most, most.PropertyType, MakeGetter(most), canWrite, !most.IsDefined(typeof(RqlReferenceAttribute)), order++));
                }
            }

            info.Members = members;
            info.ByName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var ci = new Dictionary<string, MemberAccessor?>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members) ci[m.Name] = ci.ContainsKey(m.Name) ? null : m;
            info.ByNameCI = ci;

            info.IdMember = members.FirstOrDefault(m => m.Member.IsDefined(typeof(RqlIdAttribute)))
                            ?? members.FirstOrDefault(m => m.Name == "Id" && IsIdType(m.Type));
            // Stable identities never change through RQL (refs would silently break).
            if (info.IdMember != null && info.IdMember.CanWrite)
            {
                var id = info.IdMember;
                var ro = new MemberAccessor(id.Member, id.Type, id.Getter, false, id.Owning, id.Order);
                members[members.IndexOf(id)] = ro;
                info.ByName[ro.Name] = ro;
                info.ByNameCI[ro.Name] = info.ByNameCI.TryGetValue(ro.Name, out var prev) && prev == null ? null : ro;
                info.IdMember = ro;
            }
            info.DisplayMember = members.FirstOrDefault(m => m.Member.IsDefined(typeof(RqlDisplayAttribute)))
                                 ?? members.FirstOrDefault(m => m.Name == "Name" && m.Type == typeof(string));
            info.Callable = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.IsDefined(typeof(RqlCallableAttribute)) && !m.IsGenericMethodDefinition)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsIdType(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u == typeof(int) || u == typeof(long) || u == typeof(string) || u == typeof(Guid) || u == typeof(uint) || u == typeof(ulong);
        }

        private static bool IsDelegate(Type t) => typeof(Delegate).IsAssignableFrom(t);

        private Func<object, object?> MakeGetter(MemberInfo m)
        {
            if (Mode == AccessorMode.Compiled)
            {
                try
                {
                    var obj = Expression.Parameter(typeof(object), "o");
                    var decl = m.DeclaringType!;
                    Expression target = decl.IsValueType ? Expression.Unbox(obj, decl) : (Expression)Expression.Convert(obj, decl);
                    Expression access = m is PropertyInfo p ? Expression.Property(target, p) : Expression.Field(target, (FieldInfo)m);
                    var body = Expression.Convert(access, typeof(object));
                    return Expression.Lambda<Func<object, object?>>(body, obj).Compile();
                }
                catch (PlatformNotSupportedException) { }
                catch (NotSupportedException) { }
                catch (InvalidOperationException) { }
            }
            if (m is PropertyInfo pi)
            {
                return o =>
                {
                    try { return pi.GetValue(o); }
                    catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
                };
            }
            var fi = (FieldInfo)m;
            return o => fi.GetValue(o);
        }

        private static Type? FindGeneric(Type t, Type generic)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic) return t;
            foreach (var i in t.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == generic) return i;
            }
            return null;
        }

        public static string ShortName(Type t)
        {
            if (t.IsArray) return ShortName(t.GetElementType()!) + "[]";
            var n = t.Name;
            int tick = n.IndexOf('`');
            return tick >= 0 ? n.Substring(0, tick) : n;
        }

        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            [typeof(int)] = "int", [typeof(long)] = "long", [typeof(short)] = "short", [typeof(byte)] = "byte",
            [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte",
            [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(bool)] = "bool",
            [typeof(string)] = "string", [typeof(char)] = "char", [typeof(object)] = "object",
        };

        /// <summary>C#-like readable type name: List&lt;Component&gt;, int?, Vector3.</summary>
        public static string FriendlyName(Type t)
        {
            if (Aliases.TryGetValue(t, out var a)) return a;
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) return FriendlyName(u) + "?";
            if (t.IsArray) return FriendlyName(t.GetElementType()!) + "[]";
            if (t.IsGenericType)
            {
                var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyName));
                return ShortName(t) + "<" + args + ">";
            }
            return t.Name;
        }
    }
}
