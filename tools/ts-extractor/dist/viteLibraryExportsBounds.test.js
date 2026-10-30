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
const ts_morph_1 = require("ts-morph");
const viteLibraryExports_1 = require("./viteLibraryExports");
const extractor_1 = require("./extractor");
const packageRoot = '/fixture/ui';
const entry = `${packageRoot}/src/index.ts`;
const output = `${packageRoot}/dist/ui.es.js`;
const library = `const library={entry:'src/index.ts',fileName:()=> 'ui.es.js'};`;
const configuration = `import {defineConfig} from 'vite';\n${library}\nexport default defineConfig({build:{lib:library}});`;
function fixture(text = configuration) {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
        compilerOptions: { noResolve: true, types: [] } });
    const config = project.createSourceFile(`${packageRoot}/vite.config.ts`, text);
    const index = project.createSourceFile(entry, 'export const Panel=()=>null;');
    return new Map([[config.getFilePath(), config], [index.getFilePath(), index]]);
}
function reverseAliases(count) {
    const aliases = Array.from({ length: count }, (_, offset) => {
        const number = offset + 1;
        return `a${number}=${number === 1 ? 'library' : `a${number - 1}`}`;
    });
    return `import {defineConfig} from 'vite';\n${library}\nconst ${aliases.join(',')};\na${count}.entry='src/changed.ts';\nexport default defineConfig({build:{lib:library}});`;
}
function refusal(action, reason) {
    let error;
    try {
        action();
    }
    catch (caught) {
        error = caught;
    }
    assert_1.strict.ok(error instanceof viteLibraryExports_1.ViteLibraryAnalysisLimitError);
    assert_1.strict.equal(error.code, 'vite_library_analysis_incomplete');
    assert_1.strict.equal(error.reason, reason);
    assert_1.strict.ok(Number.isFinite(error.work));
    assert_1.strict.ok(Number.isFinite(error.limit));
    return error;
}
(0, node_test_1.test)('reverse 20k alias write closure is deterministic linear work and cannot expose mutated Vite output', () => {
    const count = 20000;
    const text = reverseAliases(count);
    const measure = (maxWork) => {
        let work = 0;
        const sources = fixture(text);
        const result = (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, { maxWork, onWork: value => { work = value; } });
        assert_1.strict.deepEqual(result, []);
        assert_1.strict.ok(work > count, `work ${work} must include the alias graph`);
        assert_1.strict.ok(work <= count * 60 + 5000, `work ${work} must remain linear in ${count} aliases`);
        return work;
    };
    assert_1.strict.equal(measure(2000000), measure(), 'the default budget handles 20k aliases with deterministic linear work');
});
(0, node_test_1.test)('repeated output candidates share a Vite summary and cumulative source-map work budget', () => {
    const sources = fixture();
    let work = 0;
    const control = { maxWork: 10000, onWork: (value) => { work = value; } };
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, control), [entry]);
    const initial = work;
    for (let index = 0; index < 30; index++)
        assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, `${packageRoot}/dist/wrong-${index}.js`, () => { }, control), []);
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, control), [entry]);
    assert_1.strict.ok(work >= initial, 'onWork is cumulative for the source map');
    const cachedCallBound = 31 * (packageRoot.length * 8 + output.length * 3 + 256);
    assert_1.strict.ok(work - initial <= cachedCallBound, `repeated candidates must reuse analysis, consumed ${work - initial}`);
});
(0, node_test_1.test)('work-limit refusal poisons a run and cannot be reset by a later candidate or larger limit', () => {
    const sources = fixture();
    const error = refusal(() => (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, { maxWork: 1 }), 'work_limit');
    assert_1.strict.ok(error.work >= 0 && error.work <= error.limit, 'reported work is the consumed budget before the refused charge');
    refusal(() => (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, `${packageRoot}/dist/other.js`, () => { }, { maxWork: 1000000 }), 'work_limit');
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, new Map(sources), output, () => { }, { maxWork: 1000000 }), [entry], 'a new analysis receives a fresh budget');
});
(0, node_test_1.test)('cancellation is observed on cached summaries and poisons every subsequent query in that run', () => {
    const sources = fixture();
    let cancelled = false;
    const control = { maxWork: 10000, shouldCancel: () => cancelled };
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, control), [entry]);
    cancelled = true;
    refusal(() => (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, control), 'cancelled');
    cancelled = false;
    refusal(() => (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, sources, output, () => { }, control), 'cancelled');
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, new Map(sources), output, () => { }, control), [entry]);
});
(0, node_test_1.test)('alias cycles terminate conservatively while normal @ui Vite path aliases retain their source mapping', () => {
    const cyclic = fixture(`import {defineConfig} from 'vite'; const first=second; const second=first; second.entry='changed'; export default defineConfig(first);`);
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, cyclic, output, () => { }, { maxWork: 10000 }), []);
    const normal = fixture(`import {resolve as locate} from 'node:path'; import {defineConfig as config} from 'vite';
    const source=locate(import.meta.dirname,'src/index.ts');
    const library={entry:source,fileName:()=> 'ui.es.js'};
    export default config({build:{lib:library}});`);
    assert_1.strict.deepEqual((0, viteLibraryExports_1.viteLibrarySource)(packageRoot, normal, output, () => { }, { maxWork: 10000 }), [entry]);
});
(0, node_test_1.test)('generic project extraction preserves incomplete status when static Vite analysis exhausts its work budget', () => {
    // Trigger the real typed error with a tiny trusted budget, then inject that error at the
    // resolver seam. This covers persistence-facing failure behavior without executing a large
    // adversarial config through unrelated extraction passes.
    const captured = refusal(() => (0, viteLibraryExports_1.viteLibrarySource)(packageRoot, fixture(), output, () => { }, { maxWork: 1 }), 'work_limit');
    const module = require('./viteLibraryExports');
    const original = module.viteLibrarySource;
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-vite-budget-boundary-'));
    try {
        fs.mkdirSync(path.join(root, 'src'));
        fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: '@fixture/ui', main: './dist/ui.es.js' }));
        fs.writeFileSync(path.join(root, 'vite.config.ts'), configuration);
        fs.writeFileSync(path.join(root, 'src/index.ts'), 'export const Panel=()=>null;');
        let calls = 0;
        module.viteLibrarySource = () => { calls++; throw captured; };
        const result = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, ['package.json', 'vite.config.ts', 'src/index.ts']);
        assert_1.strict.ok(calls > 0, 'fixture must reach Vite package-entry resolution');
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.deepEqual(result.nodes, []);
        assert_1.strict.deepEqual(result.edges, []);
        assert_1.strict.deepEqual(result.processedFiles, []);
        assert_1.strict.ok(result.diagnostics?.some(message => /Vite.*work|vite_library_analysis_incomplete/.test(message)), result.diagnostics?.join('\n'));
    }
    finally {
        module.viteLibrarySource = original;
        fs.rmSync(root, { recursive: true, force: true });
    }
});
