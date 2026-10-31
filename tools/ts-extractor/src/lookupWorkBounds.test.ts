import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Project, ClassDeclaration, SourceFile, Node, ts } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { analyzeHttp } from './httpAnalysis';
import { extractProject } from './extractor';
import type { ExtractionWorkControl } from './extractionWork';
import type { PendingEdgeDto, ResolvedImportDto } from './protocol';

/** Fixtures supply the same admitted module records as workspace resolution, without dependencies. */
function modelFor(sources: Record<string, string>, locals: Record<string, string> = {}, control: ExtractionWorkControl = {}) {
  const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
    compilerOptions: { noResolve: true, types: [], experimentalDecorators: true, target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext } });
  const files = Object.entries(sources).map(([name, text]) => project.createSourceFile(`/repo/${name}`, text));
  const imports: ResolvedImportDto[] = [];
  for (const file of files) {
    const relative = file.getFilePath().slice(6);
    const add = (specifier: string, kind: 'static' | 'export') => {
      const target = locals[`${relative}:${specifier}`];
      const external = specifier.startsWith('@') ? specifier.split('/').slice(0, 2).join('/') : specifier.split('/')[0];
      imports.push({ fileQN: `Lookup:${relative}`, filePath: relative, importedNamespace: specifier, importKind: kind,
        classification: target ? 'local_file' : specifier.startsWith('.') ? 'unresolved_alias' : 'external_npm_package',
        ...(target ? { resolvedFilePath: target, targetFileQN: `Lookup:${target}` } : { externalPackageName: external }) });
    };
    for (const imported of file.getImportDeclarations()) add(imported.getModuleSpecifierValue(), 'static');
    for (const exported of file.getExportDeclarations()) { const specifier = exported.getModuleSpecifierValue(); if (specifier) add(specifier, 'export'); }
  }
  const diagnostics: string[] = [];
  const model = new SemanticModel('Lookup', '/repo', files, imports, message => diagnostics.push(message), control);
  const http = () => {
    const edges: PendingEdgeDto[] = []; const before = model.work.used;
    const coverage = analyzeHttp(model, [], edges, message => diagnostics.push(message));
    return { edges, coverage, work: model.work.used - before };
  };
  return { model, files, diagnostics, http };
}
const targets = (edges: PendingEdgeDto[], owner: string) => edges.filter(edge => edge.sourceQN === owner && edge.type === 'HTTP_CALLS').map(edge => edge.targetQN).sort();

function restore(prototype: object, key: string, descriptor: PropertyDescriptor | undefined) {
  if (descriptor) Object.defineProperty(prototype, key, descriptor); else Reflect.deleteProperty(prototype, key);
}

test('heritage reconciliation indexes each class list once instead of materializing it per IMPLEMENTS edge', context => {
  const measure = (count: number) => {
    const contracts = Array.from({ length: count }, (_, index) => `export interface I${index}{}`).join('\n');
    const names = Array.from({ length: count }, (_, index) => `C.I${index}`);
    const f = modelFor({ 'contracts.ts': contracts, 'impl.ts': `import * as C from './contracts'; export class Impl implements ${names.join(',')} {}` }, { 'impl.ts:./contracts': 'contracts.ts' });
    const original = ClassDeclaration.prototype.getImplements;
    const descriptor = Object.getOwnPropertyDescriptor(ClassDeclaration.prototype, 'getImplements');
    let calls = 0, candidates = 0;
    Object.defineProperty(ClassDeclaration.prototype, 'getImplements', { configurable: true, writable: true, value: function(this: ClassDeclaration) {
      const result = original.call(this);
      if (this.getSourceFile().getFilePath() === '/repo/impl.ts') { calls++; candidates += result.length; }
      return result;
    } });
    const before = f.model.work.used;
    try {
      for (let run = 0; run < 2; run++) {
        const edges = names.map(targetQN => ({ sourceQN: 'impl.Impl', targetQN, type: 'IMPLEMENTS' }));
        f.model.reconcileAngular([], edges);
        assert.equal(edges.length, count);
        assert.deepEqual(edges.map(edge => edge.targetQN).sort(), Array.from({ length: count }, (_, index) => `contracts.I${index}`).sort());
      }
      assert.equal(calls, 1, 'fresh raw edge sets reuse the immutable class index'); assert.equal(candidates, count);
      return { work: f.model.work.used - before, calls, candidates };
    } finally { restore(ClassDeclaration.prototype, 'getImplements', descriptor); }
  };
  const small = measure(200), large = measure(600);
  assert.ok(large.work <= small.work * 4, `3x entries must not cause quadratic reconciliation: ${small.work} -> ${large.work}`);
  context.diagnostic(`200 entries: ${small.calls} materialization/${small.candidates} candidates/${small.work} work; 600: ${large.calls}/${large.candidates}/${large.work}`);
});

test('heritage raw text keeps qualified aliases distinct, preserves the first duplicate expression and drops unknown targets', () => {
  const f = modelFor({
    'contracts.ts': 'export interface Contract<T>{}', 'one.ts': 'export interface Shared{}', 'two.ts': 'export interface Shared{}',
    'impl.ts': `import {Contract as Alias} from './contracts'; import * as One from './one'; import * as Two from './two';
      export class Impl implements Alias<string>, Alias<number>, Missing {}
      export class Qualified implements One.Shared, Two.Shared, One.Missing {}`,
  }, { 'impl.ts:./contracts': 'contracts.ts', 'impl.ts:./one': 'one.ts', 'impl.ts:./two': 'two.ts' });
  const file = f.files.find(item => item.getBaseName() === 'impl.ts')!;
  const first = file.getClassOrThrow('Impl').getImplements()[0].getExpression().getPos();
  const positions: number[] = []; const target = f.model.target;
  f.model.target = function(this: SemanticModel, node: Node | undefined) {
    if (node?.getText() === 'Alias') positions.push(node.getPos());
    return target.call(this, node);
  };
  const edges: PendingEdgeDto[] = [
    { sourceQN: 'impl.Impl', targetQN: 'Alias', type: 'IMPLEMENTS' }, { sourceQN: 'impl.Impl', targetQN: 'Alias', type: 'IMPLEMENTS' },
    { sourceQN: 'impl.Impl', targetQN: 'Missing', type: 'IMPLEMENTS' },
    { sourceQN: 'impl.Qualified', targetQN: 'One.Shared', type: 'IMPLEMENTS' }, { sourceQN: 'impl.Qualified', targetQN: 'Two.Shared', type: 'IMPLEMENTS' },
    { sourceQN: 'impl.Qualified', targetQN: 'One.Missing', type: 'IMPLEMENTS' },
  ];
  f.model.reconcileAngular([], edges);
  assert.deepEqual(positions, [first, first], 'duplicate raw text must use the first source expression');
  assert.deepEqual(edges.filter(edge => edge.sourceQN === 'impl.Impl').map(edge => edge.targetQN), ['contracts.Contract', 'contracts.Contract']);
  assert.deepEqual(edges.filter(edge => edge.sourceQN === 'impl.Qualified').map(edge => edge.targetQN).sort(), ['one.Shared', 'two.Shared']);
  assert.ok(!edges.some(edge => edge.targetQN.includes('Missing')));
});

test('HTTP-shaped unrelated calls build a bounded per-file import index instead of scanning imports per call', context => {
  const measure = (count: number) => {
    const imports = Array.from({ length: count }, (_, index) => { const id = String(index).padStart(4, '0'); return `import {Marker as M${id}} from 'pkg${id}';`; }).join('\n');
    const calls = Array.from({ length: count }, (_, index) => `client.get('/not-http/${index}');`).join('\n');
    const f = modelFor({ 'noise.ts': `${imports}\nexport function noise(client:M0000){${calls}}` });
    const original = SourceFile.prototype.getImportDeclarations;
    const descriptor = Object.getOwnPropertyDescriptor(SourceFile.prototype, 'getImportDeclarations');
    let materializations = 0, candidates = 0;
    Object.defineProperty(SourceFile.prototype, 'getImportDeclarations', { configurable: true, writable: true, value: function(this: SourceFile) {
      const result = original.call(this);
      if (this.getFilePath() === '/repo/noise.ts') { materializations++; candidates += result.length; }
      return result;
    } });
    try {
      const result = f.http(); assert.equal(result.coverage.complete, true, f.diagnostics.join('\n'));
      assert.equal(result.edges.filter(edge => edge.type === 'HTTP_CALLS').length, 0);
      assert.ok(materializations <= 3, `one construction per immutable index, never one per get(): ${materializations}`);
      assert.ok(candidates <= count * 3);
      return { work: result.work, materializations, candidates };
    } finally { restore(SourceFile.prototype, 'getImportDeclarations', descriptor); }
  };
  const small = measure(200), large = measure(600);
  assert.ok(large.work <= small.work * 4, `3x imports/calls cannot require quadratic recognition work: ${small.work} -> ${large.work}`);
  context.diagnostic(`200 imports/calls: ${small.materializations} materializations/${small.candidates} candidates/${small.work} work; 600: ${large.materializations}/${large.candidates}/${large.work}`);
});

test('HTTP import provenance preserves aliases, namespace/barrel/member receivers and caller-specific URLs while rejecting shadows', () => {
  const f = modelFor({
    'tokens.ts': "export {HttpClient as Client} from '@angular/common/http'; export {inject as acquire} from '@angular/core';",
    'client.ts': `import {Client,acquire} from './tokens'; import * as AngularHttp from '@angular/common/http';
      const client=new Client(); const namespaceClient=new AngularHttp.HttpClient();
      export function wrap(url:string){return client.get(url);}
      export function Page(){wrap('/api/first');wrap('/api/second');}
      export function namespaceRead(){return namespaceClient.get('/api/namespace');}
      export class Service { injected=acquire(Client); constructor(private http:Client){}
        member(){return this.http.post('/api/member',{});} injectedRead(){return this.injected.get('/api/injected');}}
      export function shadow(Client:any){const fake=new Client();return fake.get('/not-http/shadow');}
      const unrelated={get(url:string){return url;}};export function otherGet(){return unrelated.get('/not-http/object');}
      const endpoint={path:'/api/initial'};endpoint.path=runtimeValue;
      export function mutableUrl(){return client.get(endpoint.path);}`,
    'other.ts': "import {HttpClient as Client} from 'another-client';export function read(http:Client){return http.get('/not-http/other-file');}",
  }, { 'client.ts:./tokens': 'tokens.ts' });
  const result = f.http(); assert.equal(result.coverage.complete, true, f.diagnostics.join('\n'));
  assert.deepEqual(targets(result.edges, 'Lookup:client.ts#Page'), ['route:*:GET:/api/first', 'route:*:GET:/api/second']);
  assert.deepEqual(targets(result.edges, 'Lookup:client.ts#namespaceRead'), ['route:*:GET:/api/namespace']);
  assert.deepEqual(targets(result.edges, 'client.Service.member'), ['route:*:POST:/api/member']);
  assert.deepEqual(targets(result.edges, 'client.Service.injectedRead'), ['route:*:GET:/api/injected']);
  for (const owner of ['Lookup:client.ts#shadow', 'Lookup:client.ts#otherGet', 'Lookup:other.ts#read']) assert.deepEqual(targets(result.edges, owner), []);
  assert.ok(!result.edges.some(edge => edge.targetQN.includes('/api/initial')), 'immutable transport classification must not cache a stale mutable URL');
});

test('HTTP import recognition resets between models using the same file and alias names', () => {
  const make = (module: string, endpoint: string) => modelFor({ 'same.ts': `import {HttpClient as Client} from '${module}';export function read(http:Client){return http.get('${endpoint}');}` }).http();
  assert.deepEqual(targets(make('@angular/common/http', '/api/one').edges, 'Lookup:same.ts#read'), ['route:*:GET:/api/one']);
  assert.deepEqual(targets(make('another-client', '/not-http/two').edges, 'Lookup:same.ts#read'), []);
  assert.deepEqual(targets(make('@angular/common/http', '/api/three').edges, 'Lookup:same.ts#read'), ['route:*:GET:/api/three']);
});

test('malformed auxiliary Next configs block known-root fallback, batch syntax checks and recover on config-only edits', () => {
  for (const extension of ['mjs', 'cjs']) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-next-syntax-'));
    const name = `next.config.${extension}`, nestedName = `nested/next.config.${extension}`;
    const prefix = extension === 'mjs' ? 'export default ' : 'module.exports=';
    fs.mkdirSync(path.join(root, 'app')); fs.mkdirSync(path.join(root, 'nested/app'), { recursive: true });
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: 'next-app', dependencies: { next: '15' } }));
    const page = 'export default function Page(){return <main/>;}';
    fs.writeFileSync(path.join(root, 'app/page.jsx'), page); fs.writeFileSync(path.join(root, 'nested/app/page.jsx'), page);
    fs.writeFileSync(path.join(root, name), `${prefix}{basePath:'/broken'`);
    fs.writeFileSync(path.join(root, nestedName), `${prefix}{basePath:'/nested'};`);
    const create = Project.prototype.createSourceFile, program = Project.prototype.getProgram;
    const auxiliary = new WeakSet<Project>(); let programs = 0;
    Project.prototype.createSourceFile = function(this: Project, ...args: Parameters<Project['createSourceFile']>) {
      if (/^next\.config\.(?:mjs|cjs)$/.test(path.basename(args[0]))) auxiliary.add(this);
      return create.apply(this, args);
    };
    Project.prototype.getProgram = function(this: Project) { if (auxiliary.has(this)) programs++; return program.call(this); };
    const run = () => {
      const before = programs;
      const result = extractProject('Lookup', root, path.join(root, 'tsconfig.json'), () => {}, ['package.json', 'app/page.jsx', 'nested/app/page.jsx', name, nestedName]);
      assert.ok(programs - before <= 1, 'all auxiliary configs must be parsed before the shared syntax-check program is materialized');
      return result;
    };
    try {
      const broken = run(); assert.equal(broken.complete, false);
      assert.ok(!broken.nodes.some(node => node.properties.route_kind === 'ui' && node.properties.app_root === '.'), 'a known Next dependency must not turn a malformed config into a / fallback route');
      assert.ok(broken.diagnostics?.some(message => /(?:parse|syntax).*next\.config|next\.config.*(?:parse|syntax)/i.test(message)), broken.diagnostics?.join('\n'));
      fs.writeFileSync(path.join(root, name), `${prefix}{basePath:'/valid'};`);
      const recovered = run(); assert.equal(recovered.complete, true, recovered.diagnostics?.join('\n'));
      assert.deepEqual(recovered.nodes.filter(node => node.properties.route_kind === 'ui').map(node => node.properties.full_path).sort(), ['/nested', '/valid']);
      assert.ok(recovered.edges.some(edge => edge.type === 'HANDLES' && edge.targetQN === 'Lookup:app/page.jsx#Page'));
    } finally { Project.prototype.createSourceFile = create; Project.prototype.getProgram = program; fs.rmSync(root, { recursive: true, force: true }); }
  }
});
