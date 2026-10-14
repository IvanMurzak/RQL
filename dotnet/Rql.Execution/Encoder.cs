using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    /// <summary>Live value → JSON per spec §6. Only what survived filtering/projection is encoded.</summary>
    internal sealed class Encoder
    {
        private readonly Exec _x;

        private readonly Func<object?, int, JsonNode?> _encode;
        private readonly Func<object, JsonObject> _summary;

        /// <summary>
        /// Levels of nested lists / maps / plain objects written out in full instead of as summaries ("List&lt;T&gt;[3]").
        /// 0 = query output (compact); <see cref="RqlEngine.Present"/> raises it for whole values such as tool results.
        /// </summary>
        private readonly int _expand;

        public Encoder(Exec x, int expand = 0)
        {
            _x = x;
            _expand = expand;
            _encode = Encode;
            _summary = o => Summary(o, _x.Types.Info(o.GetType()));
        }

        public QueryResult Rows(string[] columns, List<object?[]> rows)
        {
            var r = new QueryResult { Ok = true, Kind = ResultKind.Rows, Columns = columns.ToList(), Rows = new List<JsonNode?[]>() };
            long bytes = 0;
            foreach (var row in rows)
            {
                var enc = row.Select(v => Encode(v)).ToArray();
                bytes += enc.Sum(n => (long)(n?.ToJsonString().Length ?? 4)) + columns.Length;
                if (bytes > _x.MaxOutputBytes && r.Rows.Count > 0)
                {
                    r.Truncated = true;
                    r.Diagnostics.Add(Diagnostic.Warning("output-truncated", $"output limited to {_x.MaxOutputBytes} bytes; {r.Rows.Count} of {rows.Count} rows returned", "select fewer/smaller columns or page with skip/first"));
                    break;
                }
                r.Rows.Add(enc);
            }
            r.Total = rows.Count;
            return r;
        }

        public QueryResult Items(List<object?> items)
        {
            var r = new QueryResult { Ok = true, Kind = ResultKind.Items, Items = new List<JsonNode?>() };
            long bytes = 0;
            foreach (var it in items)
            {
                var n = Encode(it);
                bytes += n?.ToJsonString().Length ?? 4;
                if (bytes > _x.MaxOutputBytes && r.Items.Count > 0)
                {
                    r.Truncated = true;
                    r.Diagnostics.Add(Diagnostic.Warning("output-truncated", $"output limited to {_x.MaxOutputBytes} bytes; {r.Items.Count} of {items.Count} items returned", "use select to project fewer members"));
                    break;
                }
                r.Items.Add(n);
            }
            return r;
        }

        public JsonNode? Encode(object? v, int depth = 0)
        {
            if (v != null && _x.Types.IsDead(v)) return null; // final guard: destroyed objects encode as null
            if (v != null && (_x.Types.Caps & AdapterCaps.Encode) != 0)
            {
                var adapters = _x.Types.Info(v.GetType()).EncodeAdapters;
                if (adapters.Length > 0)
                {
                    var ctx = new EncodeContext(_encode, _summary, depth);
                    foreach (var a in adapters)
                    {
                        var n = a.Encode(v, ctx);
                        if (n != null) return n;
                    }
                }
            }
            switch (v)
            {
                case null:
                case Missing _:
                case Unknown _:
                    return null;
                case RqlError e:
                    if (e.Detail != null && _x.CellErrors.Count < 3)
                    {
                        var text = e.Code + ": " + e.Detail;
                        if (!_x.CellErrors.Contains(text)) _x.CellErrors.Add(text);
                    }
                    return new JsonObject { ["$error"] = e.Code };
                case bool b:
                    return JsonValue.Create(b);
                case string s:
                    return JsonValue.Create(s);
                case char c:
                    return JsonValue.Create(c.ToString());
                case float f:
                    return Number(double.Parse(V.FormatNumber(f), CultureInfo.InvariantCulture));
                case double d:
                    return Number(d);
                case decimal m:
                    return JsonValue.Create(m);
                case Enum en:
                    return JsonValue.Create(en.ToString());
                case DateTime dt:
                    return JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture));
                case TimeSpan ts:
                    return JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture));
                case Guid g:
                    return JsonValue.Create(g.ToString());
                case RowValue row:
                {
                    var o = new JsonObject();
                    for (int i = 0; i < row.Columns.Length; i++) o[row.Columns[i]] = Encode(row.Values[i], depth + 1);
                    return o;
                }
                case MapEntry me:
                    return new JsonObject { ["key"] = Encode(me.Key, depth + 1), ["value"] = Encode(me.Value, depth + 1) };
            }
            if (V.TryNumber(v, out var num)) return Number(num);
            var info = _x.Types.Info(v.GetType());
            switch (info.Kind)
            {
                case TypeKind.List:
                {
                    var arr = new JsonArray();
                    int n = 0, total = 0;
                    foreach (var el in (IEnumerable)v)
                    {
                        total++;
                        if (n < _x.MaxListItems) { arr.Add(Encode(el, depth + 1)); n++; }
                    }
                    if (total > n) arr.Add(JsonValue.Create("…+" + (total - n).ToString(CultureInfo.InvariantCulture)));
                    return arr;
                }
                case TypeKind.Map:
                {
                    var obj = new JsonObject();
                    int n = 0, total = 0;
                    foreach (var e in V.EnumerateMap(v, info))
                    {
                        total++;
                        if (n >= _x.MaxListItems) continue;
                        var me = (MapEntry)e!;
                        var key = Compiler.ToText(me.Key);
                        if (obj.ContainsKey(key)) continue;
                        obj[key] = Encode(me.Value, depth + 1);
                        n++;
                    }
                    if (total > n) obj["…"] = JsonValue.Create("+" + (total - n).ToString(CultureInfo.InvariantCulture));
                    return obj;
                }
                case TypeKind.Struct:
                {
                    if (depth > 2) return JsonValue.Create("…");
                    var obj = new JsonObject();
                    foreach (var m in info.ImplicitMembers)
                    {
                        object? mv;
                        try { mv = m.Getter(v); } catch (Exception ex) { mv = new RqlError("getter-threw", ex.Message); }
                        obj[m.Name] = Encode(mv, depth + 1);
                    }
                    return obj;
                }
                case TypeKind.Object:
                    if (info.IdMember == null && info.DisplayMember == null && depth <= Math.Max(1, _expand)) return Inline(v, info, depth);
                    return Summary(v, info);
                default:
                    return JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Identity-less, nameless objects (settings blocks, inventory slots) are value-like: encode their
        /// members inline; nested objects become summaries and collections only report their size.
        /// </summary>
        private JsonObject Inline(object o, TypeInfo info, int depth)
        {
            var obj = new JsonObject();
            foreach (var m in info.ImplicitMembers)
            {
                object? mv;
                try { mv = m.Getter(o); } catch (Exception ex) { mv = new RqlError("getter-threw", ex.Message); }
                if (mv != null && !(mv is string) && !(mv is RqlError) && !(mv is Missing))
                {
                    var mi = _x.Types.Info(mv.GetType());
                    if ((mi.IsList || mi.IsMap) && depth < _expand)
                    {
                        obj[m.Name] = Encode(mv, depth + 1);
                        continue;
                    }
                    if (mi.IsList || mi.IsMap)
                    {
                        obj[m.Name] = JsonValue.Create(TypeModel.FriendlyName(mv.GetType()) + "[" + (V.Count(mv) ?? 0).ToString(CultureInfo.InvariantCulture) + "]");
                        continue;
                    }
                    if (mi.IsObject) { obj[m.Name] = mi.IdMember == null && mi.DisplayMember == null && depth < Math.Max(1, _expand) ? Inline(mv, mi, depth + 1) : Summary(mv, mi); continue; }
                }
                obj[m.Name] = Encode(mv, depth + 1);
            }
            return obj;
        }

        public JsonObject Summary(object o, TypeInfo info)
        {
            var obj = new JsonObject { ["$ref"] = _x.Engine.RefOf(o) };
            if (info.DisplayMember != null)
            {
                try
                {
                    if (info.DisplayMember.Getter(o) is string name) obj["name"] = name;
                }
                catch { /* summaries never fail */ }
            }
            return obj;
        }

        private static JsonNode Number(double d)
        {
            if (Math.Abs(d) < 9007199254740992d && Math.Floor(d) == d) return JsonValue.Create((long)d);
            if (double.IsNaN(d) || double.IsInfinity(d)) return JsonValue.Create(V.FormatNumber(d));
            // Raw number text, so the output does not depend on the runtime's double formatting (Mono prints 17 digits).
            return JsonNode.Parse(V.FormatNumber(d))!;
        }
    }
}
