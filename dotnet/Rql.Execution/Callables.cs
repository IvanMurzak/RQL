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
            Func<object, object?[], object?> invoke, string? description = null, Func<object, object?[], string?>? validate = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("method name required", nameof(name));
            Name = name; Parameters = parameters; ReturnType = returnType; _invoke = invoke; Description = description; Validate = validate;
        }

        public string Name { get; }
        public IReadOnlyList<CallableParameter> Parameters { get; }
        /// <summary><c>typeof(void)</c> for methods without a result.</summary>
        public Type ReturnType { get; }
        public string? Description { get; }

        /// <summary>
        /// Optional check of a planned call (target, coerced arguments): the rejection reason, or null. The <c>call</c> stage runs
        /// it for every target before the first call, on dry runs too, so a call that would fail is refused with nothing called
        /// (spec §8).
        /// </summary>
        public Func<object, object?[], string?>? Validate { get; }

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
            // Companion validator: a public instance method Validate<Name> with the same parameter types returning string.
            var types = m.GetParameters().Select(p => p.ParameterType).ToArray();
            var companion = m.ReflectedType?.GetMethod("Validate" + m.Name, BindingFlags.Public | BindingFlags.Instance, null, types, null);
            Func<object, object?[], string?>? validate = companion != null && companion.ReturnType == typeof(string)
                ? (target, args) => (string?)Invoke(companion, target, args)
                : (Func<object, object?[], string?>?)null;
            return new CallableMethod(m.Name, ps, m.ReturnType, (target, args) => Invoke(m, target, args), description, validate);
        }

        private static object? Invoke(MethodInfo m, object target, object?[] args)
        {
            try { return m.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
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
        /// <summary>Declared value type of one entry (mixed-type maps), or null for the map's value type.</summary>
        Type? EntryType(string key);
        /// <summary>Rejection reason for assigning <paramref name="value"/> to <paramref name="key"/>, or null.</summary>
        string? ValidateEntry(string key, object? value);
    }

    /// <summary>The callbacks of one virtual map member, shared by every owner's view.</summary>
    internal sealed class VirtualMapSpec<TValue>
    {
        public Func<object, IEnumerable<string>> Keys = null!;
        public Func<object, string, TValue> Get = null!;
        public Action<object, string, TValue>? Set;
        public Func<object, string, TValue, string?>? Validate;
        public Func<object, string, Type>? EntryType;
        /// <summary>Optional O(1) key test; without it lookups scan <see cref="Keys"/>.</summary>
        public Func<object, string, bool>? Has;
    }

    public sealed class VirtualMap<TValue> : IDictionary<string, TValue>, IDictionary, IReadOnlyDictionary<string, TValue>, IVirtualMap
    {
        private readonly object _owner;
        private readonly VirtualMapSpec<TValue> _spec;

        internal VirtualMap(object owner, VirtualMapSpec<TValue> spec) { _owner = owner; _spec = spec; }

        private IEnumerable<string> KeyList => _spec.Keys(_owner);

        Type? IVirtualMap.EntryType(string key) => _spec.EntryType?.Invoke(_owner, key);
        string? IVirtualMap.ValidateEntry(string key, object? value) => _spec.Validate?.Invoke(_owner, key, (TValue)value!);
        public bool IsReadOnly => _spec.Set == null;

        public TValue this[string key]
        {
            get => ContainsKey(key) ? _spec.Get(_owner, key) : throw new KeyNotFoundException(key);
            set
            {
                if (_spec.Set == null) throw new NotSupportedException("read-only map");
                _spec.Set(_owner, key, value);
            }
        }

        public ICollection<string> Keys => KeyList.ToList();
        public ICollection<TValue> Values => KeyList.Select(k => _spec.Get(_owner, k)).ToList();
        IEnumerable<string> IReadOnlyDictionary<string, TValue>.Keys => KeyList;
        IEnumerable<TValue> IReadOnlyDictionary<string, TValue>.Values => Values;
        public int Count => KeyList.Count();
        public bool ContainsKey(string key) => _spec.Has != null ? _spec.Has(_owner, key) : KeyList.Contains(key, StringComparer.Ordinal);

        public bool TryGetValue(string key, out TValue value)
        {
            if (ContainsKey(key)) { value = _spec.Get(_owner, key); return true; }
            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            foreach (var k in KeyList.ToList()) yield return new KeyValuePair<string, TValue>(k, _spec.Get(_owner, k));
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
        ICollection IDictionary.Keys => KeyList.ToList();
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
