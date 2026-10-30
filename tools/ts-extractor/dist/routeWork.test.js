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
const discoveryScope_1 = require("./discoveryScope");
const routeAnalysis_1 = require("./routeAnalysis");
const extractor_1 = require("./extractor");
const extractionWork_1 = require("./extractionWork");
function source(depth, segment = 'a', spreads = false, view = '') {
    const lines = ["import {createBrowserRouter} from 'react-router-dom';"];
    if (view)
        lines.push('function Page(){return null;}', `const view=${view};`);
    lines.push(`const r0=[{path:'leaf'${view ? ',element:view' : ''}}];`);
    for (let level = 1; level <= depth; level++)
        lines.push(spreads ? `const r${level}=[...r${level - 1},...r${level - 1}];`
            : `const r${level}=[{path:'${segment}',children:r${level - 1}},{path:'${segment === 'a' ? 'b' : 'b'.repeat(segment.length)}',children:r${level - 1}}];`);
    lines.push(`createBrowserRouter(r${depth});`);
    return lines.join('\n');
}
function fixture(text) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-route-work-'));
    fs.writeFileSync(path.join(root, 'routes.tsx'), text);
    const direct = (control = { maxWork: 200000000 }) => {
        const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
        const file = project.createSourceFile(path.join(root, 'routes.tsx'), text);
        const model = new semanticModel_1.SemanticModel('Routes', root, [file], [], () => { }, control);
        const before = model.work.used;
        const nodes = [];
        const edges = [];
        let status, error;
        try {
            status = (0, routeAnalysis_1.analyzeRoutes)(model, nodes, edges, new discoveryScope_1.DiscoveryScope(root, ['routes.tsx']), () => { });
        }
        catch (caught) {
            error = caught;
        }
        return { model, nodes, edges, status, error, work: model.work.used - before };
    };
    const whole = (control) => (0, extractor_1.extractProject)('Routes', root, path.join(root, 'tsconfig.json'), () => { }, ['routes.tsx'], undefined, control);
    return { direct, whole, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function refuse(error, reason) {
    strict_1.default.ok(error instanceof extractionWork_1.ExtractionWorkLimitError);
    strict_1.default.equal(error.code, 'extraction_work_incomplete');
    strict_1.default.equal(error.reason, reason);
    strict_1.default.ok(error.work <= error.limit);
}
(0, node_test_1.default)('shared child trees charge expanded route text and evidence rather than only compact source size', context => {
    for (const depth of [8, 12]) {
        const text = source(depth);
        const f = fixture(text);
        try {
            const result = f.direct();
            strict_1.default.equal(result.error, undefined);
            strict_1.default.equal(result.status?.complete, true);
            strict_1.default.equal(result.nodes.length, 3 * 2 ** depth - 2);
            const outputText = result.nodes.reduce((size, node) => size + node.qualifiedName.length + String(node.properties.full_path).length, 0);
            strict_1.default.ok(result.work > outputText, `work ${result.work} must cover expanded path/QN text ${outputText}`);
            context.diagnostic(`depth ${depth}: ${text.length} source chars, ${result.nodes.length} routes, ${outputText} path/QN chars, ${result.work} route work`);
        }
        finally {
            f.dispose();
        }
    }
});
(0, node_test_1.default)('route output allocation refuses a low shared allowance and a fresh extraction recovers all paths', () => {
    const f = fixture(source(8));
    try {
        const limited = f.direct({ maxWork: 100000 });
        refuse(limited.error, 'work_limit');
        strict_1.default.ok(limited.nodes.length > 0 && limited.nodes.length < 766, 'refuse during output expansion before the entire tree is allocated');
        strict_1.default.throws(() => limited.model.work.charge(0), error => error === limited.error, 'a refused route traversal cannot resume its poisoned run');
        const whole = f.whole({ maxWork: 100000 });
        strict_1.default.equal(whole.complete, false);
        strict_1.default.deepEqual(whole.nodes, []);
        strict_1.default.deepEqual(whole.edges, []);
        strict_1.default.deepEqual(whole.processedFiles, []);
        const recovered = f.whole({ maxWork: 200000000 });
        strict_1.default.equal(recovered.complete, true);
        strict_1.default.equal(recovered.nodes.filter(node => node.properties.route_kind === 'ui').length, 766);
        strict_1.default.deepEqual(recovered.processedFiles, ['routes.tsx']);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('shared spread arrays consume allowance before flattened references or route output are materialized', () => {
    const f = fixture(source(12, 'a', true));
    try {
        const limited = f.direct({ maxWork: 50000 });
        refuse(limited.error, 'work_limit');
        strict_1.default.deepEqual(limited.nodes, [], 'the recursive spread must refuse before the flattened root array is returned');
        strict_1.default.deepEqual(limited.edges, []);
        const recovered = f.direct();
        strict_1.default.equal(recovered.error, undefined);
        strict_1.default.equal(recovered.status?.complete, true);
        strict_1.default.equal(recovered.nodes.length, 1);
        strict_1.default.equal(recovered.nodes[0].properties.full_path, '/leaf');
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('long composed path segments are charged in proportion to the amplified output before joining', () => {
    const measure = (segment) => {
        const f = fixture(source(5, segment));
        try {
            const result = f.direct();
            strict_1.default.equal(result.error, undefined);
            strict_1.default.equal(result.status?.complete, true);
            return { work: result.work, text: result.nodes.reduce((size, node) => size + node.qualifiedName.length + String(node.properties.full_path).length, 0), count: result.nodes.length };
        }
        finally {
            f.dispose();
        }
    };
    const short = measure('a');
    const long = measure('a'.repeat(100));
    strict_1.default.equal(long.count, short.count);
    strict_1.default.ok(long.work - short.work >= long.text - short.text, 'path length amplification cannot hide behind the same number of route objects');
});
(0, node_test_1.default)('shared JSX element and Navigate discovery are cached by resolved AST instead of rescanned per expanded route', () => {
    const measure = (depth, heavy) => {
        const numbers = Array.from({ length: 2000 }, (_, index) => String(index)).join(',');
        const view = heavy ? `<section>{[${numbers}]}<Page/></section>` : '<Page/>';
        const f = fixture(source(depth, 'a', false, view));
        try {
            const result = f.direct();
            strict_1.default.equal(result.error, undefined);
            strict_1.default.equal(result.status?.complete, true);
            strict_1.default.equal(result.edges.filter(edge => edge.type === 'HANDLES' && edge.targetQN.endsWith('#Page')).length, 2 ** depth);
            return result.work;
        }
        finally {
            f.dispose();
        }
    };
    const shallowExtra = measure(4, true) - measure(4, false);
    const deepExtra = measure(8, true) - measure(8, false);
    strict_1.default.equal(deepExtra, shallowExtra, 'the same 2k-literal JSX tree is scanned once irrespective of route reuse');
});
(0, node_test_1.default)('cancellation during route expansion discards partial whole-project output', () => {
    const f = fixture(source(8));
    let cancelled = false;
    try {
        const result = f.whole({ maxWork: 200000000, shouldCancel: () => cancelled, onWork: work => { if (work >= 100000)
                cancelled = true; } });
        strict_1.default.equal(result.complete, false);
        strict_1.default.deepEqual(result.nodes, []);
        strict_1.default.deepEqual(result.edges, []);
        strict_1.default.deepEqual(result.processedFiles, []);
        const recovered = f.whole({ maxWork: 200000000 });
        strict_1.default.equal(recovered.complete, true);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.default)('source-owner property amplification is included in route output allowance', () => {
    const measure = (name) => {
        const [imports, ...body] = source(5).split('\n');
        const f = fixture(`${imports}\nfunction ${name}(){\n${body.join('\n')}\n}`);
        try {
            const result = f.direct();
            strict_1.default.equal(result.error, undefined);
            strict_1.default.equal(result.status?.complete, true);
            return { work: result.work, owners: result.nodes.reduce((size, node) => size + String(node.properties.source_owner).length, 0), count: result.nodes.length };
        }
        finally {
            f.dispose();
        }
    };
    const short = measure('Owner');
    const long = measure('Owner'.repeat(200));
    strict_1.default.equal(long.count, short.count);
    strict_1.default.ok(long.work - short.work >= long.owners - short.owners, 'reused lexical owner metadata must be charged for each emitted route');
});
