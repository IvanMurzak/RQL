using System.Collections.Generic;
using UnityEngine;

namespace RqlTest
{
    /// <summary>Spawns enemies at points; some points may be left unassigned (null).</summary>
    public class Spawner : MonoBehaviour
    {
        public GameObject prefab;
        public List<Transform> spawnPoints = new List<Transform>();
        public float interval = 3f;
        public int maxAlive = 5;
    }
}
