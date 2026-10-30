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
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const extractor_1 = require("./extractor");
function fixture(files, check) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-npm-exports-'));
    try {
        for (const [relative, content] of Object.entries(files)) {
            const full = path.join(root, relative);
            fs.mkdirSync(path.dirname(full), { recursive: true });
            fs.writeFileSync(full, content);
        }
        check(root, Object.keys(files));
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}
const aliases = (node) => node.properties.npm_exports;
(0, node_test_1.default)('public root/subpath aliases and defaults attach to declarations without exporting internal same-name components', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@sceneworks/ui', exports: { '.': './src/index.ts', './button': './src/button.tsx' } }),
        'src/index.ts': "export { default as Button } from './button';",
        'src/button.tsx': 'export default function Primary() { return <button/>; } export function Secondary() { return <i/>; }',
        'src/internal.tsx': 'export function Button() { return <b/>; }',
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.equal(output.complete, true);
        const primary = output.nodes.find(node => node.name === 'Primary');
        strict_1.default.deepEqual(aliases(primary), [
            { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'Button' },
            { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui/button', export_name: 'default' },
        ]);
        strict_1.default.equal(aliases(output.nodes.find(node => node.name === 'Button')), undefined);
        strict_1.default.deepEqual(aliases(output.nodes.find(node => node.name === 'Secondary')), [
            { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui/button', export_name: 'Secondary' },
        ]);
    });
});
(0, node_test_1.default)('conditional runtime alternatives retain multiple declarations and unadmitted alternatives cannot become unique', () => {
    fixture({
        'package.json': JSON.stringify({ name: 'shared-ui', exports: { import: './one.tsx', require: './two.tsx' } }),
        'one.tsx': 'export function Button() { return <button/>; }',
        'two.tsx': 'export function Button() { return <b/>; }',
    }, (root, scope) => {
        const both = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.equal(both.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === 'Button')).length, 2);
        const disk = require('fs');
        const read = disk.readFileSync;
        const forbidden = path.join(root, 'two.tsx');
        let reads = 0;
        disk.readFileSync = ((file, ...args) => {
            if (String(file) === forbidden) {
                reads++;
                throw new Error('unadmitted read');
            }
            return read(file, ...args);
        });
        try {
            const partial = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope.filter(file => file !== 'two.tsx'));
            strict_1.default.equal(partial.nodes.some(node => aliases(node)?.length), false);
            strict_1.default.ok(partial.diagnostics?.some(message => message.startsWith('packageExportUnresolved:')));
            strict_1.default.equal(reads, 0);
        }
        finally {
            disk.readFileSync = read;
        }
    });
});
(0, node_test_1.default)('explicit rootDir/outDir mapping proves generated entry origins and wildcard subpaths remain distinct', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@org/ui', exports: { '.': { import: './dist/index.js', types: './dist/index.d.ts' }, './widgets/*': './dist/widgets/*.js' } }),
        'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: './src', outDir: './dist', jsx: 'react-jsx' }, include: ['src/**/*'] }),
        'src/index.ts': "export { Button } from './widgets/button';",
        'src/widgets/button.tsx': 'export function Button() { return <button/>; }',
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.deepEqual(aliases(output.nodes.find(node => node.name === 'Button')), [
            { package_name: '@org/ui', import_path: '@org/ui', export_name: 'Button' },
            { package_name: '@org/ui', import_path: '@org/ui/widgets/button', export_name: 'Button' },
        ]);
    });
});
