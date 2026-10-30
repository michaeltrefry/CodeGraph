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
const semanticModel_1 = require("./semanticModel");
const extractionWork_1 = require("./extractionWork");
const npmNames = (node) => (node.properties.npm_exports ?? []).map(item => item.export_name);
const corpus = (count) => Array.from({ length: count }, (_, index) => `export const E${index}=()=>0;`).join('\n')
    + '\nconst local={key:0}; const alias=local; alias.key=1;';
function extractCorpus(count) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-export-whole-work-'));
    const source = corpus(count);
    try {
        fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: '@fixture/ui', exports: './index.ts' }));
        fs.writeFileSync(path.join(root, 'index.ts'), source);
        let work = 0;
        const result = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, ['package.json', 'index.ts'], undefined, { onWork: value => { work = value; } });
        strict_1.default.equal(result.complete, true, result.diagnostics?.join('\n'));
        strict_1.default.deepEqual(result.processedFiles, ['index.ts']);
        strict_1.default.equal(result.nodes.filter(node => npmNames(node).length).length, count);
        strict_1.default.deepEqual(npmNames(result.nodes.find(node => node.qualifiedName === `UI:index.ts#E${count - 1}`)), [`E${count - 1}`]);
        strict_1.default.ok(work > count && work < extractionWork_1.DEFAULT_EXTRACTION_WORK_LIMIT, `work=${work}, exports=${count}`);
        return work;
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}
(0, node_test_1.default)('whole extraction completes the 17k short exported-const corpus under default source and semantic work limits', context => {
    const source = corpus(17000);
    // This is the reported ~448 KB adversarial family, admitted by the ordinary 512 KiB file cap.
    strict_1.default.ok(Buffer.byteLength(source) >= 447000 && Buffer.byteLength(source) < 449000);
    strict_1.default.ok(source.length < 512 * 1024);
    const smaller = extractCorpus(4250);
    const larger = extractCorpus(17000);
    strict_1.default.equal(extractCorpus(17000), larger, 'fresh equivalent whole-project runs consume deterministic work');
    strict_1.default.ok(larger <= smaller * 6, `4x declarations must not cause quadratic export/member scans: ${smaller} -> ${larger}`);
    context.diagnostic(`4250 exports: ${smaller} work; 17000 exports: ${larger}; default allowance: ${extractionWork_1.DEFAULT_EXTRACTION_WORK_LIMIT}; remaining: ${extractionWork_1.DEFAULT_EXTRACTION_WORK_LIMIT - larger}`);
});
(0, node_test_1.default)('factory return AST caching preserves lexical symbols and does not rescan large bodies on repeated export/member queries', context => {
    const measureCachedQueries = (locals) => {
        const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
            compilerOptions: { noResolve: true, types: [] } });
        const filler = Array.from({ length: locals }, (_, index) => `const filler${index}=0;`).join('\n');
        const file = project.createSourceFile('/repo/index.ts', `
      function Page(){return 1;} function Other(){return 2;}
      function makeApi(){${filler}
        function unrelated(){return Other;}
        return {View:Page};
      }
      const rootApi=makeApi(); export {rootApi as RootApi};
      function consumer(){
        const Page=()=>3;
        const makeLocal=()=>({View:Page});
        return makeLocal();
      }
      const localApi=consumer(); export {localApi as LocalApi};
      const changed={View:Page}; const changedAlias=changed; changedAlias.View=Other;
    `);
        const diagnostics = [];
        const model = new semanticModel_1.SemanticModel('UI', '/repo', [file], [], message => diagnostics.push(message));
        const rootBinding = file.getVariableDeclarationOrThrow('rootApi');
        const changed = file.getVariableDeclarationOrThrow('changed');
        const viewTarget = (exportName) => {
            const resolved = model.resolve(model.exported(file, exportName));
            strict_1.default.ok(resolved && ts_morph_1.Node.isObjectLiteralExpression(resolved), `${exportName} must retain its unique returned object`);
            const property = resolved.getPropertyOrThrow('View');
            strict_1.default.ok(ts_morph_1.Node.isPropertyAssignment(property));
            return model.target(property.getInitializer());
        };
        strict_1.default.equal(viewTarget('RootApi'), 'UI:index.ts#Page', 'nested unrelated callable returns cannot contaminate the factory');
        strict_1.default.equal(viewTarget('LocalApi'), 'UI:index.ts#consumer.Page', 'same-spelling declarations must retain their lexical binding');
        strict_1.default.equal(model.hasMemberWrites(changed), true);
        strict_1.default.equal(model.hasMemberWrites(rootBinding), false, 'an unrelated object write cannot poison the factory result');
        const before = model.work.used;
        for (let index = 0; index < 150; index++) {
            strict_1.default.equal(viewTarget('RootApi'), 'UI:index.ts#Page');
            strict_1.default.equal(viewTarget('LocalApi'), 'UI:index.ts#consumer.Page');
            strict_1.default.equal(model.hasMemberWrites(rootBinding), false);
            strict_1.default.equal(model.hasMemberWrites(changed), true);
        }
        const delta = model.work.used - before;
        strict_1.default.ok(!diagnostics.some(message => message.startsWith('functionReturnAmbiguity:')), diagnostics.join('\n'));
        return delta;
    };
    const small = measureCachedQueries(0);
    const large = measureCachedQueries(2000);
    strict_1.default.equal(large, small, 'after warmup, repeated queries must not depend on factory body or mutation-index size');
    context.diagnostic(`150 cached lookup rounds: empty body ${small} work; 2000 locals ${large}`);
});
