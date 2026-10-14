"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
const assert_1 = require("assert");
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const node_test_1 = require("node:test");
const extractor_1 = require("./extractor");
(0, node_test_1.test)('extractProject resolves monorepo tsconfig aliases, workspaces, barrels, dynamic imports, and diagnostics', () => {
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
        const result = (0, extractor_1.extractProject)('DemoProject', rootPath, path.join(rootPath, 'tsconfig.json'));
        assert_1.strict.ok(result.nodes.some(node => node.label === 'Module' &&
            node.name === '@demo/web' &&
            String(node.properties['workspace_kind']).includes('workspace_package')));
        assert_1.strict.ok(result.workspacePackages.some(pkg => pkg.name === '@demo/util' && pkg.kind.includes('workspace_package')));
        assert_1.strict.ok(result.workspacePackages.some(pkg => pkg.name === 'shared' && pkg.kind.includes('angular_library')));
        const sharedImport = result.resolvedImports.find(resolved => resolved.importedNamespace === '@shared/index' &&
            resolved.importKind === 'static');
        assert_1.strict.equal(sharedImport?.classification, 'local_workspace_package');
        assert_1.strict.equal(sharedImport?.targetWorkspacePackage, 'shared');
        assert_1.strict.equal(sharedImport?.isBarrel, true);
        assert_1.strict.equal(sharedImport?.barrelTargetFilePath, 'libs/shared/src/shared.ts');
        assert_1.strict.ok(result.edges.some(edge => edge.type === 'IMPORTS' &&
            edge.sourceQN === 'DemoProject:apps/web/src/app.ts' &&
            edge.targetQN === 'DemoProject:libs/shared/src/shared.ts' &&
            edge.properties?.['via_barrel'] === true));
        const workspaceImport = result.resolvedImports.find(resolved => resolved.importedNamespace === '@demo/util');
        assert_1.strict.equal(workspaceImport?.classification, 'local_workspace_package');
        assert_1.strict.equal(workspaceImport?.targetWorkspacePackage, '@demo/util');
        assert_1.strict.equal(workspaceImport?.targetFileQN, 'DemoProject:packages/util/src/index.ts');
        const dynamicImport = result.resolvedImports.find(resolved => resolved.importedNamespace === '@shared/lazy' &&
            resolved.importKind === 'dynamic');
        assert_1.strict.equal(dynamicImport?.classification, 'local_workspace_package');
        assert_1.strict.equal(dynamicImport?.targetFileQN, 'DemoProject:libs/shared/src/lazy.ts');
        assert_1.strict.ok(result.resolvedImports.some(resolved => resolved.importedNamespace === '<dynamic expression>' &&
            resolved.classification === 'unsupported_dynamic_expression'));
        const externalImport = result.resolvedImports.find(resolved => resolved.importedNamespace === 'react');
        assert_1.strict.equal(externalImport?.classification, 'external_npm_package');
        assert_1.strict.equal(externalImport?.targetPackageQN, 'npm:react');
        assert_1.strict.ok(result.nodes.some(node => node.label === 'NuGetPackage' &&
            node.qualifiedName === 'npm:react' &&
            node.properties['package_type'] === 'npm'));
        assert_1.strict.ok(result.edges.some(edge => edge.type === 'REFERENCES_PACKAGE' &&
            edge.sourceQN === 'DemoProject:apps/web/src/app.ts' &&
            edge.targetQN === 'npm:react'));
        const unresolvedAlias = result.resolvedImports.find(resolved => resolved.importedNamespace === '@bad/missing');
        assert_1.strict.equal(unresolvedAlias?.classification, 'unresolved_alias');
        assert_1.strict.ok(result.unresolvedImports.some(unresolved => unresolved.fileQN === 'DemoProject:apps/web/src/app.ts' &&
            unresolved.importedNamespace === '@bad/missing'));
        assert_1.strict.ok(result.diagnostics?.some(diagnostic => diagnostic.startsWith('workspaceDiscovery:')));
        assert_1.strict.ok(result.diagnostics?.some(diagnostic => diagnostic.startsWith('moduleResolution:')));
    }
    finally {
        fs.rmSync(rootPath, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('extractProject analyzes only the files of the pipeline discovery scope', () => {
    const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-scope-'));
    try {
        const writeCopy = (prefix) => {
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
        const unscoped = (0, extractor_1.extractProject)('Demo', rootPath, path.join(rootPath, 'tsconfig.json'));
        assert_1.strict.ok(unscoped.nodes.some(node => node.filePath.startsWith('.claude/')));
        const result = (0, extractor_1.extractProject)('Demo', rootPath, path.join(rootPath, 'tsconfig.json'), () => { }, ['package.json', 'tsconfig.json', 'src/app.ts', 'angular.json']);
        assert_1.strict.deepEqual(result.processedFiles, ['src/app.ts']);
        assert_1.strict.deepEqual(result.nodes.filter(node => node.label !== 'Module' && node.label !== 'NuGetPackage')
            .map(node => `${node.label}:${node.qualifiedName}:${node.filePath}`)
            .sort(), ['Class:app.App:src/app.ts', 'Method:app.App.run:src/app.ts']);
        assert_1.strict.deepEqual(result.workspacePackages.map(pkg => `${pkg.name}@${pkg.rootPath}`).sort(), ['@demo/app@']);
        // Only the admitted tsconfig.json is read: no worktree/vendored config shapes module resolution.
        assert_1.strict.ok(result.diagnostics?.includes('workspaceDiscovery: 1 packages, 1 tsconfig files'), JSON.stringify(result.diagnostics));
        for (const node of result.nodes) {
            assert_1.strict.ok(!node.filePath.includes('.claude/') && !node.filePath.includes('node_modules'), node.filePath);
        }
        for (const edge of result.edges) {
            assert_1.strict.ok(!edge.sourceQN.includes('.claude/') && !edge.targetQN.includes('.claude/'), JSON.stringify(edge));
        }
    }
    finally {
        fs.rmSync(rootPath, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('type text rendered into qualified names and properties never embeds the checkout path', () => {
    const first = writeTypeFixture();
    const second = writeTypeFixture();
    try {
        const firstResult = (0, extractor_1.extractProject)('Demo', first, path.join(first, 'tsconfig.json'));
        const secondResult = (0, extractor_1.extractProject)('Demo', second, path.join(second, 'tsconfig.json'));
        const injects = firstResult.edges.filter(edge => edge.type === 'INJECTS').map(edge => edge.targetQN).sort();
        assert_1.strict.deepEqual(injects, [
            '(error: unknown) => import("src/errors").AppError',
            'import("src/services/foo").FooService',
        ]);
        const fingerprint = (result) => JSON.stringify({
            nodes: result.nodes.map(node => [node.qualifiedName, node.properties]),
            edges: result.edges.map(edge => [edge.sourceQN, edge.targetQN, edge.type, edge.properties ?? {}]),
        });
        assert_1.strict.equal(fingerprint(firstResult), fingerprint(secondResult));
        for (const [root, result] of [[first, firstResult], [second, secondResult]]) {
            const text = fingerprint(result);
            for (const absolute of [root, fs.realpathSync(root)]) {
                assert_1.strict.ok(!text.includes(absolute), `${absolute} leaked into ${text}`);
            }
        }
    }
    finally {
        fs.rmSync(first, { recursive: true, force: true });
        fs.rmSync(second, { recursive: true, force: true });
    }
});
function writeTypeFixture() {
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
function writeFile(rootPath, relativePath, content) {
    const filePath = path.join(rootPath, relativePath);
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, content);
}
function writeJson(rootPath, relativePath, value) {
    writeFile(rootPath, relativePath, JSON.stringify(value, null, 2));
}
