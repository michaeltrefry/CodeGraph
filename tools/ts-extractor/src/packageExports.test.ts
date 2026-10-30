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
