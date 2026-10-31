import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Project, Node } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { addNpmExportMetadata } from './packageExports';
import { DEFAULT_EXTRACTION_WORK_LIMIT, ExtractionWorkBudget, ExtractionWorkLimitError } from './extractionWork';
import { extractProject } from './extractor';
import type { GraphNodeDto, ResolvedImportDto } from './protocol';

const dto = (name: string, qualifiedName: string, properties: Record<string, unknown> = {}): GraphNodeDto => ({
  label: 'Function', name, qualifiedName, filePath: 'index.ts', startLine: 1, endLine: 1, properties,
});
const npmNames = (node: GraphNodeDto): string[] => ((node.properties.npm_exports ?? []) as Array<{ export_name: string }>).map(item => item.export_name);

test('17k public callable constants use one declaration and mutation index with deterministic proportional work', context => {
  const count = 17_000;
  const source = Array.from({ length: count }, (_, index) => `export const E${index}=()=>0;`).join('\n')
    + '\nconst local={key:0}; const alias=local; alias.key=1;';
  assert.ok(source.length < 512 * 1024);
  const run = () => {
    const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
    const file = project.createSourceFile('/repo/index.ts', source);
    const model = new SemanticModel('UI', '/repo', [file], [], () => {});
    const nodes: GraphNodeDto[] = [dto('UI', 'npm:UI', { module_kind: 'typescript_workspace_package', package_name: '@sceneworks/ui',
      package_entrypoints: [{ import_path: '@sceneworks/ui', file_path: 'index.ts' }] })];
    for (const [callable, qn] of model.callables) nodes.push(dto(Node.isArrowFunction(callable) ? callable.getParentOrThrow().getText().split('=')[0].trim() : '', qn));
    addNpmExportMetadata(model, nodes);
    assert.equal(nodes.filter(node => npmNames(node).length).length, count);
    assert.deepEqual(npmNames(nodes.find(node => node.qualifiedName === 'UI:index.ts#E16999')!), ['E16999']);
    const local = file.getVariableDeclarationOrThrow('local');
    assert.equal(model.hasMemberWrites(local), true);
    const before = model.work.used;
    for (let index = 0; index < 100; index++) {
      assert.equal(model.exported(file, `E${index}`), file.getVariableDeclarationOrThrow(`E${index}`));
      assert.equal(model.hasMemberWrites(local), true);
    }
    assert.ok(model.work.used - before < 30_000, 'cached queries must not rescan the file or mutation graph');
    assert.ok(model.work.used < count * 1000, `work=${model.work.used}`);
    assert.ok(model.work.used < DEFAULT_EXTRACTION_WORK_LIMIT);
    return model.work.used;
  };
  const work = run();
  assert.equal(work, run(), 'same admitted source must have deterministic work accounting');
  context.diagnostic(`17000 exports: ${work}/${DEFAULT_EXTRACTION_WORK_LIMIT} charged work`);
});

test('star cycles retain independent candidates and cached member surfaces do not leak prefixes or export aliases', () => {
  const project = new Project({ useInMemoryFileSystem: true, compilerOptions: { noResolve: true, types: [] } });
  const a = project.createSourceFile('/repo/a.ts', "export * from './b'; export const A=()=>1;");
  const b = project.createSourceFile('/repo/b.ts', "export * from './a'; export const B=()=>2;");
  const entry = project.createSourceFile('/repo/index.ts', "export * from './a'; const value={Inner:()=>1}; export {value as First,value as Second}; export const Wrapped={Nested:value};");
  const imports: ResolvedImportDto[] = [[a, './b', 'b.ts'], [b, './a', 'a.ts'], [entry, './a', 'a.ts']].map(([file, specifier, target]) => ({
    fileQN: `UI:${(file as typeof a).getBaseName()}`, filePath: (file as typeof a).getBaseName(), importedNamespace: specifier as string,
    importKind: 'export', classification: 'local_file', resolvedFilePath: target as string,
  }));
  const model = new SemanticModel('UI', '/repo', [a,b,entry], imports, () => {});
  assert.deepEqual([...model.publicExportNames(a)].sort(), ['A', 'B']);
  assert.equal(model.exported(a, 'B'), b.getVariableDeclarationOrThrow('B'));
  assert.equal(model.exported(b, 'A'), a.getVariableDeclarationOrThrow('A'));
  const nodes = [dto('UI', 'npm:UI', { module_kind:'typescript_workspace_package',package_name:'@ui',package_entrypoints:[{import_path:'@ui',file_path:'index.ts'}] }),
    ...[...model.callables].map(([,qn]) => dto(qn,qn))];
  addNpmExportMetadata(model,nodes);
  const inner = nodes.find(node=>node.qualifiedName==='UI:index.ts#value.Inner')!;
  assert.deepEqual((inner.properties.npm_exports as Array<{export_name:string;member_path:string}>).map(item=>[item.export_name,item.member_path]),
    [['First','Inner'],['Second','Inner'],['Wrapped','Nested.Inner'],['value','Inner']]);
});

test('shared work limits and cancellation poison the run and fresh models start with a fresh allowance', () => {
  let cancel = false;
  const budget = new ExtractionWorkBudget({ maxWork: 3, shouldCancel: () => cancel });
  budget.charge(3);
  let refused: unknown;
  assert.throws(()=>budget.charge(), error=>{refused=error;return error instanceof ExtractionWorkLimitError && error.reason==='work_limit';});
  assert.throws(()=>budget.charge(0), error=>error===refused);
  const next = new ExtractionWorkBudget({shouldCancel:()=>cancel}); next.charge(1); cancel=true;
  assert.throws(()=>next.charge(0), error=>error instanceof ExtractionWorkLimitError && error.reason==='cancelled');
  cancel=false; assert.throws(()=>next.charge(0), ExtractionWorkLimitError);
  assert.doesNotThrow(()=>new ExtractionWorkBudget().charge(1));
});

test('whole project work refusal and synchronous cancellation return no partial graph or processed files', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(),'codegraph-export-work-'));
  try {
    fs.writeFileSync(path.join(root,'package.json'),JSON.stringify({name:'@ui',exports:'./index.ts'}));
    fs.writeFileSync(path.join(root,'index.ts'),'export const Button=()=>1;');
    const scope=['package.json','index.ts'];
    for (const control of [{maxWork:1},{shouldCancel:()=>true}]) {
      const result=extractProject('UI',root,path.join(root,'tsconfig.json'),()=>{},scope,undefined,control);
      assert.equal(result.complete,false); assert.deepEqual(result.nodes,[]); assert.deepEqual(result.edges,[]);
      assert.deepEqual(result.processedFiles,[]); assert.deepEqual(result.resolvedImports,[]);
      assert.ok(result.diagnostics?.some(message=>message.includes('semantic analysis work limit or cancellation')));
    }
    const healthy=extractProject('UI',root,path.join(root,'tsconfig.json'),()=>{},scope);
    assert.equal(healthy.complete,true); assert.deepEqual(npmNames(healthy.nodes.find(node=>node.name==='Button')!),['Button']);
  } finally {fs.rmSync(root,{recursive:true,force:true});}
});
