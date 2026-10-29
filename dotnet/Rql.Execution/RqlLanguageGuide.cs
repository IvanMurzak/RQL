using System;

namespace Rql.Execution
{
    /// <summary>
    /// Agent-facing text shared by every RQL host: the language rules that do not depend on the host's sources or types
    /// (boolean conditions, ?:, list equality, group items, receipts, call and bulk create). Hosts append
    /// <see cref="Rules"/> to their own guide instead of copying it, so the rules do not drift. The same text ships with the
    /// TypeScript (LANGUAGE_RULES) and C++ (rql::language_rules()) runtimes; fixtures/guide/language-rules.txt is the source.
    /// </summary>
    public static class RqlLanguageGuide
    {
        public const string Rules =
            "Language rules:\n" +
            "- Conditions are booleans. where, and / or / not, ?: and the predicates of .any() .all() .count() .first() take a comparison or has(), never a bare value: where material != null (or has(material)), where count > 0, where name != \"\", where len(tags) > 0. A number, string, object or list there fails with type-mismatch; null counts as false.\n" +
            "- cond ? a : b (the same as if(cond, a, b)) picks a value and evaluates only the taken branch: set y = (y < 0 ? 0 : y). Inside select, wrap computed values in parentheses: select kind = (intensity > 2 ? \"bright\" : \"dim\").\n" +
            "- Lists compare by value, in order: tags == [\"a\", \"b\"], x in [[1, 2], [3, 4]]; distinct and group-by merge equal lists.\n" +
            "- group-by gives rows with key (or one column per key), count and the group's items: group-by kind | select key, count, avg = items.avg(weight); also items.sum(e), items.min(e), items.max(e), items.count(pred), items.first().name. Group by the item's type with $type (group-by $type); a bare type is a member that most items do not have.\n" +
            "- Receipts list only real changes: a set row whose value stayed the same is left out and counted as unchanged, so no rows with N unchanged means the values already matched.\n" +
            "- call Method(args) runs the method of each item (the items must be objects). call source.Method(args) runs the method of that one source object once per stream item, with $, $index, $path and $depth in the arguments being the item's; every call counts against the target limit, and every argument is checked before the first call (a dry run checks without calling).\n" +
            "- Bulk create: range(start, end[, step]) streams numbers (end excluded), and a list literal streams its elements, so one call creates many objects: range(0, 300) | call editor.CreateObject(\"Cube\", {position: [($ % 15) * 2, 0, floor($ / 15) * 2]}) builds a 15-wide grid, [[\"A\", 1], [\"B\", 2]] | call editor.CreateObject($[0], $[1]) one object per entry (editor.CreateObject stands for the host's source and creation method). The receipt has one row per call with the $ref of the created object.\n" +
            "- Vectors and colors the host marks as component-wise compute component by component: set Scale = Scale * 1.5, Position + [0, 1, 0], Position - Offset, -Velocity, v / 2. Adding a number to a vector is a type-mismatch (add [x, y, z] instead); quaternions and other types without that mark stay type-mismatch. Lists of numbers are vectors too: [x, 0, z] * k, [1, 2] + [3, 4]. Vector functions work on both: length(v) (or v.length(), magnitude), normalize(v), distance(a, b), dot(a, b), lerp(a, b, t); len(x) stays the size of a string or list.\n" +
            "- A missing map entry reads as null: props[\"k\"] == null and props[\"k\"] is null are true without the entry, and has(props[\"k\"]) tests whether it exists. A misspelt member is not null: it stays unknown and is reported.\n" +
            "- Arguments by name: call Method({name: value, ...}) with one object literal that no overload takes as a whole binds each key to the parameter of that name; parameters left out keep their defaults, and an unknown name fails listing the parameters: range(0, 10) | call editor.CreateObject({name: \"Tile\" + $, position: [$ * 2, 0, 0]}). Engine objects (scene objects, nodes, actors) are never built from an object literal: pass an existing one, e.g. ref(\"Type#id\"). Value-like assets and owned subobjects that the host marks are built from a typed literal: set mesh = {$type: \"TypeName\", size: 2}.\n";

        private const string ExampleCall = "editor.CreateObject";
        private const string ExampleNote = " (editor.CreateObject stands for the host's source and creation method)";
        private const string GridExample = "editor.CreateObject(\"Cube\", {position: [($ % 15) * 2, 0, floor($ / 15) * 2]})";
        private const string PerEntryExample = "[[\"A\", 1], [\"B\", 2]] | call editor.CreateObject($[0], $[1])";
        private const string NamedExample = "editor.CreateObject({name: \"Tile\" + $, position: [$ * 2, 0, 0]})";

        /// <summary>
        /// <see cref="Rules"/> with the host's own creation call in the examples (e.g. <c>"editor.CreateGameObject"</c>,
        /// <c>"godot.CreateNode"</c>) instead of the placeholder <c>editor.CreateObject</c>, whose "stands for" note is then left
        /// out. Null, empty or blank returns <see cref="Rules"/> unchanged.
        /// </summary>
        public static string RulesFor(string? createCall) => RulesWith(new RqlRuleExamples { CreateCall = createCall });

        /// <summary>
        /// <see cref="Rules"/> with the host's examples (<see cref="RqlRuleExamples"/>), for hosts whose creation call does not
        /// take <c>(name, {properties})</c>; the "stands for" note is left out once anything is given. Nothing given (null,
        /// empty or blank): <see cref="Rules"/> unchanged. Every text is trimmed.
        /// </summary>
        public static string RulesWith(RqlRuleExamples? examples)
        {
            string? Given(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
            var call = Given(examples?.CreateCall);
            var grid = Given(examples?.Grid);
            var perEntry = Given(examples?.PerEntry);
            var named = Given(examples?.Named);
            if (call == null && grid == null && perEntry == null && named == null) return Rules;
            // An example the host leaves out keeps the default, with its creation call.
            var text = ReplaceOnce(Rules, ExampleNote, "");
            foreach (var (fallback, given) in new[] { (GridExample, grid), (PerEntryExample, perEntry), (NamedExample, named) })
                text = ReplaceOnce(text, fallback, given ?? (call == null ? fallback : fallback.Replace(ExampleCall, call)));
            return text;
        }

        private static string ReplaceOnce(string text, string from, string to)
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            return at < 0 ? text : text.Substring(0, at) + to + text.Substring(at + from.Length);
        }
    }

    /// <summary>
    /// The host's examples for <see cref="RqlLanguageGuide.RulesWith"/>: each null or blank one keeps the default example,
    /// with <see cref="CreateCall"/> in place of the placeholder <c>editor.CreateObject</c>.
    /// </summary>
    public sealed class RqlRuleExamples
    {
        /// <summary>The creation call, e.g. <c>editor.CreateGameObject</c> (null: <c>editor.CreateObject</c>).</summary>
        public string? CreateCall { get; set; }
        /// <summary>Replaces the bulk-create call after <c>range(0, 300) | call </c> (it builds a 15-wide grid from <c>$</c>).</summary>
        public string? Grid { get; set; }
        /// <summary>Replaces the list-literal example in full (<c>[…] | call ….($[0], $[1])</c>, one call per entry).</summary>
        public string? PerEntry { get; set; }
        /// <summary>Replaces the arguments-by-name call after <c>range(0, 10) | call </c>.</summary>
        public string? Named { get; set; }
    }
}
