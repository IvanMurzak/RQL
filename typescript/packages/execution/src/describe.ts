/** Type descriptors for `describe` and the host schema (capability-manifest.schema.json#typeDescriptor). */
import type { JsonObject, QueryResult } from '@rql/contracts';
import { emptyStats } from './encoder.js';
import type { Exec } from './exec.js';
import { EnumType, friendlyName, unwrapNullable, type TypeInfo, type TypeModel, type TypeRef } from './types.js';
import { RowValue, isAbsent } from './values.js';

export function describeRun(x: Exec, values: Iterable<unknown>): QueryResult {
  const seen: { info: TypeInfo; sample: unknown }[] = [];
  for (const v of values) {
    if (v === null || v === undefined || isAbsent(v)) continue;
    const info = x.types.infoOf(v);
    if (!seen.some((s) => s.info === info)) seen.push({ info, sample: v });
    if (seen.length >= 8) break;
  }
  const types = seen.map((s) => typeDescriptor(x.types, s.info, x.engine.mutations.enabled, s.sample));
  return { ok: true, kind: 'schema', types, diagnostics: [], stats: emptyStats() };
}

export function typeDescriptor(model: TypeModel, info: TypeInfo, showWritable: boolean, sample?: unknown): JsonObject {
  const o: JsonObject = { name: info.shortName };
  if (info.base !== undefined) o.base = info.base.shortName;
  o.kind = info.kind === 'enum' ? 'enum' : info.kind === 'struct' ? 'struct' : 'object';
  if (info.description !== undefined) o.description = info.description;
  if (info.enumType !== undefined) {
    o.values = info.enumType.values.map((v) => v.name);
    o.members = [];
    return o;
  }
  const members = sample instanceof RowValue
    ? sample.columns.map((c) => ({ name: c, type: 'any' as TypeRef, canWrite: false, explicitOnly: false, description: undefined }))
    : info.membersOf(sample);
  o.members = members.map((m) => {
    const mo: JsonObject = { name: m.name, type: friendlyName(m.type, model) };
    if (showWritable && m.canWrite) mo.writable = true;
    if (m.explicitOnly) mo.explicitOnly = true;
    if (m.description !== undefined) mo.description = m.description;
    return mo;
  });
  if (info.callable.length > 0) o.methods = info.callable.map((m) => m.signature(model));
  return o;
}

/**
 * Declared types statically reachable from the given roots through member types, element types
 * and registered subclasses (polymorphic collections).
 */
export function reachableTypes(model: TypeModel, roots: Iterable<TypeInfo>, max = 200): TypeInfo[] {
  const result: TypeInfo[] = [];
  const seen = new Set<TypeInfo>();
  const queue: TypeInfo[] = [...roots];
  const enqueueRef = (r: TypeRef): void => {
    const u = unwrapNullable(r);
    if (u instanceof EnumType) queue.push(model.enumInfo(u));
    else if (typeof u === 'object') {
      if (u.kind === 'list') enqueueRef(u.of);
      else if (u.kind === 'map') {
        enqueueRef(u.key);
        enqueueRef(u.of);
      } else if (u.kind === 'class') {
        const i = model.infoForCtor(u.ctor);
        if (i !== undefined) queue.push(i);
      }
    }
  };
  while (queue.length > 0 && result.length < max) {
    const t = queue.shift()!;
    if (seen.has(t)) continue;
    seen.add(t);
    if (t.kind === 'list' || t.kind === 'map' || t.kind === 'scalar') continue;
    result.push(t);
    for (const m of t.members) enqueueRef(m.type);
    if (t.kind === 'object') {
      for (const ctor of model.registeredTypes) {
        const sub = model.infoForCtor(ctor);
        if (sub !== undefined && sub !== t && sub.isA(t.shortName)) queue.push(sub);
      }
    }
  }
  return result;
}
