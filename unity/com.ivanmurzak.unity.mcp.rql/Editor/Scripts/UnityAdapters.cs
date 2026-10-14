/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rql.Execution;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor
{
    /// <summary>
    /// The Unity adapter set: what Unity-MCP customises through ReflectorNet converters, expressed as RQL type adapters
    /// (see RQL docs/type-adapters.md, "Unity-MCP converters → RQL adapters").
    /// </summary>
    public static class UnityAdapters
    {
        public static void Register(RqlEngine engine)
        {
            // Never expose native pointers / unmanaged views (crash or garbage when read).
            engine.ExcludeTypes(t => t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer
                                     || (t.IsGenericType && (t.GetGenericTypeDefinition().FullName ?? "").StartsWith("Unity.Collections.Native", StringComparison.Ordinal)));

            engine.AddAdapter(new UnityObjectAdapter())
                  .AddAdapter(new GameObjectAdapter())
                  .AddAdapter(new ComponentAdapter())
                  .AddAdapter(new MonoBehaviourAdapter())
                  .AddAdapter(new TransformAdapter())
                  .AddAdapter(new RendererAdapter())
                  .AddAdapter(new MeshFilterAdapter())
                  .AddAdapter(new ColliderAdapter())
                  .AddAdapter(new MaterialAdapter())
                  .AddAdapter(new SceneAdapter())
                  .AddAdapter(Vectors.Vector2())
                  .AddAdapter(Vectors.Vector3())
                  .AddAdapter(Vectors.Vector4())
                  .AddAdapter(Vectors.Quaternion())
                  .AddAdapter(Vectors.Color());
        }

        /// <summary>Keep only the named members that exist on this type (Unity versions differ).</summary>
        internal static MemberTable OnlyExisting(this MemberTable table, params string[] names) =>
            table.Only(names.Where(table.Contains).ToArray());

        internal static MemberTable HideExisting(this MemberTable table, params string[] names) =>
            table.Hide(names.Where(table.Contains).ToArray());

        internal static MemberTable ExplicitOnlyExisting(this MemberTable table, params string[] names) =>
            table.ExplicitOnly(names.Where(table.Contains).ToArray());

        internal static MemberTable ReferenceExisting(this MemberTable table, params string[] names) =>
            table.Reference(names.Where(table.Contains).ToArray());
    }

    /// <summary>
    /// Every UnityEngine.Object: identity by instance id ($ref "Type#id"), fake-null objects read as null, summaries show the
    /// lowercase <c>name</c>, private [SerializeField] fields are members, [Obsolete] / [NonSerialized] members are hidden.
    /// </summary>
    internal sealed class UnityObjectAdapter : RqlTypeAdapter
    {
        public UnityObjectAdapter() : base(typeof(Object)) { }

        // $ref ids are numeric: the raw entity id on Unity 6.5+ (EntityId.ToString() is "index:version" there, which does
        // not round-trip), the instance id before; RqlUnity resolves both.
        public override string? StableId(object value) =>
#if UNITY_6000_5_OR_NEWER
            value is Object o ? EntityId.ToULong(o.GetEntityId()).ToString(CultureInfo.InvariantCulture) : null;
#else
            value is Object o ? o.GetInstanceID().ToString(CultureInfo.InvariantCulture) : null;
#endif

        public override bool? IsAlive(object value) => value is Object o ? o != null : (bool?)null;

        public override void ConfigureMembers(MemberTable members) =>
            members
                .HideWhere(m => m.Member != null && (m.Member.IsDefined(typeof(ObsoleteAttribute), true) || m.Member.IsDefined(typeof(NonSerializedAttribute), true)))
                .IncludeFields(f => f.IsPrivate && f.IsDefined(typeof(SerializeField), false))
                .HideExisting("hideFlags")
                .Display("name");
    }

    /// <summary>GameObject: components and children as members, <c>active</c> written through SetActive.</summary>
    internal sealed class GameObjectAdapter : RqlTypeAdapter<GameObject>
    {
        public override string Description => "GameObject: navigate with components / children; write active (SetActive), name, tag, layer";

        protected override void Members(MemberTable<GameObject> m)
        {
            m.Add("components", g => g.GetComponents<Component>().Where(c => c != null).ToList(),
                    description: "attached components (filter with $ is Light etc.)", owning: true)
             .Add("children", g => Children(g), description: "direct child GameObjects", owning: true)
             .Add<bool>("active", g => g.activeSelf, (g, v) => g.SetActive(v), description: "activeSelf; assigning calls SetActive");
            m.Untyped.ReferenceExisting("transform").HideExisting("gameObject", "transformHandle");
        }

        private static List<GameObject> Children(GameObject g)
        {
            var t = g.transform;
            var list = new List<GameObject>(t.childCount);
            for (int i = 0; i < t.childCount; i++) list.Add(t.GetChild(i).gameObject);
            return list;
        }
    }

    /// <summary>Components: the back-pointers are references (not followed by generic recurse).</summary>
    internal sealed class ComponentAdapter : RqlTypeAdapter<Component>
    {
        protected override void Members(MemberTable<Component> m) =>
            m.Untyped.ReferenceExisting("gameObject", "transform").HideExisting("transformHandle");
    }

    /// <summary>MonoBehaviour: hide engine bookkeeping members so scripts show their own fields.</summary>
    internal sealed class MonoBehaviourAdapter : RqlTypeAdapter<MonoBehaviour>
    {
        protected override void Members(MemberTable<MonoBehaviour> m) =>
            m.Untyped.HideExisting("destroyCancellationToken", "useGUILayout", "runInEditMode", "didStart", "didAwake");
    }

    /// <summary>Transform: parent / root are references; it is an object although it enumerates its children.</summary>
    internal sealed class TransformAdapter : RqlTypeAdapter<Transform>
    {
        protected override void Members(MemberTable<Transform> m) => m.Untyped.ReferenceExisting("parent", "root");
    }

    /// <summary>Renderer: material(s) instantiate copies when read — explicit-only; sharedMaterials returns a copy — written back.</summary>
    internal sealed class RendererAdapter : RqlTypeAdapter<Renderer>
    {
        protected override void Members(MemberTable<Renderer> m) =>
            m.Untyped.ExplicitOnlyExisting("material", "materials").CopyOnRead("sharedMaterials");
    }

    /// <summary>MeshFilter: mesh instantiates a copy when read — explicit-only.</summary>
    internal sealed class MeshFilterAdapter : RqlTypeAdapter<MeshFilter>
    {
        protected override void Members(MemberTable<MeshFilter> m) => m.Untyped.ExplicitOnlyExisting("mesh");
    }

    /// <summary>Collider (physics module, targeted by name): material instantiates a copy — explicit-only.</summary>
    internal sealed class ColliderAdapter : RqlTypeAdapter
    {
        public ColliderAdapter() : base("UnityEngine.Collider") { }

        public override void ConfigureMembers(MemberTable members) => members.ExplicitOnlyExisting("material");
    }

    /// <summary>Material: safe members plus every shader property as a typed, validated map: <c>properties["_Color"]</c>.</summary>
    internal sealed class MaterialAdapter : RqlTypeAdapter<Material>
    {
        public override string Description => "Material: shader properties via properties[\"_Name\"] (colors, vectors, floats, textures, ints)";

        protected override void Members(MemberTable<Material> m)
        {
            m.Untyped.OnlyExisting("name", "shader", "renderQueue", "enableInstancing", "doubleSidedGI", "globalIlluminationFlags", "passCount");
            m.Untyped.AddMixedMap("properties", o => Keys((Material)o), (o, k) => EntryType((Material)o, k),
                (o, k) => Get((Material)o, k), (o, k, v) => Set((Material)o, k, v), (o, k, v) => Validate((Material)o, k, v),
                "shader properties by name", has: (o, k) => ((Material)o).HasProperty(k));
        }

        private static IEnumerable<string> Keys(Material m)
        {
            var shader = m.shader;
            if (shader == null) yield break;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++) yield return shader.GetPropertyName(i);
        }

        private static ShaderPropertyType? Kind(Material m, string key)
        {
            var shader = m.shader;
            if (shader == null) return null;
            int i = shader.FindPropertyIndex(key);
            return i < 0 ? (ShaderPropertyType?)null : shader.GetPropertyType(i);
        }

        private static Type EntryType(Material m, string key) => Kind(m, key) switch
        {
            ShaderPropertyType.Color => typeof(Color),
            ShaderPropertyType.Vector => typeof(Vector4),
            ShaderPropertyType.Float => typeof(float),
            ShaderPropertyType.Range => typeof(float),
            ShaderPropertyType.Texture => typeof(Texture),
            ShaderPropertyType.Int => typeof(int),
            _ => typeof(object),
        };

        private static object? Get(Material m, string key) => Kind(m, key) switch
        {
            ShaderPropertyType.Color => m.GetColor(key),
            ShaderPropertyType.Vector => m.GetVector(key),
            ShaderPropertyType.Float => m.GetFloat(key),
            ShaderPropertyType.Range => m.GetFloat(key),
            ShaderPropertyType.Texture => m.GetTexture(key),
            ShaderPropertyType.Int => m.GetInteger(key),
            _ => null,
        };

        private static void Set(Material m, string key, object? value)
        {
            switch (Kind(m, key))
            {
                case ShaderPropertyType.Color: m.SetColor(key, (Color)value!); break;
                case ShaderPropertyType.Vector: m.SetVector(key, (Vector4)value!); break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: m.SetFloat(key, (float)value!); break;
                case ShaderPropertyType.Texture: m.SetTexture(key, (Texture?)value); break;
                case ShaderPropertyType.Int: m.SetInteger(key, (int)value!); break;
                default: throw new InvalidOperationException($"shader property '{key}' has an unsupported type");
            }
        }

        private static string? Validate(Material m, string key, object? value)
        {
            if (Kind(m, key) != ShaderPropertyType.Range || !(value is float f)) return null;
            var limits = m.shader.GetPropertyRangeLimits(m.shader.FindPropertyIndex(key));
            return f < limits.x || f > limits.y
                ? $"{key} must be within [{limits.x.ToString(CultureInfo.InvariantCulture)}, {limits.y.ToString(CultureInfo.InvariantCulture)}]"
                : null;
        }
    }

    /// <summary>Scene (struct): a few safe members plus its root GameObjects.</summary>
    internal sealed class SceneAdapter : RqlTypeAdapter<Scene>
    {
        protected override void Members(MemberTable<Scene> m)
        {
            m.Untyped.OnlyExisting("name", "path", "buildIndex", "isLoaded", "isDirty", "rootCount");
            m.Add("roots", s => s.GetRootGameObjects().ToList(), description: "root GameObjects", owning: true);
        }
    }

    /// <summary>
    /// Float structs (Vector2/3/4, Quaternion, Color): compact "(x, y, z)" output; input as "(x, y, z)", [x, y, z] or an object
    /// literal {x: 1, y: 2, z: 3} (default construction); approximate equality like Unity's own == operators.
    /// </summary>
    internal sealed class FloatStructAdapter<T> : RqlTypeAdapter<T> where T : struct
    {
        private static readonly Regex Tuple = new Regex(@"^\s*\(([^()]*)\)\s*$", RegexOptions.CultureInvariant);
        private readonly Func<T, float[]> _parts;
        private readonly Func<float[], T?> _make;
        private readonly string _description;
        private readonly Func<string, T?>? _parseExtra;
        private readonly string _expected;

        public FloatStructAdapter(Func<T, float[]> parts, Func<float[], T?> make, string description, string expected, Func<string, T?>? parseExtra = null)
        {
            _parts = parts; _make = make; _description = description; _expected = expected; _parseExtra = parseExtra;
        }

        public override string Description => _description;

        protected override JsonNode? Encode(T value, EncodeContext context) =>
            JsonValue.Create("(" + string.Join(", ", _parts(value).Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + ")");

        public override AdapterOutcome TryCoerce(object? value, Type target, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            if (value is ObjectValue) return AdapterOutcome.NotHandled; // {x, y, z}: default construction assigns the fields
            if (TryParse(value, out var v)) { result = v; return AdapterOutcome.Ok; }
            reason = "expected " + _expected;
            return AdapterOutcome.Failed;
        }

        protected override bool? Equal(T value, object? other)
        {
            if (!TryParse(other, out var o)) return null;
            var a = _parts(value);
            var b = _parts(o);
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        private bool TryParse(object? value, out T result)
        {
            result = default;
            switch (value)
            {
                case T t:
                    result = t;
                    return true;
                case string s:
                {
                    var extra = _parseExtra?.Invoke(s);
                    if (extra.HasValue) { result = extra.Value; return true; }
                    var m = Tuple.Match(s);
                    if (!m.Success) return false;
                    var numbers = new List<float>();
                    foreach (var part in m.Groups[1].Value.Split(','))
                    {
                        if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;
                        numbers.Add(f);
                    }
                    return Make(numbers, out result);
                }
                case IList<object?> list:
                {
                    var numbers = new List<float>();
                    foreach (var e in list)
                    {
                        if (!(e is double d)) return false;
                        numbers.Add((float)d);
                    }
                    return Make(numbers, out result);
                }
                default:
                    return false;
            }
        }

        private bool Make(List<float> numbers, out T result)
        {
            var made = _make(numbers.ToArray());
            result = made ?? default;
            return made.HasValue;
        }
    }

    internal static class Vectors
    {
        public static RqlTypeAdapter Vector2() => new FloatStructAdapter<Vector2>(
            v => new[] { v.x, v.y }, f => f.Length == 2 ? new Vector2(f[0], f[1]) : (Vector2?)null,
            "Vector2: write as \"(x, y)\", [x, y] or {x: 1, y: 2}", "(x, y) or [x, y]");

        public static RqlTypeAdapter Vector3() => new FloatStructAdapter<Vector3>(
            v => new[] { v.x, v.y, v.z }, f => f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : (Vector3?)null,
            "Vector3: write as \"(x, y, z)\", [x, y, z] or {x: 1, y: 2, z: 3}", "(x, y, z) or [x, y, z]");

        public static RqlTypeAdapter Vector4() => new FloatStructAdapter<Vector4>(
            v => new[] { v.x, v.y, v.z, v.w }, f => f.Length == 4 ? new Vector4(f[0], f[1], f[2], f[3]) : (Vector4?)null,
            "Vector4: write as \"(x, y, z, w)\" or [x, y, z, w]", "(x, y, z, w) or [x, y, z, w]");

        public static RqlTypeAdapter Quaternion() => new FloatStructAdapter<Quaternion>(
            q => new[] { q.x, q.y, q.z, q.w }, f => f.Length == 4 ? new Quaternion(f[0], f[1], f[2], f[3]) : (Quaternion?)null,
            "Quaternion: (x, y, z, w); prefer eulerAngles for rotations, e.g. set transform.eulerAngles = \"(0, 90, 0)\"", "(x, y, z, w) or [x, y, z, w]");

        public static RqlTypeAdapter Color() => new FloatStructAdapter<Color>(
            c => new[] { c.r, c.g, c.b, c.a },
            f => f.Length == 4 ? new Color(f[0], f[1], f[2], f[3]) : f.Length == 3 ? new Color(f[0], f[1], f[2]) : (Color?)null,
            "Color: write as \"(r, g, b, a)\" with 0..1 components, [r, g, b] or \"#RRGGBB\"", "(r, g, b, a), [r, g, b] or #RRGGBB",
            s => s.TrimStart().StartsWith("#", StringComparison.Ordinal) && ColorUtility.TryParseHtmlString(s.Trim(), out var c) ? c : (Color?)null);
    }
}
