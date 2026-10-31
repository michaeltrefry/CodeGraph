import { strict as assert } from 'assert';
import { test } from 'node:test';
import { Project, ModuleKind, ScriptTarget } from 'ts-morph';
import * as path from 'path';
import { SemanticModel } from './semanticModel';
import { analyzeTauri } from './tauriAnalysis';
import type { GraphNodeDto, PendingEdgeDto, ResolvedImportDto } from './protocol';

function analyze(files: Record<string, string>) {
  const project = new Project({ useInMemoryFileSystem: true, compilerOptions: { allowJs: true, target: ScriptTarget.ESNext, module: ModuleKind.CommonJS } });
  const sources = Object.entries(files).map(([file, content]) => project.createSourceFile(`/fixture/${file}`, content));
  const imports: ResolvedImportDto[] = sources.flatMap(source => [...source.getImportDeclarations(), ...source.getExportDeclarations()].map(declaration => {
    const file = path.relative('/fixture', source.getFilePath());
    const specifier = declaration.getModuleSpecifierValue() ?? '';
    const stem = path.posix.normalize(path.posix.join(path.posix.dirname(file), specifier));
    const target = [stem, `${stem}.ts`, `${stem}.tsx`, `${stem}.jsx`].find(candidate => files[candidate] !== undefined);
    return { fileQN: `App:${file}`, filePath: file, importedNamespace: specifier, importKind: 'static' as const,
      classification: target ? 'local_file' as const : 'external_npm_package' as const, resolvedFilePath: target };
  }));
  const diagnostics: string[] = [];
  const model = new SemanticModel('App', '/fixture', sources, imports, message => diagnostics.push(message));
  const nodes: GraphNodeDto[] = [], edges: PendingEdgeDto[] = [];
  model.extract(nodes, edges, []);
  analyzeTauri(model, nodes, edges, message => diagnostics.push(message));
  return { nodes, edges, diagnostics };
}

test('SoundWorks readOverview binds each substituted command and retains the browser fallback as separate dependency', () => {
  const result = analyze({
    'src/tauri.ts': `import { invoke as nativeInvoke } from '@tauri-apps/api/core';
      const commandName = 'get_app_overview';
      function readOverview(command: string, fallback: unknown) { if (!window.__TAURI_INTERNALS__) return fallback; return nativeInvoke(command); }
      export function loadAppOverview() { return readOverview(commandName, {}); }
      export function loadRuntimeOverview() { return readOverview('get_runtime_overview', {}); }`,
    'src/App.tsx': `import { loadAppOverview } from './tauri'; export function App() { loadAppOverview(); return <div/>; }`,
  });
  const command = result.nodes.find(node => node.properties.tauri_caller === 'App:src/tauri.ts#loadAppOverview' && node.properties.tauri_name === 'get_app_overview');
  assert.ok(command);
  assert.ok(result.edges.some(edge => edge.sourceQN === 'App:src/tauri.ts#loadAppOverview' && edge.targetQN === 'App:src/tauri.ts#readOverview'));
  assert.ok(result.edges.some(edge => edge.sourceQN === 'App:src/App.tsx#App' && edge.targetQN === 'App:src/tauri.ts#loadAppOverview'));
  assert.equal(command.properties.dependency_kind, 'possible_desktop');
  assert.ok(result.nodes.some(node => node.properties.tauri_name === 'get_runtime_overview'));
});

test('ChatWorks direct calls, event callback aliases, namespace APIs and dynamic names preserve their evidence', () => {
  const result = analyze({ 'src/ChatScreen.jsx': `import { invoke } from '@tauri-apps/api/core';
    import * as events from '@tauri-apps/api/event';
    const onStream = event => console.log(event);
    export function ChatScreen(name) {
      invoke('execute_tool'); events.listen('engine://stream', onStream);
      events.emit('engine://ready', {}); events.emitTo('main', 'engine://focused', {});
      invoke(name); events.listen('prefix-' + name, onStream);
    }` });
  assert.ok(result.nodes.some(node => node.properties.tauri_name === 'execute_tool'));
  const event = result.nodes.find(node => node.properties.tauri_name === 'engine://stream');
  assert.equal(event?.properties.tauri_callback, 'App:src/ChatScreen.jsx#onStream');
  assert.ok(result.edges.some(edge => edge.sourceQN === event?.qualifiedName && edge.type === 'HANDLES'));
  assert.equal(result.nodes.filter(node => node.properties.tauri_resolution === 'dynamic').length, 2);
  assert.ok(result.nodes.some(node => node.properties.tauri_name === 'engine://focused'));
});

test('LensWorks bridge remains callable-scoped and unrelated/shadowed invoke functions never become native operations', () => {
  const result = analyze({ 'src/bridge.ts': `import { invoke } from '@tauri-apps/api/core';
    export async function command(request) { return invoke('catalog_command', {request}); }
    export const chooseFolder = () => invoke('catalog_choose_folder');
    function unrelated(invoke) { return invoke('not_native'); }
    function other() { const invoke = x => x; return invoke('not_native_either'); }` });
  assert.deepEqual(result.nodes.filter(node => node.properties.kind === 'tauri_command_reference').map(node => node.name).sort(), ['catalog_choose_folder', 'catalog_command']);
});

test('wrapper-only command changes remove stale reference identities and recursion is bounded', () => {
  const files = (command: string) => ({ 'src/api.ts': `import { invoke } from '@tauri-apps/api/core';
    export function leaf(command) { return invoke(command); }
    export function wrapper() { return leaf('${command}'); }
    export function recursion() { return recursion(); }` });
  const first = analyze(files('before')), second = analyze(files('after'));
  assert.ok(first.nodes.some(node => node.name === 'before'));
  assert.ok(second.nodes.some(node => node.name === 'after'));
  assert.ok(!second.nodes.some(node => node.name === 'before'));
});

test('re-exported native imports retain provenance through named and namespace barrels', () => {
  const result = analyze({
    'src/barrel.ts': `export { invoke as ipc } from '@tauri-apps/api/core'; export { listen } from '@tauri-apps/api/event';`,
    'src/bridge.ts': `import { ipc as send } from './barrel'; import * as native from './barrel';
      export function load() { send('catalog_command'); native.listen('engine://stream', () => {}); }`,
  });
  assert.ok(result.nodes.some(node => node.properties.tauri_name === 'catalog_command'));
  assert.ok(result.nodes.some(node => node.properties.tauri_name === 'engine://stream'));
});

test('closure wrapper memoization isolates captured commands across callers and declaration order', () => {
  for (const reverse of [false, true]) {
    const callers = [
      `export function first() { return wrap('first_command'); }`,
      `export function second() { return wrap('second_command'); }`,
    ];
    const result = analyze({ 'src/closure.ts': `import { invoke } from '@tauri-apps/api/core';
      function wrap(command: string) { const inner = () => invoke(command); return inner(); }
      ${reverse ? callers.reverse().join('\n') : callers.join('\n')}` });
    for (const caller of ['first', 'second']) {
      const references = result.nodes.filter(node => node.properties.tauri_caller === `App:src/closure.ts#${caller}` && node.properties.kind === 'tauri_command_reference');
      assert.deepEqual(references.map(node => node.properties.tauri_name), [`${caller}_command`]);
      assert.ok(result.edges.some(edge => edge.sourceQN === `App:src/closure.ts#${caller}` && edge.targetQN === references[0].qualifiedName));
    }
  }
});

test('captured command dependencies follow local aliases and sibling closure calls', () => {
  const result = analyze({ 'src/closure.ts': `import { invoke } from '@tauri-apps/api/core';
    function wrap(command: string, irrelevant: string) {
      const alias = command;
      const read = () => alias;
      const leaf = () => invoke(read());
      const inner = () => leaf();
      return inner();
    }
    export function first() { return wrap('first_command', 'first_noise'); }
    export function second() { return wrap('second_command', 'second_noise'); }` });
  for (const caller of ['first', 'second']) {
    assert.deepEqual(result.nodes.filter(node => node.properties.tauri_caller === `App:src/closure.ts#${caller}`)
      .map(node => node.properties.tauri_name), [`${caller}_command`]);
  }
});
