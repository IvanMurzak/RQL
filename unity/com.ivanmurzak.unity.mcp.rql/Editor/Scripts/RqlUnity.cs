/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Execution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor
{
    /// <summary>
    /// The RQL engine over the live Editor state: sources, asset functions, stable references by instance id, Unity type
    /// adapters and Editor integration for writes (Undo before, SetDirty after). One engine per domain (static fields reset
    /// on domain reload). Every call must run on the main thread.
    /// </summary>
    public static class RqlUnity
    {
        /// <summary>Most assets <c>unity.assets(...)</c> loads per call (loading assets is expensive).</summary>
        public const int MaxAssets = 200;

        private static RqlEngine? _engine;

        public static RqlEngine Engine => _engine ??= Create();

        /// <summary>Drop the engine (tests, or after changing adapters).</summary>
        public static void Reset() => _engine = null;

        public static RqlEngine Create()
        {
            // Transform enumerates its children, but RQL must treat it as an object with members.
            TypeModel.TreatAsObject(typeof(Transform));

            var engine = new RqlEngine(AccessorMode.Compiled) { RuntimeName = "unity" };
            engine.AddSource("objects", AllGameObjects, "every GameObject in the loaded scenes, depth-first, inactive ones included");
            engine.AddSource("roots", RootGameObjects, "root GameObjects of the loaded scenes");
            engine.AddSource("scenes", LoadedScenes, "loaded scenes");
            engine.AddSource("selection", () => Selection.objects.Where(o => o != null).ToList(), "objects selected in the Editor");
            engine.AddFunction("unity.assets", FindAssets);
            engine.AddFunction("unity.asset", LoadAsset);
            engine.ResolveStableRef = ResolveStableRef;

            engine.Mutations.Enabled = true;
            engine.Mutations.BeforeWrite = records =>
            {
                var owners = Owners(records);
                if (owners.Length > 0) Undo.RecordObjects(owners, "RQL mutation");
            };
            engine.Mutations.Committed = records =>
            {
                foreach (var o in Owners(records))
                {
                    EditorUtility.SetDirty(o);
                    var scene = o is GameObject go ? go.scene : o is Component c ? c.gameObject.scene : default;
                    if (scene.IsValid() && !EditorApplication.isPlaying) EditorSceneManager.MarkSceneDirty(scene);
                }
            };

            UnityAdapters.Register(engine);
            return engine;
        }

        private static Object[] Owners(IReadOnlyList<MutationRecord> records) =>
            records.Select(r => r.Owner as Object).Where(o => o != null).Distinct().ToArray()!;

        // ------------------------------------------------------------ sources

        public static List<Scene> Scenes()
        {
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) scenes.Add(s);
            }
            return scenes;
        }

        private static List<Scene> LoadedScenes() => Scenes();

        public static List<GameObject> RootGameObjects()
        {
            var roots = new List<GameObject>();
            foreach (var s in Scenes()) roots.AddRange(s.GetRootGameObjects());
            return roots;
        }

        public static List<GameObject> AllGameObjects()
        {
            var all = new List<GameObject>();
            foreach (var root in RootGameObjects()) Collect(root.transform, all);
            return all;
        }

        private static void Collect(Transform t, List<GameObject> into)
        {
            into.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
        }

        // ------------------------------------------------------------ functions

        /// <summary><c>unity.assets("t:Material", folder?)</c>: assets matching an AssetDatabase filter (at most <see cref="MaxAssets"/>).</summary>
        private static object? FindAssets(object?[] args)
        {
            if (args.Length < 1 || !(args[0] is string filter)) return new RqlError("type-mismatch", "unity.assets(filter, folder?) expects a filter string such as \"t:Material\"");
            var folders = args.Length > 1 && args[1] is string folder ? new[] { folder } : null;
            var guids = folders == null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
            var result = new List<Object>();
            foreach (var guid in guids.Take(MaxAssets))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) result.Add(asset);
            }
            return result;
        }

        /// <summary><c>unity.asset("Assets/…/Red.mat")</c>: the main asset at a path, or null.</summary>
        private static object? LoadAsset(object?[] args)
        {
            if (args.Length != 1 || !(args[0] is string path)) return new RqlError("type-mismatch", "unity.asset(path) expects an asset path");
            return AssetDatabase.LoadMainAssetAtPath(path);
        }

        // ------------------------------------------------------------ references

        /// <summary><c>Type#id</c> where id is the entity / instance id (see <see cref="UnityAdapters"/>); the engine checks the type.</summary>
        private static object? ResolveStableRef(string type, string id)
        {
            if (!ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return null;
#if UNITY_6000_5_OR_NEWER
            var o = EditorUtility.EntityIdToObject(com.IvanMurzak.Unity.MCP.Runtime.Utils.EntityIdUtils.FromString(id));
#else
            var o = EditorUtility.InstanceIDToObject(int.Parse(id, CultureInfo.InvariantCulture));
#endif
            return o == null ? null : o;
        }
    }
}
