"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const node_test_1 = __importDefault(require("node:test"));
const strict_1 = __importDefault(require("node:assert/strict"));
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
(0, node_test_1.default)('module lookup indexes source plus specifier and caches admitted hits and misses per analysis', () => {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true });
    const first = project.createSourceFile('/repo/a.ts', 'export const a = 1;');
    const second = project.createSourceFile('/repo/b.ts', 'export const b = 1;');
    const target = project.createSourceFile('/repo/target.ts', 'export const target = 1;');
    const imports = [
        { fileQN: 'Repo:a.ts', filePath: 'a.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'target.ts' },
        { fileQN: 'Repo:b.ts', filePath: 'b.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'unadmitted.ts' },
    ];
    const model = new semanticModel_1.SemanticModel('Repo', '/repo', [first, second, target], imports, () => { });
    imports.find = () => { throw new Error('module lookup must not scan the import list'); };
    for (let index = 0; index < 10; index++) {
        strict_1.default.equal(model.module(first, './shared'), target);
        strict_1.default.equal(model.module(second, './shared'), undefined);
        strict_1.default.equal(model.module(first, './missing'), undefined);
    }
    const next = new semanticModel_1.SemanticModel('Repo', '/repo', [first, second, target], [
        { ...imports[0], resolvedFilePath: 'b.ts' },
    ], () => { });
    strict_1.default.equal(next.module(first, './shared'), second);
});
(0, node_test_1.default)('independent resolution queries cache per immutable model while explicit histories and cancellation remain authoritative', () => {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, compilerOptions: { noResolve: true, types: [] } });
    const file = project.createSourceFile('/repo/index.ts', `
    function Page(){return 1;} function factory(){return {View:Page};}
    const api=factory(); const cycleA=cycleB; const cycleB=cycleA; const dynamic=runtimeValue();
  `);
    let cancelled = false;
    const model = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], () => { }, { shouldCancel: () => cancelled });
    const api = file.getVariableDeclarationOrThrow('api');
    const expected = model.resolve(api);
    strict_1.default.ok(expected);
    const workBefore = model.work.used;
    for (let index = 0; index < 100; index++)
        strict_1.default.equal(model.resolve(api), expected);
    strict_1.default.equal(model.work.used - workBefore, 100, 'cached root queries must still poll/charge without following the factory again');
    // A history-dependent cutoff must neither replace nor consult the independent cache.
    strict_1.default.equal(model.resolve(api, new Set(Array.from({ length: 101 }, (_, index) => `history${index}`))), undefined);
    strict_1.default.equal(model.resolve(api), expected);
    const cycle = file.getVariableDeclarationOrThrow('cycleA');
    const firstCycle = model.resolve(cycle, new Set());
    strict_1.default.equal(model.resolve(cycle), firstCycle);
    const unknown = file.getVariableDeclarationOrThrow('dynamic').getInitializerOrThrow();
    strict_1.default.equal(model.resolve(unknown), undefined);
    const unknownBefore = model.work.used;
    strict_1.default.equal(model.resolve(unknown), undefined);
    strict_1.default.equal(model.work.used - unknownBefore, 1);
    const fresh = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], () => { });
    const freshBefore = fresh.work.used;
    strict_1.default.equal(fresh.resolve(api), expected);
    strict_1.default.ok(fresh.work.used - freshBefore > 1, 'a new model must not reuse a previous run cache');
    cancelled = true;
    strict_1.default.throws(() => model.resolve(api), error => error.code === 'extraction_work_incomplete');
    cancelled = false;
    strict_1.default.throws(() => model.resolve(api), error => error.code === 'extraction_work_incomplete');
});
(0, node_test_1.default)('React API imports materialize once per model and many-call lookup preserves aliases and lexical shadows', context => {
    const measure = (count) => {
        const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
        const file = project.createSourceFile('/repo/react.tsx', `import {createContext as make,useContext as read} from 'react'; import * as ReactNS from 'react'; import ReactDefault from 'react';\n`
            + Array.from({ length: count }, (_, index) => `import {f as f${index}} from '@other/p${index}';`).join('\n')
            + `\nconst Ctx=make(null); function Real(){read(Ctx);ReactNS.useContext(Ctx);ReactDefault.useContext(Ctx);}\n`
            + `function Shadow(read:any,ReactNS:any,ReactDefault:any){read(Ctx);ReactNS.useContext(Ctx);ReactDefault.useContext(Ctx);}\n`
            + Array.from({ length: count }, (_, index) => `f${index}();`).join('\n'));
        let cancelled = false;
        const model = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], () => { }, { shouldCancel: () => cancelled });
        const readImports = file.getImportDeclarations.bind(file);
        let materializations = 0;
        file.getImportDeclarations = () => { materializations++; return readImports(); };
        const calls = file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression);
        const results = calls.map(call => model.reactApi(call));
        strict_1.default.deepEqual(results.slice(0, 7), ['createContext', 'useContext', 'useContext', 'useContext', undefined, undefined, undefined]);
        strict_1.default.ok(results.slice(7).every(value => value === undefined), 'unrelated imports must not become React APIs');
        strict_1.default.equal(materializations, 1, 'every API query in this admitted file shares one import index');
        const before = model.work.used;
        for (const call of calls)
            model.reactApi(call);
        const repeated = model.work.used - before;
        strict_1.default.ok(repeated <= calls.length * 4, 'cached API lookups must not rescan imports or compare import-local names');
        strict_1.default.equal(materializations, 1);
        const fresh = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], () => { });
        strict_1.default.equal(fresh.reactApi(calls[0]), 'createContext');
        strict_1.default.equal(materializations, 2, 'new analyses rebuild their own immutable import index');
        cancelled = true;
        strict_1.default.throws(() => model.reactApi(calls[0]), error => error.code === 'extraction_work_incomplete');
        cancelled = false;
        strict_1.default.throws(() => model.reactApi(calls[0]), error => error.code === 'extraction_work_incomplete');
        return model.work.used;
    };
    const smaller = measure(200), larger = measure(2000);
    strict_1.default.ok(larger < smaller * 12, `10x imports/calls should stay proportional: ${smaller} -> ${larger}`);
    context.diagnostic(`React symbol API index: 200 imports/calls ${smaller}; 2000 ${larger}`);
});
