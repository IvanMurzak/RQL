#nullable enable
using System.IO;
using RqlTest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RqlTest.EditorTools
{
    /// <summary>
    /// Builds the deterministic experiment scene (Assets/Scenes/RqlTest.unity) and its materials. Re-run any time to reset the
    /// scene: menu Tools/RQL/Build Test Scene, or batch mode:
    /// Unity -batchmode -quit -projectPath RqlTestProject -executeMethod RqlTest.EditorTools.RqlTestSceneBuilder.Build
    /// </summary>
    public static class RqlTestSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/RqlTest.unity";
        private const string MaterialFolder = "Assets/Materials";

        [MenuItem("Tools/RQL/Build Test Scene")]
        public static void Build()
        {
            Selection.objects = new Object[0];
            if (AssetDatabase.IsValidFolder("Assets/EvalOut")) AssetDatabase.DeleteAsset("Assets/EvalOut");
            Directory.CreateDirectory("Assets/Scenes");
            Directory.CreateDirectory(MaterialFolder);
            var red = MaterialAsset("Red", new Color(0.9f, 0.1f, 0.1f));
            var blue = MaterialAsset("Blue", new Color(0.1f, 0.3f, 0.9f));
            var gold = MaterialAsset("Gold", new Color(1f, 0.8f, 0.2f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            camera.transform.position = new Vector3(0, 8, -12);
            camera.transform.eulerAngles = new Vector3(30, 0, 0);

            var sun = new GameObject("Directional Light", typeof(Light));
            var sunLight = sun.GetComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.intensity = 1f;
            sun.transform.eulerAngles = new Vector3(50, -30, 0);

            var lamps = new GameObject("Lamps");
            Lamp(lamps, "Lamp_A", new Vector3(-6, 3, 0), 0.5f, new Color(1f, 0.9f, 0.7f));
            Lamp(lamps, "Lamp_B", new Vector3(0, 3, 6), 2.5f, new Color(0.6f, 0.8f, 1f));
            Lamp(lamps, "Lamp_C", new Vector3(6, 3, 0), 4f, new Color(1f, 0.4f, 0.2f));

            var player = Primitive(PrimitiveType.Capsule, "Player", null, new Vector3(0, 1, 0), blue);
            player.tag = "Player";
            var playerHealth = player.AddComponent<Health>();
            playerHealth.current = 87;
            playerHealth.max = 120;

            var enemies = new GameObject("Enemies");
            var kinds = new[] { EnemyKind.Grunt, EnemyKind.Grunt, EnemyKind.Archer, EnemyKind.Brute, EnemyKind.Archer, EnemyKind.Boss };
            for (int i = 0; i < kinds.Length; i++)
            {
                var e = Primitive(PrimitiveType.Cube, $"Enemy_{i + 1:00}", enemies.transform, new Vector3(-7.5f + i * 3, 0.5f, 5 + (i % 2) * 2), kinds[i] == EnemyKind.Boss ? gold : red);
                var enemy = e.AddComponent<Enemy>();
                enemy.kind = kinds[i];
                enemy.damage = 5 + i * 7;
                enemy.aggroRadius = kinds[i] == EnemyKind.Archer ? 15f : 8f;
                enemy.target = i % 3 == 2 ? null : player;
                var hp = e.AddComponent<Health>();
                hp.max = kinds[i] == EnemyKind.Boss ? 500 : kinds[i] == EnemyKind.Brute ? 200 : 60;
                hp.current = hp.max - i * 9;
                if (i == 4) e.SetActive(false);
                if (kinds[i] == EnemyKind.Boss) e.transform.localScale = new Vector3(2, 2, 2);
            }

            var spawner = new GameObject("Spawner");
            spawner.transform.position = new Vector3(0, 0, 12);
            var sp = spawner.AddComponent<Spawner>();
            sp.prefab = enemies.transform.GetChild(0).gameObject;
            for (int i = 0; i < 3; i++)
            {
                var point = new GameObject($"SpawnPoint_{i + 1}");
                point.transform.SetParent(spawner.transform, false);
                point.transform.localPosition = new Vector3(-4 + i * 4, 0, 0);
                sp.spawnPoints.Add(point.transform);
            }
            sp.spawnPoints.Add(null); // an unassigned slot: "find missing references"

            var level = new GameObject("Level");
            var ground = Primitive(PrimitiveType.Plane, "Ground", level.transform, Vector3.zero, null);
            ground.transform.localScale = new Vector3(4, 1, 4);
            for (int i = 0; i < 4; i++)
                Primitive(PrimitiveType.Cylinder, $"Pillar_{i + 1}", level.transform, new Vector3(i < 2 ? -10 : 10, 2, i % 2 == 0 ? -10 : 10), gold).transform.localScale = new Vector3(1, 2, 1);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[RQL] test scene built: {ScenePath}");
        }

        private static GameObject Primitive(PrimitiveType type, string name, Transform? parent, Vector3 position, Material? material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            if (material != null) go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private static void Lamp(GameObject parent, string name, Vector3 position, float intensity, Color color)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = position;
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.intensity = intensity;
            light.color = color;
            light.range = 12f;
        }

        private static Material MaterialAsset(string name, Color color)
        {
            var path = $"{MaterialFolder}/{name}.mat";
            // Always reset every property (evaluation runs change glossiness, metallic, alpha …).
            var fresh = new Material(Shader.Find("Standard")) { color = color, name = name };
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) { AssetDatabase.CreateAsset(fresh, path); return fresh; }
            EditorUtility.CopySerialized(fresh, existing);
            existing.name = name;
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(fresh);
            return existing;
        }
    }
}
