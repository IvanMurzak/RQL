/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters;
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Modules
{
    /// <summary>
    /// The Unity core: sources, asset functions, the adapters for UnityEngine core types and the type blacklist. Optional
    /// Unity packages (Particle System, Terrain, Splines …) contribute their own modules from separate assemblies that only
    /// compile when the package is present, so they appear and disappear with the package.
    /// </summary>
    public sealed class UnityCoreModule : IRqlModule
    {
        public int Order => 0;

        public void Configure(RqlModuleContext ctx)
        {
            ctx.AddSource("objects", RqlUnity.AllGameObjects, "every GameObject in the loaded scenes, depth-first, inactive ones included")
               .AddSource("roots", RqlUnity.RootGameObjects, "root GameObjects of the loaded scenes")
               .AddSource("scenes", RqlUnity.Scenes, "loaded scenes")
               .AddSource("selection", RqlUnity.SelectedObjects, "objects selected in the Editor")
               .AddFunction("unity.assets", RqlUnity.FindAssets)
               .AddFunction("unity.asset", RqlUnity.LoadAsset);

            // Transform enumerates its children, but RQL must treat it as an object with members.
            ctx.TreatAsObject(typeof(Transform));

            // Never expose native pointers / unmanaged views (crash or garbage when read).
            ctx.ExcludeTypes(t => t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer
                                  || (t.IsGenericType && (t.GetGenericTypeDefinition().FullName ?? "").StartsWith("Unity.Collections.Native", StringComparison.Ordinal)));

            // UnityEngine.Object family
            ctx.AddAdapter(new UnityEngine_Object_Adapter())
               .AddAdapter(new UnityEngine_GameObject_Adapter())
               .AddAdapter(new UnityEngine_Component_Adapter())
               .AddAdapter(new UnityEngine_MonoBehaviour_Adapter())
               .AddAdapter(new UnityEngine_Transform_Adapter())
               .AddAdapter(new UnityEngine_Renderer_Adapter())
               .AddAdapter(new UnityEngine_MeshFilter_Adapter())
               .AddAdapter(new UnityEngine_Collider_Adapter());

            // Assets
            ctx.AddAdapter(new UnityEngine_Material_Adapter());

            // Structs
            ctx.AddAdapter(new UnityEngine_Vector2_Adapter())
               .AddAdapter(new UnityEngine_Vector3_Adapter())
               .AddAdapter(new UnityEngine_Vector4_Adapter())
               .AddAdapter(new UnityEngine_Quaternion_Adapter())
               .AddAdapter(new UnityEngine_Color_Adapter())
               .AddAdapter(new UnityEngine_Gradient_Adapter())
               .AddAdapter(new UnityEngine_Scene_Adapter());
        }
    }
}
