/** Live value → JSON per spec §6. Only what survived filtering/projection is encoded. */
import { diagnostic, formatNumber, isJsonInteger, type JsonObject, type JsonValue, type QueryResult } from '@rql/contracts';
import { elements, mapEntries, sizeOf, type MapLike } from './collections.js';
import { toText } from './compiler.js';
import type { EncodeContext } from './adapters.js';
import type { Exec } from './exec.js';
import { EnumValue, friendlyName, type MemberAccessor, type TypeInfo } from './types.js';
import { ErrorValue, MISSING, MapEntry, RowValue, UNKNOWN } from './values.js';

export function emptyStats(): QueryResult['stats'] {
  return { visited: 0, membersRead: 0, elapsedMs: 0 };
}

/** Sets a key on a JSON object without the `__proto__` setter pitfall. */
function put(o: JsonObject, key: string, v: JsonValue): void {
  if (key === '__proto__') Object.defineProperty(o, key, { value: v, writable: true, enumerable: true, configurable: true });
  else o[key] = v;
}

/** Size budget unit (spec §12): UTF-8 bytes of the compact JSON encoding. */
function jsonLength(v: JsonValue): number {
  const s = JSON.stringify(v);
  let bytes = s.length;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0x80) {
      if (c >= 0xd800 && c <= 0xdbff) i++; // surrogate pair: 4 bytes in total
      bytes += c < 0x800 ? 1 : 2;
    }
  }
  return bytes;
}

export class Encoder {
  constructor(private readonly x: Exec) {}

  rows(columns: readonly string[], rows: readonly (readonly unknown[])[]): QueryResult {
    const r: QueryResult = { ok: true, kind: 'rows', columns: [...columns], rows: [], diagnostics: [], stats: emptyStats() };
    const out = r.rows!;
    let bytes = 0;
    for (const row of rows) {
      const enc = row.map((v) => this.encode(v));
      bytes += enc.reduce<number>((s, n) => s + jsonLength(n), 0) + columns.length;
      if (bytes > this.x.maxOutputBytes && out.length > 0) {
        r.truncated = true;
        r.diagnostics.push(
          diagnostic('warning', 'output-truncated', `output limited to ${this.x.maxOutputBytes} bytes; ${out.length} of ${rows.length} rows returned`, 'select fewer/smaller columns or page with skip/first'),
        );
        break;
      }
      out.push(enc);
    }
    r.total = rows.length;
    return r;
  }

  items(items: readonly unknown[]): QueryResult {
    const r: QueryResult = { ok: true, kind: 'items', items: [], diagnostics: [], stats: emptyStats() };
    const out = r.items!;
    let bytes = 0;
    for (const it of items) {
      const n = this.encode(it);
      bytes += jsonLength(n);
      if (bytes > this.x.maxOutputBytes && out.length > 0) {
        r.truncated = true;
        r.diagnostics.push(
          diagnostic('warning', 'output-truncated', `output limited to ${this.x.maxOutputBytes} bytes; ${out.length} of ${items.length} items returned`, 'use select to project fewer members'),
        );
        break;
      }
      out.push(n);
    }
    return r;
  }

  encode(v: unknown, depth = 0): JsonValue {
    if (v === null || v === undefined || v === MISSING || v === UNKNOWN) return null;
    if (typeof v === 'object' && this.x.types.hasAdapters && !(v instanceof ErrorValue || v instanceof RowValue || v instanceof MapEntry)) {
      const adapters = this.x.types.infoOf(v).adapters;
      if (adapters.length > 0) {
        const ctx: EncodeContext = {
          depth,
          encodeValue: (o) => this.encode(o, depth + 1),
          summary: (o) => this.summary(o, this.x.types.infoOf(o)),
        };
        for (const a of adapters) {
          const n = a.encode?.(v, ctx);
          if (n !== undefined) return n;
        }
      }
    }
    switch (typeof v) {
      case 'boolean':
      case 'string':
        return v;
      case 'number':
        return encodeNumber(v);
      case 'bigint':
        return Number.isSafeInteger(Number(v)) ? Number(v) : v.toString();
      case 'object':
        break;
      default:
        return String(v);
    }
    if (v instanceof ErrorValue) return { $error: v.code };
    if (v instanceof EnumValue) return v.name;
    if (v instanceof Date) return Number.isNaN(v.getTime()) ? null : v.toISOString();
    if (v instanceof RowValue) {
      const o: JsonObject = {};
      v.columns.forEach((c, i) => put(o, c, this.encode(v.values[i], depth + 1)));
      return o;
    }
    if (v instanceof MapEntry) return { key: this.encode(v.Key, depth + 1), value: this.encode(v.Value, depth + 1) };
    const info = this.x.types.infoOf(v);
    switch (info.kind) {
      case 'list': {
        const arr: JsonValue[] = [];
        let total = 0;
        for (const el of elements(v, info)) {
          total++;
          if (arr.length < this.x.maxListItems) arr.push(this.encode(el, depth + 1));
        }
        const n = arr.length;
        if (total > n) arr.push('…+' + String(total - n));
        return arr;
      }
      case 'map': {
        const obj: JsonObject = {};
        let n = 0;
        let total = 0;
        for (const e of mapEntries(v as MapLike)) {
          total++;
          if (n >= this.x.maxListItems) continue;
          const key = toText(this.x, e.Key);
          if (Object.prototype.hasOwnProperty.call(obj, key)) continue;
          put(obj, key, this.encode(e.Value, depth + 1));
          n++;
        }
        if (total > n) put(obj, '…', '+' + String(total - n));
        return obj;
      }
      case 'struct': {
        if (depth > 2) return '…';
        const obj: JsonObject = {};
        for (const m of info.membersOf(v)) if (!m.explicitOnly) put(obj, m.name, this.encode(read(m, v), depth + 1));
        return obj;
      }
      case 'object':
        if (!hasIdentity(info, v) && depth <= 1) return this.inline(v, info, depth);
        return this.summary(v, info);
      default:
        return toText(this.x, v);
    }
  }

  /**
   * Identity-less, nameless objects (settings blocks, inventory slots) are value-like: encode their
   * members inline; nested objects become summaries and collections only report their size.
   */
  private inline(o: object, info: TypeInfo, depth: number): JsonObject {
    const obj: JsonObject = {};
    for (const m of info.membersOf(o)) {
      if (m.explicitOnly) continue;
      const mv = read(m, o);
      if (typeof mv === 'object' && mv !== null && !(mv instanceof ErrorValue)) {
        const mi = this.x.types.infoOf(mv);
        if (mi.isList || mi.isMap) {
          const tn = m.type !== 'any' ? friendlyName(m.type, this.x.types) : mi.shortName;
          put(obj, m.name, `${tn}[${sizeOf(mv, this.x.types) ?? 0}]`);
          continue;
        }
        if (mi.isObject && !(mv instanceof MapEntry) && !(mv instanceof RowValue)) {
          put(obj, m.name, !hasIdentity(mi, mv) && depth < 1 ? this.inline(mv, mi, depth + 1) : this.summary(mv, mi));
          continue;
        }
      }
      put(obj, m.name, this.encode(mv, depth + 1));
    }
    return obj;
  }

  summary(o: object, info: TypeInfo): JsonObject {
    const obj: JsonObject = { $ref: this.x.engine.refOf(o) };
    const dm = info.displayMemberOf(o);
    if (dm !== undefined) {
      try {
        const name = dm.get(o);
        if (typeof name === 'string') obj.name = name;
      } catch {
        // summaries never fail
      }
    }
    return obj;
  }
}

function read(m: MemberAccessor, o: object): unknown {
  try {
    return m.get(o);
  } catch (e) {
    return new ErrorValue('getter-threw', e instanceof Error ? e.message : String(e));
  }
}

export function hasIdentity(info: TypeInfo, o: object): boolean {
  return info.idMemberOf(o) !== undefined || info.displayMemberOf(o) !== undefined;
}

export function encodeNumber(d: number): JsonValue {
  if (isJsonInteger(d)) return d === 0 ? 0 : d;
  if (!Number.isFinite(d)) return formatNumber(d);
  return d;
}
