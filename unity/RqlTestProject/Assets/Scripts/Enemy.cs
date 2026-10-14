using UnityEngine;

namespace RqlTest
{
    public enum EnemyKind { Grunt, Archer, Brute, Boss }

    /// <summary>A hostile character with a target and a loot table.</summary>
    public class Enemy : MonoBehaviour
    {
        public EnemyKind kind = EnemyKind.Grunt;
        public int damage = 10;
        public float aggroRadius = 8f;
        public GameObject target;
        [SerializeField] private string lootTable = "common";
        [SerializeField] private Color tint = Color.white;

        public string LootTable => lootTable;
    }
}
