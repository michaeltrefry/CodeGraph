import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as ts from 'typescript';
import { Project, FunctionDeclaration } from 'ts-morph';
import { extractProject } from './extractor';
import { BoundedTextReader } from './boundedTextReader';
import { DiscoveryScope } from './discoveryScope';
import { analyzeWorkspaceImports } from './workspaceResolver';
import { ExtractionWorkBudget, ExtractionWorkLimitError, MAX_PARSE_SOURCE_UNITS, type ExtractionWorkControl } from './extractionWork';
import type { ExtractProjectResponse, SourceReadLimits } from './protocol';

function fixture(files: Record<string, string>) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-frontier-'));
  for (const [file, text] of Object.entries(files)) { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); }
  const run = (control: ExtractionWorkControl = {}, limits: SourceReadLimits = {}) => {
    let work = 0;
    const result = extractProject('Alloc', root, path.join(root, 'tsconfig.json'), () => {}, Object.keys(files), limits,
      { ...control, onWork: value => { work = value; control.onWork?.(value); } });
    return { result, work };
  };
  return { root, run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function empty(result: ExtractProjectResponse) {
  assert.equal(result.complete, false); assert.deepEqual(result.nodes, []); assert.deepEqual(result.edges, []);
  assert.deepEqual(result.processedFiles, []); assert.deepEqual(result.resolvedImports, []);
  assert.ok(result.diagnostics?.some(message => /work limit|retention|analysis work/i.test(message)), result.diagnostics?.join('\n'));
}
function canonical(result: ExtractProjectResponse) {
  assert.equal(result.complete, true, result.diagnostics?.join('\n'));
  return { nodes: result.nodes.map(node => JSON.stringify(node)).sort(), edges: result.edges.map(edge => JSON.stringify(edge)).sort(), processed: [...result.processedFiles].sort() };
}
function observeParsers<T>(action: () => T) {
  const counts = { config: 0, other: 0 };
  const original = Project.prototype.createSourceFile;
  Project.prototype.createSourceFile = function(this: Project, ...args: Parameters<Project['createSourceFile']>) {
    if (/^next\.config\.(?:mjs|cjs|js|ts)$/.test(path.basename(args[0]))) counts.config++; else counts.other++;
    return original.apply(this, args);
  };
  try { return { value: action(), counts }; } finally { Project.prototype.createSourceFile = original; }
}
function rawNodes(file: string, text: string) {
  const kind = file.endsWith('.jsx') ? ts.ScriptKind.JSX : file.endsWith('.tsx') ? ts.ScriptKind.TSX : file.endsWith('.ts') ? ts.ScriptKind.TS : ts.ScriptKind.JS;
  const root = ts.createSourceFile(file, text, ts.ScriptTarget.ES2015, true, kind);
  const pending: ts.Node[] = [root]; let count = 0;
  while (pending.length) { const node = pending.pop()!; count++; ts.forEachChild(node, child => { pending.push(child); }); }
  return count;
}
const page = 'export default function Page(){return <main/>;}';

test('large MJS/CJS Next configs cannot bypass the independent pre-parser window even with admitted JS source', () => {
  for (const extension of ['mjs', 'cjs']) {
    const text = `${extension === 'mjs' ? "export default {basePath:'/ui'};" : "module.exports={basePath:'/ui'};"}/*${'x'.repeat(MAX_PARSE_SOURCE_UNITS)}*/`;
    const f = fixture({ 'app/page.jsx': page, [`next.config.${extension}`]: text });
    try {
      const observed = observeParsers(() => f.run({ maxWork: Number.MAX_SAFE_INTEGER }, { maxFileSizeKb: 4096, maxAnalysisTextKb: 8192 }));
      empty(observed.value.result); assert.equal(observed.counts.config, 0, extension);
    } finally { f.dispose(); }
    const normal = fixture({ 'app/page.jsx': page, [`next.config.${extension}`]: extension === 'mjs' ? "export default {basePath:'/ui'};" : "module.exports={basePath:'/ui'};" });
    try {
      const observed = observeParsers(() => normal.run()); assert.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
      assert.ok(observed.counts.other > 0 && observed.counts.config === 1);
      assert.ok(observed.value.result.nodes.some(node => node.properties.full_path === '/ui'));
    } finally { normal.dispose(); }
  }
});

test('independent Next config projects share the source AST retention allowance', () => {
  const files = {
    'apps/one/app/page.jsx': page, 'apps/two/app/page.jsx': page,
    'apps/one/next.config.mjs': "export default {basePath:'/one'};",
    'apps/two/next.config.cjs': "module.exports={basePath:'/two'};",
  };
  const sourceCount = rawNodes('page.jsx', page) * 2;
  const configCounts = [rawNodes('one.mjs', files['apps/one/next.config.mjs']), rawNodes('two.cjs', files['apps/two/next.config.cjs'])];
  const f = fixture(files);
  try {
    const limited = observeParsers(() => f.run({ maxAstNodes: sourceCount + Math.max(...configCounts) }));
    empty(limited.value.result); assert.ok(limited.counts.config > 0 && limited.counts.config <= 2);
    const recovered = f.run(); assert.equal(recovered.result.complete, true, recovered.result.diagnostics?.join('\n'));
    assert.deepEqual(recovered.result.nodes.filter(node => node.properties.route_kind === 'ui').map(node => node.properties.full_path).sort(), ['/one', '/two']);
  } finally { f.dispose(); }
});

test('JS/TS Next configs reuse their admitted AST or charge any second retained tree', () => {
  for (const extension of ['js', 'ts']) {
    const name = `next.config.${extension}`, text = "const config={basePath:'/ui'};export default config;";
    const f = fixture({ 'app/page.jsx': page, [name]: text });
    try {
      const observed = observeParsers(() => f.run({ maxAstNodes: rawNodes('page.jsx', page) + rawNodes(name, text) }));
      if (observed.counts.config === 1) {
        assert.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
        assert.ok(observed.value.result.nodes.some(node => node.properties.full_path === '/ui'));
      } else {
        assert.equal(observed.counts.config, 2, 'a second retained configuration tree must be explicitly counted'); empty(observed.value.result);
      }
    } finally { f.dispose(); }
  }
});

test('semantic function node/edge factories reserve before position metadata and output accumulation', () => {
  const count = 80;
  const f = fixture({ 'functions.ts': Array.from({ length: count }, (_, index) => `export function F${index}(value:number){return value;}`).join('\n') });
  const originalLocation = FunctionDeclaration.prototype.getStartLineNumber;
  const locationDescriptor = Object.getOwnPropertyDescriptor(FunctionDeclaration.prototype, 'getStartLineNumber');
  const reserve = ExtractionWorkBudget.prototype.reserveOutput;
  let locations = 0;
  const events: Array<{ kind: string; before: number; denied: boolean }> = [];
  Object.defineProperty(FunctionDeclaration.prototype, 'getStartLineNumber', { configurable: true, writable: true, value: function(this: FunctionDeclaration, ...args: Parameters<FunctionDeclaration['getStartLineNumber']>) {
    if (this.getSourceFile().getFilePath().startsWith(f.root + path.sep)) locations++;
    return originalLocation.apply(this, args);
  } });
  ExtractionWorkBudget.prototype.reserveOutput = function(this: ExtractionWorkBudget, kind, textUnits = 0) {
    const before = this.retainedOutputItems;
    try { reserve.call(this, kind, textUnits); events.push({ kind, before, denied: false }); }
    catch (error) { events.push({ kind, before, denied: true }); throw error; }
  };
  try {
    empty(f.run({ maxOutputItems: 0 }).result); assert.equal(locations, 0, 'the first semantic DTO location must not be built before its reservation');
    assert.ok(events.some(event => event.kind === 'node' && event.before === 0 && event.denied));
    events.length = 0;
    empty(f.run({ maxOutputItems: 1 }).result);
    assert.ok(locations > 0 && locations < count, 'one allowed node must not trigger metadata for the remaining functions');
    assert.ok(events.some(event => event.kind === 'edge' && event.before === 1 && event.denied));
    const healthy = f.run(); assert.equal(healthy.result.complete, true, healthy.result.diagnostics?.join('\n'));
    assert.equal(healthy.result.nodes.filter(node => node.label === 'Function').length, count);
    assert.equal(healthy.result.edges.filter(edge => edge.type === 'DEFINES').length, count);
    const expected = canonical(healthy.result); assert.deepEqual(canonical(f.run().result), expected);
    let cancel = false;
    empty(f.run({ shouldCancel: () => cancel, onWork: work => { if (work >= healthy.work / 2) cancel = true; } }).result);
    assert.deepEqual(canonical(f.run().result), expected);
  } finally {
    ExtractionWorkBudget.prototype.reserveOutput = reserve;
    if (locationDescriptor) Object.defineProperty(FunctionDeclaration.prototype, 'getStartLineNumber', locationDescriptor); else Reflect.deleteProperty(FunctionDeclaration.prototype, 'getStartLineNumber');
    f.dispose();
  }
});

test('normal React context and Next route/layout facts retain canonical identities under central output reservations', () => {
  const f = fixture({
    'context.tsx': "import {createContext} from 'react';export function fallback(){}export function save(){}export const Ctx=createContext({save:fallback});",
    'app/page.tsx': "import {useContext} from 'react';import {Ctx,save} from '../context';function Leaf(){useContext(Ctx);return <article/>;}export default function Page(){return <Ctx.Provider value={{save}}><Leaf/></Ctx.Provider>;}",
    'app/layout.tsx': 'export default function Layout({children}){return <section>{children}</section>;}',
    'next.config.mjs': "const base='/ui';const config={basePath:base};export default config;",
  });
  try {
    const healthy = f.run(); const result = healthy.result; const expected = canonical(result);
    const route = result.nodes.find(node => node.properties.full_path === '/ui'); assert.ok(route);
    assert.ok(result.edges.some(edge => edge.sourceQN === route.qualifiedName && edge.targetQN === 'Alloc:app/page.tsx#Page' && edge.type === 'HANDLES'));
    assert.ok(result.edges.some(edge => edge.sourceQN === route.qualifiedName && edge.targetQN === 'Alloc:app/layout.tsx#Layout' && edge.type === 'RENDERS'));
    const provider = result.nodes.find(node => node.properties.context_kind === 'provider'); assert.ok(provider);
    assert.ok(result.edges.some(edge => edge.sourceQN === 'Alloc:app/page.tsx#Leaf' && edge.targetQN === provider.qualifiedName && edge.properties?.context_kind === 'binding'));
    assert.ok(result.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === 'Alloc:context.tsx#save' && edge.type === 'CALLS'));
    assert.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), expected);
    assert.deepEqual(canonical(f.run().result), expected);
  } finally { f.dispose(); }
});

function observeRootCandidates<T>(count: number, action: () => T) {
  const filter = Array.prototype.filter, has = Set.prototype.has;
  let visits = 0;
  const root = (value: unknown) => typeof value === 'string' && /^apps\/r\d+$/.test(value);
  Array.prototype.filter = function(this: unknown[], ...args: Parameters<typeof filter>) {
    if (this.length === count && root(this[0]) && this.every(root)) visits += this.length;
    return filter.apply(this, args);
  } as typeof filter;
  Set.prototype.has = function(this: Set<unknown>, value: unknown) {
    if (this.size === count && root(this.values().next().value)) visits++;
    return has.call(this, value);
  };
  try { return { value: action(), visits }; } finally { Array.prototype.filter = filter; Set.prototype.has = has; }
}

test('many Next roots/pages use ancestor lookups rather than copying every root for every page', context => {
  const measure = (count: number) => {
    const files = Object.fromEntries(Array.from({ length: count }, (_, index) => {
      const id = String(index).padStart(4, '0'); return [[`apps/r${id}/app/page.jsx`, page], [`apps/r${id}/next.config.mjs`, `export default {basePath:'/ui/${id}'};`]];
    }).flat());
    const f = fixture(files);
    try {
      const observed = observeRootCandidates(count, () => f.run());
      assert.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
      assert.equal(observed.value.result.nodes.filter(node => node.properties.route_kind === 'ui').length, count);
      assert.ok(observed.visits <= count * 10, `root candidate visits must be proportional: ${observed.visits} for ${count} roots/pages`);
      return { work: observed.value.work, visits: observed.visits };
    } finally { f.dispose(); }
  };
  const smaller = measure(100), larger = measure(300);
  assert.ok(larger.work <= smaller.work * 4, `3x roots/pages cannot require quadratic lookup work: ${smaller.work} -> ${larger.work}`);
  context.diagnostic(`100 roots: ${smaller.work} work/${smaller.visits} candidate visits; 300 roots: ${larger.work}/${larger.visits}`);
});

test('ancestor-indexed Next roots preserve root, nested app basePath and layout selection', () => {
  const f = fixture({
    'next.config.mjs': "export default {basePath:'/root'};", 'app/page.jsx': page,
    'apps/inner/next.config.cjs': "module.exports={basePath:'/nested'};", 'apps/inner/src/app/(group)/[id]/page.jsx': page,
    'apps/inner/src/app/layout.jsx': 'export default function Layout({children}){return <main>{children}</main>;}',
    'apps/inner/deep/next.config.mjs': "export default {basePath:'/deep'};", 'apps/inner/deep/app/page.jsx': page,
  });
  try {
    const result = f.run().result; canonical(result);
    const routes = result.nodes.filter(node => node.properties.route_kind === 'ui');
    assert.deepEqual(routes.map(node => [node.properties.app_root, node.properties.full_path]).sort(), [['.', '/root'], ['apps/inner', '/nested/[id]'], ['apps/inner/deep', '/deep']]);
    const nested = routes.find(node => node.properties.app_root === 'apps/inner')!;
    assert.ok(result.edges.some(edge => edge.sourceQN === nested.qualifiedName && edge.targetQN === 'Alloc:apps/inner/src/app/layout.jsx#Layout' && edge.type === 'RENDERS'));
  } finally { f.dispose(); }
});

test('bounded Next config alias folding retains normal literals, rejects cycles and refuses excessive depth safely', () => {
  const run = (tail: string) => {
    const f = fixture({ 'app/page.jsx': page, 'next.config.mjs': tail });
    try { return f.run().result; } finally { f.dispose(); }
  };
  const normal = run("const base='/alias';const config={basePath:base};const alias=config;export default alias;");
  canonical(normal); assert.ok(normal.nodes.some(node => node.properties.full_path === '/alias'));
  const cyclic = run('const first=second;const second=first;export default first;');
  assert.equal(cyclic.complete, false); assert.ok(!cyclic.nodes.some(node => node.properties.route_kind === 'ui'));
  const declarations = ["const c0={basePath:'/too-deep'};", ...Array.from({ length: 150 }, (_, index) => `const c${index + 1}=c${index};`), 'export default c150;'];
  const deep = run(declarations.join('\n')); empty(deep);
  assert.ok(!deep.diagnostics?.some(message => /RangeError|Maximum call stack/i.test(message)), deep.diagnostics?.join('\n'));
});


test('extended tsconfig JSON shares AST retention and cannot translate budget refusal into partial success', () => {
  const rootConfig = JSON.stringify({ extends: './configs/base.json', include: ['src/**/*.ts', 'lib/**/*.ts'] });
  const baseConfig = JSON.stringify({ compilerOptions: { baseUrl: '..', paths: { '@lib/*': ['lib/*'] }, target: 'ES2020' } });
  const entry = "import {helper} from '@lib/helper';export function App(){return helper();}";
  const helper = 'export function helper(){return 1;}';
  const f = fixture({ 'tsconfig.json': rootConfig, 'configs/base.json': baseConfig, 'src/entry.ts': entry, 'lib/helper.ts': helper });
  const json = ts.parseJsonText('tsconfig.json', rootConfig); const pending: ts.Node[] = [json]; let rootNodes = 0;
  while (pending.length) { const node = pending.pop()!; rootNodes++; ts.forEachChild(node, child => { pending.push(child); }); }
  const sourceNodes = rawNodes('entry.ts', entry) + rawNodes('helper.ts', helper);
  const read = BoundedTextReader.prototype.read, reserve = ExtractionWorkBudget.prototype.reserveOutput;
  let baseReads = 0, successfulOutputs = 0;
  BoundedTextReader.prototype.read = function(this: BoundedTextReader, ...args: Parameters<BoundedTextReader['read']>) {
    if (path.resolve(args[0]) === path.join(f.root, 'configs/base.json')) baseReads++;
    return read.apply(this, args);
  };
  ExtractionWorkBudget.prototype.reserveOutput = function(this: ExtractionWorkBudget, kind, textUnits = 0) {
    reserve.call(this, kind, textUnits); successfulOutputs++;
  };
  try {
    // The root JSON's checked parse and the compiler's hidden parse both fit; extension parsing does not.
    const observed = observeParsers(() => f.run({ maxAstNodes: sourceNodes + rootNodes * 2 + 1 }));
    empty(observed.value.result); assert.equal(observed.counts.other, 2);
    assert.ok(baseReads > 0, 'the refusal must actually enter the admitted extends JSON host');
    assert.equal(successfulOutputs, 0, 'a poisoned compiler-config read cannot emit later workspace or semantic metadata');
    const healthy = f.run(); const expected = canonical(healthy.result);
    assert.ok(healthy.result.resolvedImports.some(item => item.importedNamespace === '@lib/helper' && item.resolvedFilePath === 'lib/helper.ts'));
    assert.ok(healthy.result.edges.some(edge => edge.sourceQN === 'Alloc:src/entry.ts#App' && edge.targetQN === 'Alloc:lib/helper.ts#helper' && edge.type === 'CALLS'));
    assert.deepEqual(canonical(f.run().result), expected);
  } finally { BoundedTextReader.prototype.read = read; ExtractionWorkBudget.prototype.reserveOutput = reserve; f.dispose(); }
});

test('standalone same-directory source discovery reuses marker inputs and directory probes', context => {
  const count = 100;
  const files: Record<string, string> = { 'app/page.jsx': page, 'next.config.mjs': "export default {basePath:'/ui'};" };
  for (let index = 0; index < count; index++) files[`part${String(index).padStart(4, '0')}.ts`] = `export function F${index}(){return 1;}`;
  const f = fixture(files);
  const includes = Array.prototype.includes, exists = BoundedTextReader.prototype.exists;
  const marker = /(?:^|\/)(?:package\.json|angular\.json|tsconfig\.json|next\.config\.(?:ts|js|mjs|cjs))$/;
  let inputVisits = 0, probes = 0;
  Array.prototype.includes = function(this: unknown[], ...args: Parameters<typeof includes>) {
    if (this.length >= count && typeof this[0] === 'string' && /^(?:part\d+\.ts|app\/page\.jsx)$/.test(this[0]) && typeof args[0] === 'string' && marker.test(args[0])) inputVisits += this.length;
    return includes.apply(this, args);
  };
  BoundedTextReader.prototype.exists = function(this: BoundedTextReader, file: string) {
    if (marker.test(path.relative(f.root, file).replace(/\\/g, '/'))) probes++;
    return exists.call(this, file);
  };
  try {
    let work = 0;
    const result = extractProject('Alloc', f.root, path.join(f.root, 'tsconfig.json'), () => {}, undefined, undefined, { onWork: value => { work = value; } });
    canonical(result);
    assert.equal(result.nodes.filter(node => node.label === 'Function' && /^F\d+$/.test(node.name)).length, count);
    assert.ok(result.nodes.some(node => node.properties.full_path === '/ui'), 'standalone static Next marker detection must remain active');
    assert.ok(inputVisits <= count * 5, `input membership must not rescan every source: ${inputVisits}`);
    assert.ok(probes <= 40, `same directories must not probe markers for each source: ${probes}`);
    context.diagnostic(`100 same-directory sources: ${work} work, ${inputVisits} input-list visits, ${probes} marker probes`);
  } finally { Array.prototype.includes = includes; BoundedTextReader.prototype.exists = exists; f.dispose(); }
});

test('unmatched wildcard package exports spend bounded work before scanning all pattern/source pairs', context => {
  const patterns = Object.fromEntries(Array.from({ length: 100 }, (_, index) => [`./never-match-${index}/*`, `./dist/never-match-${index}/*.js`]));
  const files: Record<string, string> = {
    'package.json': JSON.stringify({ name: '@fixture/unmatched', exports: patterns }),
    'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: 'src', outDir: 'dist' }, include: ['src/**/*'] }),
  };
  for (let index = 0; index < 100; index++) files[`src/value${index}.ts`] = `export const value${index}=0;`;
  const f = fixture(files);
  const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
  for (const [file, text] of Object.entries(files)) if (file.endsWith('.ts')) project.createSourceFile(path.join(f.root, file), text);
  const scope = new DiscoveryScope(f.root, Object.keys(files));
  const startsWith = String.prototype.startsWith; let comparisons = 0;
  String.prototype.startsWith = function(this: string, search: string, position?: number) {
    if (search.includes('never-match-')) comparisons++;
    return startsWith.call(this, search, position);
  };
  try {
    const budget = new ExtractionWorkBudget({ maxWork: 150000 });
    assert.throws(() => analyzeWorkspaceImports('Alloc', f.root, path.join(f.root, 'tsconfig.json'), project.getSourceFiles(), () => {}, scope,
      new BoundedTextReader(f.root, file => scope.includesFile(file)), budget), error => error instanceof ExtractionWorkLimitError && error.reason === 'work_limit');
    assert.ok(comparisons > 0 && comparisons < 10000, `refuse inside the pair scan, not after all 10000 pairs: ${comparisons}`);
    context.diagnostic(`unmatched 100x100 wildcard scan refused after ${comparisons} prefix comparisons and ${budget.used} work`);
    comparisons = 0;
    empty(f.run({ maxWork: 150000 }).result);
    assert.ok(comparisons < 10000);
  } finally { String.prototype.startsWith = startsWith; f.dispose(); }
  const normal = fixture({
    'package.json': JSON.stringify({ name: '@fixture/match', exports: { './widgets/*': './dist/*.js' } }),
    'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: 'src', outDir: 'dist' }, include: ['src/**/*'] }),
    'src/a.ts': 'export function A(){return 1;}',
  });
  try {
    const result = normal.run().result; canonical(result);
    const callable = result.nodes.find(node => node.qualifiedName === 'Alloc:src/a.ts#A'); assert.ok(callable);
    assert.ok((callable.properties.npm_exports as Array<{ import_path: string; export_name: string }>).some(item => item.import_path === '@fixture/match/widgets/a' && item.export_name === 'A'));
  } finally { normal.dispose(); }
});
