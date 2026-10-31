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
const packageExports_1 = require("./packageExports");
const extractionWork_1 = require("./extractionWork");
const extractor_1 = require("./extractor");
const dto = (name, qualifiedName, properties = {}) => ({
    label: 'Function', name, qualifiedName, filePath: 'index.ts', startLine: 1, endLine: 1, properties,
});
const npmNames = (node) => (node.properties.npm_exports ?? []).map(item => item.export_name);
(0, node_test_1.default)('17k public callable constants use one declaration and mutation index with deterministic proportional work', context => {
    const count = 17000;
    const source = Array.from({ length: count }, (_, index) => `export const E${index}=()=>0;`).join('\n')
        + '\nconst local={key:0}; const alias=local; alias.key=1;';
    strict_1.default.ok(source.length < 512 * 1024);
    const run = () => {
        const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true, compilerOptions: { noResolve: true, types: [] } });
        const file = project.createSourceFile('/repo/index.ts', source);
        const model = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], () => { });
        const nodes = [dto('UI', 'npm:UI', { module_kind: 'typescript_workspace_package', package_name: '@sceneworks/ui',
                package_entrypoints: [{ import_path: '@sceneworks/ui', file_path: 'index.ts' }] })];
        for (const [callable, qn] of model.callables)
            nodes.push(dto(ts_morph_1.Node.isArrowFunction(callable) ? callable.getParentOrThrow().getText().split('=')[0].trim() : '', qn));
        (0, packageExports_1.addNpmExportMetadata)(model, nodes);
        strict_1.default.equal(nodes.filter(node => npmNames(node).length).length, count);
        strict_1.default.deepEqual(npmNames(nodes.find(node => node.qualifiedName === 'UI:index.ts#E16999')), ['E16999']);
        const local = file.getVariableDeclarationOrThrow('local');
        strict_1.default.equal(model.hasMemberWrites(local), true);
        const before = model.work.used;
        for (let index = 0; index < 100; index++) {
            strict_1.default.equal(model.exported(file, `E${index}`), file.getVariableDeclarationOrThrow(`E${index}`));
            strict_1.default.equal(model.hasMemberWrites(local), true);
        }
        strict_1.default.ok(model.work.used - before < 30000, 'cached queries must not rescan the file or mutation graph');
        strict_1.default.ok(model.work.used < count * 1000, `work=${model.work.used}`);
        strict_1.default.ok(model.work.used < extractionWork_1.DEFAULT_EXTRACTION_WORK_LIMIT);
        return model.work.used;
    };
    const work = run();
    strict_1.default.equal(work, run(), 'same admitted source must have deterministic work accounting');
    context.diagnostic(`17000 exports: ${work}/${extractionWork_1.DEFAULT_EXTRACTION_WORK_LIMIT} charged work`);
});
(0, node_test_1.default)('star cycles retain independent candidates and cached member surfaces do not leak prefixes or export aliases', () => {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, compilerOptions: { noResolve: true, types: [] } });
    const a = project.createSourceFile('/repo/a.ts', "export * from './b'; export const A=()=>1;");
    const b = project.createSourceFile('/repo/b.ts', "export * from './a'; export const B=()=>2;");
    const entry = project.createSourceFile('/repo/index.ts', "export * from './a'; const value={Inner:()=>1}; export {value as First,value as Second}; export const Wrapped={Nested:value};");
    const imports = [[a, './b', 'b.ts'], [b, './a', 'a.ts'], [entry, './a', 'a.ts']].map(([file, specifier, target]) => ({
        fileQN: `UI:${file.getBaseName()}`, filePath: file.getBaseName(), importedNamespace: specifier,
        importKind: 'export', classification: 'local_file', resolvedFilePath: target,
    }));
    const model = new semanticModel_1.SemanticModel('UI', '/repo', [a, b, entry], imports, () => { });
    strict_1.default.deepEqual([...model.publicExportNames(a)].sort(), ['A', 'B']);
    strict_1.default.equal(model.exported(a, 'B'), b.getVariableDeclarationOrThrow('B'));
    strict_1.default.equal(model.exported(b, 'A'), a.getVariableDeclarationOrThrow('A'));
    const nodes = [dto('UI', 'npm:UI', { module_kind: 'typescript_workspace_package', package_name: '@ui', package_entrypoints: [{ import_path: '@ui', file_path: 'index.ts' }] }),
        ...[...model.callables].map(([, qn]) => dto(qn, qn))];
    (0, packageExports_1.addNpmExportMetadata)(model, nodes);
    const inner = nodes.find(node => node.qualifiedName === 'UI:index.ts#value.Inner');
    strict_1.default.deepEqual(inner.properties.npm_exports.map(item => [item.export_name, item.member_path]), [['First', 'Inner'], ['Second', 'Inner'], ['Wrapped', 'Nested.Inner'], ['value', 'Inner']]);
});
(0, node_test_1.default)('shared work limits and cancellation poison the run and fresh models start with a fresh allowance', () => {
    let cancel = false;
    const budget = new extractionWork_1.ExtractionWorkBudget({ maxWork: 3, shouldCancel: () => cancel });
    budget.charge(3);
    let refused;
    strict_1.default.throws(() => budget.charge(), error => { refused = error; return error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'work_limit'; });
    strict_1.default.throws(() => budget.charge(0), error => error === refused);
    const next = new extractionWork_1.ExtractionWorkBudget({ shouldCancel: () => cancel });
    next.charge(1);
    cancel = true;
    strict_1.default.throws(() => next.charge(0), error => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'cancelled');
    cancel = false;
    strict_1.default.throws(() => next.charge(0), extractionWork_1.ExtractionWorkLimitError);
    strict_1.default.doesNotThrow(() => new extractionWork_1.ExtractionWorkBudget().charge(1));
});
(0, node_test_1.default)('whole project work refusal and synchronous cancellation return no partial graph or processed files', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-export-work-'));
    try {
        fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: '@ui', exports: './index.ts' }));
        fs.writeFileSync(path.join(root, 'index.ts'), 'export const Button=()=>1;');
        const scope = ['package.json', 'index.ts'];
        for (const control of [{ maxWork: 1 }, { shouldCancel: () => true }]) {
            const result = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope, undefined, control);
            strict_1.default.equal(result.complete, false);
            strict_1.default.deepEqual(result.nodes, []);
            strict_1.default.deepEqual(result.edges, []);
            strict_1.default.deepEqual(result.processedFiles, []);
            strict_1.default.deepEqual(result.resolvedImports, []);
            strict_1.default.ok(result.diagnostics?.some(message => message.includes('semantic analysis work limit or cancellation')));
        }
        const healthy = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.equal(healthy.complete, true);
        strict_1.default.deepEqual(npmNames(healthy.nodes.find(node => node.name === 'Button')), ['Button']);
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
});
