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
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const node_test_1 = __importDefault(require("node:test"));
const strict_1 = __importDefault(require("node:assert/strict"));
const fs = __importStar(require("node:fs"));
const os = __importStar(require("node:os"));
const path = __importStar(require("node:path"));
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const httpAnalysis_1 = require("./httpAnalysis");
const extractor_1 = require("./extractor");
/** Fixtures supply the same admitted module records as workspace resolution, without dependencies. */
function modelFor(sources, locals = {}, control = {}) {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
        compilerOptions: { noResolve: true, types: [], experimentalDecorators: true, target: ts_morph_1.ts.ScriptTarget.ES2020, module: ts_morph_1.ts.ModuleKind.ESNext } });
    const files = Object.entries(sources).map(([name, text]) => project.createSourceFile(`/repo/${name}`, text));
    const imports = [];
    for (const file of files) {
        const relative = file.getFilePath().slice(6);
        const add = (specifier, kind) => {
            const target = locals[`${relative}:${specifier}`];
            const external = specifier.startsWith('@') ? specifier.split('/').slice(0, 2).join('/') : specifier.split('/')[0];
            imports.push({ fileQN: `Lookup:${relative}`, filePath: relative, importedNamespace: specifier, importKind: kind,
                classification: target ? 'local_file' : specifier.startsWith('.') ? 'unresolved_alias' : 'external_npm_package',
                ...(target ? { resolvedFilePath: target, targetFileQN: `Lookup:${target}` } : { externalPackageName: external }) });
        };
        for (const imported of file.getImportDeclarations())
            add(imported.getModuleSpecifierValue(), 'static');
        for (const exported of file.getExportDeclarations()) {
            const specifier = exported.getModuleSpecifierValue();
            if (specifier)
                add(specifier, 'export');
        }
    }
    const diagnostics = [];
    const model = new semanticModel_1.SemanticModel('Lookup', '/repo', files, imports, message => diagnostics.push(message), control);
    const http = () => {
        const edges = [];
        const before = model.work.used;
        const coverage = (0, httpAnalysis_1.analyzeHttp)(model, [], edges, message => diagnostics.push(message));
        return { edges, coverage, work: model.work.used - before };
    };
    return { model, files, diagnostics, http };
}
const targets = (edges, owner) => edges.filter(edge => edge.sourceQN === owner && edge.type === 'HTTP_CALLS').map(edge => edge.targetQN).sort();
function restore(prototype, key, descriptor) {
    if (descriptor)
        Object.defineProperty(prototype, key, descriptor);
    else
        Reflect.deleteProperty(prototype, key);
}
(0, node_test_1.default)('heritage reconciliation indexes each class list once instead of materializing it per IMPLEMENTS edge', context => {
    const measure = (count) => {
        const contracts = Array.from({ length: count }, (_, index) => `export interface I${index}{}`).join('\n');
        const names = Array.from({ length: count }, (_, index) => `C.I${index}`);
        const f = modelFor({ 'contracts.ts': contracts, 'impl.ts': `import * as C from './contracts'; export class Impl implements ${names.join(',')} {}` }, { 'impl.ts:./contracts': 'contracts.ts' });
        const original = ts_morph_1.ClassDeclaration.prototype.getImplements;
        const descriptor = Object.getOwnPropertyDescriptor(ts_morph_1.ClassDeclaration.prototype, 'getImplements');
        let calls = 0, candidates = 0;
        Object.defineProperty(ts_morph_1.ClassDeclaration.prototype, 'getImplements', { configurable: true, writable: true, value: function () {
                const result = original.call(this);
                if (this.getSourceFile().getFilePath() === '/repo/impl.ts') {
                    calls++;
                    candidates += result.length;
                }
                return result;
            } });
        const before = f.model.work.used;
        try {
            for (let run = 0; run < 2; run++) {
                const edges = names.map(targetQN => ({ sourceQN: 'impl.Impl', targetQN, type: 'IMPLEMENTS' }));
                f.model.reconcileAngular([], edges);
                strict_1.default.equal(edges.length, count);
                strict_1.default.deepEqual(edges.map(edge => edge.targetQN).sort(), Array.from({ length: count }, (_, index) => `contracts.I${index}`).sort());
            }
            strict_1.default.equal(calls, 1, 'fresh raw edge sets reuse the immutable class index');
            strict_1.default.equal(candidates, count);
            return { work: f.model.work.used - before, calls, candidates };
        }
        finally {
            restore(ts_morph_1.ClassDeclaration.prototype, 'getImplements', descriptor);
        }
    };
    const small = measure(200), large = measure(600);
    strict_1.default.ok(large.work <= small.work * 4, `3x entries must not cause quadratic reconciliation: ${small.work} -> ${large.work}`);
    context.diagnostic(`200 entries: ${small.calls} materialization/${small.candidates} candidates/${small.work} work; 600: ${large.calls}/${large.candidates}/${large.work}`);
});
(0, node_test_1.default)('heritage raw text keeps qualified aliases distinct, preserves the first duplicate expression and drops unknown targets', () => {
    const f = modelFor({
        'contracts.ts': 'export interface Contract<T>{}', 'one.ts': 'export interface Shared{}', 'two.ts': 'export interface Shared{}',
        'impl.ts': `import {Contract as Alias} from './contracts'; import * as One from './one'; import * as Two from './two';
      export class Impl implements Alias<string>, Alias<number>, Missing {}
      export class Qualified implements One.Shared, Two.Shared, One.Missing {}`,
    }, { 'impl.ts:./contracts': 'contracts.ts', 'impl.ts:./one': 'one.ts', 'impl.ts:./two': 'two.ts' });
    const file = f.files.find(item => item.getBaseName() === 'impl.ts');
    const first = file.getClassOrThrow('Impl').getImplements()[0].getExpression().getPos();
    const positions = [];
    const target = f.model.target;
    f.model.target = function (node) {
        if (node?.getText() === 'Alias')
            positions.push(node.getPos());
        return target.call(this, node);
    };
    const edges = [
        { sourceQN: 'impl.Impl', targetQN: 'Alias', type: 'IMPLEMENTS' }, { sourceQN: 'impl.Impl', targetQN: 'Alias', type: 'IMPLEMENTS' },
        { sourceQN: 'impl.Impl', targetQN: 'Missing', type: 'IMPLEMENTS' },
        { sourceQN: 'impl.Qualified', targetQN: 'One.Shared', type: 'IMPLEMENTS' }, { sourceQN: 'impl.Qualified', targetQN: 'Two.Shared', type: 'IMPLEMENTS' },
        { sourceQN: 'impl.Qualified', targetQN: 'One.Missing', type: 'IMPLEMENTS' },
    ];
    f.model.reconcileAngular([], edges);
    strict_1.default.deepEqual(positions, [first, first], 'duplicate raw text must use the first source expression');
    strict_1.default.deepEqual(edges.filter(edge => edge.sourceQN === 'impl.Impl').map(edge => edge.targetQN), ['contracts.Contract', 'contracts.Contract']);
    strict_1.default.deepEqual(edges.filter(edge => edge.sourceQN === 'impl.Qualified').map(edge => edge.targetQN).sort(), ['one.Shared', 'two.Shared']);
    strict_1.default.ok(!edges.some(edge => edge.targetQN.includes('Missing')));
});
(0, node_test_1.default)('HTTP-shaped unrelated calls build a bounded per-file import index instead of scanning imports per call', context => {
    const measure = (count) => {
        const imports = Array.from({ length: count }, (_, index) => { const id = String(index).padStart(4, '0'); return `import {Marker as M${id}} from 'pkg${id}';`; }).join('\n');
        const calls = Array.from({ length: count }, (_, index) => `client.get('/not-http/${index}');`).join('\n');
        const f = modelFor({ 'noise.ts': `${imports}\nexport function noise(client:M0000){${calls}}` });
        const original = ts_morph_1.SourceFile.prototype.getImportDeclarations;
        const descriptor = Object.getOwnPropertyDescriptor(ts_morph_1.SourceFile.prototype, 'getImportDeclarations');
        let materializations = 0, candidates = 0;
        Object.defineProperty(ts_morph_1.SourceFile.prototype, 'getImportDeclarations', { configurable: true, writable: true, value: function () {
                const result = original.call(this);
                if (this.getFilePath() === '/repo/noise.ts') {
                    materializations++;
                    candidates += result.length;
                }
                return result;
            } });
        try {
            const result = f.http();
            strict_1.default.equal(result.coverage.complete, true, f.diagnostics.join('\n'));
            strict_1.default.equal(result.edges.filter(edge => edge.type === 'HTTP_CALLS').length, 0);
            strict_1.default.ok(materializations <= 3, `one construction per immutable index, never one per get(): ${materializations}`);
            strict_1.default.ok(candidates <= count * 3);
            return { work: result.work, materializations, candidates };
        }
        finally {
            restore(ts_morph_1.SourceFile.prototype, 'getImportDeclarations', descriptor);
        }
    };
    const small = measure(200), large = measure(600);
    strict_1.default.ok(large.work <= small.work * 4, `3x imports/calls cannot require quadratic recognition work: ${small.work} -> ${large.work}`);
    context.diagnostic(`200 imports/calls: ${small.materializations} materializations/${small.candidates} candidates/${small.work} work; 600: ${large.materializations}/${large.candidates}/${large.work}`);
});
(0, node_test_1.default)('HTTP import provenance preserves aliases, namespace/barrel/member receivers and caller-specific URLs while rejecting shadows', () => {
    const f = modelFor({
        'tokens.ts': "export {HttpClient as Client} from '@angular/common/http'; export {inject as acquire} from '@angular/core';",
        'client.ts': `import {Client,acquire} from './tokens'; import * as AngularHttp from '@angular/common/http';
      const client=new Client(); const namespaceClient=new AngularHttp.HttpClient();
      export function wrap(url:string){return client.get(url);}
      export function Page(){wrap('/api/first');wrap('/api/second');}
      export function namespaceRead(){return namespaceClient.get('/api/namespace');}
      export class Service { injected=acquire(Client); constructor(private http:Client){}
        member(){return this.http.post('/api/member',{});} injectedRead(){return this.injected.get('/api/injected');}}
      export function shadow(Client:any){const fake=new Client();return fake.get('/not-http/shadow');}
      const unrelated={get(url:string){return url;}};export function otherGet(){return unrelated.get('/not-http/object');}
      const endpoint={path:'/api/initial'};endpoint.path=runtimeValue;
      export function mutableUrl(){return client.get(endpoint.path);}`,
        'other.ts': "import {HttpClient as Client} from 'another-client';export function read(http:Client){return http.get('/not-http/other-file');}",
    }, { 'client.ts:./tokens': 'tokens.ts' });
    const result = f.http();
    strict_1.default.equal(result.coverage.complete, true, f.diagnostics.join('\n'));
    strict_1.default.deepEqual(targets(result.edges, 'Lookup:client.ts#Page'), ['route:*:GET:/api/first', 'route:*:GET:/api/second']);
    strict_1.default.deepEqual(targets(result.edges, 'Lookup:client.ts#namespaceRead'), ['route:*:GET:/api/namespace']);
    strict_1.default.deepEqual(targets(result.edges, 'client.Service.member'), ['route:*:POST:/api/member']);
    strict_1.default.deepEqual(targets(result.edges, 'client.Service.injectedRead'), ['route:*:GET:/api/injected']);
    for (const owner of ['Lookup:client.ts#shadow', 'Lookup:client.ts#otherGet', 'Lookup:other.ts#read'])
        strict_1.default.deepEqual(targets(result.edges, owner), []);
    strict_1.default.ok(!result.edges.some(edge => edge.targetQN.includes('/api/initial')), 'immutable transport classification must not cache a stale mutable URL');
});
(0, node_test_1.default)('HTTP import recognition resets between models using the same file and alias names', () => {
    const make = (module, endpoint) => modelFor({ 'same.ts': `import {HttpClient as Client} from '${module}';export function read(http:Client){return http.get('${endpoint}');}` }).http();
    strict_1.default.deepEqual(targets(make('@angular/common/http', '/api/one').edges, 'Lookup:same.ts#read'), ['route:*:GET:/api/one']);
    strict_1.default.deepEqual(targets(make('another-client', '/not-http/two').edges, 'Lookup:same.ts#read'), []);
    strict_1.default.deepEqual(targets(make('@angular/common/http', '/api/three').edges, 'Lookup:same.ts#read'), ['route:*:GET:/api/three']);
});
(0, node_test_1.default)('malformed auxiliary Next configs block known-root fallback, batch syntax checks and recover on config-only edits', () => {
    for (const extension of ['mjs', 'cjs']) {
        const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-next-syntax-'));
        const name = `next.config.${extension}`, nestedName = `nested/next.config.${extension}`;
        const prefix = extension === 'mjs' ? 'export default ' : 'module.exports=';
        fs.mkdirSync(path.join(root, 'app'));
        fs.mkdirSync(path.join(root, 'nested/app'), { recursive: true });
        fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: 'next-app', dependencies: { next: '15' } }));
        const page = 'export default function Page(){return <main/>;}';
        fs.writeFileSync(path.join(root, 'app/page.jsx'), page);
        fs.writeFileSync(path.join(root, 'nested/app/page.jsx'), page);
        fs.writeFileSync(path.join(root, name), `${prefix}{basePath:'/broken'`);
        fs.writeFileSync(path.join(root, nestedName), `${prefix}{basePath:'/nested'};`);
        const create = ts_morph_1.Project.prototype.createSourceFile, program = ts_morph_1.Project.prototype.getProgram;
        const auxiliary = new WeakSet();
        let programs = 0;
        ts_morph_1.Project.prototype.createSourceFile = function (...args) {
            if (/^next\.config\.(?:mjs|cjs)$/.test(path.basename(args[0])))
                auxiliary.add(this);
            return create.apply(this, args);
        };
        ts_morph_1.Project.prototype.getProgram = function () { if (auxiliary.has(this))
            programs++; return program.call(this); };
        const run = () => {
            const before = programs;
            const result = (0, extractor_1.extractProject)('Lookup', root, path.join(root, 'tsconfig.json'), () => { }, ['package.json', 'app/page.jsx', 'nested/app/page.jsx', name, nestedName]);
            strict_1.default.ok(programs - before <= 1, 'all auxiliary configs must be parsed before the shared syntax-check program is materialized');
            return result;
        };
        try {
            const broken = run();
            strict_1.default.equal(broken.complete, false);
            strict_1.default.ok(!broken.nodes.some(node => node.properties.route_kind === 'ui' && node.properties.app_root === '.'), 'a known Next dependency must not turn a malformed config into a / fallback route');
            strict_1.default.ok(broken.diagnostics?.some(message => /(?:parse|syntax).*next\.config|next\.config.*(?:parse|syntax)/i.test(message)), broken.diagnostics?.join('\n'));
            fs.writeFileSync(path.join(root, name), `${prefix}{basePath:'/valid'};`);
            const recovered = run();
            strict_1.default.equal(recovered.complete, true, recovered.diagnostics?.join('\n'));
            strict_1.default.deepEqual(recovered.nodes.filter(node => node.properties.route_kind === 'ui').map(node => node.properties.full_path).sort(), ['/nested', '/valid']);
            strict_1.default.ok(recovered.edges.some(edge => edge.type === 'HANDLES' && edge.targetQN === 'Lookup:app/page.jsx#Page'));
        }
        finally {
            ts_morph_1.Project.prototype.createSourceFile = create;
            ts_morph_1.Project.prototype.getProgram = program;
            fs.rmSync(root, { recursive: true, force: true });
        }
    }
});
