import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { BoundedTextReader, validateSourceScope } from './boundedTextReader';
import { extractProject } from './extractor';
import { buildApp, readIncludedFiles } from './app';
import type { SourceReadLimits } from './protocol';

function fixture(files: Record<string, string>) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-source-budget-'));
  for (const [file, text] of Object.entries(files)) { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); }
  return { root, file: (name: string) => path.join(root, name), dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test('zero defaults and unrelated zero overrides allow positively admitted frontend extensions', () => {
  const f = fixture({ 'app.jsx': 'export function App(){return <main/>}' });
  try {
    const cases: SourceReadLimits[] = [
      { maxFileSizeKb: 0, maxFileSizeKbByExtension: { '.jsx': 1 } },
      { maxFileSizeKb: 1, maxFileSizeKbByExtension: { '.sql': 0 } },
    ];
    for (const limits of cases) {
      const result = extractProject('ZeroLimits', f.root, '', () => {}, ['app.jsx'], limits);
      assert.equal(result.complete, true, result.diagnostics?.join('\n'));
      assert.deepEqual(result.processedFiles, ['app.jsx']);
      assert.ok(result.nodes.some(node => node.name === 'App'));
    }
  } finally { f.dispose(); }
});

test('zero text limits accept empty text and refuse nonempty matching inputs', () => {
  const f = fixture({ 'empty.jsx': '', 'app.jsx': 'export const value=1;' });
  try {
    const empty = new BoundedTextReader(f.root, undefined, { maxFileSizeKb: 0, maxAnalysisTextKb: 0 });
    assert.equal(empty.complete, true);
    assert.equal(empty.read(f.file('empty.jsx'), true), '');
    assert.equal(empty.complete, true);
    assert.equal(empty.read(f.file('app.jsx'), true), undefined);
    assert.equal(empty.complete, false);
    const result = extractProject('ZeroLimits', f.root, '', () => {}, ['app.jsx'], {
      maxFileSizeKb: 1, maxFileSizeKbByExtension: { '.jsx': 0 },
    });
    assert.equal(result.complete, false);
    assert.deepEqual(result.processedFiles, []);
  } finally { f.dispose(); }
});

test('file limits match decoded UTF16 units, including multibyte text and case-insensitive extension overrides', () => {
  const f = fixture({ 'bmp.txt': 'é'.repeat(1024), 'emoji.txt': '🌳'.repeat(512), 'too-long.txt': 'é'.repeat(1025), 'large.TSX': 'a'.repeat(1800) });
  try {
    const reader = new BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxFileSizeKbByExtension: { TsX: 2 } });
    assert.equal(reader.read(f.file('bmp.txt'), true)?.length, 1024);
    assert.equal(reader.read(f.file('emoji.txt'), true)?.length, 1024);
    assert.equal(reader.read(f.file('large.TSX'), true)?.length, 1800);
    assert.equal(reader.complete, true);
    assert.equal(reader.read(f.file('too-long.txt'), true), undefined);
    assert.equal(reader.complete, false);
  } finally { f.dispose(); }
});

test('aggregate budget counts unique snapshots and rejected decoded prefixes, and refusals cannot reread bytes', () => {
  const f = fixture({ 'a.txt': 'a'.repeat(700), 'b.txt': 'b'.repeat(700), 'oversize.txt': 'x'.repeat(1100), 'one.txt': 'x' });
  const disk = require('fs') as typeof fs; const originalRead = disk.readSync; let reads = 0;
  try {
    disk.readSync = ((...args: unknown[]) => { reads++; return (originalRead as (...args: unknown[]) => number)(...args); }) as typeof fs.readSync;
    const reader = new BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
    assert.equal(reader.read(f.file('a.txt'), true)?.length, 700);
    const afterFirst = reads; assert.equal(reader.read(f.file('./a.txt'), true)?.length, 700); assert.equal(reads, afterFirst);
    assert.equal(reader.read(f.file('b.txt'), true), undefined); const afterRefusal = reads;
    assert.equal(reader.read(f.file('b.txt'), true), undefined); assert.equal(reads, afterRefusal);
    assert.equal(reader.read(f.file('one.txt'), true), undefined); assert.equal(reads, afterRefusal);
    const rejected = new BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
    assert.equal(rejected.read(f.file('oversize.txt'), true), undefined); const rejectedReads = reads;
    assert.equal(rejected.read(f.file('one.txt'), true), undefined); assert.equal(reads, rejectedReads);
    assert.equal(rejected.complete, false);
  } finally { disk.readSync = originalRead; f.dispose(); }
});

test('definitely oversize metadata, directories, symlinks and replaced opened handles are refused before byte reads', () => {
  const f = fixture({ 'large.txt': 'x'.repeat(10000), 'safe.txt': 'safe' });
  const outside = fixture({ 'secret.txt': 'outside' });
  const disk = require('fs') as typeof fs; const originalOpen = disk.openSync; const originalRead = disk.readSync; let reads = 0;
  try {
    fs.mkdirSync(f.file('directory.txt')); fs.symlinkSync(outside.file('secret.txt'), f.file('link.txt')); fs.symlinkSync(outside.root, f.file('escape'));
    disk.readSync = ((...args: unknown[]) => { reads++; return (originalRead as (...args: unknown[]) => number)(...args); }) as typeof fs.readSync;
    for (const name of ['large.txt', 'directory.txt', 'link.txt', 'escape/secret.txt']) {
      const reader = new BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1 });
      assert.equal(reader.read(f.file(name), true), undefined, name); assert.equal(reader.complete, false);
    }
    assert.equal(reads, 0);
    // Simulate a path swap between lstat and open: only the actual opened handle may be trusted.
    disk.openSync = ((file: fs.PathLike, ...args: unknown[]) => (originalOpen as (...args: unknown[]) => number)(file === f.file('safe.txt') ? outside.file('secret.txt') : file, ...args)) as typeof fs.openSync;
    const swapped = new BoundedTextReader(f.root);
    assert.equal(swapped.read(f.file('safe.txt'), true), undefined); assert.equal(swapped.complete, false); assert.equal(reads, 0);
  } finally { disk.openSync = originalOpen; disk.readSync = originalRead; f.dispose(); outside.dispose(); }
});

test('cached optional absence becomes incomplete when the same input is later required', () => {
  const f = fixture({});
  try { const reader = new BoundedTextReader(f.root); assert.equal(reader.read(f.file('missing.html')), undefined); assert.equal(reader.complete, true);
    fs.writeFileSync(f.file('missing.html'), '<page/>');
    assert.equal(reader.read(f.file('missing.html'), true), undefined); assert.equal(reader.complete, false);
  } finally { f.dispose(); }
});

test('source, extended tsconfig, manifests, Next config and referenced templates share one fail-closed reader', () => {
  const cases: Array<{ files: Record<string, string>; override?: Record<string, number> }> = [
    { files: { 'app.jsx': `export const App=()=> <main/>; /*${'x'.repeat(1500)}*/` } },
    { files: { 'app.ts': 'export class App{}', 'tsconfig.json': '{"extends":"./base.json"}', 'base.json': `{"compilerOptions":{},"padding":"${'x'.repeat(1500)}"}` } },
    { files: { 'app.ts': 'export class App{}', 'pnpm-workspace.yaml': `packages:\n - packages/*\n#${'x'.repeat(1500)}` } },
    { files: { 'app.ts': 'export class App{}', 'angular.json': `{"projects":{},"padding":"${'x'.repeat(1500)}"}` } },
    { files: { 'app.ts': 'export class App{}', 'package.json': `{"name":"large","padding":"${'x'.repeat(1500)}"}` } },
    { files: { 'app/page.tsx': 'export default()=> <main/>;', 'next.config.ts': `export default {basePath:'/ui'}; /*${'x'.repeat(1500)}*/` } },
    { files: { 'app.ts': `import {Component} from '@angular/core'; @Component({templateUrl:'./page.html'}) export class App{}`, 'page.html': 'x'.repeat(1500) } },
  ];
  for (const entry of cases) {
    const f = fixture(entry.files);
    try {
      const result = extractProject('Budget', f.root, f.file('tsconfig.json'), () => {}, Object.keys(entry.files), { maxFileSizeKb: 1 });
      assert.equal(result.complete, false, Object.keys(entry.files).join(',')); assert.deepEqual(result.processedFiles, []);
      assert.ok(result.diagnostics?.some(message => message.startsWith('sourceAdmission:')), JSON.stringify(result.diagnostics));
    } finally { f.dispose(); }
  }
});

test('source/config/template decoding shares aggregate budget and unreferenced HTML is never read', () => {
  const f = fixture({
    'app.ts': `import {Component} from '@angular/core'; @Component({templateUrl:'./page.html'}) export class App{} /*${'x'.repeat(500)}*/`,
    'page.html': 'x'.repeat(600), 'unreferenced.html': 'x'.repeat(10000), 'tsconfig.json': '{}',
  });
  try {
    const included = ['app.ts', 'page.html', 'unreferenced.html', 'tsconfig.json'];
    const full = extractProject('Budget', f.root, f.file('tsconfig.json'), () => {}, included, { maxFileSizeKb: 1, maxAnalysisTextKb: 2 });
    assert.equal(full.complete, true, full.diagnostics?.join('\n'));
    const refused = extractProject('Budget', f.root, f.file('tsconfig.json'), () => {}, included, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
    assert.equal(refused.complete, false); assert.deepEqual(refused.processedFiles, []);
  } finally { f.dispose(); }
});

test('parsers and module/config resolution use the bounded text snapshot without alternate path reads', () => {
  const f = fixture({ 'app.ts': "import {dep} from './dep';export const value=dep();", 'dep.ts': 'export function dep(){return 1;}', 'tsconfig.json': '{}', 'package.json': '{"name":"snapshot"}' });
  const disk = require('fs') as typeof fs; const original = disk.readFileSync;
  try {
    disk.readFileSync = ((file: fs.PathOrFileDescriptor, ...args: unknown[]) => {
      if (typeof file === 'string' && path.resolve(file).startsWith(f.root + path.sep)) throw new Error('unbounded parser read bypass');
      return (original as (...args: unknown[]) => unknown)(file, ...args);
    }) as typeof fs.readFileSync;
    const result = extractProject('Budget', f.root, f.file('tsconfig.json'), () => {}, ['app.ts', 'dep.ts', 'tsconfig.json', 'package.json']);
    assert.equal(result.complete, true, result.diagnostics?.join('\n')); assert.deepEqual(result.processedFiles.sort(), ['app.ts', 'dep.ts']);
    assert.ok(result.edges.some(edge => edge.type === 'CALLS' && edge.targetQN.endsWith('#dep')));
  } finally { disk.readFileSync = original; f.dispose(); }
});

test('scope metadata bounds reject count/character overflow and bounded scope-file reads reject unsafe inputs', () => {
  assert.throws(() => validateSourceScope(Array(50001).fill('a.ts')), /50000/);
  assert.throws(() => validateSourceScope(['x'.repeat(8 * 1024 * 1024 + 1)]), /8Mi/);
  const f = fixture({ 'scope.json': JSON.stringify(['app.ts']), 'app.ts': 'export class App{}' });
  try {
    fs.symlinkSync(f.file('scope.json'), f.file('scope-link.json'));
    assert.throws(() => readIncludedFiles({ repositoryName: 'Budget', rootPath: f.root, tsconfigPath: f.file('tsconfig.json'), includedFilesPath: f.file('scope-link.json') }), /regular-file/);
    const result = extractProject('Budget', f.root, f.file('tsconfig.json'), () => {}, Array(50001).fill('empty.ts'));
    assert.equal(result.complete, false); assert.deepEqual(result.processedFiles, []);
  } finally { f.dispose(); }
});

test('HTTP extraction transport applies request budgets and a following request gets fresh budget state', async () => {
  const f = fixture({ 'app.jsx': `export const App=()=> <main/>; /*${'x'.repeat(1500)}*/` }); const app = buildApp(f.file('logs'));
  try {
    assert.equal((await app.inject({ method: 'GET', url: '/health' })).json().sourceBoundsVersion, 8);
    const payload = { repositoryName: 'Budget', rootPath: f.root, tsconfigPath: f.file('tsconfig.json'), includedFiles: ['app.jsx'], maxFileSizeKb: 1 };
    const refused = await app.inject({ method: 'POST', url: '/extract-project', payload }); assert.equal(refused.statusCode, 200); assert.equal(refused.json().complete, false);
    const accepted = await app.inject({ method: 'POST', url: '/extract-project', payload: { ...payload, maxFileSizeKbByExtension: { '.jsx': 2 } } });
    assert.equal(accepted.statusCode, 200); assert.equal(accepted.json().complete, true); assert.deepEqual(accepted.json().processedFiles, ['app.jsx']);
  } finally { await app.close(); f.dispose(); }
});


test('ordinary directory probes preserve standalone package directory/index resolution', () => {
  const f = fixture({
    'package.json': '{"name":"root","workspaces":["packages/*"]}',
    'packages/ui/package.json': '{"name":"@fixture/ui","exports":{".":"./src"}}',
    'packages/ui/src/index.ts': 'export function Panel(){return 1;}',
    'app.ts': "import {Panel} from '@fixture/ui'; export const render=()=>Panel();",
  });
  try {
    const reader = new BoundedTextReader(f.root);
    assert.equal(reader.exists(f.file('packages/ui/src')), false); assert.equal(reader.complete, true);
    const result = extractProject('Budget', f.root, f.file('tsconfig.json'));
    assert.equal(result.complete, true, result.diagnostics?.join('\n'));
    assert.ok(result.resolvedImports.some(item => item.importedNamespace === '@fixture/ui' && item.resolvedFilePath === 'packages/ui/src/index.ts'));
  } finally { f.dispose(); }
});
