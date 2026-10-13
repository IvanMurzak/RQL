/**
 * Native TS mirror of fixtures/cross-runtime/graph.json "types" (declaration order matters) and a
 * loader that materializes the portable JSON into real class instances (no JSON-backed execution).
 */
import { EnumType, TypeModel, t } from '@rql/execution';

export enum NodeKind {
  Alpha,
  Beta,
  Gamma,
}

export class Vec2 {
  constructor(
    public x = 0,
    public y = 0,
  ) {}
}

export class Node {
  Id = 0;
  Name = '';
  Kind: NodeKind = NodeKind.Alpha;
  Value = 0;
  Enabled = false;
  Note: string | null = null;
  Pos = new Vec2();
  Tags: string[] = [];
  Props = new Map<string, number>();
  Children: Node[] = [];
  Parent: Node | null = null;
  Peer: Node | null = null;
}

export class Special extends Node {
  Power = 0;
}

export class Graph {
  Name = '';
  Version = 0;
  Nodes: Node[] = [];
  Index = new Map<string, Node>();
  Empty: Node[] = [];
  Nothing: Node | null = null;
}

export const NodeKindType = new EnumType('NodeKind', NodeKind);

/** Type descriptors for the conformance graph (shared by every engine of the suite). */
export function conformanceModel(): TypeModel {
  return new TypeModel()
    .register(Vec2, { kind: 'struct', members: [{ name: 'x', type: t.number }, { name: 'y', type: t.number }] })
    .register(Node, {
      members: [
        { name: 'Id', type: t.int, readonly: true },
        { name: 'Name', type: t.string },
        { name: 'Kind', type: NodeKindType },
        { name: 'Value', type: t.number },
        { name: 'Enabled', type: t.bool },
        { name: 'Note', type: t.nullable(t.string) },
        { name: 'Pos', type: t.of(Vec2) },
        { name: 'Tags', type: t.list(t.string) },
        { name: 'Props', type: t.map(t.string, t.number) },
        { name: 'Children', type: t.list(t.of(Node)) },
        { name: 'Parent', type: t.nullable(t.of(Node)), reference: true },
        { name: 'Peer', type: t.nullable(t.of(Node)), reference: true },
      ],
    })
    .register(Special, { members: [{ name: 'Power', type: t.int }] })
    .register(Graph, {
      members: [
        { name: 'Name', type: t.string },
        { name: 'Version', type: t.int },
        { name: 'Nodes', type: t.list(t.of(Node)) },
        { name: 'Index', type: t.map(t.string, t.of(Node)), reference: true },
        { name: 'Empty', type: t.list(t.of(Node)) },
        { name: 'Nothing', type: t.nullable(t.of(Node)), reference: true },
      ],
    });
}

// ------------------------------------------------------------------ loader

interface Link {
  $link: number;
}
interface NodeJson {
  $type: 'Node' | 'Special';
  $id: number;
  Id: number;
  Name: string;
  Kind: keyof typeof NodeKind;
  Value: number;
  Enabled: boolean;
  Note: string | null;
  Pos: { x: number; y: number };
  Tags: string[];
  Props: Record<string, number>;
  Children: NodeJson[];
  Parent: Link | null;
  Peer: Link | null;
  Power?: number;
}
interface GraphJson {
  root: { Name: string; Version: number; Nodes: NodeJson[]; Index: Record<string, Link>; Nothing: Link | null };
}

/** Builds the native graph (two passes: objects, then non-owning links). */
export function loadGraph(json: unknown): Graph {
  const root = (json as GraphJson).root;
  const byId = new Map<number, Node>();
  const links: [NodeJson, Node][] = [];
  const link = (l: Link | null): Node | null => (l === null ? null : byId.get(l.$link)!);
  const build = (e: NodeJson): Node => {
    const n = e.$type === 'Special' ? Object.assign(new Special(), { Power: e.Power ?? 0 }) : new Node();
    n.Id = e.Id;
    n.Name = e.Name;
    n.Kind = NodeKind[e.Kind];
    n.Value = e.Value;
    n.Enabled = e.Enabled;
    n.Note = e.Note;
    n.Pos = new Vec2(e.Pos.x, e.Pos.y);
    n.Tags = [...e.Tags];
    n.Props = new Map(Object.entries(e.Props));
    n.Children = e.Children.map(build);
    byId.set(e.$id, n);
    links.push([e, n]);
    return n;
  };
  const g = new Graph();
  g.Name = root.Name;
  g.Version = root.Version;
  g.Nodes = root.Nodes.map(build);
  for (const [e, n] of links) {
    n.Parent = link(e.Parent);
    n.Peer = link(e.Peer);
  }
  for (const [k, l] of Object.entries(root.Index)) g.Index.set(k, link(l)!);
  g.Nothing = link(root.Nothing);
  return g;
}
