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
const node_test_1 = require("node:test");
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
const semanticModel_1 = require("./semanticModel");
const tauriAnalysis_1 = require("./tauriAnalysis");
function analyze(files) {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, compilerOptions: { allowJs: true, target: ts_morph_1.ScriptTarget.ESNext, module: ts_morph_1.ModuleKind.CommonJS } });
    const sources = Object.entries(files).map(([file, content]) => project.createSourceFile(`/fixture/${file}`, content));
    const imports = sources.flatMap(source => [...source.getImportDeclarations(), ...source.getExportDeclarations()].map(declaration => {
        const file = path.relative('/fixture', source.getFilePath());
        const specifier = declaration.getModuleSpecifierValue() ?? '';
        const stem = path.posix.normalize(path.posix.join(path.posix.dirname(file), specifier));
        const target = [stem, `${stem}.ts`, `${stem}.tsx`, `${stem}.jsx`].find(candidate => files[candidate] !== undefined);
        return { fileQN: `App:${file}`, filePath: file, importedNamespace: specifier, importKind: 'static',
            classification: target ? 'local_file' : 'external_npm_package', resolvedFilePath: target };
    }));
    const diagnostics = [];
    const model = new semanticModel_1.SemanticModel('App', '/fixture', sources, imports, message => diagnostics.push(message));
    const nodes = [], edges = [];
    model.extract(nodes, edges, []);
    (0, tauriAnalysis_1.analyzeTauri)(model, nodes, edges, message => diagnostics.push(message));
    return { nodes, edges, diagnostics };
}
(0, node_test_1.test)('SoundWorks readOverview binds each substituted command and retains the browser fallback as separate dependency', () => {
    const result = analyze({
        'src/tauri.ts': `import { invoke as nativeInvoke } from '@tauri-apps/api/core';
      const commandName = 'get_app_overview';
      function readOverview(command: string, fallback: unknown) { if (!window.__TAURI_INTERNALS__) return fallback; return nativeInvoke(command); }
      export function loadAppOverview() { return readOverview(commandName, {}); }
      export function loadRuntimeOverview() { return readOverview('get_runtime_overview', {}); }`,
        'src/App.tsx': `import { loadAppOverview } from './tauri'; export function App() { loadAppOverview(); return <div/>; }`,
    });
    const command = result.nodes.find(node => node.properties.tauri_caller === 'App:src/tauri.ts#loadAppOverview' && node.properties.tauri_name === 'get_app_overview');
    assert_1.strict.ok(command);
    assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'App:src/tauri.ts#loadAppOverview' && edge.targetQN === 'App:src/tauri.ts#readOverview'));
    assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === 'App:src/App.tsx#App' && edge.targetQN === 'App:src/tauri.ts#loadAppOverview'));
    assert_1.strict.equal(command.properties.dependency_kind, 'possible_desktop');
    assert_1.strict.ok(result.nodes.some(node => node.properties.tauri_name === 'get_runtime_overview'));
});
(0, node_test_1.test)('ChatWorks direct calls, event callback aliases, namespace APIs and dynamic names preserve their evidence', () => {
    const result = analyze({ 'src/ChatScreen.jsx': `import { invoke } from '@tauri-apps/api/core';
    import * as events from '@tauri-apps/api/event';
    const onStream = event => console.log(event);
    export function ChatScreen(name) {
      invoke('execute_tool'); events.listen('engine://stream', onStream);
      events.emit('engine://ready', {}); events.emitTo('main', 'engine://focused', {});
      invoke(name); events.listen('prefix-' + name, onStream);
    }` });
    assert_1.strict.ok(result.nodes.some(node => node.properties.tauri_name === 'execute_tool'));
    const event = result.nodes.find(node => node.properties.tauri_name === 'engine://stream');
    assert_1.strict.equal(event?.properties.tauri_callback, 'App:src/ChatScreen.jsx#onStream');
    assert_1.strict.ok(result.edges.some(edge => edge.sourceQN === event?.qualifiedName && edge.type === 'HANDLES'));
    assert_1.strict.equal(result.nodes.filter(node => node.properties.tauri_resolution === 'dynamic').length, 2);
    assert_1.strict.ok(result.nodes.some(node => node.properties.tauri_name === 'engine://focused'));
});
(0, node_test_1.test)('LensWorks bridge remains callable-scoped and unrelated/shadowed invoke functions never become native operations', () => {
    const result = analyze({ 'src/bridge.ts': `import { invoke } from '@tauri-apps/api/core';
    export async function command(request) { return invoke('catalog_command', {request}); }
    export const chooseFolder = () => invoke('catalog_choose_folder');
    function unrelated(invoke) { return invoke('not_native'); }
    function other() { const invoke = x => x; return invoke('not_native_either'); }` });
    assert_1.strict.deepEqual(result.nodes.filter(node => node.properties.kind === 'tauri_command_reference').map(node => node.name).sort(), ['catalog_choose_folder', 'catalog_command']);
});
(0, node_test_1.test)('wrapper-only command changes remove stale reference identities and recursion is bounded', () => {
    const files = (command) => ({ 'src/api.ts': `import { invoke } from '@tauri-apps/api/core';
    export function leaf(command) { return invoke(command); }
    export function wrapper() { return leaf('${command}'); }
    export function recursion() { return recursion(); }` });
    const first = analyze(files('before')), second = analyze(files('after'));
    assert_1.strict.ok(first.nodes.some(node => node.name === 'before'));
    assert_1.strict.ok(second.nodes.some(node => node.name === 'after'));
    assert_1.strict.ok(!second.nodes.some(node => node.name === 'before'));
});
(0, node_test_1.test)('re-exported native imports retain provenance through named and namespace barrels', () => {
    const result = analyze({
        'src/barrel.ts': `export { invoke as ipc } from '@tauri-apps/api/core'; export { listen } from '@tauri-apps/api/event';`,
        'src/bridge.ts': `import { ipc as send } from './barrel'; import * as native from './barrel';
      export function load() { send('catalog_command'); native.listen('engine://stream', () => {}); }`,
    });
    assert_1.strict.ok(result.nodes.some(node => node.properties.tauri_name === 'catalog_command'));
    assert_1.strict.ok(result.nodes.some(node => node.properties.tauri_name === 'engine://stream'));
});
