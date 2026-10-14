#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RqlTest.Eval
{
    /// <summary>
    /// Flat key → value view of the evaluation scene and its materials, used to detect changes a task did not ask for.
    /// Keys: "<path>|<what>" for GameObjects (path = names from the root), "mat:<name>|<property>" for materials.
    /// </summary>
    public static class SceneSnapshot
    {
        public static readonly string[] Materials = { "Assets/Materials/Red.mat", "Assets/Materials/Blue.mat", "Assets/Materials/Gold.mat" };

        public static Dictionary<string, string> Take()
        {
            var d = new Dictionary<string, string>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) Walk(root.transform, root.name, d);
            foreach (var path in Materials)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                var s = m.shader;
                for (int i = 0; i < s.GetPropertyCount(); i++)
                {
                    var n = s.GetPropertyName(i);
                    d[$"mat:{m.name}|{n}"] = s.GetPropertyType(i) switch
                    {
                        UnityEngine.Rendering.ShaderPropertyType.Color => C(m.GetColor(n)),
                        UnityEngine.Rendering.ShaderPropertyType.Vector => V(m.GetVector(n)),
                        UnityEngine.Rendering.ShaderPropertyType.Texture => m.GetTexture(n) == null ? "null" : m.GetTexture(n).name,
                        UnityEngine.Rendering.ShaderPropertyType.Int => m.GetInteger(n).ToString(CultureInfo.InvariantCulture),
                        _ => F(m.GetFloat(n)),
                    };
                }
            }
            return d;
        }

        /// <summary>Keys added, removed or changed between two snapshots.</summary>
        public static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after) =>
            before.Keys.Union(after.Keys)
                  .Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b)
                  .OrderBy(k => k, System.StringComparer.Ordinal).ToList();

        private static void Walk(Transform t, string path, Dictionary<string, string> d)
        {
            var go = t.gameObject;
            d[path + "|active"] = go.activeSelf ? "1" : "0";
            d[path + "|tag"] = go.tag;
            d[path + "|pos"] = V(t.position);
            d[path + "|rot"] = Euler(t.eulerAngles);
            d[path + "|scale"] = V(t.localScale);
            var comps = go.GetComponents<Component>().Where(c => c != null).ToList();
            d[path + "|components"] = string.Join(",", comps.Select(c => c.GetType().Name).OrderBy(n => n, System.StringComparer.Ordinal));
            foreach (var c in comps)
            {
                switch (c)
                {
                    case Light l:
                        d[path + "|Light.type"] = l.type.ToString(); d[path + "|Light.intensity"] = F(l.intensity);
                        d[path + "|Light.color"] = C(l.color); d[path + "|Light.range"] = F(l.range); break;
                    case Renderer r:
                        d[path + "|Renderer.materials"] = string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.name)); break;
                    case Rigidbody rb:
                        d[path + "|Rigidbody.mass"] = F(rb.mass); d[path + "|Rigidbody.useGravity"] = rb.useGravity ? "1" : "0";
                        d[path + "|Rigidbody.isKinematic"] = rb.isKinematic ? "1" : "0"; break;
                    case Collider col:
                        d[path + "|" + col.GetType().Name + ".isTrigger"] = col.isTrigger ? "1" : "0"; break;
                    case Enemy e:
                        d[path + "|Enemy.kind"] = e.kind.ToString(); d[path + "|Enemy.damage"] = e.damage.ToString(CultureInfo.InvariantCulture);
                        d[path + "|Enemy.aggroRadius"] = F(e.aggroRadius); d[path + "|Enemy.target"] = e.target == null ? "null" : e.target.name;
                        d[path + "|Enemy.lootTable"] = e.LootTable; break;
                    case Health h:
                        d[path + "|Health.current"] = h.current.ToString(CultureInfo.InvariantCulture); d[path + "|Health.max"] = h.max.ToString(CultureInfo.InvariantCulture);
                        d[path + "|Health.invulnerable"] = h.invulnerable ? "1" : "0"; d[path + "|Health.regen"] = F(h.RegenPerSecond); break;
                    case Spawner s:
                        d[path + "|Spawner.interval"] = F(s.interval); d[path + "|Spawner.maxAlive"] = s.maxAlive.ToString(CultureInfo.InvariantCulture);
                        d[path + "|Spawner.points"] = string.Join(",", s.spawnPoints.Select(p => p == null ? "null" : p.name)); break;
                }
            }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), path + "/" + t.GetChild(i).name, d);
        }

        /// <summary>Euler angles rounded to 0.1° and wrapped to [0, 360) so 359.99° and 0° compare equal.</summary>
        public static string Euler(Vector3 e)
        {
            static double W(float a) { var r = System.Math.Round(((a % 360) + 360) % 360, 1); return r >= 360 ? 0 : r; }
            return $"({W(e.x).ToString(CultureInfo.InvariantCulture)}, {W(e.y).ToString(CultureInfo.InvariantCulture)}, {W(e.z).ToString(CultureInfo.InvariantCulture)})";
        }

        private static string F(float f) => System.Math.Round(f, 3).ToString(CultureInfo.InvariantCulture);
        private static string V(Vector3 v, int digits = 2) => $"({R(v.x, digits)}, {R(v.y, digits)}, {R(v.z, digits)})";
        private static string V(Vector4 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)}, {F(v.w)})";
        private static string C(Color c) => $"({F(c.r)}, {F(c.g)}, {F(c.b)}, {F(c.a)})";

        private static string R(float f, int digits)
        {
            var r = System.Math.Round(f, digits);
            if (r == 0) r = 0; // normalise -0
            return r.ToString(CultureInfo.InvariantCulture);
        }
    }
}
