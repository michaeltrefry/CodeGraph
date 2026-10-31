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
const staticEvaluation_1 = require("./staticEvaluation");
const extractor_1 = require("./extractor");
const extractionWork_1 = require("./extractionWork");
function arrayCorpus(count) {
    return `const [${Array.from({ length: count }, (_, i) => `v${i}`).join(',')}]=[${Array.from({ length: count }, (_, i) => `{value:${i}}`).join(',')}];const probe={method:'GET'};`;
}
function objectCorpus(count) {
    return `const {${Array.from({ length: count }, (_, i) => `k${i}:v${i}`).join(',')}}={${Array.from({ length: count }, (_, i) => `k${i}:{value:${i}}`).join(',')}};const probe={method:'GET'};`;
}
function direct(source, control = {}) {
    const root = fs.realpathSync(os.tmpdir());
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
    const file = project.createSourceFile(path.join(root, 'bindings.ts'), source);
    const diagnostics = [];
    const model = new semanticModel_1.SemanticModel('Bindings', root, [file], [], message => diagnostics.push(message), control);
    const evaluator = new staticEvaluation_1.StaticEvaluator(model, message => diagnostics.push(message));
    // Resolve the test probe before observing evaluator work: ts-morph's name lookup itself
    // scans a preceding destructuring pattern, which is outside the indexed path under test.
    const probe = file.getVariableDeclarationOrThrow('probe');
    return { file, model, evaluator, run: () => evaluator.evaluate(probe) };
}
function restore(target, key, descriptor) {
    if (descriptor)
        Object.defineProperty(target, key, descriptor);
    else
        Reflect.deleteProperty(target, key);
}
/** Count numeric element reads from the actual materialized AST arrays. Native some/indexOf
 * traverse this Proxy too, so moving the same quadratic scan inside a helper cannot pass. */
function observe(f, action) {
    const binding = f.file.getVariableDeclarations()[0].getNameNode();
    const source = f.file.getVariableDeclarations()[0].getInitializerOrThrow();
    const counts = { patterns: 0, patternWidth: 0, patternCandidates: 0, arrays: 0, arrayWidth: 0, arrayCandidates: 0, objects: 0, objectWidth: 0, objectCandidates: 0 };
    const wrap = (values, candidate) => new Proxy(values, { get(target, key, receiver) { if (typeof key === 'string' && /^(?:0|[1-9]\d*)$/.test(key))
            candidate(); return Reflect.get(target, key, receiver); } });
    const pattern = ts_morph_1.ArrayBindingPattern.prototype.getElements, array = ts_morph_1.ArrayLiteralExpression.prototype.getElements, object = ts_morph_1.ObjectLiteralExpression.prototype.getProperties;
    const pd = Object.getOwnPropertyDescriptor(ts_morph_1.ArrayBindingPattern.prototype, 'getElements'), ad = Object.getOwnPropertyDescriptor(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements'), od = Object.getOwnPropertyDescriptor(ts_morph_1.ObjectLiteralExpression.prototype, 'getProperties');
    Object.defineProperty(ts_morph_1.ArrayBindingPattern.prototype, 'getElements', { configurable: true, writable: true, value: function () { const result = pattern.call(this); if (this !== binding)
            return result; counts.patterns++; counts.patternWidth += result.length; return wrap(result, () => counts.patternCandidates++); } });
    Object.defineProperty(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements', { configurable: true, writable: true, value: function () { const result = array.call(this); if (this !== source)
            return result; counts.arrays++; counts.arrayWidth += result.length; return wrap(result, () => counts.arrayCandidates++); } });
    Object.defineProperty(ts_morph_1.ObjectLiteralExpression.prototype, 'getProperties', { configurable: true, writable: true, value: function () { const result = object.call(this); if (this !== source)
            return result; counts.objects++; counts.objectWidth += result.length; return wrap(result, () => counts.objectCandidates++); } });
    try {
        return { value: action(), counts };
    }
    finally {
        restore(ts_morph_1.ArrayBindingPattern.prototype, 'getElements', pd);
        restore(ts_morph_1.ArrayLiteralExpression.prototype, 'getElements', ad);
        restore(ts_morph_1.ObjectLiteralExpression.prototype, 'getProperties', od);
    }
}
function fixture(source) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-binding-index-'));
    fs.writeFileSync(path.join(root, 'bindings.ts'), source);
    return { run: (control = {}) => { let work = 0; const result = (0, extractor_1.extractProject)('Bindings', root, path.join(root, 'tsconfig.json'), () => { }, ['bindings.ts'], undefined, { ...control, onWork: value => { work = value; control.onWork?.(value); } }); return { result, work }; }, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function empty(result) {
    strict_1.default.equal(result.complete, false);
    strict_1.default.deepEqual(result.nodes, []);
    strict_1.default.deepEqual(result.edges, []);
    strict_1.default.deepEqual(result.processedFiles, []);
    strict_1.default.deepEqual(result.resolvedImports, []);
}
function canonical(result) {
    strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
    return { nodes: result.nodes.map(value => JSON.stringify(value)).sort(), edges: result.edges.map(value => JSON.stringify(value)).sort(), processed: result.processedFiles };
}
const routes = (result, owner) => result.edges.filter(edge => edge.type === 'HTTP_CALLS' && edge.sourceQN === `Bindings:bindings.ts#${owner}`).map(edge => edge.targetQN).sort();
(0, node_test_1.default)('array alias indexing materializes each pattern/RHS once with linear actual candidate visits', context => {
    const measure = (count) => {
        const f = direct(arrayCorpus(count));
        const start = f.model.work.used;
        const observed = observe(f, () => { const first = f.run(); strict_1.default.ok((0, staticEvaluation_1.isObject)(first)); strict_1.default.equal(first.properties.get('method'), 'GET'); f.run(); return f.model.work.used - start; });
        const c = observed.counts;
        strict_1.default.equal(c.patterns, 1);
        strict_1.default.equal(c.patternWidth, count);
        strict_1.default.equal(c.arrays, 1);
        strict_1.default.equal(c.arrayWidth, count);
        strict_1.default.ok(c.patternCandidates <= count * 4, `binding candidate accesses ${c.patternCandidates}/${count}`);
        strict_1.default.ok(c.arrayCandidates <= count * 4, `RHS candidate accesses ${c.arrayCandidates}/${count}`);
        return { work: observed.value, ...c };
    };
    const small = measure(200), large = measure(600);
    strict_1.default.ok(large.work <= small.work * 4, JSON.stringify({ small, large }));
    context.diagnostic(JSON.stringify({ small, large }));
});
(0, node_test_1.default)('object alias selection indexes one source property view instead of scanning every property per binding', context => {
    const measure = (count) => {
        const f = direct(objectCorpus(count));
        const start = f.model.work.used;
        const observed = observe(f, () => { const value = f.run(); strict_1.default.ok((0, staticEvaluation_1.isObject)(value)); strict_1.default.equal(value.properties.get('method'), 'GET'); return f.model.work.used - start; });
        strict_1.default.equal(observed.counts.objects, 1);
        strict_1.default.equal(observed.counts.objectWidth, count);
        strict_1.default.ok(observed.counts.objectCandidates <= count * 5, JSON.stringify(observed.counts));
        return { work: observed.value, ...observed.counts };
    };
    const small = measure(100), large = measure(300);
    strict_1.default.ok(large.work <= small.work * 4, JSON.stringify({ small, large }));
    context.diagnostic(JSON.stringify({ small, large }));
});
(0, node_test_1.default)('structural binding admission refuses and poisons before materializing a wide pattern or RHS', () => {
    const source = arrayCorpus(200);
    const baseline = direct(source).model.work.used;
    for (const control of [{ maxArrayReferences: 0 }, { maxWork: baseline + 1 }]) {
        const f = direct(source, control);
        const observed = observe(f, () => strict_1.default.throws(f.run, extractionWork_1.ExtractionWorkLimitError));
        strict_1.default.equal(observed.counts.patterns, 0);
        strict_1.default.equal(observed.counts.arrays, 0);
        strict_1.default.throws(() => f.model.work.charge(0), extractionWork_1.ExtractionWorkLimitError);
    }
    let cancel = false;
    const f = direct(source, { shouldCancel: () => cancel });
    cancel = true;
    const observed = observe(f, () => strict_1.default.throws(f.run, (error) => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'cancelled'));
    strict_1.default.equal(observed.counts.patterns, 0);
    strict_1.default.equal(observed.counts.arrays, 0);
    strict_1.default.throws(() => f.model.work.charge(0), extractionWork_1.ExtractionWorkLimitError);
});
(0, node_test_1.default)('array position/default/rest/spread mutations retain selected-object identity and independent siblings', () => {
    const f = fixture(`
    export function positional(){const first={method:'GET'},second={method:'POST'};const holder=[first,second];const [,alias]=holder;alias.method='PATCH';fetch('/api/first',first);return fetch('/api/second',second);}
    export function defaulted(){const options={method:'GET'};const [alias=options]=[];alias.method='DELETE';return fetch('/api/default',options);}
    export function rest(){const options={method:'GET'};const holder=[null,options];const [,...tail]=holder;tail[0].method='DELETE';return fetch('/api/rest',options);}
    export function spread(items){const options={method:'GET'};const holder=[...items,options];const [alias]=holder;alias.method='DELETE';return fetch('/api/spread',options);}
    export function immutable(){const options={method:'GET'};const holder=[options];const [alias]=holder;return fetch('/api/immutable',options);}
    export function shadow(){const options={method:'GET'};{const options={method:'POST'};const [alias]=[options];alias.method='PATCH';}return fetch('/api/shadow',options);}
  `);
    try {
        const result = f.run().result;
        canonical(result);
        strict_1.default.deepEqual(routes(result, 'positional'), ['route:*:GET:/api/first']);
        for (const name of ['defaulted', 'rest', 'spread'])
            strict_1.default.deepEqual(routes(result, name), [], name);
        strict_1.default.deepEqual(routes(result, 'immutable'), ['route:*:GET:/api/immutable']);
        strict_1.default.deepEqual(routes(result, 'shadow'), ['route:*:GET:/api/shadow']);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('structural caches preserve caller-specific environments and reset between models with identical binding names', () => {
    const source = `function wrapper({path,options}){const holder=[options];const [alias]=holder;const nested={path,alias};return fetch(nested.path,options);}
    export function caller(){wrapper({path:'/api/first',options:{method:'PUT'}});wrapper({path:'/api/second',options:{method:'DELETE'}});}`;
    const f = fixture(source);
    try {
        const result = f.run().result;
        canonical(result);
        strict_1.default.deepEqual(routes(result, 'caller'), ['route:*:DELETE:/api/second', 'route:*:PUT:/api/first']);
    }
    finally {
        f.dispose();
    }
    for (const method of ['GET', 'POST', 'GET']) {
        const f = direct(`const [alias]=[{method:'${method}'}];const probe={method:'${method}'};`);
        const value = f.run();
        strict_1.default.ok((0, staticEvaluation_1.isObject)(value));
        strict_1.default.equal(value.properties.get('method'), method);
    }
    const f2 = direct(`function select({path}){return path;}const probe={};`);
    const fn = f2.file.getFunctionOrThrow('select');
    const expression = fn.getBodyOrThrow().getDescendants().find(ts_morph_1.Node.isReturnStatement)?.getExpression();
    strict_1.default.ok(expression);
    for (const value of ['/one', '/two']) {
        const env = f2.evaluator.bind(fn, [{ kind: 'object', properties: new Map([['path', value]]), unknownKeys: false }]);
        strict_1.default.equal(f2.evaluator.evaluate(expression, env), value);
    }
});
(0, node_test_1.default)('binding-index work refusal and cancellation discard all extraction output and recover freshly', () => {
    const f = fixture(arrayCorpus(200) + `const endpoint='/api/healthy';export function load(){return fetch(endpoint);}`);
    try {
        const healthy = f.run();
        const expected = canonical(healthy.result);
        empty(f.run({ maxArrayReferences: 0 }).result);
        empty(f.run({ maxWork: Math.floor(healthy.work / 2) }).result);
        let cancel = false;
        empty(f.run({ shouldCancel: () => cancel, onWork: used => { if (used > healthy.work / 2)
                cancel = true; } }).result);
        strict_1.default.deepEqual(canonical(f.run().result), expected);
        strict_1.default.equal(f.run().work, healthy.work);
    }
    finally {
        f.dispose();
    }
});
