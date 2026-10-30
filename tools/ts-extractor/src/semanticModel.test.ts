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
