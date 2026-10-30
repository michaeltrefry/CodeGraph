import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Node, Project } from 'ts-morph';
import { extractProject } from './extractor';
import { SemanticModel } from './semanticModel';
import { DEFAULT_EXTRACTION_WORK_LIMIT } from './extractionWork';
import type { GraphNodeDto } from './protocol';

const npmNames = (node: GraphNodeDto): string[] => ((node.properties.npm_exports ?? []) as Array<{ export_name: string }>).map(item => item.export_name);
const corpus = (count: number) => Array.from({ length: count }, (_, index) => `export const E${index}=()=>0;`).join('\n')
  + '\nconst local={key:0}; const alias=local; alias.key=1;';

function extractCorpus(count: number) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-export-whole-work-'));
  const source = corpus(count);
  try {
    fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: '@fixture/ui', exports: './index.ts' }));
    fs.writeFileSync(path.join(root, 'index.ts'), source);
    let work = 0;
    const result = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, ['package.json', 'index.ts'], undefined,
      { onWork: value => { work = value; } });
    assert.equal(result.complete, true, result.diagnostics?.join('\n'));
    assert.deepEqual(result.processedFiles, ['index.ts']);
    assert.equal(result.nodes.filter(node => npmNames(node).length).length, count);
    assert.deepEqual(npmNames(result.nodes.find(node => node.qualifiedName === `UI:index.ts#E${count - 1}`)!), [`E${count - 1}`]);
    assert.ok(work > count && work < DEFAULT_EXTRACTION_WORK_LIMIT, `work=${work}, exports=${count}`);
    return work;
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test('whole extraction completes the 17k short exported-const corpus under default source and semantic work limits', context => {
  const source = corpus(17000);
  // This is the reported ~448 KB adversarial family, admitted by the ordinary 512 KiB file cap.
  assert.ok(Buffer.byteLength(source) >= 447000 && Buffer.byteLength(source) < 449000);
  assert.ok(source.length < 512 * 1024);
  const smaller = extractCorpus(4250);
  const larger = extractCorpus(17000);
  assert.equal(extractCorpus(17000), larger, 'fresh equivalent whole-project runs consume deterministic work');
  assert.ok(larger <= smaller * 6, `4x declarations must not cause quadratic export/member scans: ${smaller} -> ${larger}`);
  context.diagnostic(`4250 exports: ${smaller} work; 17000 exports: ${larger}; default allowance: ${DEFAULT_EXTRACTION_WORK_LIMIT}; remaining: ${DEFAULT_EXTRACTION_WORK_LIMIT - larger}`);
});

test('factory return AST caching preserves lexical symbols and does not rescan large bodies on repeated export/member queries', context => {
  const measureCachedQueries = (locals: number) => {
    const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
      compilerOptions: { noResolve: true, types: [] } });
    const filler = Array.from({ length: locals }, (_, index) => `const filler${index}=0;`).join('\n');
    const file = project.createSourceFile('/repo/index.ts', `
      function Page(){return 1;} function Other(){return 2;}
      function makeApi(){${filler}
        function unrelated(){return Other;}
        return {View:Page};
      }
      const rootApi=makeApi(); export {rootApi as RootApi};
      function consumer(){
        const Page=()=>3;
        const makeLocal=()=>({View:Page});
        return makeLocal();
      }
      const localApi=consumer(); export {localApi as LocalApi};
      const changed={View:Page}; const changedAlias=changed; changedAlias.View=Other;
    `);
    const diagnostics: string[] = [];
    const model = new SemanticModel('UI', '/repo', [file], [], message => diagnostics.push(message));
    const rootBinding = file.getVariableDeclarationOrThrow('rootApi');
    const changed = file.getVariableDeclarationOrThrow('changed');
    const viewTarget = (exportName: string) => {
      const resolved = model.resolve(model.exported(file, exportName));
      assert.ok(resolved && Node.isObjectLiteralExpression(resolved), `${exportName} must retain its unique returned object`);
      const property = resolved.getPropertyOrThrow('View');
      assert.ok(Node.isPropertyAssignment(property));
      return model.target(property.getInitializer());
    };
    assert.equal(viewTarget('RootApi'), 'UI:index.ts#Page', 'nested unrelated callable returns cannot contaminate the factory');
    assert.equal(viewTarget('LocalApi'), 'UI:index.ts#consumer.Page', 'same-spelling declarations must retain their lexical binding');
    assert.equal(model.hasMemberWrites(changed), true);
    assert.equal(model.hasMemberWrites(rootBinding), false, 'an unrelated object write cannot poison the factory result');
    const before = model.work.used;
    for (let index = 0; index < 150; index++) {
      assert.equal(viewTarget('RootApi'), 'UI:index.ts#Page');
      assert.equal(viewTarget('LocalApi'), 'UI:index.ts#consumer.Page');
      assert.equal(model.hasMemberWrites(rootBinding), false);
      assert.equal(model.hasMemberWrites(changed), true);
    }
    const delta = model.work.used - before;
    assert.ok(!diagnostics.some(message => message.startsWith('functionReturnAmbiguity:')), diagnostics.join('\n'));
    return delta;
  };
  const small = measureCachedQueries(0);
  const large = measureCachedQueries(2000);
  assert.equal(large, small, 'after warmup, repeated queries must not depend on factory body or mutation-index size');
  context.diagnostic(`150 cached lookup rounds: empty body ${small} work; 2000 locals ${large}`);
});
