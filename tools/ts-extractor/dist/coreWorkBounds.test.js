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
const extractor_1 = require("./extractor");
const extractionWork_1 = require("./extractionWork");
function fixture(files) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-core-reservation-'));
    for (const [file, text] of Object.entries(files)) {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.writeFileSync(path.join(root, file), text);
    }
    const run = (control = {}) => {
        let work = 0;
        const result = (0, extractor_1.extractProject)('Core', root, path.join(root, 'tsconfig.json'), () => { }, Object.keys(files), undefined, { ...control, onWork: value => { work = value; control.onWork?.(value); } });
        return { result, work };
    };
    return { root, run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
const corpus = (classes, methods) => Array.from({ length: classes }, (_, index) => `export class C${index}{${Array.from({ length: methods }, (_, member) => `m${member}(value:number):number{return value;}`).join('')}}`).join('\n');
function empty(result) {
    strict_1.default.equal(result.complete, false);
    strict_1.default.deepEqual(result.nodes, []);
    strict_1.default.deepEqual(result.edges, []);
    strict_1.default.deepEqual(result.processedFiles, []);
    strict_1.default.deepEqual(result.resolvedImports, []);
    strict_1.default.ok(result.diagnostics?.some(message => /work limit|retention|analysis work/i.test(message)), result.diagnostics?.join('\n'));
}
function canonical(result) {
    strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
    return { nodes: result.nodes.map(node => JSON.stringify(node)).sort(), edges: result.edges.map(edge => JSON.stringify(edge)).sort(),
        processed: [...result.processedFiles].sort() };
}
/** Count the actual parser and legacy metadata entry points, rather than only checking the
 * empty failure response after an unrestricted compiler/DTO allocation has already happened. */
function observe(root, action) {
    const counts = { parses: 0, classNames: 0, methodNames: 0 };
    const source = ts_morph_1.Project.prototype.createSourceFile;
    const className = ts_morph_1.ClassDeclaration.prototype.getName;
    const methodName = ts_morph_1.MethodDeclaration.prototype.getName;
    const sourceDescriptor = Object.getOwnPropertyDescriptor(ts_morph_1.Project.prototype, 'createSourceFile');
    const classDescriptor = Object.getOwnPropertyDescriptor(ts_morph_1.ClassDeclaration.prototype, 'getName');
    const methodDescriptor = Object.getOwnPropertyDescriptor(ts_morph_1.MethodDeclaration.prototype, 'getName');
    const own = (file) => file.startsWith(root + path.sep);
    Object.defineProperty(ts_morph_1.Project.prototype, 'createSourceFile', { configurable: true, writable: true, value: function (...args) {
            if (typeof args[0] === 'string' && own(path.resolve(args[0])))
                counts.parses++;
            return source.apply(this, args);
        } });
    Object.defineProperty(ts_morph_1.ClassDeclaration.prototype, 'getName', { configurable: true, writable: true, value: function () {
            if (own(this.getSourceFile().getFilePath()))
                counts.classNames++;
            return className.call(this);
        } });
    Object.defineProperty(ts_morph_1.MethodDeclaration.prototype, 'getName', { configurable: true, writable: true, value: function () {
            if (own(this.getSourceFile().getFilePath()))
                counts.methodNames++;
            return methodName.call(this);
        } });
    try {
        return { value: action(), counts };
    }
    finally {
        if (sourceDescriptor)
            Object.defineProperty(ts_morph_1.Project.prototype, 'createSourceFile', sourceDescriptor);
        else
            Reflect.deleteProperty(ts_morph_1.Project.prototype, 'createSourceFile');
        if (classDescriptor)
            Object.defineProperty(ts_morph_1.ClassDeclaration.prototype, 'getName', classDescriptor);
        else
            Reflect.deleteProperty(ts_morph_1.ClassDeclaration.prototype, 'getName');
        if (methodDescriptor)
            Object.defineProperty(ts_morph_1.MethodDeclaration.prototype, 'getName', methodDescriptor);
        else
            Reflect.deleteProperty(ts_morph_1.MethodDeclaration.prototype, 'getName');
    }
}
(0, node_test_1.default)('small class/method corpus reserves output before legacy metadata and edge DTO construction', context => {
    const classes = 40, methods = 8;
    const f = fixture({ 'core.ts': corpus(classes, methods) });
    try {
        const deniedClass = observe(f.root, () => f.run({ maxOutputItems: 0 }));
        empty(deniedClass.value.result);
        strict_1.default.equal(deniedClass.counts.classNames, 0, 'class reservation must precede expensive name/type/property metadata');
        strict_1.default.equal(deniedClass.counts.methodNames, 0);
        const deniedMethod = observe(f.root, () => f.run({ maxOutputItems: 2 }));
        empty(deniedMethod.value.result);
        strict_1.default.equal(deniedMethod.counts.classNames, 1, 'the first class may be emitted before the next method/edge reservation refuses');
        strict_1.default.equal(deniedMethod.counts.methodNames, 0, 'method and edge reservations must precede method DTO metadata');
        const healthy = f.run();
        strict_1.default.equal(healthy.result.complete, true, healthy.result.diagnostics?.join('\n'));
        strict_1.default.equal(healthy.result.nodes.filter(node => node.label === 'Class').length, classes);
        strict_1.default.equal(healthy.result.nodes.filter(node => node.label === 'Method').length, classes * methods);
        strict_1.default.equal(healthy.result.edges.filter(edge => edge.type === 'DEFINES_METHOD').length, classes * methods);
        const method = healthy.result.nodes.find(node => node.qualifiedName === 'core.C0.m0');
        strict_1.default.ok(method);
        strict_1.default.equal(method.properties.parameter_count, 1);
        strict_1.default.equal(method.properties.return_type, 'number');
        strict_1.default.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), canonical(healthy.result));
        context.diagnostic(`40 classes x 8 methods: ${healthy.work} work; refused metadata calls class=${deniedClass.counts.classNames}, method=${deniedMethod.counts.methodNames}`);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('source and aggregate AST admission stop before retaining an entire many-file corpus', context => {
    const files = Object.fromEntries(Array.from({ length: 12 }, (_, index) => [`part${index}.ts`, corpus(2, 8)]));
    const f = fixture(files);
    try {
        const beforeParse = observe(f.root, () => f.run({ maxWork: 1 }));
        empty(beforeParse.value.result);
        strict_1.default.equal(beforeParse.counts.parses, 0, 'source retention must be reserved before createSourceFile');
        const retained = observe(f.root, () => f.run({ maxAstNodes: 10 }));
        empty(retained.value.result);
        strict_1.default.ok(retained.counts.parses > 0 && retained.counts.parses < Object.keys(files).length, 'count the parsed AST before admitting the next file');
        strict_1.default.equal(retained.counts.classNames, 0);
        strict_1.default.equal(retained.counts.methodNames, 0);
        const recovered = observe(f.root, () => f.run());
        strict_1.default.equal(recovered.value.result.complete, true, recovered.value.result.diagnostics?.join('\n'));
        strict_1.default.equal(recovered.counts.parses, Object.keys(files).length);
        strict_1.default.equal(recovered.value.result.processedFiles.length, Object.keys(files).length);
        context.diagnostic(`source refusal parsed ${beforeParse.counts.parses}; AST refusal parsed ${retained.counts.parses}; fresh success parsed ${recovered.counts.parses}`);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('legacy core output work refusal and cancellation discard all partial facts and recover with fresh reservations', () => {
    const f = fixture({ 'core.ts': corpus(12, 6) });
    try {
        const healthy = f.run();
        const expected = canonical(healthy.result);
        const threshold = Math.floor(healthy.work / 2);
        empty(f.run({ maxWork: threshold }).result);
        let cancelled = false;
        empty(f.run({ shouldCancel: () => cancelled, onWork: used => { if (used >= threshold)
                cancelled = true; } }).result);
        const recovered = f.run();
        strict_1.default.deepEqual(canonical(recovered.result), expected);
        strict_1.default.equal(recovered.work, healthy.work);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('normal generic/interface, Angular injection and React render facts remain canonical under core reservations', () => {
    const f = fixture({
        'contracts.ts': `export interface Contract<T>{run(value:T):T;} export class Base{ping():string{return 'ok';}} export enum State{Ready,Done}`,
        'service.ts': `import {Injectable} from '@angular/core'; import {Base,Contract} from './contracts';
      @Injectable() export class Service extends Base implements Contract<string>{
        run(value:string):string{return value;} async fetch(id:string):Promise<string>{return this.run(id);}
        arrow=(value:string)=>this.run(value);
      }`,
        'page.ts': `import {Component,Input,inject} from '@angular/core'; import {Service} from './service';
      @Component({selector:'app-page',template:'<span/>'}) export class Page{
        @Input() title:string=''; private service=inject(Service); constructor(private second:Service){}
        run(){return this.service.run(this.title);}
      }`,
        'app.tsx': `export function Panel(){return <section/>;} export function App(){return <Panel/>;}`,
    });
    try {
        const healthy = f.run();
        const result = healthy.result;
        strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
        const node = (qn) => { const value = result.nodes.find(item => item.qualifiedName === qn); strict_1.default.ok(value, qn); return value; };
        strict_1.default.equal(node('contracts.Contract').label, 'Interface');
        strict_1.default.equal(node('contracts.Contract').properties.is_generic, true);
        strict_1.default.equal(node('contracts.State').label, 'Enum');
        strict_1.default.equal(node('service.Service').properties.is_service, true);
        strict_1.default.equal(node('page.Page').label, 'Component');
        strict_1.default.equal(node('page.Page.title').properties.is_input, true);
        strict_1.default.equal(node('service.Service.fetch').properties.is_async, true);
        strict_1.default.equal(node('service.Service.run').properties.parameter_count, 1);
        strict_1.default.equal(node('service.Service.arrow').properties.is_arrow_property, true);
        for (const [source, target, type] of [
            ['service.Service', 'contracts.Base', 'INHERITS'], ['service.Service', 'contracts.Contract', 'IMPLEMENTS'],
            ['page.Page', 'service.Service', 'INJECTS'], ['Core:app.tsx#App', 'Core:app.tsx#Panel', 'RENDERS'],
        ])
            strict_1.default.ok(result.edges.some(edge => edge.sourceQN === source && edge.targetQN === target && edge.type === type), `${source} ${type} ${target}`);
        const expected = canonical(result);
        strict_1.default.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), expected);
        strict_1.default.deepEqual(canonical(f.run().result), expected);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('large configured reader and work budgets cannot disable the independent pre-parser window', () => {
    const source = '/*' + 'x'.repeat(extractionWork_1.MAX_PARSE_SOURCE_UNITS) + '*/';
    strict_1.default.ok(source.length > extractionWork_1.MAX_PARSE_SOURCE_UNITS && source.length < 4 * 1024 * 1024);
    const f = fixture({ 'comment.ts': source });
    try {
        const refused = observe(f.root, () => (0, extractor_1.extractProject)('Core', f.root, path.join(f.root, 'tsconfig.json'), () => { }, ['comment.ts'], { maxFileSizeKb: 4096, maxAnalysisTextKb: 8192 }, { maxWork: Number.MAX_SAFE_INTEGER }));
        empty(refused.value);
        strict_1.default.equal(refused.counts.parses, 0, 'the independent temporary parse window must refuse before createSourceFile');
        strict_1.default.equal(refused.counts.classNames, 0);
        strict_1.default.equal(refused.counts.methodNames, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('ownership writes do not consume DTO slots and absent edge properties reserve their one new object', () => {
    const f = fixture({ 'only.ts': 'export function Only(){return 1;}' });
    try {
        // One function + one DEFINES edge + canonical planning + the edge's absent properties.
        const result = f.run({ maxOutputItems: 4 }).result;
        strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
        strict_1.default.equal(result.nodes.length, 1);
        strict_1.default.equal(result.edges.length, 1);
        strict_1.default.equal(result.nodes[0].properties.extraction_owner, 'typescript');
        strict_1.default.deepEqual(result.edges[0].properties, { extraction_owner: 'typescript' });
        const refused = f.run({ maxOutputItems: 3 }).result;
        empty(refused);
        strict_1.default.ok(refused.diagnostics?.some(message => message.includes('retention_limit')));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('final ownership preserves canonical property identity and does not stamp nested evidence witnesses', () => {
    const f = fixture({ 'app.tsx': 'export function handler(){} export function App(){handler();return <button onClick={handler}/>;}' });
    const module = require('./extractionEdges');
    const original = module.canonicalExtractionEdges;
    const before = new Map();
    module.canonicalExtractionEdges = edges => {
        const result = original(edges);
        for (const edge of result)
            if (edge.properties)
                before.set(edge, edge.properties);
        return result;
    };
    try {
        const result = f.run().result;
        strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
        const call = result.edges.find(edge => edge.type === 'CALLS' && edge.sourceQN === 'Core:app.tsx#App' && edge.targetQN === 'Core:app.tsx#handler');
        strict_1.default.ok(call);
        strict_1.default.equal(call.properties, before.get(call), 'an existing canonical property object needs no replacement');
        strict_1.default.equal(call.properties?.extraction_owner, 'typescript');
        strict_1.default.equal(call.properties?.call_kind, 'event_handler');
        const evidence = call.properties?.extraction_evidence;
        strict_1.default.deepEqual(evidence.map(value => JSON.stringify(value)).sort(), [{}, { call_kind: 'event_handler' }].map(value => JSON.stringify(value)).sort());
        strict_1.default.ok(evidence.every(value => value.extraction_owner === undefined), 'canonical evidence remains independent of the output stamp');
    }
    finally {
        module.canonicalExtractionEdges = original;
        f.dispose();
    }
});
