/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using UnityEditor;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>GameObject: components and children as members, <c>active</c> written through SetActive.</summary>
    public sealed class UnityEngine_GameObject_Adapter : RqlTypeAdapter<GameObject>
    {
        public override string Description => "GameObject: navigate with components / children; write active (SetActive), name, tag, layer";

        protected override void Members(MemberTable<GameObject> m)
        {
            m.Add("components", g => g.GetComponents<Component>().Where(c => c != null).ToList(),
                    description: "attached components (filter with $ is Light etc.)", owning: true)
             .Add("children", g => Children(g), description: "direct child GameObjects", owning: true)
             .Add<bool>("active", g => g.activeSelf, (g, v) => g.SetActive(v), description: "activeSelf; assigning calls SetActive")
             .Function<string, Component>("AddComponent", (g, type) => AddComponent(g, type), "type",
                 "add a component by type name (e.g. \"Rigidbody\", \"Light\", \"BoxCollider\", a script class) and return it")
             .Method("Destroy", g => Undo.DestroyObjectImmediate(g), "destroy this GameObject (undoable)");
            m.Untyped.ReferenceExisting("transform").HideExisting("gameObject", "transformHandle");
        }

        private static Component AddComponent(GameObject g, string typeName)
        {
            var type = ComponentTypes.Find(typeName)
                       ?? throw new ArgumentException($"no component type named '{typeName}'");
            return Undo.AddComponent(g, type);
        }

        private static List<GameObject> Children(GameObject g)
        {
            var t = g.transform;
            var list = new List<GameObject>(t.childCount);
            for (int i = 0; i < t.childCount; i++) list.Add(t.GetChild(i).gameObject);
            return list;
        }
    }
}
