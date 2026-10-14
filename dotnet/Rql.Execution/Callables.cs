using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Rql.Execution
{
    /// <summary>One parameter of a <see cref="CallableMethod"/>.</summary>
    public sealed class CallableParameter
    {
        public CallableParameter(string name, Type type, bool hasDefault = false, object? defaultValue = null)
        {
            Name = name; Type = type; HasDefault = hasDefault; Default = defaultValue;
        }

        public string Name { get; }
        public Type Type { get; }
        public bool HasDefault { get; }
        public object? Default { get; }
    }

    /// <summary>
    /// A method the <c>call</c> stage may invoke: a reflected <c>[RqlCallable]</c> method, a CLR method exposed by an adapter
    /// (for engine types that cannot carry attributes), or a pure adapter method (spec §13).
    /// </summary>
    public sealed class CallableMethod
    {
        private readonly Func<object, object?[], object?> _invoke;

        public CallableMethod(string name, IReadOnlyList<CallableParameter> parameters, Type returnType,
            Func<object, object?[], object?> invoke, string? description = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("method name required", nameof(name));
            Name = name; Parameters = parameters; ReturnType = returnType; _invoke = invoke; Description = description;
        }

        public string Name { get; }
        public IReadOnlyList<CallableParameter> Parameters { get; }
        /// <summary><c>typeof(void)</c> for methods without a result.</summary>
        public Type ReturnType { get; }
        public string? Description { get; }

        public string Signature =>
            Name + "(" + string.Join(", ", Parameters.Select(p => TypeModel.FriendlyName(p.Type) + " " + p.Name)) + ")";

        /// <summary>Invoke with already-coerced arguments; exceptions are the target's own (never TargetInvocationException).</summary>
        public object? Invoke(object target, object?[] args)
        {
            var r = _invoke(target, args);
            return ReturnType == typeof(void) ? null : r;
        }

        internal static CallableMethod FromReflection(MethodInfo m, string? description)
        {
            var ps = m.GetParameters()
                .Select(p => new CallableParameter(p.Name ?? "arg", p.ParameterType, p.HasDefaultValue, p.HasDefaultValue ? p.DefaultValue : null))
                .ToArray();
            return new CallableMethod(m.Name, ps, m.ReturnType, (target, args) =>
            {
                try { return m.Invoke(target, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
            }, description);
        }
    }

    /// <summary>
    /// A virtual map member: keys and per-key get/set provided by an adapter (e.g. a Material's shader floats). Reads
    /// and writes use the ordinary map paths (<c>Floats["_Glossiness"]</c>, <c>set Floats["_Glossiness"] = 0.3</c>),
    /// so coercion, validation, dry-run and rollback apply as for any other map.
    /// </summary>
    internal interface IVirtualMap
    {
        bool IsReadOnly { get; }
        /// <summary>Rejection reason for assigning <paramref name="value"/> to <paramref name="key"/>, or null.</summary>
        string? ValidateEntry(string key, object? value);
    }

    public sealed class VirtualMap<TValue> : IDictionary<string, TValue>, IDictionary, IReadOnlyDictionary<string, TValue>, IVirtualMap
    {
        private readonly object _owner;
        private readonly Func<object, IEnumerable<string>> _keys;
        private readonly Func<object, string, TValue> _get;
        private readonly Action<object, string, TValue>? _set;

        internal VirtualMap(object owner, Func<object, IEnumerable<string>> keys, Func<object, string, TValue> get,
            Action<object, string, TValue>? set, Func<object, string, TValue, string?>? validate)
        {
            _owner = owner; _keys = keys; _get = get; _set = set; Validate = validate;
        }

        /// <summary>Per-entry validation (rejection reason or null), run while a mutation is planned.</summary>
        internal Func<object, string, TValue, string?>? Validate { get; }
        internal object Owner => _owner;
        public bool IsReadOnly => _set == null;

        string? IVirtualMap.ValidateEntry(string key, object? value) => Validate?.Invoke(_owner, key, (TValue)value!);

        public TValue this[string key]
        {
            get => ContainsKey(key) ? _get(_owner, key) : throw new KeyNotFoundException(key);
            set
            {
                if (_set == null) throw new NotSupportedException("read-only map");
                _set(_owner, key, value);
            }
        }

        public ICollection<string> Keys => _keys(_owner).ToList();
        public ICollection<TValue> Values => _keys(_owner).Select(k => _get(_owner, k)).ToList();
        IEnumerable<string> IReadOnlyDictionary<string, TValue>.Keys => _keys(_owner);
        IEnumerable<TValue> IReadOnlyDictionary<string, TValue>.Values => Values;
        public int Count => _keys(_owner).Count();
        public bool ContainsKey(string key) => _keys(_owner).Contains(key, StringComparer.Ordinal);

        public bool TryGetValue(string key, out TValue value)
        {
            if (ContainsKey(key)) { value = _get(_owner, key); return true; }
            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            foreach (var k in _keys(_owner).ToList()) yield return new KeyValuePair<string, TValue>(k, _get(_owner, k));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Entries are defined by the owner: the map can be read and assigned, not grown or shrunk structurally.
        public void Add(string key, TValue value) => this[key] = value;
        public bool Remove(string key) => throw new NotSupportedException("entries of a virtual map cannot be removed");
        public void Add(KeyValuePair<string, TValue> item) => this[item.Key] = item.Value;
        public void Clear() => throw new NotSupportedException("entries of a virtual map cannot be removed");
        public bool Contains(KeyValuePair<string, TValue> item) => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);
        public void CopyTo(KeyValuePair<string, TValue>[] array, int arrayIndex) { foreach (var kv in this) array[arrayIndex++] = kv; }
        public bool Remove(KeyValuePair<string, TValue> item) => throw new NotSupportedException("entries of a virtual map cannot be removed");

        // Non-generic IDictionary (the engine's map write path).
        object? IDictionary.this[object key]
        {
            get => key is string s && TryGetValue(s, out var v) ? v : null;
            set => this[(string)key] = (TValue)value!;
        }
        bool IDictionary.IsFixedSize => true;
        ICollection IDictionary.Keys => _keys(_owner).ToList();
        ICollection IDictionary.Values => (ICollection)Values;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        void IDictionary.Add(object key, object? value) => this[(string)key] = (TValue)value!;
        bool IDictionary.Contains(object key) => key is string s && ContainsKey(s);
        IDictionaryEnumerator IDictionary.GetEnumerator() => new Enumerator(GetEnumerator());
        void IDictionary.Remove(object key) => throw new NotSupportedException("entries of a virtual map cannot be removed");
        void ICollection.CopyTo(Array array, int index) { foreach (var kv in this) array.SetValue(new DictionaryEntry(kv.Key, kv.Value), index++); }

        private sealed class Enumerator : IDictionaryEnumerator
        {
            private readonly IEnumerator<KeyValuePair<string, TValue>> _inner;
            public Enumerator(IEnumerator<KeyValuePair<string, TValue>> inner) { _inner = inner; }
            public DictionaryEntry Entry => new DictionaryEntry(_inner.Current.Key, _inner.Current.Value);
            public object Key => _inner.Current.Key;
            public object? Value => _inner.Current.Value;
            public object Current => Entry;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
        }
    }
}
