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
function writeFile(rootPath, relativePath, content) {
    const filePath = path.join(rootPath, relativePath);
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, content);
}
function writeJson(rootPath, relativePath, value) {
    writeFile(rootPath, relativePath, JSON.stringify(value, null, 2));
}
