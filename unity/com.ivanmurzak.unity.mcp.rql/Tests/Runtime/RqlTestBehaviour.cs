/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Tests
{
    /// <summary>A script with the member kinds the adapters handle (in its own file so Unity can serialize it).</summary>
    public class RqlTestBehaviour : MonoBehaviour
    {
        [SerializeField] private int secret = 7;
        [System.NonSerialized] public int transient = 3;
        [System.Obsolete("use speed")] public int legacy;
        public float speed = 1;
        public Light? lamp;
        public Transform? anchor;
        public Vector2 offset;
        public Vector4 weights;
        public Color tint = Color.white;
        public Gradient gradient = new Gradient();
    }
}
