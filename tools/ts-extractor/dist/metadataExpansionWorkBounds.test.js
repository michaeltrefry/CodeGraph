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
const angularAnalysis_1 = require("./angularAnalysis");
const extractor_1 = require("./extractor");
const semanticModel_1 = require("./semanticModel");
const extractionWork_1 = require("./extractionWork");
const angular = `import {NgModule,Component,InjectionToken,inject} from '@angular/core';\n`;
function doubled(depth, field = 'imports') {
    return angular + `class Leaf {} const a0=[Leaf];\n` + Array.from({ length: depth }, (_, i) => `const a${i + 1}=[...a${i},...a${i}];`).join('\n') +
        `\n@NgModule({${field}:a${depth}}) export class Module {}`;
}
function direct(source, control = {}) {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [], experimentalDecorators: true } });
    const root = fs.realpathSync(os.tmpdir());
    const file = project.createSourceFile(path.join(root, 'app.ts'), source);
    const imports = [{ fileQN: 'Metadata:app.ts', filePath: 'app.ts', importedNamespace: '@angular/core', importKind: 'static', classification: 'external_npm_package', externalPackageName: '@angular/core' }];
    const diagnostics = [];
    const model = new semanticModel_1.SemanticModel('Metadata', root, [file], imports, message => diagnostics.push(message), control);
    const edges = [];
    return { file, model, edges, diagnostics, run: () => (0, angularAnalysis_1.analyzeAngular)(model, [], edges, undefined, message => diagnostics.push(message)) };
}
function fixture(source) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-metadata-expansion-'));
    fs.writeFileSync(path.join(root, 'app.ts'), source);
    return { run: (control = {}) => (0, extractor_1.extractProject)('Metadata', root, path.join(root, 'tsconfig.json'), () => { }, ['app.ts'], undefined, control),
        dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function refused(result, reason) {
    strict_1.default.equal(result.complete, false);
    strict_1.default.deepEqual(result.nodes, []);
    strict_1.default.deepEqual(result.edges, []);
    strict_1.default.deepEqual(result.processedFiles, []);
    strict_1.default.deepEqual(result.resolvedImports, []);
    strict_1.default.ok(result.diagnostics?.some(message => message.includes(reason)), result.diagnostics?.join('\n'));
}
function canonical(result) {
    strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
    return { nodes: result.nodes.map(value => JSON.stringify(value)).sort(), edges: result.edges.map(value => JSON.stringify(value)).sort(), processed: result.processedFiles };
}
function restore(target, key, descriptor) {
    if (descriptor)
        Object.defineProperty(target, key, descriptor);
    else
        Reflect.deleteProperty(target, key);
}
for (const field of ['imports', 'providers'])
    (0, node_test_1.default)(`shared ${field} arrays reserve reference slots before copying an expanded child result`, context => {
        const cap = 256;
        const f = direct(doubled(12, field), { maxArrayReferences: cap, maxWork: Number.MAX_SAFE_INTEGER });
        const leaf = f.file.getClassOrThrow('Leaf');
        let copies = 0, requests = 0, largestRequest = 0, largestAllocatedArray = 0;
        const reserve = f.model.work.reserveArrayReferences.bind(f.model.work);
        f.model.work.reserveArrayReferences = (slots) => { requests++; largestRequest = Math.max(largestRequest, slots); reserve(slots); };
        const ArrayConstructor = Array;
        const arrayDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'Array');
        Object.defineProperty(globalThis, 'Array', { configurable: true, writable: true, value: new Proxy(ArrayConstructor, { construct(target, args, newTarget) {
                    if (requests && args.length === 1 && typeof args[0] === 'number')
                        largestAllocatedArray = Math.max(largestAllocatedArray, args[0]);
                    return Reflect.construct(target, args, newTarget);
                } }) });
        const push = Array.prototype.push, flatMap = Array.prototype.flatMap;
        const pushDescriptor = Object.getOwnPropertyDescriptor(Array.prototype, 'push'), flatDescriptor = Object.getOwnPropertyDescriptor(Array.prototype, 'flatMap');
        Object.defineProperty(Array.prototype, 'push', { configurable: true, writable: true, value: function (...items) {
                for (const item of items)
                    if (item === leaf)
                        copies++;
                return push.apply(this, items);
            } });
        Object.defineProperty(Array.prototype, 'flatMap', { configurable: true, writable: true, value: function (...args) {
                const result = flatMap.apply(this, args);
                for (const item of result)
                    if (item === leaf)
                        copies++;
                return result;
            } });
        try {
            strict_1.default.throws(f.run, (error) => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'retention_limit');
            strict_1.default.ok(requests > 0, 'expansion must use the shared preallocation guard');
            strict_1.default.ok(f.model.work.retainedArrayReferences <= cap);
            strict_1.default.ok(copies <= cap + 16, `leaf references copied before refusal: ${copies}`);
            strict_1.default.ok(largestAllocatedArray <= cap, `array allocated before refusal: ${largestAllocatedArray}`);
            strict_1.default.throws(() => f.model.work.charge(0), extractionWork_1.ExtractionWorkLimitError, 'a refused expansion poisons this run');
            context.diagnostic(`${field} depth12/4096 leaves: ${copies} observed leaf copies, ${requests} reservations, largest request ${largestRequest}, largest allocation ${largestAllocatedArray}, retained ${f.model.work.retainedArrayReferences}/${cap}`);
        }
        finally {
            restore(ArrayConstructor.prototype, 'push', pushDescriptor);
            restore(ArrayConstructor.prototype, 'flatMap', flatDescriptor);
            restore(globalThis, 'Array', arrayDescriptor);
        }
    });
(0, node_test_1.default)('shared raw metadata expansions are cached without rescanning every spread for each consumer', context => {
    const depth = 8, consumers = 10;
    const source = doubled(depth) + Array.from({ length: consumers - 1 }, (_, i) => `\n@NgModule({imports:a${depth}}) class Other${i}{}`).join('');
    const f = direct(source);
    const arrays = new Set(f.file.getVariableDeclarations().filter(node => /^a\d+$/.test(node.getName())).map(node => node.getInitializer()).filter(ts_morph_1.Node.isArrayLiteralExpression));
    const descriptor = Object.getOwnPropertyDescriptor(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements');
    const getElements = ts_morph_1.ArrayLiteralExpression.prototype.getElements;
    let reads = 0;
    Object.defineProperty(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements', { configurable: true, writable: true, value: function () { if (arrays.has(this))
            reads++; return getElements.call(this); } });
    try {
        strict_1.default.equal(f.run().complete, true, f.diagnostics.join('\n'));
        strict_1.default.ok(reads <= arrays.size * 2, `${reads} array materializations for ${arrays.size} shared AST arrays`);
        strict_1.default.ok(f.model.work.retainedArrayReferences >= 2 ** depth, 'duplicates remain actual reference slots');
        context.diagnostic(`${consumers} consumers / ${arrays.size} arrays: ${reads} materializations, ${f.model.work.retainedArrayReferences} reserved references`);
    }
    finally {
        restore(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements', descriptor);
    }
});
(0, node_test_1.default)('provider flattening preserves duplicate multi entries and ordering instead of deduplicating shared arrays', () => {
    const f = direct(angular + `interface Contract{run():void;} const TOKEN=new InjectionToken<Contract>('t');
    class First implements Contract{run(){}} class Second implements Contract{run(){}}
    const first={provide:TOKEN,useClass:First,multi:true};const second={provide:TOKEN,useClass:Second,multi:true};
    const shared=[first,second,first]; @NgModule({providers:[...shared,...shared]}) class Module{}`);
    const first = f.file.getVariableDeclarationOrThrow('first').getInitializerOrThrow();
    const second = f.file.getVariableDeclarationOrThrow('second').getInitializerOrThrow();
    const observed = [];
    const property = f.model.objectProperty.bind(f.model);
    f.model.objectProperty = (object, name) => { if (name === 'provide' && (object === first || object === second))
        observed.push(object === first ? 'first' : 'second'); return property(object, name); };
    strict_1.default.equal(f.run().complete, true, f.diagnostics.join('\n'));
    strict_1.default.deepEqual(observed, ['first', 'second', 'first', 'first', 'second', 'first']);
    strict_1.default.ok(f.edges.some(edge => edge.type === 'REGISTERS' && edge.targetQN === 'app.First'));
    strict_1.default.ok(f.edges.some(edge => edge.type === 'REGISTERS' && edge.targetQN === 'app.Second'));
});
(0, node_test_1.default)('raw shared provider values preserve ordered NgModule imports, declarations and isolated component overrides', () => {
    const source = angular + `interface Contract{run():string;} const TOKEN=new InjectionToken<Contract>('t');
    class First implements Contract{run(){return 'first';}}class Second implements Contract{run(){return 'second';}}
    const shared=[{provide:TOKEN,useClass:First},{provide:TOKEN,useClass:Second}];
    @Component({selector:'feature',template:''}) class Feature{service=inject(TOKEN);run(){this.service.run();}}
    @Component({selector:'other',template:'',providers:[...shared,{provide:TOKEN,useClass:First}]}) class Other{service=inject(TOKEN);run(){this.service.run();}}
    const declared=[Feature,Other];@NgModule({declarations:[...declared],providers:shared}) class ChildModule{}
    const imported=[ChildModule];@NgModule({imports:[...imported],providers:[...shared,{provide:TOKEN,useClass:First}]}) class AppModule{}
    @Component({selector:'separate',template:''}) class Separate{service=inject(TOKEN);run(){this.service.run();}}
    @NgModule({declarations:[Separate],providers:shared}) class SeparateModule{}`;
    const f = fixture(source);
    try {
        const result = f.run();
        const expected = canonical(result);
        const calls = (owner) => result.edges.filter(edge => edge.type === 'CALLS' && edge.sourceQN === `app.${owner}.run` && /app\.(First|Second)\.run$/.test(edge.targetQN)).map(edge => edge.targetQN).sort();
        strict_1.default.deepEqual(calls('Feature'), ['app.First.run']);
        strict_1.default.deepEqual(calls('Other'), ['app.First.run']);
        strict_1.default.deepEqual(calls('Separate'), ['app.Second.run']);
        strict_1.default.deepEqual(canonical(f.run()), expected);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('array reference refusal, cancellation and hard default admission discard whole extraction and recover freshly', () => {
    const f = fixture(doubled(10));
    try {
        const healthy = f.run();
        const expected = canonical(healthy);
        refused(f.run({ maxArrayReferences: 128, maxWork: Number.MAX_SAFE_INTEGER }), 'retention_limit');
        let cancelled = false;
        refused(f.run({ shouldCancel: () => cancelled, onWork: used => { if (used > 2000)
                cancelled = true; } }), 'cancelled');
        strict_1.default.deepEqual(canonical(f.run()), expected);
    }
    finally {
        f.dispose();
    }
    const bounded = fixture(doubled(18));
    try {
        refused(bounded.run({ maxArrayReferences: Number.MAX_SAFE_INTEGER, maxWork: Number.MAX_SAFE_INTEGER }), 'retention_limit');
    }
    finally {
        bounded.dispose();
    }
});
(0, node_test_1.default)('metadata alias depth and cycles refuse safely and do not contaminate a fresh extraction', () => {
    for (const source of [angular + `class Leaf{};const a0=[Leaf];` + Array.from({ length: 150 }, (_, i) => `const a${i + 1}=[...a${i}];`).join('') + `@NgModule({imports:a150}) class Module{}`,
        angular + `const a=[...b];const b=[...a];@NgModule({imports:a})class Module{}`]) {
        const f = fixture(source);
        try {
            const result = f.run();
            refused(result, 'depth_limit');
            strict_1.default.ok(!result.diagnostics?.some(message => /RangeError|maximum call stack/i.test(message)), result.diagnostics?.join('\n'));
        }
        finally {
            f.dispose();
        }
    }
    const healthy = fixture(doubled(2));
    try {
        canonical(healthy.run());
    }
    finally {
        healthy.dispose();
    }
});
