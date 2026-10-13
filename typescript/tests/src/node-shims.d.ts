// Minimal ambient declarations for the Node built-ins used by the tests
// (TypeScript is the only allowed dev dependency, so @types/node is not installed).
declare module 'node:test' {
  type Fn = () => void | Promise<void>;
  export function test(name: string, fn: Fn): Promise<void>;
  export function describe(name: string, fn: () => void): void;
  export function after(fn: Fn): void;
}
declare module 'node:assert/strict' {
  interface Assert {
    (value: unknown, message?: string): asserts value;
    equal(actual: unknown, expected: unknown, message?: string): void;
    deepEqual(actual: unknown, expected: unknown, message?: string): void;
    ok(value: unknown, message?: string): asserts value;
    throws(fn: () => unknown, check?: unknown, message?: string): void;
    fail(message?: string): never;
  }
  const assert: Assert;
  export default assert;
}
declare module 'node:fs' {
  export function readFileSync(path: string, encoding: 'utf8'): string;
}
declare module 'node:url' {
  export function fileURLToPath(url: string): string;
}
declare module 'node:path' {
  export function dirname(p: string): string;
  export function resolve(...parts: string[]): string;
}
interface ImportMeta {
  readonly url: string;
}
declare const process: { argv: string[]; exitCode: number | undefined; stdout: { write(s: string): void } };
