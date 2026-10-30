import test from 'node:test';
import assert from 'node:assert/strict';
import { Project } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import type { ResolvedImportDto } from './protocol';

test('module lookup indexes source plus specifier and caches admitted hits and misses per analysis', () => {
  const project = new Project({ useInMemoryFileSystem: true });
  const first = project.createSourceFile('/repo/a.ts', 'export const a = 1;');
  const second = project.createSourceFile('/repo/b.ts', 'export const b = 1;');
  const target = project.createSourceFile('/repo/target.ts', 'export const target = 1;');
  const imports: ResolvedImportDto[] = [
    { fileQN: 'Repo:a.ts', filePath: 'a.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'target.ts' },
    { fileQN: 'Repo:b.ts', filePath: 'b.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'unadmitted.ts' },
  ];
  const model = new SemanticModel('Repo', '/repo', [first, second, target], imports, () => {});
  imports.find = () => { throw new Error('module lookup must not scan the import list'); };
  for (let index = 0; index < 10; index++) {
    assert.equal(model.module(first, './shared'), target);
    assert.equal(model.module(second, './shared'), undefined);
    assert.equal(model.module(first, './missing'), undefined);
  }
  const next = new SemanticModel('Repo', '/repo', [first, second, target], [
    { ...imports[0], resolvedFilePath: 'b.ts' },
  ], () => {});
  assert.equal(next.module(first, './shared'), second);
});

test('independent resolution queries cache per immutable model while explicit histories and cancellation remain authoritative', () => {
  const project = new Project({ useInMemoryFileSystem: true, compilerOptions: { noResolve: true, types: [] } });
  const file = project.createSourceFile('/repo/index.ts', `
    function Page(){return 1;} function factory(){return {View:Page};}
    const api=factory(); const cycleA=cycleB; const cycleB=cycleA; const dynamic=runtimeValue();
  `);
  let cancelled = false;
  const model = new SemanticModel('UI','/repo',[file],[],()=>{}, {shouldCancel:()=>cancelled});
  const api = file.getVariableDeclarationOrThrow('api');
  const expected = model.resolve(api);
  assert.ok(expected);
  const workBefore = model.work.used;
  for (let index=0;index<100;index++) assert.equal(model.resolve(api),expected);
  assert.equal(model.work.used-workBefore,100, 'cached root queries must still poll/charge without following the factory again');
  // A history-dependent cutoff must neither replace nor consult the independent cache.
  assert.equal(model.resolve(api,new Set(Array.from({length:101},(_,index)=>`history${index}`))),undefined);
  assert.equal(model.resolve(api),expected);
  const cycle = file.getVariableDeclarationOrThrow('cycleA');
  const firstCycle = model.resolve(cycle,new Set());
  assert.equal(model.resolve(cycle),firstCycle);
  const unknown = file.getVariableDeclarationOrThrow('dynamic').getInitializerOrThrow();
  assert.equal(model.resolve(unknown),undefined);
  const unknownBefore=model.work.used; assert.equal(model.resolve(unknown),undefined); assert.equal(model.work.used-unknownBefore,1);
  const fresh = new SemanticModel('UI','/repo',[file],[],()=>{});
  const freshBefore=fresh.work.used; assert.equal(fresh.resolve(api),expected);
  assert.ok(fresh.work.used-freshBefore>1,'a new model must not reuse a previous run cache');
  cancelled=true;
  assert.throws(()=>model.resolve(api), error => (error as {code?:string}).code==='extraction_work_incomplete');
  cancelled=false;
  assert.throws(()=>model.resolve(api), error => (error as {code?:string}).code==='extraction_work_incomplete');
});
