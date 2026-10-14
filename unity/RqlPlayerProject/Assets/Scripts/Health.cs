using Rql;
using UnityEngine;

namespace RqlPlayer
{
    /// <summary>Test component: public fields, a property and an [RqlCallable] method, as user scripts have.</summary>
    public class Health : MonoBehaviour
    {
        public float current = 100;
        public float max = 100;
        public string Label { get; set; } = "unit";

        [RqlCallable("deal damage")]
        public void Damage(float amount) => current -= amount;
    }
}
