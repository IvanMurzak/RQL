/** Uniform access to list-like and map-like runtime values. */
import { formatNumber } from '@rql/contracts';
import { EnumValue, StringMap, type TypeInfo, type TypeModel } from './types.js';
import { MapEntry, MISSING } from './values.js';

export type MapLike = Map<unknown, unknown> | StringMap;

export function isMapLike(v: unknown): v is MapLike {
  return v instanceof Map || v instanceof StringMap;
}

/** Elements of a list-kind value. */
export function listItems(v: object): Iterable<unknown> {
  return v as Iterable<unknown>;
}

export function* mapEntries(m: MapLike): Generator<MapEntry> {
  for (const [k, v] of m.entries()) yield new MapEntry(k, v);
}

/** List elements or map entries of a collection value. */
export function elements(v: object, info: TypeInfo): Iterable<unknown> {
  return info.isMap ? mapEntries(v as MapLike) : listItems(v);
}

export function sizeOf(v: unknown, types: TypeModel): number | undefined {
  if (typeof v === 'string') return v.length;
  if (Array.isArray(v)) return v.length;
  if (v instanceof Map || v instanceof Set || v instanceof StringMap) return v.size;
  if (typeof v !== 'object' || v === null) return undefined;
  const info = types.infoOf(v);
  if (!info.isList && !info.isMap) return undefined;
  if (ArrayBuffer.isView(v)) return (v as unknown as ArrayLike<unknown>).length;
  let n = 0;
  for (const _ of elements(v, info)) n++;
  return n;
}

/** Element at a (possibly negative) index; MISSING when out of range. */
export function indexAt(list: object, idx: number): unknown {
  const arr: ArrayLike<unknown> = Array.isArray(list) || ArrayBuffer.isView(list) ? (list as unknown as ArrayLike<unknown>) : [...(list as Iterable<unknown>)];
  const i = idx < 0 ? arr.length + idx : idx;
  if (i < 0 || i >= arr.length) return MISSING;
  const v = arr[i];
  return v === undefined ? null : v;
}

/**
 * Map lookup: exact key, then the same key as number/string, then a case-insensitive match of
 * string keys (and enum keys by name). Returns `found: false` when no key matches.
 */
export function findMapKey(m: MapLike, key: unknown): { found: true; key: unknown } | { found: false } {
  if (m instanceof StringMap) {
    const k = typeof key === 'number' ? formatNumber(key) : key instanceof EnumValue ? key.name : key;
    if (typeof k !== 'string') return { found: false };
    if (m.has(k)) return { found: true, key: k };
    const up = k.toUpperCase();
    for (const rk of m.keys()) if (rk.toUpperCase() === up) return { found: true, key: rk };
    return { found: false };
  }
  if (m.has(key)) return { found: true, key };
  if (typeof key === 'number') {
    const s = formatNumber(key);
    if (m.has(s)) return { found: true, key: s };
  } else if (typeof key === 'string') {
    const n = Number(key);
    if (key.trim() !== '' && !Number.isNaN(n) && m.has(n)) return { found: true, key: n };
  }
  if (typeof key === 'string') {
    const up = key.toUpperCase();
    let resolved: EnumValue | undefined; // the key resolved against the map's enum key type, once (spec §8)
    for (const k of m.keys()) {
      if (typeof k === 'string' && k.toUpperCase() === up) return { found: true, key: k };
      if (k instanceof EnumValue) {
        resolved ??= k.type.parse(key);
        if (resolved !== undefined && resolved.type === k.type && resolved.value === k.value) return { found: true, key: k };
      }
    }
  } else if (typeof key === 'number') {
    for (const k of m.keys()) if (k instanceof EnumValue && k.ordinal === key) return { found: true, key: k };
  }
  return { found: false };
}

export function mapGet(m: MapLike, key: unknown): unknown {
  return m instanceof StringMap ? m.get(key as string) : m.get(key);
}
