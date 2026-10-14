using UnityEngine;

namespace RqlTest
{
    /// <summary>Hit points of a character (player or enemy).</summary>
    public class Health : MonoBehaviour
    {
        public int current = 100;
        public int max = 100;
        [SerializeField] private float regenPerSecond = 1.5f;
        public bool invulnerable;

        public float RegenPerSecond => regenPerSecond;
    }
}
