import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { Project } from 'ts-morph';
import { viteLibrarySource, ViteLibraryAnalysisLimitError } from './viteLibraryExports';
import { extractProject } from './extractor';

const packageRoot = '/fixture/ui';
const entry = `${packageRoot}/src/index.ts`;
const output = `${packageRoot}/dist/ui.es.js`;
const library = `const library={entry:'src/index.ts',fileName:()=> 'ui.es.js'};`;
const configuration = `import {defineConfig} from 'vite';\n${library}\nexport default defineConfig({build:{lib:library}});`;

function fixture(text = configuration) {
  const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
    compilerOptions: { noResolve: true, types: [] } });
  const config = project.createSourceFile(`${packageRoot}/vite.config.ts`, text);
  const index = project.createSourceFile(entry, 'export const Panel=()=>null;');
  return new Map([[config.getFilePath(), config], [index.getFilePath(), index]]);
}
function reverseAliases(count: number): string {
  const aliases = Array.from({ length: count }, (_, offset) => {
    const number = offset + 1;
    return `a${number}=${number === 1 ? 'library' : `a${number - 1}`}`;
  });
  return `import {defineConfig} from 'vite';\n${library}\nconst ${aliases.join(',')};\na${count}.entry='src/changed.ts';\nexport default defineConfig({build:{lib:library}});`;
}
function refusal(action: () => unknown, reason: 'work_limit' | 'cancelled'): ViteLibraryAnalysisLimitError {
  let error: unknown;
  try { action(); } catch (caught) { error = caught; }
  assert.ok(error instanceof ViteLibraryAnalysisLimitError);
  assert.equal(error.code, 'vite_library_analysis_incomplete');
  assert.equal(error.reason, reason);
  assert.ok(Number.isFinite(error.work)); assert.ok(Number.isFinite(error.limit));
  return error;
}

test('reverse 20k alias write closure is deterministic linear work and cannot expose mutated Vite output', () => {
  const count = 20000;
  const text = reverseAliases(count);
  const measure = (maxWork?: number) => {
    let work = 0;
    const sources = fixture(text);
    const result = viteLibrarySource(packageRoot, sources, output, () => {}, { maxWork, onWork: value => { work = value; } });
    assert.deepEqual(result, []);
    assert.ok(work > count, `work ${work} must include the alias graph`);
    assert.ok(work <= count * 60 + 5000, `work ${work} must remain linear in ${count} aliases`);
    return work;
  };
  assert.equal(measure(2000000), measure(), 'the default budget handles 20k aliases with deterministic linear work');
});

test('repeated output candidates share a Vite summary and cumulative source-map work budget', () => {
  const sources = fixture(); let work = 0;
  const control = { maxWork: 10000, onWork: (value: number) => { work = value; } };
  assert.deepEqual(viteLibrarySource(packageRoot, sources, output, () => {}, control), [entry]);
  const initial = work;
  for (let index = 0; index < 30; index++) assert.deepEqual(viteLibrarySource(packageRoot, sources, `${packageRoot}/dist/wrong-${index}.js`, () => {}, control), []);
  assert.deepEqual(viteLibrarySource(packageRoot, sources, output, () => {}, control), [entry]);
  assert.ok(work >= initial, 'onWork is cumulative for the source map');
  const cachedCallBound = 31 * (packageRoot.length * 8 + output.length * 3 + 256);
  assert.ok(work - initial <= cachedCallBound, `repeated candidates must reuse analysis, consumed ${work - initial}`);
});

test('work-limit refusal poisons a run and cannot be reset by a later candidate or larger limit', () => {
  const sources = fixture();
  const error = refusal(() => viteLibrarySource(packageRoot, sources, output, () => {}, { maxWork: 1 }), 'work_limit');
  assert.ok(error.work >= 0 && error.work <= error.limit, 'reported work is the consumed budget before the refused charge');
  refusal(() => viteLibrarySource(packageRoot, sources, `${packageRoot}/dist/other.js`, () => {}, { maxWork: 1000000 }), 'work_limit');
  assert.deepEqual(viteLibrarySource(packageRoot, new Map(sources), output, () => {}, { maxWork: 1000000 }), [entry], 'a new analysis receives a fresh budget');
});

test('cancellation is observed on cached summaries and poisons every subsequent query in that run', () => {
  const sources = fixture(); let cancelled = false;
  const control = { maxWork: 10000, shouldCancel: () => cancelled };
  assert.deepEqual(viteLibrarySource(packageRoot, sources, output, () => {}, control), [entry]);
  cancelled = true;
  refusal(() => viteLibrarySource(packageRoot, sources, output, () => {}, control), 'cancelled');
  cancelled = false;
  refusal(() => viteLibrarySource(packageRoot, sources, output, () => {}, control), 'cancelled');
  assert.deepEqual(viteLibrarySource(packageRoot, new Map(sources), output, () => {}, control), [entry]);
});

test('alias cycles terminate conservatively while normal @ui Vite path aliases retain their source mapping', () => {
  const cyclic = fixture(`import {defineConfig} from 'vite'; const first=second; const second=first; second.entry='changed'; export default defineConfig(first);`);
  assert.deepEqual(viteLibrarySource(packageRoot, cyclic, output, () => {}, { maxWork: 10000 }), []);
  const normal = fixture(`import {resolve as locate} from 'node:path'; import {defineConfig as config} from 'vite';
    const source=locate(import.meta.dirname,'src/index.ts');
    const library={entry:source,fileName:()=> 'ui.es.js'};
    export default config({build:{lib:library}});`);
  assert.deepEqual(viteLibrarySource(packageRoot, normal, output, () => {}, { maxWork: 10000 }), [entry]);
});

test('generic project extraction preserves incomplete status when static Vite analysis exhausts its work budget', () => {
  // Trigger the real typed error with a tiny trusted budget, then inject that error at the
  // resolver seam. This covers persistence-facing failure behavior without executing a large
  // adversarial config through unrelated extraction passes.
  const captured = refusal(() => viteLibrarySource(packageRoot, fixture(), output, () => {}, { maxWork: 1 }), 'work_limit');
  const module = require('./viteLibraryExports') as { viteLibrarySource: typeof viteLibrarySource };
  const original = module.viteLibrarySource;
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-vite-budget-boundary-'));
  try {
    fs.mkdirSync(path.join(root, 'src'));
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: '@fixture/ui', main: './dist/ui.es.js' }));
    fs.writeFileSync(path.join(root, 'vite.config.ts'), configuration);
    fs.writeFileSync(path.join(root, 'src/index.ts'), 'export const Panel=()=>null;');
    let calls = 0;
    module.viteLibrarySource = () => { calls++; throw captured; };
    const result = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, ['package.json', 'vite.config.ts', 'src/index.ts']);
    assert.ok(calls > 0, 'fixture must reach Vite package-entry resolution');
    assert.equal(result.complete, false);
    assert.deepEqual(result.nodes, []); assert.deepEqual(result.edges, []); assert.deepEqual(result.processedFiles, []);
    assert.ok(result.diagnostics?.some(message => /Vite.*work|vite_library_analysis_incomplete/.test(message)), result.diagnostics?.join('\n'));
  } finally { module.viteLibrarySource = original; fs.rmSync(root, { recursive: true, force: true }); }
});
