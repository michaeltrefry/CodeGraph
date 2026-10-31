import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Project } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { DiscoveryScope } from './discoveryScope';
import { analyzeRoutes } from './routeAnalysis';
import { extractProject } from './extractor';
import { ExtractionWorkLimitError, type ExtractionWorkControl } from './extractionWork';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';

function source(depth: number, segment = 'a', spreads = false, view = ''): string {
  const lines = ["import {createBrowserRouter} from 'react-router-dom';"];
  if (view) lines.push('function Page(){return null;}', `const view=${view};`);
  lines.push(`const r0=[{path:'leaf'${view ? ',element:view' : ''}}];`);
  for (let level = 1; level <= depth; level++) lines.push(spreads ? `const r${level}=[...r${level - 1},...r${level - 1}];`
    : `const r${level}=[{path:'${segment}',children:r${level - 1}},{path:'${segment === 'a' ? 'b' : 'b'.repeat(segment.length)}',children:r${level - 1}}];`);
  lines.push(`createBrowserRouter(r${depth});`);
  return lines.join('\n');
}
function fixture(text: string) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-route-work-'));
  fs.writeFileSync(path.join(root, 'routes.tsx'), text);
  const direct = (control: ExtractionWorkControl = { maxWork: 200000000 }) => {
    const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
    const file = project.createSourceFile(path.join(root, 'routes.tsx'), text);
    const model = new SemanticModel('Routes', root, [file], [], () => {}, control);
    const before = model.work.used;
    const nodes: GraphNodeDto[] = []; const edges: PendingEdgeDto[] = [];
    let status: { complete: boolean } | undefined, error: unknown;
    try { status = analyzeRoutes(model, nodes, edges, new DiscoveryScope(root, ['routes.tsx']), () => {}); }
    catch (caught) { error = caught; }
    return { model, nodes, edges, status, error, work: model.work.used - before };
  };
  const whole = (control: ExtractionWorkControl) => extractProject('Routes', root, path.join(root, 'tsconfig.json'), () => {}, ['routes.tsx'], undefined, control);
  return { direct, whole, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function refuse(error: unknown, reason: 'work_limit' | 'cancelled') {
  assert.ok(error instanceof ExtractionWorkLimitError);
  assert.equal(error.code, 'extraction_work_incomplete'); assert.equal(error.reason, reason);
  assert.ok(error.work <= error.limit);
}

test('shared child trees charge expanded route text and evidence rather than only compact source size', context => {
  for (const depth of [8, 12]) {
    const text = source(depth); const f = fixture(text);
    try {
      const result = f.direct(); assert.equal(result.error, undefined); assert.equal(result.status?.complete, true);
      assert.equal(result.nodes.length, 3 * 2 ** depth - 2);
      const outputText = result.nodes.reduce((size, node) => size + node.qualifiedName.length + String(node.properties.full_path).length, 0);
      assert.ok(result.work > outputText, `work ${result.work} must cover expanded path/QN text ${outputText}`);
      context.diagnostic(`depth ${depth}: ${text.length} source chars, ${result.nodes.length} routes, ${outputText} path/QN chars, ${result.work} route work`);
    } finally { f.dispose(); }
  }
});

test('route output allocation refuses a low shared allowance and a fresh extraction recovers all paths', () => {
  const f = fixture(source(8));
  try {
    const limited = f.direct({ maxWork: 100000 }); refuse(limited.error, 'work_limit');
    assert.ok(limited.nodes.length > 0 && limited.nodes.length < 766, 'refuse during output expansion before the entire tree is allocated');
    assert.throws(() => limited.model.work.charge(0), error => error === limited.error, 'a refused route traversal cannot resume its poisoned run');
    const whole = f.whole({ maxWork: 100000 });
    assert.equal(whole.complete, false); assert.deepEqual(whole.nodes, []); assert.deepEqual(whole.edges, []); assert.deepEqual(whole.processedFiles, []);
    const recovered = f.whole({ maxWork: 200000000 });
    assert.equal(recovered.complete, true); assert.equal(recovered.nodes.filter(node => node.properties.route_kind === 'ui').length, 766);
    assert.deepEqual(recovered.processedFiles, ['routes.tsx']);
  } finally { f.dispose(); }
});

test('shared spread arrays consume allowance before flattened references or route output are materialized', () => {
  const f = fixture(source(12, 'a', true));
  try {
    const limited = f.direct({ maxWork: 50000 }); refuse(limited.error, 'work_limit');
    assert.deepEqual(limited.nodes, [], 'the recursive spread must refuse before the flattened root array is returned');
    assert.deepEqual(limited.edges, []);
    const recovered = f.direct(); assert.equal(recovered.error, undefined); assert.equal(recovered.status?.complete, true);
    assert.equal(recovered.nodes.length, 1); assert.equal(recovered.nodes[0].properties.full_path, '/leaf');
  } finally { f.dispose(); }
});

test('long composed path segments are charged in proportion to the amplified output before joining', () => {
  const measure = (segment: string) => {
    const f = fixture(source(5, segment));
    try {
      const result = f.direct(); assert.equal(result.error, undefined); assert.equal(result.status?.complete, true);
      return { work: result.work, text: result.nodes.reduce((size, node) => size + node.qualifiedName.length + String(node.properties.full_path).length, 0), count: result.nodes.length };
    } finally { f.dispose(); }
  };
  const short = measure('a'); const long = measure('a'.repeat(100));
  assert.equal(long.count, short.count);
  assert.ok(long.work - short.work >= long.text - short.text, 'path length amplification cannot hide behind the same number of route objects');
});

test('shared JSX element and Navigate discovery are cached by resolved AST instead of rescanned per expanded route', () => {
  const measure = (depth: number, heavy: boolean) => {
    const numbers = Array.from({ length: 2000 }, (_, index) => String(index)).join(',');
    const view = heavy ? `<section>{[${numbers}]}<Page/></section>` : '<Page/>';
    const f = fixture(source(depth, 'a', false, view));
    try {
      const result = f.direct(); assert.equal(result.error, undefined); assert.equal(result.status?.complete, true);
      assert.equal(result.edges.filter(edge => edge.type === 'HANDLES' && edge.targetQN.endsWith('#Page')).length, 2 ** depth);
      return result.work;
    } finally { f.dispose(); }
  };
  const shallowExtra = measure(4, true) - measure(4, false);
  const deepExtra = measure(8, true) - measure(8, false);
  assert.equal(deepExtra, shallowExtra, 'the same 2k-literal JSX tree is scanned once irrespective of route reuse');
});

test('cancellation during route expansion discards partial whole-project output', () => {
  const f = fixture(source(8)); let cancelled = false;
  try {
    const result = f.whole({ maxWork: 200000000, shouldCancel: () => cancelled, onWork: work => { if (work >= 100000) cancelled = true; } });
    assert.equal(result.complete, false); assert.deepEqual(result.nodes, []); assert.deepEqual(result.edges, []); assert.deepEqual(result.processedFiles, []);
    const recovered = f.whole({ maxWork: 200000000 }); assert.equal(recovered.complete, true);
  } finally { f.dispose(); }
});

test('source-owner property amplification is included in route output allowance', () => {
  const measure = (name: string) => {
    const [imports, ...body] = source(5).split('\n');
    const f = fixture(`${imports}\nfunction ${name}(){\n${body.join('\n')}\n}`);
    try {
      const result = f.direct(); assert.equal(result.error, undefined); assert.equal(result.status?.complete, true);
      return { work: result.work, owners: result.nodes.reduce((size, node) => size + String(node.properties.source_owner).length, 0), count: result.nodes.length };
    } finally { f.dispose(); }
  };
  const short = measure('Owner'); const long = measure('Owner'.repeat(200));
  assert.equal(long.count, short.count);
  assert.ok(long.work - short.work >= long.owners - short.owners, 'reused lexical owner metadata must be charged for each emitted route');
});
