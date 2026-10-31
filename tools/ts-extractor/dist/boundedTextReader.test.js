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
const boundedTextReader_1 = require("./boundedTextReader");
const extractor_1 = require("./extractor");
const app_1 = require("./app");
function fixture(files) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-source-budget-'));
    for (const [file, text] of Object.entries(files)) {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.writeFileSync(path.join(root, file), text);
    }
    return { root, file: (name) => path.join(root, name), dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
(0, node_test_1.test)('zero defaults and unrelated zero overrides allow positively admitted frontend extensions', () => {
    const f = fixture({ 'app.jsx': 'export function App(){return <main/>}' });
    try {
        const cases = [
            { maxFileSizeKb: 0, maxFileSizeKbByExtension: { '.jsx': 1 } },
            { maxFileSizeKb: 1, maxFileSizeKbByExtension: { '.sql': 0 } },
        ];
        for (const limits of cases) {
            const result = (0, extractor_1.extractProject)('ZeroLimits', f.root, '', () => { }, ['app.jsx'], limits);
            assert_1.strict.equal(result.complete, true, result.diagnostics?.join('\n'));
            assert_1.strict.deepEqual(result.processedFiles, ['app.jsx']);
            assert_1.strict.ok(result.nodes.some(node => node.name === 'App'));
        }
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('zero text limits accept empty text and refuse nonempty matching inputs', () => {
    const f = fixture({ 'empty.jsx': '', 'app.jsx': 'export const value=1;' });
    try {
        const empty = new boundedTextReader_1.BoundedTextReader(f.root, undefined, { maxFileSizeKb: 0, maxAnalysisTextKb: 0 });
        assert_1.strict.equal(empty.complete, true);
        assert_1.strict.equal(empty.read(f.file('empty.jsx'), true), '');
        assert_1.strict.equal(empty.complete, true);
        assert_1.strict.equal(empty.read(f.file('app.jsx'), true), undefined);
        assert_1.strict.equal(empty.complete, false);
        const result = (0, extractor_1.extractProject)('ZeroLimits', f.root, '', () => { }, ['app.jsx'], {
            maxFileSizeKb: 1, maxFileSizeKbByExtension: { '.jsx': 0 },
        });
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.deepEqual(result.processedFiles, []);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('file limits match decoded UTF16 units, including multibyte text and case-insensitive extension overrides', () => {
    const f = fixture({ 'bmp.txt': 'é'.repeat(1024), 'emoji.txt': '🌳'.repeat(512), 'too-long.txt': 'é'.repeat(1025), 'large.TSX': 'a'.repeat(1800) });
    try {
        const reader = new boundedTextReader_1.BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxFileSizeKbByExtension: { TsX: 2 } });
        assert_1.strict.equal(reader.read(f.file('bmp.txt'), true)?.length, 1024);
        assert_1.strict.equal(reader.read(f.file('emoji.txt'), true)?.length, 1024);
        assert_1.strict.equal(reader.read(f.file('large.TSX'), true)?.length, 1800);
        assert_1.strict.equal(reader.complete, true);
        assert_1.strict.equal(reader.read(f.file('too-long.txt'), true), undefined);
        assert_1.strict.equal(reader.complete, false);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('aggregate budget counts unique snapshots and rejected decoded prefixes, and refusals cannot reread bytes', () => {
    const f = fixture({ 'a.txt': 'a'.repeat(700), 'b.txt': 'b'.repeat(700), 'oversize.txt': 'x'.repeat(1100), 'one.txt': 'x' });
    const disk = require('fs');
    const originalRead = disk.readSync;
    let reads = 0;
    try {
        disk.readSync = ((...args) => { reads++; return originalRead(...args); });
        const reader = new boundedTextReader_1.BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
        assert_1.strict.equal(reader.read(f.file('a.txt'), true)?.length, 700);
        const afterFirst = reads;
        assert_1.strict.equal(reader.read(f.file('./a.txt'), true)?.length, 700);
        assert_1.strict.equal(reads, afterFirst);
        assert_1.strict.equal(reader.read(f.file('b.txt'), true), undefined);
        const afterRefusal = reads;
        assert_1.strict.equal(reader.read(f.file('b.txt'), true), undefined);
        assert_1.strict.equal(reads, afterRefusal);
        assert_1.strict.equal(reader.read(f.file('one.txt'), true), undefined);
        assert_1.strict.equal(reads, afterRefusal);
        const rejected = new boundedTextReader_1.BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
        assert_1.strict.equal(rejected.read(f.file('oversize.txt'), true), undefined);
        const rejectedReads = reads;
        assert_1.strict.equal(rejected.read(f.file('one.txt'), true), undefined);
        assert_1.strict.equal(reads, rejectedReads);
        assert_1.strict.equal(rejected.complete, false);
    }
    finally {
        disk.readSync = originalRead;
        f.dispose();
    }
});
(0, node_test_1.test)('definitely oversize metadata, directories, symlinks and replaced opened handles are refused before byte reads', () => {
    const f = fixture({ 'large.txt': 'x'.repeat(10000), 'safe.txt': 'safe' });
    const outside = fixture({ 'secret.txt': 'outside' });
    const disk = require('fs');
    const originalOpen = disk.openSync;
    const originalRead = disk.readSync;
    let reads = 0;
    try {
        fs.mkdirSync(f.file('directory.txt'));
        fs.symlinkSync(outside.file('secret.txt'), f.file('link.txt'));
        fs.symlinkSync(outside.root, f.file('escape'));
        disk.readSync = ((...args) => { reads++; return originalRead(...args); });
        for (const name of ['large.txt', 'directory.txt', 'link.txt', 'escape/secret.txt']) {
            const reader = new boundedTextReader_1.BoundedTextReader(f.root, undefined, { maxFileSizeKb: 1 });
            assert_1.strict.equal(reader.read(f.file(name), true), undefined, name);
            assert_1.strict.equal(reader.complete, false);
        }
        assert_1.strict.equal(reads, 0);
        // Simulate a path swap between lstat and open: only the actual opened handle may be trusted.
        disk.openSync = ((file, ...args) => originalOpen(file === f.file('safe.txt') ? outside.file('secret.txt') : file, ...args));
        const swapped = new boundedTextReader_1.BoundedTextReader(f.root);
        assert_1.strict.equal(swapped.read(f.file('safe.txt'), true), undefined);
        assert_1.strict.equal(swapped.complete, false);
        assert_1.strict.equal(reads, 0);
    }
    finally {
        disk.openSync = originalOpen;
        disk.readSync = originalRead;
        f.dispose();
        outside.dispose();
    }
});
(0, node_test_1.test)('cached optional absence becomes incomplete when the same input is later required', () => {
    const f = fixture({});
    try {
        const reader = new boundedTextReader_1.BoundedTextReader(f.root);
        assert_1.strict.equal(reader.read(f.file('missing.html')), undefined);
        assert_1.strict.equal(reader.complete, true);
        fs.writeFileSync(f.file('missing.html'), '<page/>');
        assert_1.strict.equal(reader.read(f.file('missing.html'), true), undefined);
        assert_1.strict.equal(reader.complete, false);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('source, extended tsconfig, manifests, Next config and referenced templates share one fail-closed reader', () => {
    const cases = [
        { files: { 'app.jsx': `export const App=()=> <main/>; /*${'x'.repeat(1500)}*/` } },
        { files: { 'app.ts': 'export class App{}', 'tsconfig.json': '{"extends":"./base.json"}', 'base.json': `{"compilerOptions":{},"padding":"${'x'.repeat(1500)}"}` } },
        { files: { 'app.ts': 'export class App{}', 'pnpm-workspace.yaml': `packages:\n - packages/*\n#${'x'.repeat(1500)}` } },
        { files: { 'app.ts': 'export class App{}', 'angular.json': `{"projects":{},"padding":"${'x'.repeat(1500)}"}` } },
        { files: { 'app.ts': 'export class App{}', 'package.json': `{"name":"large","padding":"${'x'.repeat(1500)}"}` } },
        { files: { 'app/page.tsx': 'export default()=> <main/>;', 'next.config.ts': `export default {basePath:'/ui'}; /*${'x'.repeat(1500)}*/` } },
        { files: { 'app.ts': `import {Component} from '@angular/core'; @Component({templateUrl:'./page.html'}) export class App{}`, 'page.html': 'x'.repeat(1500) } },
    ];
    for (const entry of cases) {
        const f = fixture(entry.files);
        try {
            const result = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'), () => { }, Object.keys(entry.files), { maxFileSizeKb: 1 });
            assert_1.strict.equal(result.complete, false, Object.keys(entry.files).join(','));
            assert_1.strict.deepEqual(result.processedFiles, []);
            assert_1.strict.ok(result.diagnostics?.some(message => message.startsWith('sourceAdmission:')), JSON.stringify(result.diagnostics));
        }
        finally {
            f.dispose();
        }
    }
});
(0, node_test_1.test)('source/config/template decoding shares aggregate budget and unreferenced HTML is never read', () => {
    const f = fixture({
        'app.ts': `import {Component} from '@angular/core'; @Component({templateUrl:'./page.html'}) export class App{} /*${'x'.repeat(500)}*/`,
        'page.html': 'x'.repeat(600), 'unreferenced.html': 'x'.repeat(10000), 'tsconfig.json': '{}',
    });
    try {
        const included = ['app.ts', 'page.html', 'unreferenced.html', 'tsconfig.json'];
        const full = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'), () => { }, included, { maxFileSizeKb: 1, maxAnalysisTextKb: 2 });
        assert_1.strict.equal(full.complete, true, full.diagnostics?.join('\n'));
        const refused = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'), () => { }, included, { maxFileSizeKb: 1, maxAnalysisTextKb: 1 });
        assert_1.strict.equal(refused.complete, false);
        assert_1.strict.deepEqual(refused.processedFiles, []);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('parsers and module/config resolution use the bounded text snapshot without alternate path reads', () => {
    const f = fixture({ 'app.ts': "import {dep} from './dep';export const value=dep();", 'dep.ts': 'export function dep(){return 1;}', 'tsconfig.json': '{}', 'package.json': '{"name":"snapshot"}' });
    const disk = require('fs');
    const original = disk.readFileSync;
    try {
        disk.readFileSync = ((file, ...args) => {
            if (typeof file === 'string' && path.resolve(file).startsWith(f.root + path.sep))
                throw new Error('unbounded parser read bypass');
            return original(file, ...args);
        });
        const result = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'), () => { }, ['app.ts', 'dep.ts', 'tsconfig.json', 'package.json']);
        assert_1.strict.equal(result.complete, true, result.diagnostics?.join('\n'));
        assert_1.strict.deepEqual(result.processedFiles.sort(), ['app.ts', 'dep.ts']);
        assert_1.strict.ok(result.edges.some(edge => edge.type === 'CALLS' && edge.targetQN.endsWith('#dep')));
    }
    finally {
        disk.readFileSync = original;
        f.dispose();
    }
});
(0, node_test_1.test)('scope metadata bounds reject count/character overflow and bounded scope-file reads reject unsafe inputs', () => {
    assert_1.strict.throws(() => (0, boundedTextReader_1.validateSourceScope)(Array(50001).fill('a.ts')), /50000/);
    assert_1.strict.throws(() => (0, boundedTextReader_1.validateSourceScope)(['x'.repeat(8 * 1024 * 1024 + 1)]), /8Mi/);
    const f = fixture({ 'scope.json': JSON.stringify(['app.ts']), 'app.ts': 'export class App{}' });
    try {
        fs.symlinkSync(f.file('scope.json'), f.file('scope-link.json'));
        assert_1.strict.throws(() => (0, app_1.readIncludedFiles)({ repositoryName: 'Budget', rootPath: f.root, tsconfigPath: f.file('tsconfig.json'), includedFilesPath: f.file('scope-link.json') }), /regular-file/);
        const result = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'), () => { }, Array(50001).fill('empty.ts'));
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.deepEqual(result.processedFiles, []);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('HTTP extraction transport applies request budgets and a following request gets fresh budget state', async () => {
    const f = fixture({ 'app.jsx': `export const App=()=> <main/>; /*${'x'.repeat(1500)}*/` });
    const app = (0, app_1.buildApp)(f.file('logs'));
    try {
        assert_1.strict.equal((await app.inject({ method: 'GET', url: '/health' })).json().sourceBoundsVersion, 5);
        const payload = { repositoryName: 'Budget', rootPath: f.root, tsconfigPath: f.file('tsconfig.json'), includedFiles: ['app.jsx'], maxFileSizeKb: 1 };
        const refused = await app.inject({ method: 'POST', url: '/extract-project', payload });
        assert_1.strict.equal(refused.statusCode, 200);
        assert_1.strict.equal(refused.json().complete, false);
        const accepted = await app.inject({ method: 'POST', url: '/extract-project', payload: { ...payload, maxFileSizeKbByExtension: { '.jsx': 2 } } });
        assert_1.strict.equal(accepted.statusCode, 200);
        assert_1.strict.equal(accepted.json().complete, true);
        assert_1.strict.deepEqual(accepted.json().processedFiles, ['app.jsx']);
    }
    finally {
        await app.close();
        f.dispose();
    }
});
(0, node_test_1.test)('ordinary directory probes preserve standalone package directory/index resolution', () => {
    const f = fixture({
        'package.json': '{"name":"root","workspaces":["packages/*"]}',
        'packages/ui/package.json': '{"name":"@fixture/ui","exports":{".":"./src"}}',
        'packages/ui/src/index.ts': 'export function Panel(){return 1;}',
        'app.ts': "import {Panel} from '@fixture/ui'; export const render=()=>Panel();",
    });
    try {
        const reader = new boundedTextReader_1.BoundedTextReader(f.root);
        assert_1.strict.equal(reader.exists(f.file('packages/ui/src')), false);
        assert_1.strict.equal(reader.complete, true);
        const result = (0, extractor_1.extractProject)('Budget', f.root, f.file('tsconfig.json'));
        assert_1.strict.equal(result.complete, true, result.diagnostics?.join('\n'));
        assert_1.strict.ok(result.resolvedImports.some(item => item.importedNamespace === '@fixture/ui' && item.resolvedFilePath === 'packages/ui/src/index.ts'));
    }
    finally {
        f.dispose();
    }
});
