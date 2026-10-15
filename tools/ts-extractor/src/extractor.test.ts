import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { extractProject, relativizeImportPaths } from './extractor';

test('extractProject resolves monorepo tsconfig aliases, workspaces, barrels, dynamic imports, and diagnostics', () => {
  const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-fixture-'));
  try {
    writeJson(rootPath, 'package.json', {
      name: 'demo-root',
      workspaces: ['apps/*', 'libs/*'],
    });
    writeFile(rootPath, 'pnpm-workspace.yaml', [
      'packages:',
      "  - 'packages/*'",
      '',
    ].join('\n'));
    writeJson(rootPath, 'angular.json', {
      projects: {
        web: {
          root: 'apps/web',
          sourceRoot: 'apps/web/src',
          projectType: 'application',
        },
        shared: {
          root: 'libs/shared',
          sourceRoot: 'libs/shared/src',
          projectType: 'library',
        },
      },
    });
    writeJson(rootPath, 'tsconfig.base.json', {
      compilerOptions: {
        target: 'ES2020',
        module: 'commonjs',
        moduleResolution: 'node',
        baseUrl: '.',
        rootDirs: ['apps/web/src', 'libs/shared/src'],
        paths: {
          '@shared/*': ['libs/shared/src/*'],
          '@bad/*': ['missing/*'],
        },
      },
    });
    writeJson(rootPath, 'tsconfig.json', {
      extends: './tsconfig.base.json',
      references: [{ path: './libs/shared' }],
    });
    writeJson(rootPath, 'libs/shared/tsconfig.json', {
      extends: '../../tsconfig.base.json',
    });
    writeJson(rootPath, 'apps/web/package.json', {
      name: '@demo/web',
    });
    writeJson(rootPath, 'packages/util/package.json', {
      name: '@demo/util',
      exports: {
        '.': './src/index.ts',
      },
    });
    writeFile(rootPath, 'apps/web/src/app.ts', [
      "import { Shared } from '@shared/index';",
      "import { Util } from '@demo/util';",
      "import React from 'react';",
      "import { Missing } from '@bad/missing';",
      "export { Shared as ReExportedShared } from '@shared/index';",
      "const lazy = () => import('@shared/lazy').then(m => m.LazyModule);",
      'const dynamic = (name: string) => import(`@shared/${name}`);',
      'export class AppComponent {',
      '  use(shared: Shared, util: Util, missing: Missing) {',
      '    return [shared, util, missing, React, lazy, dynamic];',
      '  }',
      '}',
      '',
    ].join('\n'));
    writeFile(rootPath, 'libs/shared/src/index.ts', [
      "export * from './shared';",
      '',
    ].join('\n'));
    writeFile(rootPath, 'libs/shared/src/shared.ts', [
      'export class Shared {}',
      '',
    ].join('\n'));
    writeFile(rootPath, 'libs/shared/src/lazy.ts', [
      'export class LazyModule {}',
      '',
    ].join('\n'));
    writeFile(rootPath, 'packages/util/src/index.ts', [
      'export class Util {}',
      '',
    ].join('\n'));

    const result = extractProject('DemoProject', rootPath, path.join(rootPath, 'tsconfig.json'));

    assert.ok(result.nodes.some(node =>
      node.label === 'Module' &&
      node.name === '@demo/web' &&
      String(node.properties['workspace_kind']).includes('workspace_package')));
    assert.ok(result.workspacePackages.some(pkg => pkg.name === '@demo/util' && pkg.kind.includes('workspace_package')));
    assert.ok(result.workspacePackages.some(pkg => pkg.name === 'shared' && pkg.kind.includes('angular_library')));

    const sharedImport = result.resolvedImports.find(resolved =>
      resolved.importedNamespace === '@shared/index' &&
      resolved.importKind === 'static');
    assert.equal(sharedImport?.classification, 'local_workspace_package');
    assert.equal(sharedImport?.targetWorkspacePackage, 'shared');
    assert.equal(sharedImport?.isBarrel, true);
    assert.equal(sharedImport?.barrelTargetFilePath, 'libs/shared/src/shared.ts');

    assert.ok(result.edges.some(edge =>
      edge.type === 'IMPORTS' &&
      edge.sourceQN === 'DemoProject:apps/web/src/app.ts' &&
      edge.targetQN === 'DemoProject:libs/shared/src/shared.ts' &&
      edge.properties?.['via_barrel'] === true));

    const workspaceImport = result.resolvedImports.find(resolved => resolved.importedNamespace === '@demo/util');
    assert.equal(workspaceImport?.classification, 'local_workspace_package');
    assert.equal(workspaceImport?.targetWorkspacePackage, '@demo/util');
    assert.equal(workspaceImport?.targetFileQN, 'DemoProject:packages/util/src/index.ts');

    const dynamicImport = result.resolvedImports.find(resolved =>
      resolved.importedNamespace === '@shared/lazy' &&
      resolved.importKind === 'dynamic');
    assert.equal(dynamicImport?.classification, 'local_workspace_package');
    assert.equal(dynamicImport?.targetFileQN, 'DemoProject:libs/shared/src/lazy.ts');

    assert.ok(result.resolvedImports.some(resolved =>
      resolved.importedNamespace === '<dynamic expression>' &&
      resolved.classification === 'unsupported_dynamic_expression'));

    const externalImport = result.resolvedImports.find(resolved => resolved.importedNamespace === 'react');
    assert.equal(externalImport?.classification, 'external_npm_package');
    assert.equal(externalImport?.targetPackageQN, 'npm:react');
    assert.ok(result.nodes.some(node =>
      node.label === 'NuGetPackage' &&
      node.qualifiedName === 'npm:react' &&
      node.properties['package_type'] === 'npm'));
    assert.ok(result.edges.some(edge =>
      edge.type === 'REFERENCES_PACKAGE' &&
      edge.sourceQN === 'DemoProject:apps/web/src/app.ts' &&
      edge.targetQN === 'npm:react'));

    const unresolvedAlias = result.resolvedImports.find(resolved => resolved.importedNamespace === '@bad/missing');
    assert.equal(unresolvedAlias?.classification, 'unresolved_alias');
    assert.ok(result.unresolvedImports.some(unresolved =>
      unresolved.fileQN === 'DemoProject:apps/web/src/app.ts' &&
      unresolved.importedNamespace === '@bad/missing'));

    assert.ok(result.diagnostics?.some(diagnostic => diagnostic.startsWith('workspaceDiscovery:')));
    assert.ok(result.diagnostics?.some(diagnostic => diagnostic.startsWith('moduleResolution:')));
  } finally {
    fs.rmSync(rootPath, { recursive: true, force: true });
  }
});

test('extractProject analyzes only the files of the pipeline discovery scope', () => {
  const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-scope-'));
  try {
    const writeCopy = (prefix: string) => {
      writeJson(rootPath, `${prefix}package.json`, { name: '@demo/app' });
      // An include glob and project references that would reach every copy below the root.
      writeJson(rootPath, `${prefix}tsconfig.json`, {
        compilerOptions: { target: 'ES2020' },
        include: ['**/*.ts'],
        references: [{ path: './.claude/worktrees/agent-1' }, { path: './web/node_modules/vendored' }],
      });
      writeFile(rootPath, `${prefix}src/app.ts`, 'export class App { run() { return 1; } }\n');
    };
    writeCopy('');
    writeCopy('.claude/worktrees/agent-1/');
    writeFile(rootPath, '.claude/worktrees/agent-1/src/worktree-only.ts', 'export class WorktreeOnly { go() {} }\n');
    writeJson(rootPath, 'web/node_modules/vendored/package.json', { name: '@demo/app' });
    writeJson(rootPath, 'web/node_modules/vendored/tsconfig.json', { compilerOptions: {} });
    writeFile(rootPath, 'web/node_modules/vendored/index.ts', 'export class Vendored { go() {} }\n');
    writeJson(rootPath, 'angular.json', {
      projects: {
        app: { root: '', projectType: 'application' },
        copy: { root: '.claude/worktrees/agent-1', projectType: 'application' },
      },
    });

    // Without a scope the sidecar walks the disk and picks the copies up (the fixture is effective).
    const unscoped = extractProject('Demo', rootPath, path.join(rootPath, 'tsconfig.json'));
    assert.ok(unscoped.nodes.some(node => node.filePath.startsWith('.claude/')));

    const result = extractProject(
      'Demo',
      rootPath,
      path.join(rootPath, 'tsconfig.json'),
      () => {},
      ['package.json', 'tsconfig.json', 'src/app.ts', 'angular.json'],
    );

    assert.deepEqual(result.processedFiles, ['src/app.ts']);
    assert.deepEqual(
      result.nodes.filter(node => node.label !== 'Module' && node.label !== 'NuGetPackage')
        .map(node => `${node.label}:${node.qualifiedName}:${node.filePath}`)
        .sort(),
      ['Class:app.App:src/app.ts', 'Method:app.App.run:src/app.ts']);
    assert.deepEqual(
      result.workspacePackages.map(pkg => `${pkg.name}@${pkg.rootPath}`).sort(),
      ['@demo/app@']);
    // Only the admitted tsconfig.json is read: no worktree/vendored config shapes module resolution.
    assert.ok(result.diagnostics?.includes('workspaceDiscovery: 1 packages, 1 tsconfig files'),
      JSON.stringify(result.diagnostics));
    for (const node of result.nodes) {
      assert.ok(!node.filePath.includes('.claude/') && !node.filePath.includes('node_modules'), node.filePath);
    }
    for (const edge of result.edges) {
      assert.ok(!edge.sourceQN.includes('.claude/') && !edge.targetQN.includes('.claude/'), JSON.stringify(edge));
    }
  } finally {
    fs.rmSync(rootPath, { recursive: true, force: true });
  }
});

test('type text rendered into qualified names and properties never embeds the checkout path', () => {
  const first = writeTypeFixture();
  const second = writeTypeFixture();
  try {
    const firstResult = extractProject('Demo', first, path.join(first, 'tsconfig.json'));
    const secondResult = extractProject('Demo', second, path.join(second, 'tsconfig.json'));

    const injects = firstResult.edges.filter(edge => edge.type === 'INJECTS').map(edge => edge.targetQN).sort();
    assert.deepEqual(injects, [
      '(error: unknown) => import("src/errors").AppError',
      'import("src/services/foo").FooService',
    ]);

    const fingerprint = (result: ReturnType<typeof extractProject>) => JSON.stringify({
      nodes: result.nodes.map(node => [node.qualifiedName, node.properties]),
      edges: result.edges.map(edge => [edge.sourceQN, edge.targetQN, edge.type, edge.properties ?? {}]),
    });
    assert.equal(fingerprint(firstResult), fingerprint(secondResult));

    for (const [root, result] of [[first, firstResult], [second, secondResult]] as const) {
      const text = fingerprint(result);
      for (const absolute of [root, fs.realpathSync(root)]) {
        assert.ok(!text.includes(absolute), `${absolute} leaked into ${text}`);
      }
    }
  } finally {
    fs.rmSync(first, { recursive: true, force: true });
    fs.rmSync(second, { recursive: true, force: true });
  }
});

test('import() paths outside the checkout keep their directories relative to the root', () => {
  const roots = ['/work/repo'];
  assert.equal(
    relativizeImportPaths('import("/work/shared/src/index").Shared | import("/work/other/types").Other', roots),
    'import("../shared/src/index").Shared | import("../other/types").Other');
  // Two unrelated `index` modules outside the checkout stay distinct.
  assert.notEqual(
    relativizeImportPaths('import("/work/a/index").X', roots),
    relativizeImportPaths('import("/work/b/index").X', roots));
});

test('import() paths compare Windows drive letters case-insensitively', () => {
  assert.equal(
    relativizeImportPaths('import("C:/Work/Repo/src/a").A | import("c:\\\\Work\\\\Repo\\\\src\\\\b").B', ['c:\\Work\\Repo']),
    'import("src/a").A | import("src/b").B');
  assert.equal(
    relativizeImportPaths('import("c:/Work/Repo/src/a").A', ['C:/Work/Repo']),
    'import("src/a").A');
});

test('import() paths into a pnpm store resolve to the package path', () => {
  assert.equal(
    relativizeImportPaths(
      'import("/work/repo/node_modules/.pnpm/x@1.0.0/node_modules/x/dist/index").X | ' +
      'import("/work/repo/node_modules/.pnpm/@scope+y@2.0.0/node_modules/@scope/y/types").Y',
      ['/work/repo']),
    'import("x/dist/index").X | import("@scope/y/types").Y');
});

function writeTypeFixture(): string {
  const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-types-'));
  writeJson(rootPath, 'tsconfig.json', { compilerOptions: { target: 'ES2020' } });
  writeFile(rootPath, 'src/services/foo.ts', 'export class FooService { value = 1; }\n');
  writeFile(rootPath, 'src/errors.ts', 'export class AppError { message = ""; }\n');
  writeFile(rootPath, 'src/app/widget.ts', [
    "import { FooService } from '../services/foo';",
    "import { AppError } from '../errors';",
    'export class Widget {',
    '  constructor(private foo: FooService, private onError: (error: unknown) => AppError) {}',
    '  current(): FooService { return this.foo; }',
    '  fail() { return this.onError(null); }',
    '}',
    '',
  ].join('\n'));
  return rootPath;
}

function writeFile(rootPath: string, relativePath: string, content: string): void {
  const filePath = path.join(rootPath, relativePath);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, content);
}

function writeJson(rootPath: string, relativePath: string, value: unknown): void {
  writeFile(rootPath, relativePath, JSON.stringify(value, null, 2));
}
