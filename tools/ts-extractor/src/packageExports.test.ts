import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { extractProject } from './extractor';
import type { GraphNodeDto } from './protocol';

function fixture(files: Record<string, string>, check: (root: string, scope: string[]) => void): void {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-npm-exports-'));
  try {
    for (const [relative, content] of Object.entries(files)) {
      const full = path.join(root, relative); fs.mkdirSync(path.dirname(full), { recursive: true }); fs.writeFileSync(full, content);
    }
    check(root, Object.keys(files));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
}
const aliases = (node: GraphNodeDto) => node.properties.npm_exports as Array<{ package_name: string; import_path: string; export_name: string }> | undefined;

test('public root/subpath aliases and defaults attach to declarations without exporting internal same-name components', () => {
  fixture({
    'package.json': JSON.stringify({ name: '@sceneworks/ui', exports: { '.': './src/index.ts', './button': './src/button.tsx' } }),
    'src/index.ts': "export { default as Button } from './button';",
    'src/button.tsx': 'export default function Primary() { return <button/>; } export function Secondary() { return <i/>; }',
    'src/internal.tsx': 'export function Button() { return <b/>; }',
  }, (root, scope) => {
    const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    assert.equal(output.complete, true);
    const primary = output.nodes.find(node => node.name === 'Primary')!;
    assert.deepEqual(aliases(primary), [
      { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'Button' },
      { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui/button', export_name: 'default' },
    ]);
    assert.equal(aliases(output.nodes.find(node => node.name === 'Button')!), undefined);
    assert.deepEqual(aliases(output.nodes.find(node => node.name === 'Secondary')!), [
      { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui/button', export_name: 'Secondary' },
    ]);
  });
});

test('conditional runtime alternatives retain multiple declarations and unadmitted alternatives cannot become unique', () => {
  fixture({
    'package.json': JSON.stringify({ name: 'shared-ui', exports: { import: './one.tsx', require: './two.tsx' } }),
    'one.tsx': 'export function Button() { return <button/>; }',
    'two.tsx': 'export function Button() { return <b/>; }',
  }, (root, scope) => {
    const both = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    assert.equal(both.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === 'Button')).length, 2);
    const disk = require('fs') as { readFileSync: typeof fs.readFileSync };
    const read = disk.readFileSync; const forbidden = path.join(root, 'two.tsx'); let reads = 0;
    disk.readFileSync = ((file: fs.PathOrFileDescriptor, ...args: unknown[]) => {
      if (String(file) === forbidden) { reads++; throw new Error('unadmitted read'); }
      return (read as (...args: unknown[]) => unknown)(file, ...args);
    }) as typeof fs.readFileSync;
    try {
      const partial = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope.filter(file => file !== 'two.tsx'));
      assert.equal(partial.nodes.some(node => aliases(node)?.length), false);
      assert.ok(partial.diagnostics?.some(message => message.startsWith('packageExportUnresolved:')));
      assert.equal(reads, 0);
    } finally { disk.readFileSync = read; }
  });
});

test('explicit rootDir/outDir mapping proves generated entry origins and wildcard subpaths remain distinct', () => {
  fixture({
    'package.json': JSON.stringify({ name: '@org/ui', exports: { '.': { import: './dist/index.js', types: './dist/index.d.ts' }, './widgets/*': './dist/widgets/*.js' } }),
    'tsconfig.json': JSON.stringify({ compilerOptions: { rootDir: './src', outDir: './dist', jsx: 'react-jsx' }, include: ['src/**/*'] }),
    'src/index.ts': "export { Button } from './widgets/button';",
    'src/widgets/button.tsx': 'export function Button() { return <button/>; }',
  }, (root, scope) => {
    const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    assert.deepEqual(aliases(output.nodes.find(node => node.name === 'Button')!), [
      { package_name: '@org/ui', import_path: '@org/ui', export_name: 'Button' },
      { package_name: '@org/ui', import_path: '@org/ui/widgets/button', export_name: 'Button' },
    ]);
  });
});

test('a public name missing from one admitted runtime entry never becomes a unique package export', () => {
  fixture({
    'package.json': JSON.stringify({ name: 'shared-ui', exports: { '.': { import: './a.jsx', require: './b.jsx' } } }),
    'a.jsx': 'export function Button() { return <button/>; } export function Shared() { return <i/>; }',
    'b.jsx': 'export function OnlyOther() { return <b/>; } export function Shared() { return <span/>; }',
  }, (root, scope) => {
    const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    assert.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.export_name === 'Button')), false);
    assert.equal(output.nodes.some(node => aliases(node)?.some(alias => alias.export_name === 'OnlyOther')), false);
    // Both runtime implementations remain visible as ambiguous candidates when every entry
    // resolves the public name; the linker must not choose either declaration arbitrarily.
    assert.equal(output.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === 'Shared')).length, 2);
    assert.ok(output.diagnostics?.some(message => message.startsWith('packageExportUnresolved: shared-ui#Button')));
  });
});

test('barrel aliases retain exported context, memo callable and hook declaration identities after framework passes', () => {
  fixture({
    'package.json': JSON.stringify({ name: '@org/ui', exports: './index.ts' }),
    'index.ts': "export { Theme as ThemeAlias, Wrapped as Button, useTools } from './widgets';",
    'widgets.tsx': `import { createContext, memo } from 'react';
      export const Theme = createContext(null);
      export const Wrapped = memo(() => <button/>);
      export const useTools = () => () => 1;`,
  }, (root, scope) => {
    const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    const publicExport = (name: string) => output.nodes.filter(node => aliases(node)?.some(alias => alias.export_name === name));
    assert.equal(publicExport('ThemeAlias').length, 1);
    assert.equal(publicExport('ThemeAlias')[0].properties.context_kind, 'context');
    assert.equal(publicExport('Button').length, 1);
    assert.equal(publicExport('Button')[0].qualifiedName, 'UI:widgets.tsx#Wrapped');
    assert.equal(publicExport('useTools').length, 1);
    assert.equal(publicExport('useTools')[0].qualifiedName, 'UI:widgets.tsx#useTools');
  });
});

test('actual ui Vite library output maps to its admitted public barrel without tsconfig or dist reads', () => {
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
    const disk = require('fs') as { readFileSync: typeof fs.readFileSync };
    const read = disk.readFileSync; let forbiddenReads = 0;
    disk.readFileSync = ((file: fs.PathOrFileDescriptor, ...args: unknown[]) => {
      if (String(file).startsWith(path.join(root, 'dist'))) { forbiddenReads++; throw new Error('dist must never be read'); }
      return (read as (...args: unknown[]) => unknown)(file, ...args);
    }) as typeof fs.readFileSync;
    try {
      const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
      assert.equal(output.complete, true);
      for (const name of ['StatusDot', 'isAccentId']) assert.deepEqual(aliases(output.nodes.find(node => node.name === name)!), [
        { package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: name },
      ]);
      assert.equal(forbiddenReads, 0);
      const noConfig = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope.filter(file => file !== 'vite.config.js'));
      assert.equal(noConfig.nodes.some(node => aliases(node)?.length), false);
      fs.writeFileSync(path.join(root, 'vite.config.js'), `import {defineConfig} from 'vite';
        const library={entry:'src/index.js',fileName:()=> 'sceneworks-ui.js'};
        const alias=library; alias.entry='src/other.js'; export default defineConfig({build:{lib:library}});`);
      assert.equal(extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope).nodes.some(node => aliases(node)?.length), false);
      fs.writeFileSync(path.join(root, 'vite.config.js'), `import {defineConfig} from 'vite';
        export default defineConfig({build:{lib:{entry:'src/index.js',fileName:()=>unknownAtRuntime()}}});`);
      assert.equal(extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope).nodes.some(node => aliases(node)?.length), false);
    } finally { disk.readFileSync = read; }
  });
});

test('external imported function aliases, namespaces and barrels retain exact provenance while local shadows do not', () => {
  fixture({
    'package.json': JSON.stringify({ name: 'app', dependencies: { '@sceneworks/ui': '*' } }),
    'barrel.js': "export { isAccentId as checkAccent } from '@sceneworks/ui';",
    'src/app.tsx': `import { isAccentId as valid, StatusDot } from '@sceneworks/ui';
      import * as ui from '@sceneworks/ui'; import {checkAccent} from '../barrel.js';
      const check = valid;
      export function App() { valid('blue'); check('green'); ui.isAccentId('red'); checkAccent('blue'); StatusDot(); return <StatusDot/>; }
      export function Direct() { return valid('blue'); }
      export function LocalAlias() { return check('green'); }
      export function NamespaceAlias() { return ui.isAccentId('red'); }
      export function BarrelAlias() { return checkAccent('blue'); }
      export function Shadow(valid: (value:string)=>boolean) { return valid('blue'); }
      export function NamespaceShadow(ui: any) { return ui.isAccentId('blue'); }`,
  }, (root, scope) => {
    const output = extractProject('App', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    const app = output.nodes.find(node => node.name === 'App')!.qualifiedName;
    const calls = output.edges.filter(edge => edge.type === 'CALLS' && edge.targetQN === 'npm-symbol:@sceneworks/ui#isAccentId');
    // Repeated call sites share one stored edge. Distinct callers keep every alias form
    // independently observable, including the negative shadowing cases below.
    assert.equal(calls.length, 5);
    assert.equal(calls.filter(edge => edge.sourceQN === app).length, 1);
    assert.deepEqual(calls.map(edge => output.nodes.find(node => node.qualifiedName === edge.sourceQN)!.name).sort(),
      ['App', 'BarrelAlias', 'Direct', 'LocalAlias', 'NamespaceAlias']);
    assert.equal(output.nodes.filter(node => node.qualifiedName === 'npm-symbol:@sceneworks/ui#StatusDot').length, 1);
    assert.deepEqual(output.nodes.find(node => node.qualifiedName === 'npm-symbol:@sceneworks/ui#isAccentId')!.properties, {
      package_name: '@sceneworks/ui', import_path: '@sceneworks/ui', export_name: 'isAccentId', extraction_owner: 'typescript',
    });
  });
});

test('static public object component members retain separate export and member identities across packages', () => {
  fixture({
    'package.json': JSON.stringify({ name: '@sceneworks/ui', exports: './index.ts' }),
    'index.ts': "export {Icon} from './icons';",
    'icons.tsx': 'export const Icon = { Model: () => <svg/>, Nested: { Small: () => <b/> } };',
    'consumer.jsx': `import { Icon as Glyphs } from '@other/ui'; import * as shared from '@other/ui';
      export function Screen(){ return <><Glyphs.Model/><shared.Icon.Nested.Small/></>; }`,
  }, (root, scope) => {
    const output = extractProject('UI', root, path.join(root, 'tsconfig.json'), () => {}, scope);
    const model = output.nodes.find(node => node.qualifiedName === 'UI:icons.tsx#Icon.Model')!;
    assert.deepEqual(aliases(model), [{ package_name:'@sceneworks/ui',import_path:'@sceneworks/ui',export_name:'Icon',member_path:'Model' }]);
    assert.deepEqual(aliases(output.nodes.find(node=>node.qualifiedName==='UI:icons.tsx#Icon.Nested.Small')!), [
      { package_name:'@sceneworks/ui',import_path:'@sceneworks/ui',export_name:'Icon',member_path:'Nested.Small' },
    ]);
    assert.ok(output.edges.some(edge=>edge.type==='RENDERS'&&edge.targetQN==='npm-symbol:@other/ui#Icon::Model'));
    const proxy=output.nodes.find(node=>node.qualifiedName==='npm-symbol:@other/ui#Icon::Nested.Small')!;
    assert.equal(proxy.properties.export_name,'Icon'); assert.equal(proxy.properties.member_path,'Nested.Small');
  });
});

test('written imported members, public object aliases, spreads and computed overrides remain unresolved', () => {
  for (const object of ['const alias = Icon; alias.Model = Other;', 'const alias = Icon; Object.assign(alias, {Model:Other});']) fixture({
    'package.json': JSON.stringify({name:'app'}),
    'app.jsx': `import {Icon} from '@other/ui'; function Other(){return <b/>} ${object} export function App(){return <Icon.Model/>}`,
  }, (root,scope)=> {
    const output=extractProject('App',root,path.join(root,'tsconfig.json'),()=>{},scope);
    assert.equal(output.edges.some(edge=>edge.type==='RENDERS'&&edge.targetQN==='npm-symbol:@other/ui#Icon::Model'),false);
    assert.ok(output.diagnostics?.some(message=>message.startsWith('externalMemberUnresolved:')));
  });
  for (const definition of [
    'export const Icon={Model:()=> <b/>}; const alias=Icon; alias.Model=Other;',
    'export const Icon={Model:()=> <b/>, ...unknown};',
    'export const Icon={Model:()=> <b/>, [key]:Other};',
  ]) fixture({
    'package.json': JSON.stringify({name:'@org/ui',exports:'./index.jsx'}),
    'index.jsx': `function Other(){return <i/>} ${definition}`,
  },(root,scope)=> {
    const output=extractProject('UI',root,path.join(root,'tsconfig.json'),()=>{},scope);
    assert.equal(output.nodes.some(node=>aliases(node)?.some(alias=>(alias as any).member_path==='Model')),false);
  });
});

test('nested exported object aliases check writes before following their initializers', () => {
  fixture({
    'package.json': JSON.stringify({name:'@org/ui',exports:'./index.jsx'}),
    'index.jsx': `function Good(){return <b/>} function Other(){return <i/>}
      const icons={Model:Good}; export const Icon={group:icons}; icons.Model=Other;`,
  },(root,scope)=> {
    const output=extractProject('UI',root,path.join(root,'tsconfig.json'),()=>{},scope);
    assert.equal(output.nodes.some(node=>aliases(node)?.some(alias=>(alias as any).member_path==='group.Model')),false);
    assert.ok(output.diagnostics?.some(message=>message.startsWith('packageExportUnresolved: @org/ui#Icon')));
  });
});

test('nested imported object aliases preserve valid members but refuse writes through imported aliases', () => {
  fixture({
    'package.json': JSON.stringify({name:'@org/ui',exports:'./index.jsx'}),
    'icons.jsx': 'function Good(){return <b/>} export const icons={Model:Good};',
    'index.jsx': "import {icons as shared} from './icons'; const alias=shared; export const Icon={group:alias};",
  },(root,scope)=> {
    const extract=()=>extractProject('UI',root,path.join(root,'tsconfig.json'),()=>{},scope);
    const valid=extract();
    assert.equal(valid.nodes.some(node=>aliases(node)?.some(alias=>(alias as any).member_path==='group.Model')),true);
    fs.appendFileSync(path.join(root,'index.jsx'), '\nfunction Other(){return <i/>} alias.Model=Other;');
    const changed=extract();
    assert.equal(changed.nodes.some(node=>aliases(node)?.some(alias=>(alias as any).member_path==='group.Model')),false);
  });
});
