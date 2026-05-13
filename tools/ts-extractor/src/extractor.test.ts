import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { extractProject } from './extractor';

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

function writeFile(rootPath: string, relativePath: string, content: string): void {
  const filePath = path.join(rootPath, relativePath);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, content);
}

function writeJson(rootPath: string, relativePath: string, value: unknown): void {
  writeFile(rootPath, relativePath, JSON.stringify(value, null, 2));
}
