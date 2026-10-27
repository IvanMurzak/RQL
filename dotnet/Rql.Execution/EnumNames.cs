using System;
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

        /// <summary>
        /// Resolve <paramref name="text"/> against enum <paramref name="type"/>. Integer text is the underlying value (not
        /// checked for being defined); a flags enum accepts a comma-separated list. <paramref name="why"/> explains a failure.
        /// </summary>
        internal static Outcome Resolve(Type type, string text, IReadOnlyDictionary<string, string>? aliases, out object? value, out string? why)
        {
            value = null;
            why = null;
            var s = text.Trim();
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                value = Enum.ToObject(type, n);
                return Outcome.Found;
            }
            if (s.IndexOf(',') >= 0 && type.IsDefined(typeof(FlagsAttribute), false))
            {
                ulong acc = 0;
                foreach (var part in s.Split(','))
                {
                    var o = ResolveName(type, part.Trim(), aliases, out var pv, out why);
                    if (o != Outcome.Found) return o;
                    acc |= ToBits(pv!);
                }
                value = Enum.ToObject(type, acc);
                return Outcome.Found;
            }
            return ResolveName(type, s, aliases, out value, out why);
        }

        /// <summary>The "expected one of …" reason listing the entry names.</summary>
        internal static string Expected(Type type) => "expected one of " + string.Join(", ", Enum.GetNames(type));

        private static Outcome ResolveName(Type type, string s, IReadOnlyDictionary<string, string>? aliases, out object? value, out string? why)
        {
            var names = Enum.GetNames(type);
            value = null;
            why = null;
            List<string>? found = Step(names, aliases, s, StringComparison.Ordinal, prefix: false);
            if (found == null)
            {
                int q = s.LastIndexOf("::", StringComparison.Ordinal);
                int cut = q >= 0 ? q + 2 : s.LastIndexOf('.') + 1;
                if (cut > 0 && cut < s.Length)
                {
                    var part = s.Substring(cut);
                    found = Step(names, aliases, part, StringComparison.Ordinal, prefix: false)
                        ?? Step(names, aliases, part, StringComparison.OrdinalIgnoreCase, prefix: false)
                        ?? Step(names, null, part, StringComparison.OrdinalIgnoreCase, prefix: true);
                }
            }
            found ??= Step(names, aliases, s, StringComparison.OrdinalIgnoreCase, prefix: false)
                ?? Step(names, null, s, StringComparison.OrdinalIgnoreCase, prefix: true);
            if (found == null)
            {
                why = Expected(type);
                return Outcome.NotFound;
            }
            // Distinct entries: names sharing one value (C# `None = 0, Default = 0`) are one entry.
            var values = found.Select(f => Enum.Parse(type, f)).Distinct().ToList();
            if (values.Count > 1)
            {
                why = $"\"{s}\" is ambiguous for {type.Name}: {string.Join(", ", found)}";
                return Outcome.Ambiguous;
            }
            value = values[0];
            return Outcome.Found;
        }

        /// <summary>The entry names one step matches (aliases mapped to their entry), or null when none.</summary>
        private static List<string>? Step(string[] names, IReadOnlyDictionary<string, string>? aliases, string s, StringComparison cmp, bool prefix)
        {
            List<string>? found = null;
            void Add(string entry)
            {
                found ??= new List<string>();
                if (!found.Contains(entry)) found.Add(entry);
            }
            foreach (var name in names)
                if (string.Equals(prefix ? WithoutPrefix(name) : name, s, cmp)) Add(name);
            if (aliases != null)
                foreach (var kv in aliases)
                    if (string.Equals(kv.Key, s, cmp) && Array.IndexOf(names, kv.Value) >= 0) Add(kv.Value);
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
