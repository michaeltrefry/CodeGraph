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
(0, node_test_1.default)('a public name missing from one admitted runtime entry never becomes a unique package export', () => {
    fixture({
        'package.json': JSON.stringify({ name: 'shared-ui', exports: { '.': { import: './a.jsx', require: './b.jsx' } } }),
        'a.jsx': 'export function Button() { return <button/>; } export function Shared() { return <i/>; }',
        'b.jsx': 'export function OnlyOther() { return <b/>; } export function Shared() { return <span/>; }',
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.export_name === 'Button')), false);
        strict_1.default.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.export_name === 'OnlyOther')), false);
        // Both runtime implementations remain visible as ambiguous candidates when every entry
        // resolves the public name; the linker must not choose either declaration arbitrarily.
        strict_1.default.equal(output.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === 'Shared')).length, 2);
        strict_1.default.ok(output.diagnostics?.some(message => message.startsWith('packageExportUnresolved: shared-ui#Button')));
    });
});
(0, node_test_1.default)('barrel aliases retain exported context, memo callable and hook declaration identities after framework passes', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@org/ui', exports: './index.ts' }),
        'index.ts': "export { Theme as ThemeAlias, Wrapped as Button, useTools } from './widgets';",
        'widgets.tsx': `import { createContext, memo } from 'react';
      export const Theme = createContext(null);
      export const Wrapped = memo(() => <button/>);
      export const useTools = () => () => 1;`,
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        const publicExport = (name) => output.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === name));
        strict_1.default.equal(publicExport('ThemeAlias').length, 1);
        strict_1.default.equal(publicExport('ThemeAlias')[0].properties.context_kind, 'context');
        strict_1.default.equal(publicExport('Button').length, 1);
        strict_1.default.equal(publicExport('Button')[0].qualifiedName, 'UI:widgets.tsx#Wrapped');
        strict_1.default.equal(publicExport('useTools').length, 1);
        strict_1.default.equal(publicExport('useTools')[0].qualifiedName, 'UI:widgets.tsx#useTools');
    });
});
(0, node_test_1.default)('actual ui Vite library output maps to its admitted public barrel without tsconfig or dist reads', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@sceneworks/ui', type: 'module', exports: { '.': { import: './dist/sceneworks-ui.js' } } }),
        'vite.config.js': `import { resolve as locate } from 'node:path'; import { defineConfig as config } from 'vite';
      const root = import.meta.dirname; const outputName = () => 'sceneworks-ui.js';
      const library = { entry: locate(root, 'src/index.js'), formats: ['es'], fileName: outputName };
      export default config({ build: { lib: library } });`,
        'src/index.js': "export { StatusDot } from './StatusDot.jsx'; export { isAccentId } from './accents.js';",
        'src/StatusDot.jsx': 'export function StatusDot() { return <span/>; }',
        'src/accents.js': "export function isAccentId(value) { return value === 'blue'; }",
    }, (root, scope) => {
        const disk = require('fs');
        const read = disk.readFileSync;
        let forbiddenReads = 0;
        disk.readFileSync = ((file, ...args) => {
            if (String(file).startsWith(path.join(root, 'dist'))) {
                forbiddenReads++;
                throw new Error('dist must never be read');
            }
            return read(file, ...args);
        });
        try {
            const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
            strict_1.default.equal(output.complete, true);
            for (const name of ['StatusDot', 'isAccentId'])
                strict_1.default.deepEqual(aliases(output.nodes.find(node => node.name === name)), [
                    { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: name },
                ]);
            strict_1.default.equal(forbiddenReads, 0);
            const noConfig = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope.filter(file => file !== 'vite.config.js'));
            strict_1.default.equal(noConfig.nodes.some(node => aliases(node)?.length), false);
            fs.writeFileSync(path.join(root, 'vite.config.js'), `import {defineConfig} from 'vite';
        const library={entry:'src/index.js',fileName:()=> 'sceneworks-ui.js'};
        const alias=library; alias.entry='src/other.js'; export default defineConfig({build:{lib:library}});`);
            strict_1.default.equal((0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope).nodes.some(node => aliases(node)?.length), false);
            fs.writeFileSync(path.join(root, 'vite.config.js'), `import {defineConfig} from 'vite';
        export default defineConfig({build:{lib:{entry:'src/index.js',fileName:()=>unknownAtRuntime()}}});`);
            strict_1.default.equal((0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope).nodes.some(node => aliases(node)?.length), false);
        }
        finally {
            disk.readFileSync = read;
        }
    });
});
(0, node_test_1.default)('external imported function aliases, namespaces and barrels retain exact provenance while local shadows do not', () => {
    fixture({
        'package.json': JSON.stringify({ name: 'app', dependencies: { '@sceneworks/ui': '*' } }),
        'barrel.js': "export { isAccentId as checkAccent } from '@sceneworks/ui';",
        'src/app.tsx': `import { isAccentId as valid, StatusDot } from '@sceneworks/ui';
      import * as ui from '@sceneworks/ui'; import {checkAccent} from '../barrel.js';
      const check = valid;
      export function App() { valid('blue'); check('green'); ui.isAccentId('red'); checkAccent('blue'); StatusDot(); return <StatusDot/>; }
      export function Shadow(valid: (value:string)=>boolean) { return valid('blue'); }
      export function NamespaceShadow(ui: any) { return ui.isAccentId('blue'); }`,
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('App', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        const app = output.nodes.find(node => node.name === 'App').qualifiedName;
        const calls = output.edges.filter(edge => edge.type === 'CALLS' && edge.targetQN === 'npm-symbol:@sceneworks/ui#isAccentId');
        strict_1.default.equal(calls.length, 4);
        strict_1.default.ok(calls.every(edge => edge.sourceQN === app));
        strict_1.default.equal(output.nodes.filter(node => node.qualifiedName === 'npm-symbol:@sceneworks/ui#StatusDot').length, 1);
        strict_1.default.deepEqual(output.nodes.find(node => node.qualifiedName === 'npm-symbol:@sceneworks/ui#isAccentId').properties, {
            package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'isAccentId', extraction_owner: 'typescript',
        });
    });
});
(0, node_test_1.default)('static public object component members retain separate export and member identities across packages', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@sceneworks/ui', exports: './index.ts' }),
        'index.ts': "export {Icon} from './icons';",
        'icons.tsx': 'export const Icon = { Model: () => <svg/>, Nested: { Small: () => <b/> } };',
        'consumer.jsx': `import { Icon as Glyphs } from '@other/ui'; import * as shared from '@other/ui';
      export function Screen(){ return <><Glyphs.Model/><shared.Icon.Nested.Small/></>; }`,
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        const model = output.nodes.find(node => node.qualifiedName === 'UI:icons.tsx#Icon.Model');
        strict_1.default.deepEqual(aliases(model), [{ package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'Icon', member_path: 'Model' }]);
        strict_1.default.deepEqual(aliases(output.nodes.find(node => node.qualifiedName === 'UI:icons.tsx#Icon.Nested.Small')), [
            { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'Icon', member_path: 'Nested.Small' },
        ]);
        strict_1.default.ok(output.edges.some(edge => edge.type === 'RENDERS' && edge.targetQN === 'npm-symbol:@other/ui#Icon::Model'));
        const proxy = output.nodes.find(node => node.qualifiedName === 'npm-symbol:@other/ui#Icon::Nested.Small');
        strict_1.default.equal(proxy.properties.export_name, 'Icon');
        strict_1.default.equal(proxy.properties.member_path, 'Nested.Small');
    });
});
(0, node_test_1.default)('written imported members, public object aliases, spreads and computed overrides remain unresolved', () => {
    for (const object of ['const alias = Icon; alias.Model = Other;', 'const alias = Icon; Object.assign(alias, {Model:Other});'])
        fixture({
            'package.json': JSON.stringify({ name: 'app' }),
            'app.jsx': `import {Icon} from '@other/ui'; function Other(){return <b/>} ${object} export function App(){return <Icon.Model/>}`,
        }, (root, scope) => {
            const output = (0, extractor_1.extractProject)('App', root, path.join(root, 'tsconfig.json'), () => { }, scope);
            strict_1.default.equal(output.edges.some(edge => edge.type === 'RENDERS' && edge.targetQN === 'npm-symbol:@other/ui#Icon::Model'), false);
            strict_1.default.ok(output.diagnostics?.some(message => message.startsWith('externalMemberUnresolved:')));
        });
    for (const definition of [
        'export const Icon={Model:()=> <b/>}; const alias=Icon; alias.Model=Other;',
        'export const Icon={Model:()=> <b/>, ...unknown};',
        'export const Icon={Model:()=> <b/>, [key]:Other};',
    ])
        fixture({
            'package.json': JSON.stringify({ name: '@org/ui', exports: './index.jsx' }),
            'index.jsx': `function Other(){return <i/>} ${definition}`,
        }, (root, scope) => {
            const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
            strict_1.default.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.member_path === 'Model')), false);
        });
});
(0, node_test_1.default)('nested exported object aliases check writes before following their initializers', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@org/ui', exports: './index.jsx' }),
        'index.jsx': `function Good(){return <b/>} function Other(){return <i/>}
      const icons={Model:Good}; export const Icon={group:icons}; icons.Model=Other;`,
    }, (root, scope) => {
        const output = (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        strict_1.default.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.member_path === 'group.Model')), false);
        strict_1.default.ok(output.diagnostics?.some(message => message.startsWith('packageExportUnresolved: @org/ui#Icon')));
    });
});
(0, node_test_1.default)('nested imported object aliases preserve valid members but refuse writes through imported aliases', () => {
    fixture({
        'package.json': JSON.stringify({ name: '@org/ui', exports: './index.jsx' }),
        'icons.jsx': 'function Good(){return <b/>} export const icons={Model:Good};',
        'index.jsx': "import {icons as shared} from './icons'; const alias=shared; export const Icon={group:alias};",
    }, (root, scope) => {
        const extract = () => (0, extractor_1.extractProject)('UI', root, path.join(root, 'tsconfig.json'), () => { }, scope);
        const valid = extract();
        strict_1.default.equal(valid.nodes.some(node => aliases(node)?.some(alias => alias.member_path === 'group.Model')), true);
        fs.appendFileSync(path.join(root, 'index.jsx'), '\nfunction Other(){return <i/>} alias.Model=Other;');
        const changed = extract();
        strict_1.default.equal(changed.nodes.some(node => aliases(node)?.some(alias => alias.member_path === 'group.Model')), false);
    });
});
