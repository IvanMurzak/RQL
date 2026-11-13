import assert from 'node:assert/strict';
import { test } from 'node:test';
import { resultToJson, type QueryPlan } from '@rql/contracts';
import { RqlEngine, TypeModel, runStatements, t } from '@rql/execution';
import { MAX_STATEMENTS, splitStatements } from '@rql/parser';

// Several statements in one request, separated by ';' (spec §14). Mirrors dotnet/Rql.Tests/StatementTests.cs;
// the splitting vectors are in fixtures/statements/cases.json.

class Node {
  Name = '';
  Value = 0;
}

const mutate = { allowMutation: true };

function host(): { e: RqlEngine; nodes: Node[] } {
  const types = new TypeModel().register(Node, { members: ['Name', { name: 'Value', type: t.number }] });
  const nodes = [Object.assign(new Node(), { Name: 'a', Value: 1 }), Object.assign(new Node(), { Name: 'b', Value: 2 })];
  const e = new RqlEngine({ types }).addSource('nodes', () => nodes);
  e.mutations.enabled = true;
  return { e, nodes };
}

const json = (r: Parameters<typeof resultToJson>[0]): string => JSON.stringify(resultToJson(r, false));

test('one statement keeps the single result', () => {
  const { e } = host();
  const trailing = e.execute('nodes | select Name;');
  assert.equal(trailing.statements, undefined);
  assert.equal(json(trailing), json(e.execute('nodes | select Name')));
  assert.equal(json(e.execute('nodes | where;')), json(e.execute('nodes | where')));
  assert.equal(e.execute(';').diagnostics[0]!.code, 'syntax-error');
  const lead = e.execute('; nodes | wher x');
  assert.equal(lead.diagnostics[0]!.at!.start, 10);
  assert.equal(json(lead), json(e.execute('  nodes | wher x')));
  assert.deepEqual(e.execute(';; nodes | where Name == "a" | select n = (Name + ";b");').rows, [['a;b']]);
  const whole = splitStatements('  nodes | count \n');
  assert.deepEqual(whole.map((s) => s.span), [{ start: 0, end: 17 }]);
});

test('reads run every statement, a failing one does not stop the others', () => {
  const { e } = host();
  const ok = e.execute('nodes | count; nodes | where Name == "b" | select Name Value');
  assert.equal(ok.ok, true);
  assert.equal(ok.rows, undefined);
  assert.deepEqual(ok.statements!.map((s) => s.result!.value ?? s.result!.rows), [2, [['b', 2]]]);
  const bad = e.execute('nodes | select Nme; nodes | where | count; nodes | count');
  assert.equal(bad.ok, false);
  assert.equal(bad.statements!.length, 3);
  assert.equal(bad.statements![1]!.result!.diagnostics[0]!.code, 'syntax-error');
  assert.equal(bad.statements![2]!.result!.ok, true);
  const write = e.execute('nodes | set Value = 9; nodes | count');
  assert.equal(write.statements![0]!.result!.diagnostics[0]!.code, 'permission-denied');
  assert.equal(write.statements![1]!.result!.ok, true);
});

test('changes run in order, each its own change, and see the earlier ones', () => {
  const { e, nodes } = host();
  let before = 0;
  let committed = 0;
  e.mutations.beforeWrite = () => before++;
  e.mutations.committed = () => committed++;
  const r = e.execute('nodes | where Name == "a" | set Value = 10; nodes | where Value == 10 | set Name = "ten"', {}, mutate);
  assert.equal(r.ok, true, json(r));
  assert.deepEqual(nodes.map((n) => n.Name), ['ten', 'b']);
  assert.equal(before, 2);
  assert.equal(committed, 2);
});

test('a failed change stops the rest and keeps the earlier changes', () => {
  const { e, nodes } = host();
  const r = e.execute('nodes | set Value = 5; nodes | set Valu = 6; nodes | set Name = "z"', {}, mutate);
  assert.equal(r.ok, false);
  assert.deepEqual(nodes.map((n) => [n.Name, n.Value]), [['a', 5], ['b', 5]]);
  assert.equal(r.statements![1]!.result!.ok, false);
  assert.ok(/^statement 2 failed/.test(r.statements![2]!.skipped!));
});

test('a change runs only when every statement is valid', () => {
  const { e, nodes } = host();
  const syntax = e.execute('nodes | set Value = 5; nodes | set Name =', {}, mutate);
  assert.ok(/^statement 2 is invalid/.test(syntax.statements![0]!.skipped!));
  const read = e.execute('nodes | set Value = 5; nodes | count', {}, mutate);
  assert.equal(read.statements![1]!.result!.diagnostics[0]!.code, 'invalid-plan');
  assert.deepEqual(nodes.map((n) => n.Value), [1, 2]);
  assert.ok(/^\{"rql":"0\.1","ok":false,"statements":\[\{"text":"nodes \| set Value = 5","skipped":"statement 2 is invalid/.test(json(read)));
});

test('dry run marks the request; too many statements fail as a whole', () => {
  const { e, nodes } = host();
  const dry = e.execute('nodes | set Value = 5; nodes | set Name = "q"', {}, { allowMutation: true, dryRun: true });
  assert.equal(dry.ok, true);
  assert.equal(dry.dryRun, true);
  assert.deepEqual(nodes.map((n) => n.Value), [1, 2]);
  const many = e.execute(Array(MAX_STATEMENTS + 1).fill('nodes | count').join(';'));
  assert.equal(many.statements, undefined);
  assert.equal(many.diagnostics[0]!.code, 'syntax-error');
  assert.equal(e.execute(Array(MAX_STATEMENTS).fill('nodes | count').join(';')).ok, true);
});

test('hosts run statements with their own executor', () => {
  const { e } = host();
  const seen: QueryPlan[] = [];
  const r = runStatements(splitStatements('nodes | count; nodes | first'), {}, (plan) => {
    seen.push(plan);
    return e.execute(plan);
  });
  assert.equal(r.ok, true);
  assert.equal(seen.length, 2);
});
