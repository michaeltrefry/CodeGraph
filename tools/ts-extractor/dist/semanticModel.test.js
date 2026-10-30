"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const node_test_1 = __importDefault(require("node:test"));
const strict_1 = __importDefault(require("node:assert/strict"));
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
(0, node_test_1.default)('module lookup indexes source plus specifier and caches admitted hits and misses per analysis', () => {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true });
    const first = project.createSourceFile('/repo/a.ts', 'export const a = 1;');
    const second = project.createSourceFile('/repo/b.ts', 'export const b = 1;');
    const target = project.createSourceFile('/repo/target.ts', 'export const target = 1;');
    const imports = [
        { fileQN: 'Repo:a.ts', filePath: 'a.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'target.ts' },
        { fileQN: 'Repo:b.ts', filePath: 'b.ts', importedNamespace: './shared', importKind: 'static', classification: 'local_file', resolvedFilePath: 'unadmitted.ts' },
    ];
    const model = new semanticModel_1.SemanticModel('Repo', '/repo', [first, second, target], imports, () => { });
    imports.find = () => { throw new Error('module lookup must not scan the import list'); };
    for (let index = 0; index < 10; index++) {
        strict_1.default.equal(model.module(first, './shared'), target);
        strict_1.default.equal(model.module(second, './shared'), undefined);
        strict_1.default.equal(model.module(first, './missing'), undefined);
    }
    const next = new semanticModel_1.SemanticModel('Repo', '/repo', [first, second, target], [
        { ...imports[0], resolvedFilePath: 'b.ts' },
    ], () => { });
    strict_1.default.equal(next.module(first, './shared'), second);
});
