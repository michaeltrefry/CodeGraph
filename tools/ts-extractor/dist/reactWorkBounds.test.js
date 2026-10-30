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
const extractor_1 = require("./extractor");
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const reactAnalysis_1 = require("./reactAnalysis");
const extractionWork_1 = require("./extractionWork");
function fixture(source) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-react-work-'));
    fs.writeFileSync(path.join(root, 'app.tsx'), source);
    const run = (control = {}) => {
        let work = 0;
        const result = (0, extractor_1.extractProject)('ReactWork', root, path.join(root, 'tsconfig.json'), () => { }, ['app.tsx'], undefined, { ...control, onWork: value => { work = value; control.onWork?.(value); } });
        return { result, work };
    };
    return { run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function emptyRefusal(result, parserRefusal = false) {
    strict_1.default.equal(result.complete, false);
    strict_1.default.deepEqual(result.nodes, []);
    strict_1.default.deepEqual(result.edges, []);
    strict_1.default.deepEqual(result.processedFiles, []);
    strict_1.default.deepEqual(result.resolvedImports, []);
    if (!parserRefusal)
        strict_1.default.ok(!result.diagnostics?.some(message => /RangeError|Maximum call stack/i.test(message)), result.diagnostics?.join('\n'));
}
function broad(count) {
    const siblings = '<Leaf/>'.repeat(count);
    return `import {createContext,useContext,useMemo} from 'react';
    function fallback(){} function outerSave(){} function innerSave(){} function unusedSave(){}
    const Ctx=createContext({save:fallback});
    function useActions(){return useContext(Ctx);}
    function Leaf(){const actions=useActions();return <button onClick={actions.save}/>;}
    function Slot({children}){return <main>{children}</main>;}
    export function App(){const value=useMemo(()=>({save:outerSave}),[]); return <Ctx.Provider value={value}>
      <Slot>${siblings}<Ctx.Provider value={{save:innerSave}}><Slot>${siblings}</Slot></Ctx.Provider></Slot>
    </Ctx.Provider>;}
    export function Unprovided(){return <Slot><Leaf/></Slot>;}
    export function UnusedProvider(){return <Ctx.Provider value={{save:unusedSave}}><aside/></Ctx.Provider>;}
  `;
}
function bindings(result) {
    strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
    const providers = result.nodes.filter(node => node.properties.context_kind === 'provider');
    const providerFor = (name) => {
        const provider = providers.find(candidate => result.edges.some(edge => edge.sourceQN === candidate.qualifiedName && edge.type === 'CALLS' && edge.targetQN === `ReactWork:app.tsx#${name}`));
        strict_1.default.ok(provider, `provider value ${name} must remain linked`);
        return provider;
    };
    const outer = providerFor('outerSave'), inner = providerFor('innerSave'), unused = providerFor('unusedSave');
    const selected = result.edges.filter(edge => edge.sourceQN === 'ReactWork:app.tsx#useActions' && edge.properties?.context_kind === 'binding');
    strict_1.default.ok(selected.some(edge => edge.targetQN === outer.qualifiedName), 'outer provider must reach the custom hook through the children slot');
    strict_1.default.ok(selected.some(edge => edge.targetQN === inner.qualifiedName), 'nested provider override must remain a distinct alternative');
    strict_1.default.ok(selected.some(edge => edge.targetQN === 'ReactWork:app.tsx#fallback'), 'unprovided root must retain the context default');
    strict_1.default.ok(!selected.some(edge => edge.targetQN === unused.qualifiedName), 'a provider with no observed consumer cannot leak into another path');
    strict_1.default.ok(selected.every(edge => edge.properties?.candidate === true && edge.properties?.confidence_band === 'medium'));
    strict_1.default.ok(result.edges.some(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.targetQN === 'ReactWork:app.tsx#useActions' && edge.type === 'CALLS'));
    return selected.map(edge => JSON.stringify([edge.type, edge.sourceQN, edge.targetQN, edge.properties])).sort();
}
(0, node_test_1.default)('21KB of 3000 nested children safely refuses at the parser or React guard without partial graph output', context => {
    const nesting = '<C>'.repeat(3000) + '<Leaf/>' + '</C>'.repeat(3000);
    strict_1.default.equal(nesting.length, 21007);
    const source = `import {createContext,useContext} from 'react';
    function save(){} const Ctx=createContext({save});
    function Child({children}){return children;} const C=Child;
    function Leaf(){useContext(Ctx);return <span/>;}
    export function App(){return <Ctx.Provider value={{save}}>${nesting}</Ctx.Provider>;}`;
    strict_1.default.ok(source.length < 512 * 1024);
    const f = fixture(source);
    try {
        const { result, work } = f.run();
        const parserRefusal = result.diagnostics?.some(message => /^sourceRead:.*(?:RangeError|Maximum call stack)/i.test(message)) ?? false;
        if (result.complete) {
            const provider = result.nodes.find(node => node.properties.context_kind === 'provider');
            strict_1.default.ok(provider);
            strict_1.default.ok(result.edges.some(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.targetQN === provider.qualifiedName && edge.properties?.context_kind === 'binding'), 'a complete result must trace the leaf through the children chain');
        }
        else {
            emptyRefusal(result, parserRefusal);
            strict_1.default.ok(parserRefusal || result.diagnostics?.some(message => /depth|nesting|traversal|work limit|analysis work/i.test(message)), result.diagnostics?.join('\n'));
        }
        context.diagnostic(`3000 nested children: complete=${result.complete}, parserRefusal=${parserRefusal}, work=${work}, diagnostic=${result.diagnostics?.[0] ?? '(none)'}`);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('parsed 512-level JSX reaches the React depth guard and poisons the analysis allowance', () => {
    const nesting = '<C>'.repeat(512) + '<Leaf/>' + '</C>'.repeat(512);
    const text = `import {createContext,useContext} from 'react';
    function save(){} const Ctx=createContext({save});
    function Child({children}){return children;} const C=Child;
    function Leaf(){useContext(Ctx);return <span/>;}
    export function App(){return <Ctx.Provider value={{save}}>${nesting}</Ctx.Provider>;}`;
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
        compilerOptions: { noResolve: true, types: [] } });
    const file = project.createSourceFile('/repo/app.tsx', text);
    const model = new semanticModel_1.SemanticModel('ReactWork', '/repo', [file], [], () => { });
    const nodes = [], edges = [];
    model.extract(nodes, edges, []);
    const before = model.work.used;
    let failure;
    strict_1.default.throws(() => (0, reactAnalysis_1.analyzeReact)(model, nodes, edges, () => { }), error => {
        failure = error;
        return error instanceof extractionWork_1.ExtractionWorkLimitError && error.code === 'extraction_work_incomplete' && error.reason === 'depth_limit';
    });
    strict_1.default.ok(model.work.used > before, 'the parsed input must actually enter the iterative React index');
    strict_1.default.throws(() => model.work.charge(0), error => error === failure, 'depth refusal must poison subsequent work in this run');
});
(0, node_test_1.default)('wide React sibling trees use proportional work and preserve providers, hooks, overrides and defaults', context => {
    const measure = (count) => {
        const f = fixture(broad(count));
        try {
            const first = f.run();
            const canonical = bindings(first.result);
            const next = f.run();
            strict_1.default.deepEqual(bindings(next.result), canonical, 'fresh equivalent runs retain the same scoped binding evidence');
            strict_1.default.equal(next.work, first.work, 'work accounting must reset and remain deterministic');
            return first.work;
        }
        finally {
            f.dispose();
        }
    };
    const smaller = measure(32), larger = measure(128);
    strict_1.default.ok(larger <= smaller * 6, `4x sibling source must not trigger quadratic ancestor/render scans: ${smaller} -> ${larger}`);
    context.diagnostic(`32 siblings per provider: ${smaller} work; 128 siblings per provider: ${larger}`);
});
(0, node_test_1.default)('React work/cancellation refusal returns no partial graph and following analyses start fresh', () => {
    const f = fixture(broad(32));
    try {
        const healthy = f.run();
        const canonical = bindings(healthy.result);
        const threshold = Math.floor(healthy.work / 2);
        strict_1.default.ok(threshold > 0);
        emptyRefusal(f.run({ maxWork: 1 }).result);
        emptyRefusal(f.run({ maxWork: threshold }).result);
        let cancelled = false;
        const cancelledRun = f.run({ shouldCancel: () => cancelled, onWork: work => { if (work >= threshold)
                cancelled = true; } });
        emptyRefusal(cancelledRun.result);
        strict_1.default.ok(cancelledRun.result.diagnostics?.some(message => /work limit|cancell|analysis work/i.test(message)), cancelledRun.result.diagnostics?.join('\n'));
        const recovered = f.run();
        strict_1.default.deepEqual(bindings(recovered.result), canonical);
        strict_1.default.equal(recovered.work, healthy.work);
        strict_1.default.deepEqual(recovered.result.processedFiles, ['app.tsx']);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('equivalent children slots share their provider effect instead of allocating a slots-by-children product', context => {
    const measure = (slots, children) => {
        const f = fixture(`import {createContext,useContext} from 'react';
      function save(){} const Ctx=createContext(null);
      function Leaf(){useContext(Ctx);return <span/>;}
      function ManySlots({children}){return <section>${'{children}'.repeat(slots)}</section>;}
      export function App(){return <Ctx.Provider value={{save}}><ManySlots>${'<Leaf/>'.repeat(children)}</ManySlots></Ctx.Provider>;}`);
        try {
            const { result, work } = f.run();
            strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
            const provider = result.nodes.find(node => node.properties.context_kind === 'provider');
            strict_1.default.ok(provider);
            strict_1.default.ok(result.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === 'ReactWork:app.tsx#save' && edge.type === 'CALLS'));
            const selected = result.edges.filter(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.properties?.context_kind === 'binding');
            strict_1.default.equal(selected.length, 1);
            strict_1.default.equal(selected[0].targetQN, provider.qualifiedName);
            strict_1.default.equal(selected[0].properties?.confidence_band, 'high');
            strict_1.default.notEqual(selected[0].properties?.candidate, true);
            return work;
        }
        finally {
            f.dispose();
        }
    };
    const small = measure(10, 10), large = measure(1000, 1000);
    strict_1.default.ok(large <= small * 150, `100x slot/child input must not require 10000x product work: ${small} -> ${large}`);
    context.diagnostic(`10 slots x 10 children: ${small} work; 1000 x 1000: ${large}`);
});
