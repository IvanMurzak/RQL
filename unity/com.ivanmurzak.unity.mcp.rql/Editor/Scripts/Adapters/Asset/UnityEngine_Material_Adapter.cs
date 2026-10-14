/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Rql.Execution;
using UnityEngine;
using UnityEngine.Rendering;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Material: safe members plus every shader property as a typed, validated map: <c>properties["_Color"]</c>.</summary>
    public sealed class UnityEngine_Material_Adapter : RqlTypeAdapter<Material>
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
}
