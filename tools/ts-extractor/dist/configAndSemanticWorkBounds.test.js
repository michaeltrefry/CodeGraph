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
const ts = __importStar(require("typescript"));
const ts_morph_1 = require("ts-morph");
const extractor_1 = require("./extractor");
const boundedTextReader_1 = require("./boundedTextReader");
const discoveryScope_1 = require("./discoveryScope");
const workspaceResolver_1 = require("./workspaceResolver");
const extractionWork_1 = require("./extractionWork");
function fixture(files) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-frontier-'));
    for (const [file, text] of Object.entries(files)) {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.writeFileSync(path.join(root, file), text);
    }
    const run = (control = {}, limits = {}) => {
        let work = 0;
        const result = (0, extractor_1.extractProject)('Alloc', root, path.join(root, 'tsconfig.json'), () => { }, Object.keys(files), limits, { ...control, onWork: value => { work = value; control.onWork?.(value); } });
        return { result, work };
    };
    return { root, run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
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
    return { nodes: result.nodes.map(node => JSON.stringify(node)).sort(), edges: result.edges.map(edge => JSON.stringify(edge)).sort(), processed: [...result.processedFiles].sort() };
}
function observeParsers(action) {
    const counts = { config: 0, other: 0 };
    const original = ts_morph_1.Project.prototype.createSourceFile;
    ts_morph_1.Project.prototype.createSourceFile = function (...args) {
        if (/^next\.config\.(?:mjs|cjs|js|ts)$/.test(path.basename(args[0])))
            counts.config++;
        else
            counts.other++;
        return original.apply(this, args);
    };
    try {
        return { value: action(), counts };
    }
    finally {
        ts_morph_1.Project.prototype.createSourceFile = original;
    }
}
function rawNodes(file, text) {
    const kind = file.endsWith('.jsx') ? ts.ScriptKind.JSX : file.endsWith('.tsx') ? ts.ScriptKind.TSX : file.endsWith('.ts') ? ts.ScriptKind.TS : ts.ScriptKind.JS;
    const root = ts.createSourceFile(file, text, ts.ScriptTarget.ES2015, true, kind);
    const pending = [root];
    let count = 0;
    while (pending.length) {
        const node = pending.pop();
        count++;
        ts.forEachChild(node, child => { pending.push(child); });
    }
    return count;
}
const page = 'export default function Page(){return <main/>;}';
(0, node_test_1.default)('large MJS/CJS Next configs cannot bypass the independent pre-parser window even with admitted JS source', () => {
    for (const extension of ['mjs', 'cjs']) {
        const text = `${extension === 'mjs' ? "export default {basePath:'/ui'};" : "module.exports={basePath:'/ui'};"}/*${'x'.repeat(extractionWork_1.MAX_PARSE_SOURCE_UNITS)}*/`;
        const f = fixture({ 'app/page.jsx': page, [`next.config.${extension}`]: text });
        try {
            const observed = observeParsers(() => f.run({ maxWork: Number.MAX_SAFE_INTEGER }, { maxFileSizeKb: 4096, maxAnalysisTextKb: 8192 }));
            empty(observed.value.result);
            strict_1.default.equal(observed.counts.config, 0, extension);
        }
        finally {
            f.dispose();
        }
        const normal = fixture({ 'app/page.jsx': page, [`next.config.${extension}`]: extension === 'mjs' ? "export default {basePath:'/ui'};" : "module.exports={basePath:'/ui'};" });
        try {
            const observed = observeParsers(() => normal.run());
            strict_1.default.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
            strict_1.default.ok(observed.counts.other > 0 && observed.counts.config === 1);
            strict_1.default.ok(observed.value.result.nodes.some(node => node.properties.full_path === '/ui'));
        }
        finally {
            normal.dispose();
        }
    }
});
(0, node_test_1.default)('independent Next config projects share the source AST retention allowance', () => {
    const files = {
        'apps/one/app/page.jsx': page, 'apps/two/app/page.jsx': page,
        'apps/one/next.config.mjs': "export default {basePath:'/one'};",
        'apps/two/next.config.cjs': "module.exports={basePath:'/two'};",
    };
    const sourceCount = rawNodes('page.jsx', page) * 2;
    const configCounts = [rawNodes('one.mjs', files['apps/one/next.config.mjs']), rawNodes('two.cjs', files['apps/two/next.config.cjs'])];
    const f = fixture(files);
    try {
        const limited = observeParsers(() => f.run({ maxAstNodes: sourceCount + Math.max(...configCounts) }));
        empty(limited.value.result);
        strict_1.default.ok(limited.counts.config > 0 && limited.counts.config <= 2);
        const recovered = f.run();
        strict_1.default.equal(recovered.result.complete, true, recovered.result.diagnostics?.join('\n'));
        strict_1.default.deepEqual(recovered.result.nodes.filter(node => node.properties.route_kind === 'ui').map(node => node.properties.full_path).sort(), ['/one', '/two']);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('JS/TS Next configs reuse their admitted AST or charge any second retained tree', () => {
    for (const extension of ['js', 'ts']) {
        const name = `next.config.${extension}`, text = "const config={basePath:'/ui'};export default config;";
        const f = fixture({ 'app/page.jsx': page, [name]: text });
        try {
            const observed = observeParsers(() => f.run({ maxAstNodes: rawNodes('page.jsx', page) + rawNodes(name, text) }));
            if (observed.counts.config === 1) {
                strict_1.default.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
                strict_1.default.ok(observed.value.result.nodes.some(node => node.properties.full_path === '/ui'));
            }
            else {
                strict_1.default.equal(observed.counts.config, 2, 'a second retained configuration tree must be explicitly counted');
                empty(observed.value.result);
            }
        }
        finally {
            f.dispose();
        }
    }
});
(0, node_test_1.default)('semantic function node/edge factories reserve before position metadata and output accumulation', () => {
    const count = 80;
    const f = fixture({ 'functions.ts': Array.from({ length: count }, (_, index) => `export function F${index}(value:number){return value;}`).join('\n') });
    const originalLocation = ts_morph_1.FunctionDeclaration.prototype.getStartLineNumber;
    const locationDescriptor = Object.getOwnPropertyDescriptor(ts_morph_1.FunctionDeclaration.prototype, 'getStartLineNumber');
    const reserve = extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput;
    let locations = 0;
    const events = [];
    Object.defineProperty(ts_morph_1.FunctionDeclaration.prototype, 'getStartLineNumber', { configurable: true, writable: true, value: function (...args) {
            if (this.getSourceFile().getFilePath().startsWith(f.root + path.sep))
                locations++;
            return originalLocation.apply(this, args);
        } });
    extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput = function (kind, textUnits = 0) {
        const before = this.retainedOutputItems;
        try {
            reserve.call(this, kind, textUnits);
            events.push({ kind, before, denied: false });
        }
        catch (error) {
            events.push({ kind, before, denied: true });
            throw error;
        }
    };
    try {
        empty(f.run({ maxOutputItems: 0 }).result);
        strict_1.default.equal(locations, 0, 'the first semantic DTO location must not be built before its reservation');
        strict_1.default.ok(events.some(event => event.kind === 'node' && event.before === 0 && event.denied));
        events.length = 0;
        empty(f.run({ maxOutputItems: 1 }).result);
        strict_1.default.ok(locations > 0 && locations < count, 'one allowed node must not trigger metadata for the remaining functions');
        strict_1.default.ok(events.some(event => event.kind === 'edge' && event.before === 1 && event.denied));
        const healthy = f.run();
        strict_1.default.equal(healthy.result.complete, true, healthy.result.diagnostics?.join('\n'));
        strict_1.default.equal(healthy.result.nodes.filter(node => node.label === 'Function').length, count);
        strict_1.default.equal(healthy.result.edges.filter(edge => edge.type === 'DEFINES').length, count);
        const expected = canonical(healthy.result);
        strict_1.default.deepEqual(canonical(f.run().result), expected);
        let cancel = false;
        empty(f.run({ shouldCancel: () => cancel, onWork: work => { if (work >= healthy.work / 2)
                cancel = true; } }).result);
        strict_1.default.deepEqual(canonical(f.run().result), expected);
    }
    finally {
        extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput = reserve;
        if (locationDescriptor)
            Object.defineProperty(ts_morph_1.FunctionDeclaration.prototype, 'getStartLineNumber', locationDescriptor);
        else
            Reflect.deleteProperty(ts_morph_1.FunctionDeclaration.prototype, 'getStartLineNumber');
        f.dispose();
    }
});
(0, node_test_1.default)('normal React context and Next route/layout facts retain canonical identities under central output reservations', () => {
    const f = fixture({
        'context.tsx': "import {createContext} from 'react';export function fallback(){}export function save(){}export const Ctx=createContext({save:fallback});",
        'app/page.tsx': "import {useContext} from 'react';import {Ctx,save} from '../context';function Leaf(){useContext(Ctx);return <article/>;}export default function Page(){return <Ctx.Provider value={{save}}><Leaf/></Ctx.Provider>;}",
        'app/layout.tsx': 'export default function Layout({children}){return <section>{children}</section>;}',
        'next.config.mjs': "const base='/ui';const config={basePath:base};export default config;",
    });
    try {
        const healthy = f.run();
        const result = healthy.result;
        const expected = canonical(result);
        const route = result.nodes.find(node => node.properties.full_path === '/ui');
        strict_1.default.ok(route);
        strict_1.default.ok(result.edges.some(edge => edge.sourceQN === route.qualifiedName && edge.targetQN === 'Alloc:app/page.tsx#Page' && edge.type === 'HANDLES'));
        strict_1.default.ok(result.edges.some(edge => edge.sourceQN === route.qualifiedName && edge.targetQN === 'Alloc:app/layout.tsx#Layout' && edge.type === 'RENDERS'));
        const provider = result.nodes.find(node => node.properties.context_kind === 'provider');
        strict_1.default.ok(provider);
        strict_1.default.ok(result.edges.some(edge => edge.sourceQN === 'Alloc:app/page.tsx#Leaf' && edge.targetQN === provider.qualifiedName && edge.properties?.context_kind === 'binding'));
        strict_1.default.ok(result.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === 'Alloc:context.tsx#save' && edge.type === 'CALLS'));
        strict_1.default.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), expected);
        strict_1.default.deepEqual(canonical(f.run().result), expected);
    }
    finally {
        f.dispose();
    }
});
function observeRootCandidates(count, action) {
    const filter = Array.prototype.filter, has = Set.prototype.has;
    let visits = 0;
    const root = (value) => typeof value === 'string' && /^apps\/r\d+$/.test(value);
    Array.prototype.filter = function (...args) {
        if (this.length === count && root(this[0]) && this.every(root))
            visits += this.length;
        return filter.apply(this, args);
    };
    Set.prototype.has = function (value) {
        if (this.size === count && root(this.values().next().value))
            visits++;
        return has.call(this, value);
    };
    try {
        return { value: action(), visits };
    }
    finally {
        Array.prototype.filter = filter;
        Set.prototype.has = has;
    }
}
(0, node_test_1.default)('many Next roots/pages use ancestor lookups rather than copying every root for every page', context => {
    const measure = (count) => {
        const files = Object.fromEntries(Array.from({ length: count }, (_, index) => {
            const id = String(index).padStart(4, '0');
            return [[`apps/r${id}/app/page.jsx`, page], [`apps/r${id}/next.config.mjs`, `export default {basePath:'/ui/${id}'};`]];
        }).flat());
        const f = fixture(files);
        try {
            const observed = observeRootCandidates(count, () => f.run());
            strict_1.default.equal(observed.value.result.complete, true, observed.value.result.diagnostics?.join('\n'));
            strict_1.default.equal(observed.value.result.nodes.filter(node => node.properties.route_kind === 'ui').length, count);
            strict_1.default.ok(observed.visits <= count * 10, `root candidate visits must be proportional: ${observed.visits} for ${count} roots/pages`);
            return { work: observed.value.work, visits: observed.visits };
        }
        finally {
            f.dispose();
        }
    };
    const smaller = measure(100), larger = measure(300);
    strict_1.default.ok(larger.work <= smaller.work * 4, `3x roots/pages cannot require quadratic lookup work: ${smaller.work} -> ${larger.work}`);
    context.diagnostic(`100 roots: ${smaller.work} work/${smaller.visits} candidate visits; 300 roots: ${larger.work}/${larger.visits}`);
});
(0, node_test_1.default)('ancestor-indexed Next roots preserve root, nested app basePath and layout selection', () => {
    const f = fixture({
        'next.config.mjs': "export default {basePath:'/root'};", 'app/page.jsx': page,
        'apps/inner/next.config.cjs': "module.exports={basePath:'/nested'};", 'apps/inner/src/app/(group)/[id]/page.jsx': page,
        'apps/inner/src/app/layout.jsx': 'export default function Layout({children}){return <main>{children}</main>;}',
        'apps/inner/deep/next.config.mjs': "export default {basePath:'/deep'};", 'apps/inner/deep/app/page.jsx': page,
    });
    try {
        const result = f.run().result;
        canonical(result);
        const routes = result.nodes.filter(node => node.properties.route_kind === 'ui');
        strict_1.default.deepEqual(routes.map(node => [node.properties.app_root, node.properties.full_path]).sort(), [['.', '/root'], ['apps/inner', '/nested/[id]'], ['apps/inner/deep', '/deep']]);
        const nested = routes.find(node => node.properties.app_root === 'apps/inner');
        strict_1.default.ok(result.edges.some(edge => edge.sourceQN === nested.qualifiedName && edge.targetQN === 'Alloc:apps/inner/src/app/layout.jsx#Layout' && edge.type === 'RENDERS'));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('bounded Next config alias folding retains normal literals, rejects cycles and refuses excessive depth safely', () => {
    const run = (tail) => {
        const f = fixture({ 'app/page.jsx': page, 'next.config.mjs': tail });
        try {
            return f.run().result;
        }
        finally {
            f.dispose();
        }
    };
    const normal = run("const base='/alias';const config={basePath:base};const alias=config;export default alias;");
    canonical(normal);
    strict_1.default.ok(normal.nodes.some(node => node.properties.full_path === '/alias'));
    const cyclic = run('const first=second;const second=first;export default first;');
    strict_1.default.equal(cyclic.complete, false);
    strict_1.default.ok(!cyclic.nodes.some(node => node.properties.route_kind === 'ui'));
    const declarations = ["const c0={basePath:'/too-deep'};", ...Array.from({ length: 150 }, (_, index) => `const c${index + 1}=c${index};`), 'export default c150;'];
    const deep = run(declarations.join('\n'));
    empty(deep);
    strict_1.default.ok(!deep.diagnostics?.some(message => /RangeError|Maximum call stack/i.test(message)), deep.diagnostics?.join('\n'));
});
(0, node_test_1.default)('extended tsconfig JSON shares AST retention and cannot translate budget refusal into partial success', () => {
    const rootConfig = JSON.stringify({ extends: './configs/base.json', include: ['src/**/*.ts', 'lib/**/*.ts'] });
    const baseConfig = JSON.stringify({ compilerOptions: { baseUrl: '..', paths: { '@lib/*': ['lib/*'] }, target: 'ES2020' } });
    const entry = "import {helper} from '@lib/helper';export function App(){return helper();}";
    const helper = 'export function helper(){return 1;}';
    const f = fixture({ 'tsconfig.json': rootConfig, 'configs/base.json': baseConfig, 'src/entry.ts': entry, 'lib/helper.ts': helper });
    const json = ts.parseJsonText('tsconfig.json', rootConfig);
    const pending = [json];
    let rootNodes = 0;
    while (pending.length) {
        const node = pending.pop();
        rootNodes++;
        ts.forEachChild(node, child => { pending.push(child); });
    }
    const sourceNodes = rawNodes('entry.ts', entry) + rawNodes('helper.ts', helper);
    const read = boundedTextReader_1.BoundedTextReader.prototype.read, reserve = extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput;
    let baseReads = 0, successfulOutputs = 0;
    boundedTextReader_1.BoundedTextReader.prototype.read = function (...args) {
        if (path.resolve(args[0]) === path.join(f.root, 'configs/base.json'))
            baseReads++;
        return read.apply(this, args);
    };
    extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput = function (kind, textUnits = 0) {
        reserve.call(this, kind, textUnits);
        successfulOutputs++;
    };
    try {
        // The root JSON's checked parse and the compiler's hidden parse both fit; extension parsing does not.
        const observed = observeParsers(() => f.run({ maxAstNodes: sourceNodes + rootNodes * 2 + 1 }));
        empty(observed.value.result);
        strict_1.default.equal(observed.counts.other, 2);
        strict_1.default.ok(baseReads > 0, 'the refusal must actually enter the admitted extends JSON host');
        strict_1.default.equal(successfulOutputs, 0, 'a poisoned compiler-config read cannot emit later workspace or semantic metadata');
        const healthy = f.run();
        const expected = canonical(healthy.result);
        strict_1.default.ok(healthy.result.resolvedImports.some(item => item.importedNamespace === '@lib/helper' && item.resolvedFilePath === 'lib/helper.ts'));
        strict_1.default.ok(healthy.result.edges.some(edge => edge.sourceQN === 'Alloc:src/entry.ts#App' && edge.targetQN === 'Alloc:lib/helper.ts#helper' && edge.type === 'CALLS'));
        strict_1.default.deepEqual(canonical(f.run().result), expected);
    }
    finally {
        boundedTextReader_1.BoundedTextReader.prototype.read = read;
        extractionWork_1.ExtractionWorkBudget.prototype.reserveOutput = reserve;
        f.dispose();
    }
});
(0, node_test_1.default)('standalone same-directory source discovery reuses marker inputs and directory probes', context => {
    const count = 100;
    const files = { 'app/page.jsx': page, 'next.config.mjs': "export default {basePath:'/ui'};" };
    for (let index = 0; index < count; index++)
        files[`part${String(index).padStart(4, '0')}.ts`] = `export function F${index}(){return 1;}`;
    const f = fixture(files);
    const includes = Array.prototype.includes, exists = boundedTextReader_1.BoundedTextReader.prototype.exists;
    const marker = /(?:^|\/)(?:package\.json|angular\.json|tsconfig\.json|next\.config\.(?:ts|js|mjs|cjs))$/;
    let inputVisits = 0, probes = 0;
    Array.prototype.includes = function (...args) {
        if (this.length >= count && typeof this[0] === 'string' && /^(?:part\d+\.ts|app\/page\.jsx)$/.test(this[0]) && typeof args[0] === 'string' && marker.test(args[0]))
            inputVisits += this.length;
        return includes.apply(this, args);
    };
    boundedTextReader_1.BoundedTextReader.prototype.exists = function (file) {
        if (marker.test(path.relative(f.root, file).replace(/\\/g, '/')))
            probes++;
        return exists.call(this, file);
    };
    try {
        let work = 0;
        const result = (0, extractor_1.extractProject)('Alloc', f.root, path.join(f.root, 'tsconfig.json'), () => { }, undefined, undefined, { onWork: value => { work = value; } });
        canonical(result);
        strict_1.default.equal(result.nodes.filter(node => node.label === 'Function' && /^F\d+$/.test(node.name)).length, count);
        strict_1.default.ok(result.nodes.some(node => node.properties.full_path === '/ui'), 'standalone static Next marker detection must remain active');
        strict_1.default.ok(inputVisits <= count * 5, `input membership must not rescan every source: ${inputVisits}`);
        strict_1.default.ok(probes <= 40, `same directories must not probe markers for each source: ${probes}`);
        context.diagnostic(`100 same-directory sources: ${work} work, ${inputVisits} input-list visits, ${probes} marker probes`);
    }
    finally {
        Array.prototype.includes = includes;
        boundedTextReader_1.BoundedTextReader.prototype.exists = exists;
        f.dispose();
    }
});
(0, node_test_1.default)('unmatched wildcard package exports spend bounded work before scanning all pattern/source pairs', context => {
    const patterns = Object.fromEntries(Array.from({ length: 100 }, (_, index) => [`./never-match-${index}/*`, `./dist/never-match-${index}/*.js`]));
    const files = {
        'package.json': JSON.stringify({ name: '@fixture/unmatched', exports: patterns }),
        'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: 'src', outDir: 'dist' }, include: ['src/**/*'] }),
    };
    for (let index = 0; index < 100; index++)
        files[`src/value${index}.ts`] = `export const value${index}=0;`;
    const f = fixture(files);
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
    for (const [file, text] of Object.entries(files))
        if (file.endsWith('.ts'))
            project.createSourceFile(path.join(f.root, file), text);
    const scope = new discoveryScope_1.DiscoveryScope(f.root, Object.keys(files));
    const startsWith = String.prototype.startsWith;
    let comparisons = 0;
    String.prototype.startsWith = function (search, position) {
        if (search.includes('never-match-'))
            comparisons++;
        return startsWith.call(this, search, position);
    };
    try {
        const budget = new extractionWork_1.ExtractionWorkBudget({ maxWork: 150000 });
        strict_1.default.throws(() => (0, workspaceResolver_1.analyzeWorkspaceImports)('Alloc', f.root, path.join(f.root, 'tsconfig.json'), project.getSourceFiles(), () => { }, scope, new boundedTextReader_1.BoundedTextReader(f.root, file => scope.includesFile(file)), budget), error => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'work_limit');
        strict_1.default.ok(comparisons > 0 && comparisons < 10000, `refuse inside the pair scan, not after all 10000 pairs: ${comparisons}`);
        context.diagnostic(`unmatched 100x100 wildcard scan refused after ${comparisons} prefix comparisons and ${budget.used} work`);
        comparisons = 0;
        empty(f.run({ maxWork: 150000 }).result);
        strict_1.default.ok(comparisons < 10000);
    }
    finally {
        String.prototype.startsWith = startsWith;
        f.dispose();
    }
    const normal = fixture({
        'package.json': JSON.stringify({ name: '@fixture/match', exports: { './widgets/*': './dist/*.js' } }),
        'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: 'src', outDir: 'dist' }, include: ['src/**/*'] }),
        'src/a.ts': 'export function A(){return 1;}',
    });
    try {
        const result = normal.run().result;
        canonical(result);
        const callable = result.nodes.find(node => node.qualifiedName === 'Alloc:src/a.ts#A');
        strict_1.default.ok(callable);
        strict_1.default.ok(callable.properties.npm_exports.some(item => item.import_path === '@fixture/match/widgets/a' && item.export_name === 'A'));
    }
    finally {
        normal.dispose();
    }
});
