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
(0, node_test_1.test)('a pnpm workspace under packages/ in the discovery scope yields its workspace package', () => {
    const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-pnpm-'));
    try {
        writeJson(rootPath, 'package.json', { name: 'mono', workspaces: ['packages/*'] });
        writeFile(rootPath, 'pnpm-workspace.yaml', "packages:\n  - 'packages/*'\n");
        writeJson(rootPath, 'tsconfig.json', { compilerOptions: { target: 'ES2020' } });
        writeJson(rootPath, 'packages/a/package.json', { name: '@mono/a' });
        writeFile(rootPath, 'packages/a/src/index.ts', 'export class Index {}\n');
        const result = (0, extractor_1.extractProject)('Mono', rootPath, path.join(rootPath, 'tsconfig.json'), () => { }, ['package.json', 'tsconfig.json', 'packages/a/package.json', 'packages/a/src/index.ts']);
        assert_1.strict.deepEqual(result.processedFiles, ['packages/a/src/index.ts']);
        assert_1.strict.ok(result.workspacePackages.some(pkg => pkg.name === '@mono/a' && pkg.rootPath === 'packages/a' && pkg.kind.includes('workspace_package')), JSON.stringify(result.workspacePackages));
        assert_1.strict.ok(result.nodes.some(node => node.label === 'Module' && node.name === '@mono/a'));
    }
    finally {
        fs.rmSync(rootPath, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('same-named workspace packages get distinct qualified names and imports bind the nearest one', () => {
    const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-samename-'));
    try {
        writeJson(rootPath, 'tsconfig.json', { compilerOptions: { target: 'ES2020' } });
        // The app and a vendored copy of it publish the same package name.
        for (const root of ['engines/catalog', 'vendor/PhotoCatalog']) {
            writeJson(rootPath, `${root}/desktop/package.json`, { name: 'photocatalog-desktop' });
            writeFile(rootPath, `${root}/desktop/src/main.ts`, "import React from 'react';\nexport const main = React;\n");
            writeFile(rootPath, `${root}/web/src/app.ts`, "import { main } from 'photocatalog-desktop';\nexport const app = main;\n");
        }
        writeJson(rootPath, 'tools/package.json', { name: 'catalog-tools' });
        const files = [
            'tsconfig.json', 'tools/package.json',
            ...['engines/catalog', 'vendor/PhotoCatalog'].flatMap(root => [`${root}/desktop/package.json`, `${root}/desktop/src/main.ts`, `${root}/web/src/app.ts`]),
        ];
        const result = (0, extractor_1.extractProject)('LensWorks', rootPath, path.join(rootPath, 'tsconfig.json'), () => { }, files);
        const app = 'typescript-package:LensWorks:photocatalog-desktop#engines/catalog/desktop';
        const vendored = 'typescript-package:LensWorks:photocatalog-desktop#vendor/PhotoCatalog/desktop';
        assert_1.strict.deepEqual(result.nodes.filter(node => node.label === 'Module').map(node => `${node.qualifiedName}@${node.filePath}`).sort(), [
            `${app}@engines/catalog/desktop`,
            // A unique name keeps its name-only qualified name.
            'typescript-package:LensWorks:catalog-tools@tools',
            `${vendored}@vendor/PhotoCatalog/desktop`,
        ].sort());
        assert_1.strict.deepEqual(result.edges.filter(edge => edge.type === 'REFERENCES_PACKAGE' && edge.sourceQN.startsWith('typescript-package:'))
            .map(edge => `${edge.sourceQN}->${edge.targetQN}`).sort(), [`${app}->npm:react`, `${vendored}->npm:react`]);
        assert_1.strict.deepEqual(result.edges.filter(edge => edge.type === 'IMPORTS' && edge.targetQN.startsWith('typescript-package:'))
            .map(edge => `${edge.sourceQN}->${edge.targetQN}`).sort(), [
            `LensWorks:engines/catalog/web/src/app.ts->${app}`,
            `LensWorks:vendor/PhotoCatalog/web/src/app.ts->${vendored}`,
        ]);
    }
    finally {
        fs.rmSync(rootPath, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('a scoped tsconfig whose include globs match admitted files reports no "No inputs" error', () => {
    const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-inputs-'));
    try {
        writeJson(rootPath, 'tsconfig.json', { compilerOptions: { target: 'ES2020' } });
        writeJson(rootPath, 'engines/catalog/desktop/tsconfig.json', { include: ['src/**/*.ts'] });
        writeFile(rootPath, 'engines/catalog/desktop/src/main.ts', 'export class Main {}\n');
        // A config whose include matches none of the admitted files still reports it.
        writeJson(rootPath, 'empty/tsconfig.json', { include: ['lib/**/*.ts'] });
        writeFile(rootPath, 'empty/src/other.ts', 'export class Other {}\n');
        const files = [
            'tsconfig.json', 'engines/catalog/desktop/tsconfig.json', 'engines/catalog/desktop/src/main.ts',
            'empty/tsconfig.json', 'empty/src/other.ts',
        ];
        const scoped = (0, extractor_1.extractProject)('Demo', rootPath, path.join(rootPath, 'tsconfig.json'), () => { }, files);
        const noInputs = (diagnostics) => (diagnostics ?? []).filter(diagnostic => diagnostic.includes('No inputs were found'));
        assert_1.strict.ok(scoped.processedFiles.includes('engines/catalog/desktop/src/main.ts'));
        assert_1.strict.deepEqual(noInputs(scoped.diagnostics).map(diagnostic => diagnostic.split(':')[0]), ['tsconfig empty/tsconfig.json'], JSON.stringify(scoped.diagnostics));
        // Unscoped, the sidecar never walks a config's file list, so the error would always be false.
        const unscoped = (0, extractor_1.extractProject)('Demo', rootPath, path.join(rootPath, 'tsconfig.json'));
        assert_1.strict.deepEqual(noInputs(unscoped.diagnostics), []);
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
        assert_1.strict.deepEqual(injects, ['services.foo.FooService']);
        // Function-type constructor arguments are not injectable application declarations.
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
(0, node_test_1.test)('import() paths outside the checkout keep their directories relative to the root', () => {
    const roots = ['/work/repo'];
    assert_1.strict.equal((0, extractor_1.relativizeImportPaths)('import("/work/shared/src/index").Shared | import("/work/other/types").Other', roots), 'import("../shared/src/index").Shared | import("../other/types").Other');
    // Two unrelated `index` modules outside the checkout stay distinct.
    assert_1.strict.notEqual((0, extractor_1.relativizeImportPaths)('import("/work/a/index").X', roots), (0, extractor_1.relativizeImportPaths)('import("/work/b/index").X', roots));
});
(0, node_test_1.test)('import() paths compare Windows drive letters case-insensitively', () => {
    assert_1.strict.equal((0, extractor_1.relativizeImportPaths)('import("C:/Work/Repo/src/a").A | import("c:\\\\Work\\\\Repo\\\\src\\\\b").B', ['c:\\Work\\Repo']), 'import("src/a").A | import("src/b").B');
    assert_1.strict.equal((0, extractor_1.relativizeImportPaths)('import("c:/Work/Repo/src/a").A', ['C:/Work/Repo']), 'import("src/a").A');
});
(0, node_test_1.test)('import() paths into a pnpm store resolve to the package path', () => {
    assert_1.strict.equal((0, extractor_1.relativizeImportPaths)('import("/work/repo/node_modules/.pnpm/x@1.0.0/node_modules/x/dist/index").X | ' +
        'import("/work/repo/node_modules/.pnpm/@scope+y@2.0.0/node_modules/@scope/y/types").Y', ['/work/repo']), 'import("x/dist/index").X | import("@scope/y/types").Y');
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
(0, node_test_1.test)('JS/JSX without config resolves callables, aliases, callbacks, namespace renders and wrappers', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-js-react-'));
    try {
        writeFile(root, 'src/helpers.js', 'export function send() {}\nexport const useData = () => send();');
        writeFile(root, 'src/other.js', 'export function send() {}');
        writeFile(root, 'src/ui/Leaf.jsx', 'export default function Leaf() { return <span/>; }');
        writeFile(root, 'src/ui/index.js', "export { default as Leaf } from './Leaf';");
        writeFile(root, 'src/App.jsx', `import React, { useEffect as effect, memo, forwardRef, lazy } from 'react';
import { send as api, useData } from './helpers';
import { send as other } from './other';
import * as UI from './ui';
import { Button as External } from '@shared/ui/button';
const Wrapped = memo(forwardRef((props, ref) => <UI.Leaf/>));
const Lazy = lazy(() => import('./ui/Leaf'));
export default function App() {
  const handle = () => api();
  effect(() => { useData(); other(); }, []);
  return <main><Wrapped/><Lazy/><External onClick={handle}/></main>;
}`);
        const included = ['src/helpers.js', 'src/other.js', 'src/ui/Leaf.jsx', 'src/ui/index.js', 'src/App.jsx'];
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'), () => { }, included);
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.deepEqual(result.processedFiles.sort(), included.sort());
        const edge = (source, target, type) => result.edges.some(edge => edge.sourceQN === source && edge.targetQN === target && edge.type === type);
        assert_1.strict.ok(edge('Demo:src/App.jsx#App.handle', 'Demo:src/helpers.js#send', 'CALLS'));
        assert_1.strict.ok(result.edges.some(edge => edge.sourceQN.startsWith('Demo:src/App.jsx#App.$callback') && edge.targetQN === 'Demo:src/other.js#send' && edge.type === 'CALLS'));
        assert_1.strict.ok(edge('Demo:src/helpers.js#useData', 'Demo:src/helpers.js#send', 'CALLS'));
        assert_1.strict.ok(edge('Demo:src/App.jsx#App', 'Demo:src/App.jsx#Wrapped', 'RENDERS'));
        assert_1.strict.ok(edge('Demo:src/App.jsx#App', 'Demo:src/ui/Leaf.jsx#Leaf', 'RENDERS'));
        assert_1.strict.ok(edge('Demo:src/App.jsx#Wrapped', 'Demo:src/ui/Leaf.jsx#Leaf', 'RENDERS'));
        assert_1.strict.ok(edge('Demo:src/App.jsx#App', 'npm-symbol:@shared/ui/button#Button', 'RENDERS'));
        assert_1.strict.ok(!result.nodes.some(node => node.name === 'main' || node.name === 'span'));
        assert_1.strict.equal(result.nodes.filter(node => node.qualifiedName === 'Demo:src/App.jsx#Wrapped').length, 1);
        assert_1.strict.ok(result.nodes.every(node => node.properties.extraction_owner === 'typescript'));
        assert_1.strict.ok(result.edges.every(edge => edge.properties?.extraction_owner === 'typescript'));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('TSX jsconfig aliases and workspace export retargeting resolve declarations, never excluded files', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-tsx-alias-'));
    try {
        writeJson(root, 'jsconfig.json', { compilerOptions: { baseUrl: '.', paths: { '@app/*': ['src/*'] } } });
        writeJson(root, 'package.json', { name: 'demo', workspaces: ['packages/*'] });
        writeJson(root, 'packages/shared/package.json', { name: '@shared/ui', exports: { '.': './index.tsx' } });
        writeFile(root, 'packages/shared/index.tsx', "export { First as Button } from './first';");
        writeFile(root, 'packages/shared/first.tsx', 'export function First() { return <button/>; }');
        writeFile(root, 'packages/shared/second.tsx', 'export function Second() { return <button/>; }');
        writeFile(root, 'src/service.ts', 'export function service() {}');
        writeFile(root, 'src/App.tsx', "import { Button } from '@shared/ui'; import { service as run } from '@app/service'; export const App = () => { run(); return <Button/>; };");
        writeFile(root, 'excluded/Secret.tsx', 'export const Secret = () => <private/>;');
        const included = ['jsconfig.json', 'package.json', 'packages/shared/package.json', 'packages/shared/index.tsx', 'packages/shared/first.tsx', 'packages/shared/second.tsx', 'src/service.ts', 'src/App.tsx'];
        const analyze = () => (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'), () => { }, included);
        const first = analyze();
        assert_1.strict.ok(first.edges.some(edge => edge.sourceQN === 'Demo:src/App.tsx#App' && edge.targetQN === 'Demo:packages/shared/first.tsx#First' && edge.type === 'RENDERS'));
        assert_1.strict.ok(first.edges.some(edge => edge.sourceQN === 'Demo:src/App.tsx#App' && edge.targetQN === 'Demo:src/service.ts#service' && edge.type === 'CALLS'));
        const stable = (result) => JSON.stringify({ nodes: result.nodes, edges: result.edges, processedFiles: result.processedFiles });
        assert_1.strict.equal(stable(first), stable(analyze()));
        writeFile(root, 'packages/shared/index.tsx', "export { Second as Button } from './second';");
        const second = analyze();
        assert_1.strict.ok(second.edges.some(edge => edge.sourceQN === 'Demo:src/App.tsx#App' && edge.targetQN === 'Demo:packages/shared/second.tsx#Second' && edge.type === 'RENDERS'));
        assert_1.strict.ok(!second.edges.some(edge => edge.sourceQN === 'Demo:src/App.tsx#App' && edge.targetQN === 'Demo:packages/shared/first.tsx#First' && edge.type === 'RENDERS'));
        assert_1.strict.ok(!second.nodes.some(node => node.filePath.includes('excluded')));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('React context follows render scope, nested overrides, custom hooks and provider alternatives', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-context-'));
    try {
        writeFile(root, 'app.tsx', `import { createContext, useContext } from 'react';
function fallback() {} function a() {} function b() {} function unused() {}
const Ctx = createContext({ save: fallback });
function useActions() { return useContext(Ctx); }
function Child() { const actions = useActions(); return <button onClick={actions.save}/>; }
function Outer() { useActions(); return <Ctx.Provider value={{save:a}}><Child/><Ctx.Provider value={{save:b}}><Child/></Ctx.Provider></Ctx.Provider>; }
function UnusedProvider() { return <Ctx.Provider value={{save:unused}}><div/></Ctx.Provider>; }
export function App() { return <Outer/>; }`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'), () => { }, ['app.tsx']);
        const providers = result.nodes.filter(node => node.properties.context_kind === 'provider');
        const providerFor = (target) => providers.find(provider => result.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === target && edge.type === 'CALLS'));
        assert_1.strict.ok(providerFor('Demo:app.tsx#a'));
        assert_1.strict.ok(providerFor('Demo:app.tsx#b'));
        assert_1.strict.ok(providerFor('Demo:app.tsx#unused'));
        const bindings = result.edges.filter(edge => edge.sourceQN === 'Demo:app.tsx#useActions' && edge.properties?.context_kind === 'binding');
        assert_1.strict.ok(bindings.some(edge => edge.targetQN === providerFor('Demo:app.tsx#a').qualifiedName));
        assert_1.strict.ok(bindings.some(edge => edge.targetQN === providerFor('Demo:app.tsx#b').qualifiedName));
        assert_1.strict.ok(bindings.some(edge => edge.targetQN === 'Demo:app.tsx#fallback'));
        assert_1.strict.ok(!bindings.some(edge => edge.targetQN === providerFor('Demo:app.tsx#unused').qualifiedName));
        assert_1.strict.ok(bindings.every(edge => edge.properties?.candidate === true));
        assert_1.strict.ok(result.diagnostics?.some(message => message.startsWith('contextAlternatives:')));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('Angular declarations resolve aliases for inheritance, interfaces, injection and route handlers', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-angular-qn-'));
    try {
        writeFile(root, 'src/base.ts', 'export class Base {} export interface Contract {} export class Service {}');
        writeFile(root, 'src/page.ts', `import { Base as Parent, Contract, Service as Svc } from './base';
import { Component, inject } from '@angular/core'; import { Router } from '@angular/router';
@Component({selector:'app-page'}) export class Page extends Parent implements Contract {
  service = inject(Svc); constructor(private second: Svc, private router: Router, callback: (x: string) => void) { super(); }
}`);
        writeFile(root, 'src/routes.ts', "import { Page as View } from './page'; const routes: Routes = [{path:'home',component:View}];");
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        for (const [type, target] of [['INHERITS', 'base.Base'], ['IMPLEMENTS', 'base.Contract'], ['INJECTS', 'base.Service']])
            assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'page.Page' && edge.targetQN === target && edge.type === type), `${type} -> ${target}`);
        assert_1.strict.ok(result.edges.some(edge => edge.type === 'HANDLES' && edge.targetQN === 'page.Page'));
        assert_1.strict.ok(!result.edges.some(edge => ['INHERITS', 'IMPLEMENTS', 'INJECTS', 'HANDLES'].includes(edge.type) && !['base.Base', 'base.Contract', 'base.Service', 'page.Page'].includes(edge.targetQN)));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('syntax and missing admitted sources report incomplete and never advance processed files', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-parse-'));
    try {
        writeFile(root, 'valid.jsx', 'export default () => <div/>;');
        writeFile(root, 'broken.tsx', 'export const Broken = () => <div>');
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'), () => { }, ['valid.jsx', 'broken.tsx', 'missing.js']);
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.deepEqual(result.processedFiles, ['valid.jsx']);
        assert_1.strict.ok(result.diagnostics?.some(message => message.startsWith('sourceParse: broken.tsx')));
        assert_1.strict.ok(result.diagnostics?.some(message => message.startsWith('sourceRead: missing.js')));
        assert_1.strict.ok(!result.nodes.some(node => node.filePath === 'broken.tsx'));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('new callable module identities preserve src and extension distinctions and lexical owners', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-identity-'));
    try {
        for (const file of ['src/lib/a.ts', 'lib/a.ts', 'lib/a.tsx'])
            writeFile(root, file, 'export function same() {}');
        writeFile(root, 'src/owners.ts', `export const one = { run() {} }; export const two = { run() {} };
export const named = function privateName() {}; export function outer() { if (flag) { const same = () => one.run(); same(); } else { const same = () => two.run(); same(); } }`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        const same = result.nodes.filter(node => node.name === 'same');
        assert_1.strict.equal(new Set(same.map(node => node.qualifiedName)).size, 5);
        for (const file of ['src/lib/a.ts', 'lib/a.ts', 'lib/a.tsx'])
            assert_1.strict.ok(same.some(node => node.qualifiedName === `Demo:${file}#same`));
        assert_1.strict.ok(result.nodes.some(node => node.qualifiedName === 'Demo:src/owners.ts#one.run'));
        assert_1.strict.ok(result.nodes.some(node => node.qualifiedName === 'Demo:src/owners.ts#two.run'));
        assert_1.strict.ok(result.nodes.some(node => node.qualifiedName === 'Demo:src/owners.ts#named'));
        assert_1.strict.equal(new Set(result.nodes.map(node => node.qualifiedName)).size, result.nodes.length);
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('React API imports and external JSX references cannot bind through shadowing', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-shadow-'));
    try {
        writeFile(root, 'app.tsx', `import React, { createContext, useContext, memo } from 'react'; import { Button } from '@shared/ui';
const Ctx = createContext(null);
export function App(useContext: Function, memo: Function, React: any, Button: any) {
  useContext(Ctx); React.useContext(Ctx); const Fake = memo(() => <span/>); return <Button/>;
}`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        assert_1.strict.ok(!result.edges.some(edge => edge.properties?.context_kind === 'consumer'));
        assert_1.strict.ok(!result.edges.some(edge => edge.type === 'RENDERS' && edge.targetQN.startsWith('npm-symbol:')));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('children slots carry provider scope across imported custom provider components', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-provider-children-'));
    try {
        writeFile(root, 'context.tsx', `import {createContext} from 'react'; export const Ctx=createContext(null); function save(){};
export function Provider({children}) { return <Ctx.Provider value={{save}}>{children}</Ctx.Provider>; }`);
        writeFile(root, 'app.tsx', `import {useContext} from 'react'; import {Ctx,Provider} from './context';
function Child(){const actions=useContext(Ctx);return <span/>;} export function App(){return <Provider><Child/></Provider>;}`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        const provider = result.nodes.find(node => node.properties.context_kind === 'provider');
        assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'Demo:app.tsx#Child' && edge.targetQN === provider.qualifiedName && edge.properties?.context_kind === 'binding'));
        assert_1.strict.ok(!result.diagnostics?.some(message => message.startsWith('contextScopeUnresolved:')));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('admitted declaration/spec/generated files are parsed and processed as empty semantic slices', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-empty-slices-'));
    try {
        const included = ['api.d.ts', 'component.spec.ts', 'client.generated.ts', 'app.jsx'];
        writeFile(root, included[0], 'export declare class Declared {}');
        writeFile(root, included[1], 'export function testOnly() {}');
        writeFile(root, included[2], 'export function generatedOnly() {}');
        writeFile(root, included[3], 'export const App = () => <div/>;');
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'), () => { }, included);
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.deepEqual(result.processedFiles.sort(), included.sort());
        assert_1.strict.ok(result.nodes.filter(node => node.filePath).every(node => node.filePath === 'app.jsx'));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('malformed or unsupported config resolution is incomplete while missing synthetic config is supported', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-config-failure-'));
    try {
        writeFile(root, 'app.jsx', 'export const App = () => <div/>;');
        writeFile(root, 'tsconfig.json', '{ "extends": "./not-admitted.json" }');
        const config = path.join(root, 'tsconfig.json');
        assert_1.strict.equal((0, extractor_1.extractProject)('Demo', root, config, () => { }, ['app.jsx']).complete, true);
        const failed = (0, extractor_1.extractProject)('Demo', root, config, () => { }, ['app.jsx', 'tsconfig.json']);
        assert_1.strict.equal(failed.complete, false);
        assert_1.strict.ok(failed.diagnostics?.some(message => message.includes('Cannot read file')));
        writeFile(root, 'package.json', '{ broken');
        assert_1.strict.equal((0, extractor_1.extractProject)('Demo', root, config, () => { }, ['app.jsx', 'package.json']).complete, false);
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('sidecar labels and edges use the owning C# graph protocol enums', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-protocol-enums-'));
    try {
        writeFile(root, 'app.tsx', `import {createContext,useContext} from 'react'; const Ctx=createContext(null);
function Child(){useContext(Ctx);return <div/>;} export function App(){const run=()=>{}; return <Ctx.Provider value={{run}}><Child/></Ctx.Provider>;}`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        const models = path.resolve(__dirname, '../../../src/CodeGraph.Models');
        const members = (name) => new Set(fs.readFileSync(path.join(models, `${name}.cs`), 'utf8').split('\n').map(line => line.replace(/\/\/.*$/, '').trim().replace(/,$/, '')));
        const labels = members('NodeLabel');
        const types = members('EdgeType');
        for (const node of result.nodes)
            assert_1.strict.ok(labels.has(node.label), `unsupported node label ${node.label}`);
        for (const edge of result.edges)
            assert_1.strict.ok(types.has(edge.type), `unsupported edge type ${edge.type}`);
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('discovery excludes source and config reads, including requested configs and relative import targets', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-no-excluded-read-'));
    const nodeFs = require('fs');
    const read = nodeFs.readFileSync;
    try {
        writeFile(root, 'src/App.jsx', "import { Secret } from '../excluded/Secret'; export const App = () => <Secret/>;");
        writeFile(root, 'excluded/Secret.jsx', 'export function Secret(){return <div/>;}');
        writeFile(root, 'excluded/tsconfig.json', '{"compilerOptions":{"baseUrl":"."}}');
        const reads = [];
        nodeFs.readFileSync = ((file, ...args) => {
            if (typeof file === 'string' && file.startsWith(root) && file.includes('/excluded/'))
                reads.push(file);
            return read(file, ...args);
        });
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'excluded/tsconfig.json'), () => { }, ['src/App.jsx']);
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.deepEqual(reads, []);
        assert_1.strict.deepEqual(result.processedFiles, ['src/App.jsx']);
        assert_1.strict.ok(!result.edges.some(edge => edge.type === 'RENDERS'));
        assert_1.strict.ok(!result.nodes.some(node => node.qualifiedName.startsWith('npm:../')));
    }
    finally {
        nodeFs.readFileSync = read;
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('memoized context values retain their supplied callable and unrelated providers do not rename owners', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-memo-context-'));
    try {
        writeFile(root, 'app.tsx', `import {createContext,useMemo,useContext} from 'react'; const Ctx=createContext(null); function save(){}
function Child(){useContext(Ctx);return <span/>;} export function App(){const value=useMemo(()=>({save}),[]);return <Ctx.Provider value={value}><Child/></Ctx.Provider>;}`);
        const config = path.join(root, 'tsconfig.json');
        const first = (0, extractor_1.extractProject)('Demo', root, config);
        const provider = first.nodes.find(node => node.properties.context_kind === 'provider');
        assert_1.strict.ok(first.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === 'Demo:app.tsx#save' && edge.type === 'CALLS'));
        writeFile(root, 'aaa.tsx', `import {createContext} from 'react';const Other=createContext(null);export function OtherApp(){return <Other.Provider value={null}><div/></Other.Provider>;}`);
        const second = (0, extractor_1.extractProject)('Demo', root, config);
        assert_1.strict.ok(second.nodes.some(node => node.qualifiedName === provider.qualifiedName));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('custom hooks and factories resolve uniquely returned callables and object members', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-returned-callable-'));
    try {
        writeFile(root, 'hooks.ts', `function request(){} export function useRun(){return () => request();}
export function useActions(){const reload=()=>request();return {reload};}`);
        writeFile(root, 'app.tsx', `import {useRun,useActions} from './hooks';export function App(){const run=useRun();const actions=useActions();run();actions.reload();return <button onClick={run}/>;}`);
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        const returned = result.nodes.find(node => node.qualifiedName.startsWith('Demo:hooks.ts#useRun.$callback'));
        assert_1.strict.ok(returned);
        assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'Demo:app.tsx#App' && edge.targetQN === returned.qualifiedName && edge.type === 'CALLS'));
        assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'Demo:app.tsx#App' && edge.targetQN === 'Demo:hooks.ts#useActions.reload' && edge.type === 'CALLS'));
        assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === returned.qualifiedName && edge.targetQN === 'Demo:hooks.ts#request' && edge.type === 'CALLS'));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('imported class instance members resolve through jsconfig aliases without installed libraries', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-class-instance-'));
    try {
        writeJson(root, 'jsconfig.json', { compilerOptions: { baseUrl: '.', paths: { '@app/*': ['src/*'] } } });
        writeFile(root, 'src/service.ts', 'export class Api { request(){} }');
        writeFile(root, 'src/app.ts', "import {Api as Client} from '@app/service';export function run(){const api=new Client();api.request();} export function typed(api:Client){api.request();}");
        const result = (0, extractor_1.extractProject)('Demo', root, path.join(root, 'tsconfig.json'));
        for (const caller of ['Demo:src/app.ts#run', 'Demo:src/app.ts#typed'])
            assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === caller && edge.targetQN === 'service.Api.request' && edge.type === 'CALLS'), caller);
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
