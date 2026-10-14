/** The test adapter set specified in fixtures/adapters/cases.json ("adapters" section). */
import { formatNumber } from '@rql/contracts';
import { t, type RqlEngine, type TypeAdapter } from '@rql/execution';
import { Node, NodeKind, Special, Vec2 } from './graph.js';

const asciiCase = (s: string, upper: boolean): string => (upper ? s.replace(/[a-z]+/g, (m) => m.toUpperCase()) : s.replace(/[A-Z]+/g, (m) => m.toLowerCase()));
const length = (v: Vec2): number => Math.sqrt(v.x * v.x + v.y * v.y);
const VEC_TEXT = /^\(\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\)$/;

/** "(x, y)" text, a [x, y] list of two numbers, or a Vec2. */
function toVec(value: unknown): Vec2 | undefined {
  if (value instanceof Vec2) return value;
  if (typeof value === 'string') {
    const m = VEC_TEXT.exec(value);
    if (m === null) return undefined;
    const x = Number(m[1]);
    const y = Number(m[2]);
    return Number.isNaN(x) || Number.isNaN(y) ? undefined : new Vec2(x, y);
  }
  if (Array.isArray(value) && value.length === 2 && typeof value[0] === 'number' && typeof value[1] === 'number') return new Vec2(value[0], value[1]);
  return undefined;
}

export const vec2Adapter: TypeAdapter<Vec2> = {
  target: Vec2,
  description: 'Vec2: write as "(x, y)" or [x, y]',
  members: (m) => m.add('len', { type: t.number, get: length, description: 'vector length sqrt(x*x+y*y)' }),
  encode: (v) => `(${formatNumber(v.x)}, ${formatNumber(v.y)})`,
  coerce: (value) => {
    const v = toVec(value);
    return v !== undefined ? { ok: true, value: v } : { ok: false, reason: 'expected (x, y) or [x, y]' };
  },
  equals: (v, other) => {
    const o = toVec(other);
    return o === undefined ? undefined : Math.abs(v.x - o.x) <= 1e-9 && Math.abs(v.y - o.y) <= 1e-9;
  },
  compare: (v, other) => {
    const o = toVec(other);
    return o === undefined ? undefined : Math.sign(length(v) - length(o));
  },
};

export const nodeAdapter: TypeAdapter<Node> = {
  target: Node,
  description: 'Node: a conformance graph node',
  members: (m) =>
    m
      .add('childCount', { type: t.int, get: (n) => n.Children.length, description: 'number of direct children' })
      .add<string>('title', {
        type: t.string,
        get: (n) => asciiCase(n.Name, true),
        set: (n, v) => {
          if (n.Id === 3) throw new Error('node 3 is locked');
          n.Name = asciiCase(v, false);
        },
        validate: (_n, v) => (v === '' || v === null ? 'title must not be empty' : undefined),
        description: 'upper-cased Name; assigning sets Name lower-cased',
      })
      .hide('Note')
      .explicitOnly('Children')
      .map<number>(
        'stats',
        {
          keys: (n) => n.Props.keys(),
          get: (n, k) => n.Props.get(k)!,
          set: (n, k, v) => void n.Props.set(k, v),
          validate: (n, k, v) => (!n.Props.has(k) ? `unknown stat '${k}'` : v < 0 ? 'stats must be non-negative' : undefined),
        },
        t.number,
        'Props as a validated map',
      )
      .map<string>('labels', { keys: () => LABELS, get: (n, k) => (k === 'name' ? n.Name : NodeKind[n.Kind]) }, t.string, 'read-only labels')
      .method('greet', [{ name: 'who', type: t.string }], t.string, (n, who: string) => `hi ${who}, I am ${n.Name}`, 'a greeting (no state change)')
      .method(
        'bump',
        [{ name: 'by', type: t.number, default: 1 }],
        t.number,
        (n, by: number) => {
          n.Value += by;
          return n.Value;
        },
        'Value += by; returns the new Value',
      ),
};

const LABELS = ['name', 'kind'];

export const specialAdapter: TypeAdapter<Special> = {
  target: Special,
  members: (m) => m.add('powerLevel', { type: t.number, get: (s) => s.Power / 1000, description: 'Power / 1000' }),
};

export function registerTestAdapters(engine: RqlEngine): RqlEngine {
  return engine.addAdapter(vec2Adapter).addAdapter(nodeAdapter).addAdapter(specialAdapter);
}
