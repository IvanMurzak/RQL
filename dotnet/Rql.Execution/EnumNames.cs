using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Rql.Execution
{
    /// <summary>
    /// Enum name resolution (spec §8): how a string written to, used as a key of, or compared with an enum picks one
    /// entry — exact name or host alias, then the part after a <c>Type::</c> / <c>Type.</c> qualifier, then ignoring case,
    /// then ignoring the entry prefix (<c>HAlign_</c>). Several candidates at the deciding step are ambiguous.
    /// </summary>
    internal static class EnumNames
    {
        internal enum Outcome { Found, NotFound, Ambiguous }

        private readonly struct Result
        {
            public Result(Outcome outcome, object? value, string? why) { Outcome = outcome; Value = value; Why = why; }
            public Outcome Outcome { get; }
            public object? Value { get; }
            public string? Why { get; }
        }

        /// <summary>Per enum type: entry names, their values and their names without the entry prefix (null: none).</summary>
        private sealed class Entries
        {
            public Entries(Type type)
            {
                Names = Enum.GetNames(type);
                Values = Names.Select(n => Enum.Parse(type, n)).ToArray();
                Unprefixed = Names.Select(WithoutPrefix).ToArray();
            }

            public string[] Names { get; }
            public object[] Values { get; }
            public string?[] Unprefixed { get; }
        }

        private static readonly ConcurrentDictionary<Type, Entries> EntriesOf = new ConcurrentDictionary<Type, Entries>();

        // Comparisons resolve the same literal once per item: memoize per (type, host aliases, text), bounded.
        private static readonly ConcurrentDictionary<(Type, object?, string), Result> Memo = new ConcurrentDictionary<(Type, object?, string), Result>();
        private const int MemoLimit = 4096;

        /// <summary>
        /// Resolve <paramref name="text"/> against enum <paramref name="type"/>. Integer text is the underlying value (not
        /// checked for being defined); a flags enum accepts a comma-separated list. <paramref name="why"/> explains a failure.
        /// </summary>
        internal static Outcome Resolve(Type type, string text, IReadOnlyDictionary<string, string>? aliases, out object? value, out string? why)
        {
            var key = (type, (object?)aliases, text);
            if (!Memo.TryGetValue(key, out var r))
            {
                r = Compute(type, text, aliases);
                if (Memo.Count >= MemoLimit) Memo.Clear();
                Memo[key] = r;
            }
            value = r.Value;
            why = r.Why;
            return r.Outcome;
        }

        /// <summary>The "expected one of …" reason listing the entry names.</summary>
        internal static string Expected(Type type) => "expected one of " + string.Join(", ", EntriesOf.GetOrAdd(type, t => new Entries(t)).Names);

        private static Result Compute(Type type, string text, IReadOnlyDictionary<string, string>? aliases)
        {
            var s = text.Trim();
            if (TryInteger(type, s, out var number, out bool isInteger)) return new Result(Outcome.Found, number, null);
            if (isInteger) return new Result(Outcome.NotFound, null, Expected(type)); // out of the underlying type's range
            var entries = EntriesOf.GetOrAdd(type, t => new Entries(t));
            if (s.IndexOf(',') >= 0 && type.IsDefined(typeof(FlagsAttribute), false))
            {
                ulong acc = 0;
                foreach (var part in s.Split(','))
                {
                    var r = ResolveName(type, entries, part.Trim(), aliases);
                    if (r.Outcome != Outcome.Found) return r;
                    acc |= ToBits(r.Value!);
                }
                return new Result(Outcome.Found, Enum.ToObject(type, acc), null);
            }
            return ResolveName(type, entries, s, aliases);
        }

        /// <summary>Integer text as a value of the enum's underlying type; <paramref name="isInteger"/> also when out of range.</summary>
        private static bool TryInteger(Type type, string s, out object? value, out bool isInteger)
        {
            value = null;
            int digits = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
            isInteger = digits < s.Length && s.Skip(digits).All(c => c >= '0' && c <= '9');
            if (!isInteger) return false;
            object parsed;
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) parsed = n;
            else if (ulong.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var u)) parsed = u;
            else return false; // beyond 64 bits
            try { value = Enum.ToObject(type, Convert.ChangeType(parsed, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)); }
            catch (OverflowException) { return false; }
            return true;
        }

        private static Result ResolveName(Type type, Entries entries, string s, IReadOnlyDictionary<string, string>? aliases)
        {
            List<int>? found = Step(entries, aliases, s, StringComparison.Ordinal, prefix: false);
            if (found == null)
            {
                int q = s.LastIndexOf("::", StringComparison.Ordinal);
                int cut = q >= 0 ? q + 2 : s.LastIndexOf('.') + 1;
                if (cut > 0 && cut < s.Length)
                {
                    var part = s.Substring(cut);
                    found = Step(entries, aliases, part, StringComparison.Ordinal, prefix: false)
                        ?? Step(entries, aliases, part, StringComparison.OrdinalIgnoreCase, prefix: false)
                        ?? Step(entries, null, part, StringComparison.OrdinalIgnoreCase, prefix: true);
                }
            }
            found ??= Step(entries, aliases, s, StringComparison.OrdinalIgnoreCase, prefix: false)
                ?? Step(entries, null, s, StringComparison.OrdinalIgnoreCase, prefix: true);
            if (found == null) return new Result(Outcome.NotFound, null, Expected(type));
            // Distinct entries: names sharing one value (C# `None = 0, Default = 0`) are one entry.
            var first = entries.Values[found[0]];
            if (found.Any(i => !entries.Values[i].Equals(first)))
                return new Result(Outcome.Ambiguous, null, $"\"{s}\" is ambiguous for {type.Name}: {string.Join(", ", found.Select(i => entries.Names[i]))}");
            return new Result(Outcome.Found, first, null);
        }

        /// <summary>The indexes of the entries one step matches (aliases mapped to their entry), or null when none.</summary>
        private static List<int>? Step(Entries entries, IReadOnlyDictionary<string, string>? aliases, string s, StringComparison cmp, bool prefix)
        {
            List<int>? found = null;
            void Add(int i)
            {
                found ??= new List<int>();
                if (!found.Contains(i)) found.Add(i);
            }
            var names = prefix ? entries.Unprefixed : entries.Names;
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], s, cmp)) Add(i);
            if (aliases != null)
                foreach (var kv in aliases)
                    if (string.Equals(kv.Key, s, cmp))
                    {
                        int i = Array.IndexOf(entries.Names, kv.Value);
                        if (i >= 0) Add(i);
                    }
            return found;
        }

        /// <summary>The entry name without its entry prefix (text up to and including the first '_'), or null.</summary>
        private static string? WithoutPrefix(string name)
        {
            int i = name.IndexOf('_');
            return i > 0 && i < name.Length - 1 ? name.Substring(i + 1) : null;
        }

        private static ulong ToBits(object e) =>
            Type.GetTypeCode(Enum.GetUnderlyingType(e.GetType())) switch
            {
                TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(e, CultureInfo.InvariantCulture)),
                _ => Convert.ToUInt64(e, CultureInfo.InvariantCulture),
            };
    }
}
