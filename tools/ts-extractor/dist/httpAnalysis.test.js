"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
const assert_1 = require("assert");
const node_test_1 = require("node:test");
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const httpAnalysis_1 = require("./httpAnalysis");
function fixture(sources) {
    const project = new ts_morph_1.Project({ useInMemoryFileSystem: true, compilerOptions: { allowJs: true, target: ts_morph_1.ts.ScriptTarget.ES2020, module: ts_morph_1.ts.ModuleKind.ESNext, experimentalDecorators: true } });
    const files = Object.entries(sources).map(([file, source]) => project.createSourceFile(`/repo/${file}`, source));
    const imports = [];
    for (const file of files)
        for (const imp of file.getImportDeclarations()) {
            const spec = imp.getModuleSpecifierValue();
            const resolved = imp.getModuleSpecifierSourceFile();
            imports.push({ fileQN: `Demo:${file.getBaseName()}`, filePath: file.getFilePath().slice(6), importedNamespace: spec, importKind: 'static', classification: resolved ? 'local_file' : 'external_npm_package', ...(resolved ? { resolvedFilePath: resolved.getFilePath().slice(6) } : {}) });
        }
    const diagnostics = [];
    const model = new semanticModel_1.SemanticModel('Demo', '/repo', files, imports, d => diagnostics.push(d));
    const edges = [];
    const coverage = (0, httpAnalysis_1.analyzeHttp)(model, [], edges, d => diagnostics.push(d));
    return { edges, coverage, diagnostics };
}
const routes = (result, owner) => result.edges.filter(e => e.sourceQN === owner).map(e => e.targetQN).sort();
(0, node_test_1.test)('SceneWorks apiFetch source-shaped options and exported helper chains retain POST and /api/v1', () => {
    const result = fixture({
        'api.js': `const configuredApiBaseUrl = import.meta.env.VITE_API_BASE_URL;
      export const API_BASE_URL = configuredApiBaseUrl === undefined ? "http://localhost:8000" : configuredApiBaseUrl === "" && typeof window !== "undefined" ? window.location.origin : configuredApiBaseUrl;
      export async function apiFetch(path, token, options = {}) {
        const headers = new Headers(options.headers ?? {});
        const isFormData = options.body instanceof FormData;
        if (options.body && !isFormData) headers.set('Content-Type', 'application/json');
        if (token) headers.set('X-SceneWorks-Token', token);
        const response = await fetch(\`\${API_BASE_URL}\${path}\`, { ...options, headers });
        return response.json();
      }
      export async function inspectWorkflowFile(file, { projectId, token, signal } = {}) {
        const body = new FormData(); body.append('file', file);
        if (projectId) body.append('projectId', projectId);
        return apiFetch('/api/v1/workflows/inspect', token, { method:'POST', body, signal });
      }
      export function readProject(id,token) { return apiFetch(\`/api/v1/projects/\${id}?expand=1#details\`, token); }`,
        'screen.jsx': `import { inspectWorkflowFile, readProject } from './api.js';
      export function Screen() { inspectWorkflowFile(file, {token}); readProject('project-one',token); return <div/>; }`,
    });
    assert_1.strict.deepEqual(routes(result, 'Demo:api.js#inspectWorkflowFile'), ['route:*:POST:/api/v1/workflows/inspect']);
    assert_1.strict.deepEqual(routes(result, 'Demo:screen.jsx#Screen'), ['route:*:GET:/api/v1/projects/project-one', 'route:*:POST:/api/v1/workflows/inspect']);
    assert_1.strict.ok(result.edges.find(e => e.sourceQN === 'Demo:api.js#readProject' && e.targetQN === 'route:*:GET:/api/v1/projects/{param}'));
    assert_1.strict.ok(result.edges.every(e => !e.targetQN.includes('localhost:8000')));
    assert_1.strict.ok(result.edges.find(e => e.sourceQN === 'Demo:screen.jsx#Screen')?.properties?.wrapper_chain);
});
(0, node_test_1.test)('router request source shape preserves /admin/v1, object methods, defaults, spreads and query suffix', () => {
    const result = fixture({ 'api.ts': `const BASE='/admin/v1';
    async function request<T>(path:string, init?:RequestInit):Promise<T> {
      const headers = {'Content-Type':'application/json', ...((init?.headers as Record<string,string>) ?? {})};
      const res = await fetch(\`\${BASE}\${path}\`, {...init,credentials:'include',headers}); return res.json();
    }
    const defaults={method:'PATCH'};
    export const api={
      logout:()=>request('/auth/logout',{method:'POST'}),
      update:(id:string, init={})=>request('/models/'+id,{...defaults,...init}),
      summary:(from?:string)=> { const qs = new URLSearchParams({from}).toString(); return request(\`/metrics/summary\${qs ? \`?\${qs}\` : ''}\`); },
    };
    export function Page() { api.logout(); api.update('a',{method:'DELETE'}); api.summary(); }
  ` });
    assert_1.strict.deepEqual(routes(result, 'Demo:api.ts#Page'), ['route:*:DELETE:/admin/v1/models/a', 'route:*:GET:/admin/v1/metrics/summary', 'route:*:POST:/admin/v1/auth/logout']);
});
(0, node_test_1.test)('constant evaluation is symbol-scoped across imports, enums, nested endpoint objects, fields and URL values', () => {
    const result = fixture({
        'constants.ts': `export const endpoints={ nested:{users:'/api/v1/users'}}; export enum Verb { Remove='DELETE' } export const BASE='https://backend.example/service/';`,
        'calls.ts': `import {endpoints as e,Verb,BASE} from './constants';
      export function one(id:string){ const url=e.nested.users+'/'+id; return fetch(url,{method:Verb.Remove}); }
      export function two(){ const url='/api/v1/other'; return fetch(url); }
      export function absolute(){ return fetch(new URL('widgets?x=1#f',BASE).href); }
      export function stringURL(){ const value=new URL('/api/v1/last','https://other.example'); return fetch(value.toString()); }
      export function urlField(){ const value=new URL('/api/v1/href','https://other.example'); return fetch(value.href); }
      export class Service { static ROOT='/api/v1'; private url=Service.ROOT+'/settings'; load(){return fetch(this.url);} }
    `,
    });
    assert_1.strict.deepEqual(routes(result, 'Demo:calls.ts#one'), ['route:*:DELETE:/api/v1/users/{param}']);
    assert_1.strict.deepEqual(routes(result, 'Demo:calls.ts#two'), ['route:*:GET:/api/v1/other']);
    assert_1.strict.deepEqual(routes(result, 'Demo:calls.ts#absolute'), ['route:*:GET:/service/widgets']);
    assert_1.strict.equal(result.edges.find(e => e.sourceQN === 'Demo:calls.ts#absolute')?.properties?.origin, 'https://backend.example');
    assert_1.strict.deepEqual(routes(result, 'Demo:calls.ts#stringURL'), ['route:*:GET:/api/v1/last']);
    assert_1.strict.deepEqual(routes(result, 'Demo:calls.ts#urlField'), ['route:*:GET:/api/v1/href']);
    assert_1.strict.deepEqual(routes(result, 'calls.Service.load'), ['route:*:GET:/api/v1/settings']);
});
(0, node_test_1.test)('unknown methods and overriding spreads remain unresolved while final explicit methods win', () => {
    const result = fixture({ 'unknown.ts': `
    function request(path,init){ return fetch(path,{method:'GET',...init}); }
    export function ambiguous(init){ return request('/api/v1/items',init); }
    export function unknownMethod(method){ return fetch('/api/v1/items',{method}); }
    export function finalOverride(init){ return fetch('/api/v1/items',{...init,method:'PUT'}); }
    export function entirelyUnknown(url){ return fetch(url); }
    export function onlyBase(path){ return fetch('/api/v1/'+path); }
    export function conditionalMethod(toggle){ return fetch('/api/v1/items',{method:toggle?'GET':'DELETE'}); }
    export function reassigned(){ let path='/old'; path='/new'; return fetch(path); }
  ` });
    for (const name of ['ambiguous', 'unknownMethod', 'entirelyUnknown', 'onlyBase', 'conditionalMethod', 'reassigned'])
        assert_1.strict.deepEqual(routes(result, `Demo:unknown.ts#${name}`), [], name);
    assert_1.strict.deepEqual(routes(result, 'Demo:unknown.ts#finalOverride'), ['route:*:PUT:/api/v1/items']);
    assert_1.strict.ok(result.coverage.unresolved >= 6);
    assert_1.strict.ok(result.diagnostics.some(d => d.includes('unknown method')));
});
(0, node_test_1.test)('transport ownership rejects shadowed fetch, duplicate identifiers and unrelated http methods', () => {
    const result = fixture({
        'real.ts': `export function request(url){ return fetch(url); }`,
        'unrelated.ts': `export function request(url){ return local(url); }`,
        'use.ts': `import {request as real} from './real'; import {request as other} from './unrelated';
      function shadow(fetch){ return fetch('/false'); }
      function fake(){ const http={ get(x){return x;} }; return http.get('/false'); }
      function ownGlobal(){const window={fetch(x){return x;}};return window.fetch('/false');}
      export function caller(){ real('/api/v1/real'); other('/api/v1/false'); }`,
    });
    assert_1.strict.deepEqual(routes(result, 'Demo:use.ts#caller'), ['route:*:GET:/api/v1/real']);
    assert_1.strict.ok(result.edges.every(e => !e.targetQN.includes('false')));
});
(0, node_test_1.test)('Angular HttpClient imported aliases, inject and ApiContract calls work without installed dependencies', () => {
    const result = fixture({
        'contracts.ts': `import {ApiContract,HttpMethods} from '@tc/contracts';
      export const ENDPOINTS={search:'/api/v1/search'};
      @ApiContract({endpoint:ENDPOINTS.search,httpMethod:HttpMethods.post}) export class SearchRequest {}
    `,
        'client.ts': `import {HttpClient as Client} from '@angular/common/http'; import {inject} from '@angular/core'; import {SearchRequest} from './contracts';
      export class Service {
        http=inject(Client); constructor(private client:Client){}
        one(id:string){ return this.client.get(\`/api/v1/items/\${id}\`); }
        two(){return this.http.request('PATCH','/api/v1/settings');}
      }
      export function sendContract(service){return service.send(new SearchRequest());}
    `,
    });
    assert_1.strict.deepEqual(routes(result, 'client.Service.one'), ['route:*:GET:/api/v1/items/{param}']);
    assert_1.strict.deepEqual(routes(result, 'client.Service.two'), ['route:*:PATCH:/api/v1/settings']);
    assert_1.strict.deepEqual(routes(result, 'Demo:client.ts#sendContract'), ['route:*:POST:/api/v1/search']);
});
(0, node_test_1.test)('wrapper-only and imported-constant retargeting alter unchanged callers and caches reset each run', () => {
    const source = (path, method) => ({ 'values.ts': `export const ENDPOINT='${path}';`, 'wrapper.ts': `import {ENDPOINT} from './values'; export function wrap(){return fetch(ENDPOINT,{method:'${method}'});}`, 'caller.ts': `import {wrap} from './wrapper'; export function Screen(){return wrap();}` });
    const before = fixture(source('/api/v1/first', 'GET'));
    const after = fixture(source('/api/v1/second', 'POST'));
    assert_1.strict.deepEqual(routes(before, 'Demo:caller.ts#Screen'), ['route:*:GET:/api/v1/first']);
    assert_1.strict.deepEqual(routes(after, 'Demo:caller.ts#Screen'), ['route:*:POST:/api/v1/second']);
    assert_1.strict.deepEqual(fixture(source('/api/v1/second', 'POST')).edges, after.edges);
});
(0, node_test_1.test)('recursive wrappers are bounded and URL normalization retains base paths and origins', () => {
    const result = fixture({ 'recursive.ts': `function a(path){return b(path);} function b(path){a(path);return fetch('/api/v1/items');} export function use(){return a('/x');}` });
    assert_1.strict.ok(result.coverage.bounded > 0);
    assert_1.strict.ok(result.edges.length < 10);
    assert_1.strict.deepEqual((0, httpAnalysis_1.normalizeHttpUrl)('https://example.test/admin/v1/items?q=x#hash'), { path: '/admin/v1/items', origin: 'https://example.test' });
    assert_1.strict.equal((0, httpAnalysis_1.normalizeHttpUrl)('{param}'), undefined);
    assert_1.strict.deepEqual((0, httpAnalysis_1.normalizeHttpUrl)('/api/v1'), { path: '/api/v1' });
    assert_1.strict.deepEqual((0, httpAnalysis_1.normalizeHttpUrl)('https://backend.example/'), { path: '/', origin: 'https://backend.example' });
});
(0, node_test_1.test)('mutated option fields, unknown computed overrides and null methods never silently become GET', () => {
    const result = fixture({ 'mutated.ts': `
    export function changed(){const options={method:'GET'};options.method='DELETE';return fetch('/api/v1/items',options);}
    export function computed(key){return fetch('/api/v1/items',{method:'POST',[key]:'DELETE'});}
    export function explicit(key){return fetch('/api/v1/items',{[key]:'DELETE',method:'PUT'});}
    export function nullable(){return fetch('/api/v1/items',{method:null});}
    export class Client { url='/api/v1/old'; constructor(){this.url=dynamic;} load(){return fetch(this.url);} }
  ` });
    assert_1.strict.deepEqual(routes(result, 'Demo:mutated.ts#changed'), []);
    assert_1.strict.deepEqual(routes(result, 'Demo:mutated.ts#computed'), []);
    assert_1.strict.deepEqual(routes(result, 'Demo:mutated.ts#nullable'), []);
    assert_1.strict.deepEqual(routes(result, 'mutated.Client.load'), []);
    assert_1.strict.deepEqual(routes(result, 'Demo:mutated.ts#explicit'), ['route:*:PUT:/api/v1/items']);
});
(0, node_test_1.test)('const transport aliases and imported pure helpers preserve static defaults without name-based recognition', () => {
    const result = fixture({
        'constants.ts': `export const endpoints={items:'/api/v1/items'};export function options(method='PATCH'){return {method};}`,
        'use.ts': `import * as constants from './constants';
      const transport=globalThis.fetch;
      function send(path=constants.endpoints.items, init=constants.options()){return transport(path,init);}
      export function call(){return send();}
      export function shadow(){const fetch=(x)=>x;const alias=fetch;return alias('/false');}
    `,
    });
    assert_1.strict.deepEqual(routes(result, 'Demo:use.ts#call'), ['route:*:PATCH:/api/v1/items']);
    assert_1.strict.deepEqual(routes(result, 'Demo:use.ts#shadow'), []);
});
(0, node_test_1.test)('an inferred default-object property never proves an unknown caller override', () => {
    const result = fixture({ 'defaults.ts': `
    function wrapped(path,options={method:'GET'}){return fetch(path,{method:options.method});}
    export function dynamic(options){return wrapped('/api/v1/items',options);}
    export function literal(){return wrapped('/api/v1/items',{method:'POST'});}
    export function defaulted(){return wrapped('/api/v1/items');}
  ` });
    assert_1.strict.deepEqual(routes(result, 'Demo:defaults.ts#dynamic'), []);
    assert_1.strict.deepEqual(routes(result, 'Demo:defaults.ts#literal'), ['route:*:POST:/api/v1/items']);
    assert_1.strict.deepEqual(routes(result, 'Demo:defaults.ts#defaulted'), ['route:*:GET:/api/v1/items']);
});
(0, node_test_1.test)('module initialization HTTP has explicit file ownership and origin query normalization', () => {
    const result = fixture({ 'startup.ts': `const url='https://api.example/api/v1/config?cache=1';export const config=fetch(url);` });
    assert_1.strict.deepEqual(routes(result, 'Demo:startup.ts'), ['route:*:GET:/api/v1/config']);
    assert_1.strict.deepEqual((0, httpAnalysis_1.normalizeHttpUrl)('https://example.test?x=1'), { path: '/', origin: 'https://example.test' });
});
