import { strict as assert } from 'assert';
import { test } from 'node:test';
import { Project, ts } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { analyzeHttp, normalizeHttpUrl } from './httpAnalysis';
import type { PendingEdgeDto, ResolvedImportDto } from './protocol';

function fixture(sources: Record<string, string>, serviceName?: string) {
  const project = new Project({ useInMemoryFileSystem: true, compilerOptions: { allowJs: true, target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext, experimentalDecorators: true } });
  const files = Object.entries(sources).map(([file, source]) => project.createSourceFile(`/repo/${file}`, source));
  const imports: ResolvedImportDto[] = [];
  for (const file of files) for (const imp of file.getImportDeclarations()) {
    const spec = imp.getModuleSpecifierValue();
    const resolved = imp.getModuleSpecifierSourceFile();
    imports.push({ fileQN: `Demo:${file.getBaseName()}`, filePath: file.getFilePath().slice(6), importedNamespace: spec, importKind: 'static', classification: resolved ? 'local_file' : 'external_npm_package', ...(resolved ? { resolvedFilePath: resolved.getFilePath().slice(6) } : {}) });
  }
  const diagnostics: string[] = [];
  const model = new SemanticModel('Demo', '/repo', files, imports, d => diagnostics.push(d));
  const edges: PendingEdgeDto[] = [];
  const coverage = analyzeHttp(model, [], edges, d => diagnostics.push(d), serviceName);
  return { edges, coverage, diagnostics };
}
const routes = (result: ReturnType<typeof fixture>, owner: string) => result.edges.filter(e => e.sourceQN === owner).map(e => e.targetQN).sort();

test('SceneWorks apiFetch source-shaped options and exported helper chains retain POST and /api/v1', () => {
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
  assert.deepEqual(routes(result, 'Demo:api.js#inspectWorkflowFile'), ['route:*:POST:/api/v1/workflows/inspect']);
  assert.deepEqual(routes(result, 'Demo:screen.jsx#Screen'), ['route:*:GET:/api/v1/projects/project-one', 'route:*:POST:/api/v1/workflows/inspect']);
  assert.ok(result.edges.find(e => e.sourceQN === 'Demo:api.js#readProject' && e.targetQN === 'route:*:GET:/api/v1/projects/{param}'));
  assert.ok(result.edges.every(e => !e.targetQN.includes('localhost:8000')));
  assert.ok(result.edges.find(e => e.sourceQN === 'Demo:screen.jsx#Screen')?.properties?.wrapper_chain);
});

test('router request source shape preserves /admin/v1, object methods, defaults, spreads and query suffix', () => {
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
  assert.deepEqual(routes(result, 'Demo:api.ts#Page'), ['route:*:DELETE:/admin/v1/models/a', 'route:*:GET:/admin/v1/metrics/summary', 'route:*:POST:/admin/v1/auth/logout']);
});

test('constant evaluation is symbol-scoped across imports, enums, nested endpoint objects, fields and URL values', () => {
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
  assert.deepEqual(routes(result, 'Demo:calls.ts#one'), ['route:*:DELETE:/api/v1/users/{param}']);
  assert.deepEqual(routes(result, 'Demo:calls.ts#two'), ['route:*:GET:/api/v1/other']);
  assert.deepEqual(routes(result, 'Demo:calls.ts#absolute'), ['route:*:GET:/service/widgets']);
  assert.equal(result.edges.find(e => e.sourceQN === 'Demo:calls.ts#absolute')?.properties?.origin, 'https://backend.example');
  assert.deepEqual(routes(result, 'Demo:calls.ts#stringURL'), ['route:*:GET:/api/v1/last']);
  assert.deepEqual(routes(result, 'Demo:calls.ts#urlField'), ['route:*:GET:/api/v1/href']);
  assert.deepEqual(routes(result, 'calls.Service.load'), ['route:*:GET:/api/v1/settings']);
});

test('unknown methods and overriding spreads remain unresolved while final explicit methods win', () => {
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
  for (const name of ['ambiguous','unknownMethod','entirelyUnknown','onlyBase','conditionalMethod','reassigned']) assert.deepEqual(routes(result, `Demo:unknown.ts#${name}`), [], name);
  assert.deepEqual(routes(result, 'Demo:unknown.ts#finalOverride'), ['route:*:PUT:/api/v1/items']);
  assert.ok(result.coverage.unresolved >= 6);
  assert.ok(result.diagnostics.some(d => d.includes('unknown method')));
});

test('transport ownership rejects shadowed fetch, duplicate identifiers and unrelated http methods', () => {
  const result = fixture({
    'real.ts': `export function request(url){ return fetch(url); }`,
    'unrelated.ts': `export function request(url){ return local(url); }`,
    'use.ts': `import {request as real} from './real'; import {request as other} from './unrelated';
      function shadow(fetch){ return fetch('/false'); }
      function fake(){ const http={ get(x){return x;} }; return http.get('/false'); }
      function ownGlobal(){const window={fetch(x){return x;}};return window.fetch('/false');}
      export function caller(){ real('/api/v1/real'); other('/api/v1/false'); }`,
  });
  assert.deepEqual(routes(result,'Demo:use.ts#caller'), ['route:*:GET:/api/v1/real']);
  assert.ok(result.edges.every(e => !e.targetQN.includes('false')));
});

test('Angular HttpClient imported aliases, inject and ApiContract calls work without installed dependencies', () => {
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
  assert.deepEqual(routes(result,'client.Service.one'), ['route:*:GET:/api/v1/items/{param}']);
  assert.deepEqual(routes(result,'client.Service.two'), ['route:*:PATCH:/api/v1/settings']);
  assert.deepEqual(routes(result,'Demo:client.ts#sendContract'), ['route:*:POST:/api/v1/search']);
});

test('wrapper-only and imported-constant retargeting alter unchanged callers and caches reset each run', () => {
  const source=(path:string, method:string)=> ({'values.ts':`export const ENDPOINT='${path}';`, 'wrapper.ts':`import {ENDPOINT} from './values'; export function wrap(){return fetch(ENDPOINT,{method:'${method}'});}`, 'caller.ts':`import {wrap} from './wrapper'; export function Screen(){return wrap();}`});
  const before=fixture(source('/api/v1/first','GET'));
  const after=fixture(source('/api/v1/second','POST'));
  assert.deepEqual(routes(before,'Demo:caller.ts#Screen'),['route:*:GET:/api/v1/first']);
  assert.deepEqual(routes(after,'Demo:caller.ts#Screen'),['route:*:POST:/api/v1/second']);
  assert.deepEqual(fixture(source('/api/v1/second','POST')).edges, after.edges);
});

test('recursive wrappers are bounded and URL normalization retains base paths and origins', () => {
  const result=fixture({'recursive.ts':`function a(path){return b(path);} function b(path){a(path);return fetch('/api/v1/items');} export function use(){return a('/x');}`});
  assert.ok(result.coverage.bounded>0);
  assert.ok(result.edges.length<10);
  assert.deepEqual(normalizeHttpUrl('https://example.test/admin/v1/items?q=x#hash'),{path:'/admin/v1/items',origin:'https://example.test'});
  assert.equal(normalizeHttpUrl('{param}'),undefined);
  assert.deepEqual(normalizeHttpUrl('/api/v1'),{path:'/api/v1'});
  assert.deepEqual(normalizeHttpUrl('https://backend.example/'),{path:'/',origin:'https://backend.example'});
});


test('mutated option fields, unknown computed overrides and null methods never silently become GET', () => {
  const result=fixture({'mutated.ts':`
    export function changed(){const options={method:'GET'};options.method='DELETE';return fetch('/api/v1/items',options);}
    export function computed(key){return fetch('/api/v1/items',{method:'POST',[key]:'DELETE'});}
    export function explicit(key){return fetch('/api/v1/items',{[key]:'DELETE',method:'PUT'});}
    export function nullable(){return fetch('/api/v1/items',{method:null});}
    export class Client { url='/api/v1/old'; constructor(){this.url=dynamic;} load(){return fetch(this.url);} }
  `});
  assert.deepEqual(routes(result,'Demo:mutated.ts#changed'),[]);
  assert.deepEqual(routes(result,'Demo:mutated.ts#computed'),[]);
  assert.deepEqual(routes(result,'Demo:mutated.ts#nullable'),[]);
  assert.deepEqual(routes(result,'mutated.Client.load'),[]);
  assert.deepEqual(routes(result,'Demo:mutated.ts#explicit'),['route:*:PUT:/api/v1/items']);
});


test('const transport aliases and imported pure helpers preserve static defaults without name-based recognition', () => {
  const result=fixture({
    'constants.ts':`export const endpoints={items:'/api/v1/items'};export function options(method='PATCH'){return {method};}`,
    'use.ts':`import * as constants from './constants';
      const transport=globalThis.fetch;
      function send(path=constants.endpoints.items, init=constants.options()){return transport(path,init);}
      export function call(){return send();}
      export function shadow(){const fetch=(x)=>x;const alias=fetch;return alias('/false');}
    `,
  });
  assert.deepEqual(routes(result,'Demo:use.ts#call'),['route:*:PATCH:/api/v1/items']);
  assert.deepEqual(routes(result,'Demo:use.ts#shadow'),[]);
});


test('an inferred default-object property never proves an unknown caller override', () => {
  const result=fixture({'defaults.ts':`
    function wrapped(path,options={method:'GET'}){return fetch(path,{method:options.method});}
    export function dynamic(options){return wrapped('/api/v1/items',options);}
    export function literal(){return wrapped('/api/v1/items',{method:'POST'});}
    export function defaulted(){return wrapped('/api/v1/items');}
  `});
  assert.deepEqual(routes(result,'Demo:defaults.ts#dynamic'),[]);
  assert.deepEqual(routes(result,'Demo:defaults.ts#literal'),['route:*:POST:/api/v1/items']);
  assert.deepEqual(routes(result,'Demo:defaults.ts#defaulted'),['route:*:GET:/api/v1/items']);
});


test('module initialization HTTP has explicit file ownership and origin query normalization', () => {
  const result=fixture({'startup.ts':`const url='https://api.example/api/v1/config?cache=1';export const config=fetch(url);`});
  assert.deepEqual(routes(result,'Demo:startup.ts'),['route:*:GET:/api/v1/config']);
  assert.deepEqual(normalizeHttpUrl('https://example.test?x=1'),{path:'/',origin:'https://example.test'});
});


test('same path and method with conflicting origins persists one ambiguous raw edge independent of source order', () => {
  const extract=(origins:string[])=>fixture({'origins.ts':`export function load(){${origins.map(origin=>`fetch('${origin}/api/v1/items');`).join('')}}`});
  const before=extract(['https://a.example','https://b.example']);
  const reversed=extract(['https://b.example','https://a.example']);
  assert.deepEqual(before.edges,reversed.edges);
  assert.equal(before.edges.length,1);
  const properties=before.edges[0].properties!;
  assert.equal(properties.binding_ambiguous,true);
  assert.equal(properties.binding_candidate_count,2);
  assert.deepEqual(properties.binding_candidates,[
    {http_method:'GET',origin:'https://a.example'},
    {http_method:'GET',origin:'https://b.example'},
  ]);
  assert.equal(properties.origin,undefined);
  assert.equal(properties.confidence_band,'low');
  assert.equal(before.coverage.direct,1);
  assert.equal(before.coverage.ambiguous,1);
  assert.equal(before.coverage.unresolved,1);
  assert.deepEqual(before.coverage,reversed.coverage);
  assert.ok(before.diagnostics.some(message=>message.includes('conflicting HTTP target evidence')));
});

test('same matching evidence deduplicates while different methods retain separate identities', () => {
  const result=fixture({'same.ts':`export function load(){
    fetch('https://API.example:443/api/v1/items?x=1');
    fetch('https://api.example/api/v1/items?x=2#fragment');
    fetch('https://api.example/api/v1/items',{method:'DELETE'});
  }`});
  assert.deepEqual(routes(result,'Demo:same.ts#load'),['route:*:DELETE:/api/v1/items','route:*:GET:/api/v1/items']);
  assert.ok(result.edges.every(edge=>edge.properties?.binding_ambiguous===undefined));
  assert.ok(result.edges.every(edge=>edge.properties?.origin==='https://api.example'));
  assert.equal(result.coverage.direct,2);
  assert.equal(result.coverage.ambiguous,0);
  assert.equal(result.coverage.unresolved,0);
  assert.deepEqual(normalizeHttpUrl('//API.example:80/api/v1/items'),{path:'/api/v1/items',origin:'//api.example:80'});
});

test('same normalized route with scoped and unscoped service evidence cannot retain an authoritative service hint', () => {
  const extract=(reverse:boolean)=>fixture({'services.ts':`
    @ApiContract({endpoint:'/api/v1/items',httpMethod:'POST'}) class Request {}
    export function load(service){${reverse?
      `service.send(new Request());fetch('/api/v1/items',{method:'POST'});`:
      `fetch('/api/v1/items',{method:'POST'});service.send(new Request());`}}
  `},'Catalog');
  const result=extract(false);
  assert.deepEqual(result.edges,extract(true).edges);
  assert.equal(result.edges.length,1);
  assert.equal(result.edges[0].properties?.binding_ambiguous,true);
  assert.equal(result.edges[0].properties?.service_name,undefined);
  assert.equal(result.edges[0].properties?.request_dto,undefined);
  assert.equal(result.coverage.ambiguous,1);
});

test('ambiguous target candidate evidence is bounded and canonical across many origins', () => {
  const origins=Array.from({length:15},(_,index)=>`https://host-${index.toString().padStart(2,'0')}.example`);
  const extract=(values:string[])=>fixture({'bounded.ts':`export function load(){${values.map(origin=>`fetch('${origin}/api/v1/items');`).join('')}}`});
  const result=extract(origins);
  assert.deepEqual(result.edges,extract([...origins].reverse()).edges);
  assert.equal(result.edges[0].properties?.binding_candidate_count,15);
  assert.equal((result.edges[0].properties?.binding_candidates as unknown[]).length,8);
  assert.equal(result.edges[0].properties?.origin,undefined);
  assert.equal(result.coverage.unresolved,1);
  assert.equal(result.coverage.direct,1);
});

test('unrelated branching UI/helper graphs never consume the HTTP wrapper expansion budget', () => {
  const noise = Array.from({length:40}, (_, index) => index === 39
    ? `function noise${index}(value){return value;}`
    : `function noise${index}(value){noise${index+1}(value+'a');return noise${index+1}(value+'b');}`).join('\n');
  const result=fixture({'noise.ts':`${noise}\nexport function load(){return fetch('/api/v1/items');}`});
  assert.equal(result.coverage.complete,true);
  assert.equal(result.coverage.bounded,0);
  assert.deepEqual(routes(result,'Demo:noise.ts#load'),['route:*:GET:/api/v1/items']);
  const stats=JSON.parse(result.diagnostics.find(message=>message.startsWith('HTTP analysis:'))!.slice('HTTP analysis:'.length));
  assert.equal(stats.relevant_roots,1);
  assert.equal(stats.summaries,1);
});

test('wrapper memoization ignores unrelated caller parameters but retains explicit substitutions', () => {
  const callers=Array.from({length:100},(_,index)=>`export function caller${index}(unrelated){return request('/api/v1/items');}`).join('\n');
  const result=fixture({'memo.ts':`function request(path){return fetch(path);}\n${callers}`});
  assert.equal(result.coverage.complete,true);
  assert.equal(result.coverage.wrapped,100);
  const stats=JSON.parse(result.diagnostics.find(message=>message.startsWith('HTTP analysis:'))!.slice('HTTP analysis:'.length));
  assert.equal(stats.summaries,102); // 100 callers, unknown request root, one shared known request.
  assert.equal(stats.cache_hits,99);
});

test('projected HTTP environments retain lexical URL and method captures, including destructured bindings', () => {
  const result=fixture({'captures.ts':`
    function wrap({path,method}){const inner=()=>fetch(path,{method});return inner();}
    export function load(){wrap({path:'/api/v1/first',method:'GET'});return wrap({path:'/api/v1/second',method:'DELETE'});}
  `});
  assert.equal(result.coverage.complete,true);
  assert.deepEqual(routes(result,'Demo:captures.ts#load'),['route:*:DELETE:/api/v1/second','route:*:GET:/api/v1/first']);
  assert.deepEqual(routes(result,'Demo:captures.ts#wrap.inner'),[]);
});


test('standalone nested callable analysis never assumes an unbound outer parameter default', () => {
  const result=fixture({'default-capture.ts':`
    function wrap(path='/api/v1/default'){const inner=()=>fetch(path);return inner();}
    export function load(){return wrap('/api/v1/actual');}
  `});
  assert.deepEqual(routes(result,'Demo:default-capture.ts#load'),['route:*:GET:/api/v1/actual']);
  assert.deepEqual(routes(result,'Demo:default-capture.ts#wrap.inner'),[]);
});

test('diamond wrapper paths retain endpoint scope with one deterministic transport witness', () => {
  const result=fixture({'diamond.ts':`
    function request(){return fetch('/api/v1/items');}
    function left(){return request();}function right(){return request();}
    export function load(){left();right();}
  `});
  const load=result.edges.find(edge=>edge.sourceQN==='Demo:diamond.ts#load')!;
  assert.deepEqual(load.properties?.wrapper_chain,['Demo:diamond.ts#left','Demo:diamond.ts#request']);
  assert.equal(result.coverage.wrapped,3);
  assert.equal(result.coverage.complete,true);
});

test('legacy ApiContract relative endpoints retain default POST without inventing browser fetch roots', () => {
  const result=fixture({'legacy.ts':`
    @ApiContract({endpoint:'PlaceBid'}) class PlaceBidRequest {}
    @ApiContract({endpoint:'api/v1/orders/PlaceBid?cache=1#receipt',httpMethod:'PUT'}) class VersionedRequest {}
    @ApiContract({endpoint:'https://api.example/admin/v1/bids?cache=1'}) class AbsoluteRequest {}
    export function legacy(service){return service.send(new PlaceBidRequest());}
    export function versioned(service){return service.send(new VersionedRequest());}
    export function absolute(service){return service.send(new AbsoluteRequest());}
    export function browser(){return fetch('PlaceBid');}
  `});
  assert.deepEqual(routes(result,'Demo:legacy.ts#legacy'),['route:*:POST:/PlaceBid']);
  assert.deepEqual(routes(result,'Demo:legacy.ts#versioned'),['route:*:PUT:/api/v1/orders/PlaceBid']);
  assert.deepEqual(routes(result,'Demo:legacy.ts#absolute'),['route:*:POST:/admin/v1/bids']);
  assert.equal(result.edges.find(edge=>edge.sourceQN==='Demo:legacy.ts#absolute')?.properties?.origin,'https://api.example');
  assert.deepEqual(routes(result,'Demo:legacy.ts#browser'),[]);
});

test('option writes through direct and nested static aliases cannot emit a stale method', () => {
  const result=fixture({'aliases.ts':`
    export function direct(){const options={method:'GET'};const alias=options;alias.method='DELETE';return fetch('/api/items',options);}
    export function nested(){const options={method:'GET'};const holder={options};const first=holder.options;const second=first;second.method='DELETE';return fetch('/api/items',options);}
    export function unchanged(){const options={method:'GET'};return fetch('/api/items',options);}
  `});
  assert.deepEqual(routes(result,'Demo:aliases.ts#direct'),[]);
  assert.deepEqual(routes(result,'Demo:aliases.ts#nested'),[]);
  assert.deepEqual(routes(result,'Demo:aliases.ts#unchanged'),['route:*:GET:/api/items']);
});

test('writes through imported named and namespace aliases invalidate only their source object', () => {
  const result=fixture({
    'options.ts':`export const options={method:'GET'};export const untouched={method:'POST'};`,
    'writer.ts':`import {options as imported} from './options';const first=imported;const nested={value:first};const alias=nested.value;alias.method='DELETE';`,
    'namespace.ts':`import * as config from './options';const alias=config.options;alias.method='PUT';`,
    'calls.ts':`import {options,untouched} from './options';export function stale(){return fetch('/api/items',options);}export function good(){return fetch('/api/items',untouched);}`,
  });
  assert.deepEqual(routes(result,'Demo:calls.ts#stale'),[]);
  assert.deepEqual(routes(result,'Demo:calls.ts#good'),['route:*:POST:/api/items']);
});

test('bound option parameters and their aliases are guarded before environment lookup', () => {
  const result=fixture({'parameters.ts':`
    function direct(options){options.method='DELETE';return fetch('/api/items',options);}
    function aliased(options){const alias=options;alias.method='DELETE';return fetch('/api/items',options);}
    function methodValue(method){method='DELETE';return fetch('/api/items',{method});}
    export function caller(){direct({method:'GET'});aliased({method:'GET'});methodValue('GET');}
  `});
  assert.deepEqual(routes(result,'Demo:parameters.ts#caller'),[]);
  assert.ok(result.coverage.unresolved>=3);
});


test('destructured object and array aliases retain the selected underlying option binding', () => {
  const result=fixture({'destructure.ts':`
    export function objectWrite(){const options={method:'GET'};const holder={options};const {options:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function arrayWrite(){const options={method:'GET'};const holder=[options];const [alias]=holder;alias.method='DELETE';return fetch('/api',options);}
    export function restWrite(){const options={method:'GET'};const holder={options};const {...alias}=holder;alias.options.method='DELETE';return fetch('/api',options);}
    export function deepWrite(){const options={method:'GET'};const holder={nested:{value:options}};const {nested:{value:alias}}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function immutable(){const options={method:'GET'};const holder={options};const {options:alias}=holder;return fetch('/api',options);}
  `});
  for (const name of ['objectWrite','arrayWrite','deepWrite','restWrite']) assert.deepEqual(routes(result,`Demo:destructure.ts#${name}`),[]);
  assert.deepEqual(routes(result,'Demo:destructure.ts#immutable'),['route:*:GET:/api']);
});

test('nested container member writes retain bound parameter identity before caller substitution', () => {
  const result=fixture({'bound-container.ts':`
    function mutate(options){const holder={options};holder.options.method='DELETE';return fetch('/api',options);}
    function deep(options){const holder={nested:{value:options}};const alias=holder.nested;alias.value.method='DELETE';return fetch('/api',options);}
    function destructured(options){const holder={value:options};const {value:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    function immutable(options){const holder={options};return fetch('/api',holder.options);}
    export function caller(){mutate({method:'GET'});deep({method:'GET'});destructured({method:'GET'});}
    export function good(){return immutable({method:'GET'});}
  `});
  assert.deepEqual(routes(result,'Demo:bound-container.ts#caller'),[]);
  assert.deepEqual(routes(result,'Demo:bound-container.ts#good'),['route:*:GET:/api']);
});


test('rebinding a contained local never poisons an immutable captured endpoint or options object', () => {
  const result=fixture({'rebindings.ts':`
    function wrapper({path}){let scratch;scratch={id:'one'};const holder={path,scratch};return fetch(path);}
    export function caller(){return wrapper({path:'/api/items'});}
    export function local(){const options={method:'GET'};let scratch;const holder={options,scratch};scratch={id:'two'};return fetch('/api/items',options);}
  `});
  assert.deepEqual(routes(result,'Demo:rebindings.ts#caller'),['route:*:GET:/api/items']);
  assert.deepEqual(routes(result,'Demo:rebindings.ts#local'),['route:*:GET:/api/items']);
});


test('mutable object siblings and external instance member types never alias an immutable endpoint', () => {
  const result=fixture({'independent.ts':`
    function wrapper({path,options}){const holder={options};holder.options.method='DELETE';return fetch(path,{method:'POST'});}
    export function caller(){return wrapper({path:'/api/items',options:{method:'GET'}});}
    export function instances(){const one=new AbortController();const two=new AbortController();const options={method:'GET',signal:one.signal};const other={signal:two.signal};two.signal.onabort=()=>{};return fetch('/api/items',options);}
  `});
  assert.deepEqual(routes(result,'Demo:independent.ts#caller'),['route:*:POST:/api/items']);
  assert.deepEqual(routes(result,'Demo:independent.ts#instances'),['route:*:GET:/api/items']);
});


test('ChatWorks SSE wrappers keep immutable URL and POST independent from written header/body siblings', () => {
  const result=fixture({'sse.ts':`
    function buildBase(status){return 'https://chat.example';}
    async function streamChatCompletion({url,headers,body,onUpdate,signal}){return fetch(url,{method:'POST',headers,body:JSON.stringify(body),signal});}
    export function ChatScreen(auth){const url=buildBase(status)+'/v1/chat/completions';const headers={'Content-Type':'application/json'};if(auth)headers.Authorization='Bearer token';const body={messages:[]};body.temperature=0.8;return streamChatCompletion({url,headers,body,onUpdate:()=>{},signal});}
    function forwarded(url,headers){headers.Authorization='Bearer token';return streamChatCompletion({url,headers,body:{},onUpdate:()=>{}});}
    export function second(){return forwarded('/v1/chat/completions',{});}
  `});
  assert.deepEqual(routes(result,'Demo:sse.ts#ChatScreen'),['route:*:POST:/v1/chat/completions']);
  assert.deepEqual(routes(result,'Demo:sse.ts#second'),['route:*:POST:/v1/chat/completions']);
});


test('computed object keys preserve mutation identity, with unknown keys retaining receiver uncertainty', () => {
  const result=fixture({'computed-members.ts':`
    export function literal(){const options={method:'GET'};const holder={['options']:options};const {options:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function computedBinding(){const options={method:'GET'};const holder={options};const {['options']:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function unknown(key){const first={method:'GET'};const second={method:'POST'};const holder={options:first,[key]:second};const {options:alias}=holder;alias.method='DELETE';fetch('/api',first);return fetch('/api',second);}
    export function unknownBinding(key){const options={method:'GET'};const holder={options};const {[key]:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function immutable(){const options={method:'GET'};const holder={['options']:options};const {options:alias}=holder;return fetch('/api',options);}
  `});
  for(const name of ['literal','computedBinding','unknown','unknownBinding'])assert.deepEqual(routes(result,`Demo:computed-members.ts#${name}`),[]);
  assert.deepEqual(routes(result,'Demo:computed-members.ts#immutable'),['route:*:GET:/api']);
});

test('array/object spread and rest selections cannot hide writes to their contained options', () => {
  const result=fixture({'spread-members.ts':`
    export function array(){const options={method:'GET'};const array=[options];const holder=[...array];const [alias]=holder;alias.method='DELETE';return fetch('/api',options);}
    export function unknownOffset(unknownArray){const options={method:'GET'};const holder=[...unknownArray,options];const [alias]=holder;alias.method='DELETE';return fetch('/api',options);}
    export function knownBefore(){const options={method:'GET'};const safe=[{}];const holder=[...safe,options];const [,alias]=holder;alias.method='DELETE';return fetch('/api',options);}
    export function offset(){const options={method:'GET'};const array=[{method:'POST'},options];const holder=[{},...array];const [,,alias]=holder;alias.method='DELETE';return fetch('/api',options);}
    export function rest(){const options={method:'GET'};const array=[null,options];const [,...tail]=array;const [alias]=tail;alias.method='DELETE';return fetch('/api',options);}
    export function object(){const options={method:'GET'};const other={options};const holder={...other};const {options:alias}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function immutable(){const options={method:'GET'};const holder=[...[options]];const [alias]=holder;return fetch('/api',options);}
  `});
  for(const name of ['array','offset','rest','object','unknownOffset','knownBefore'])assert.deepEqual(routes(result,`Demo:spread-members.ts#${name}`),[]);
  assert.deepEqual(routes(result,'Demo:spread-members.ts#immutable'),['route:*:GET:/api']);
});


test('uncertain member identities never become exact siblings on a later nested selection', () => {
  const result=fixture({'uncertain-members.ts':`
    export function destructured(key){const options={method:'GET'};const wrong={method:'POST'};const holder={[key]:{value:options},value:wrong};const {nested:{value:alias}}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function property(key){const options={method:'GET'};const wrong={method:'POST'};const holder={[key]:{value:options},value:wrong};const alias=holder.nested.value;alias.method='DELETE';return fetch('/api',options);}
    export function spread(){const options={method:'GET'};const wrong={method:'POST'};const possible={nested:{value:options}};const holder={...possible,value:wrong};const {nested:{value:alias}}=holder;alias.method='DELETE';return fetch('/api',options);}
    export function forwarded(key){const options={method:'GET'};const holder={[key]:{value:options},value:{method:'POST'}};const intermediate=holder.nested;const alias=intermediate.value;alias.method='DELETE';return fetch('/api',options);}
  `});
  for(const name of ['destructured','property','spread','forwarded'])assert.deepEqual(routes(result,`Demo:uncertain-members.ts#${name}`),[]);
});

test('nested sibling writes preserve captured URLs and methods through direct and imported aliases', () => {
  const result=fixture({
    'urls.ts':`export const url='/api/imported';`,
    'siblings.ts':`import {url as importedUrl} from './urls';
      export function direct(){const url='/api/items';const holder={url,options:{}};holder.options.headers={};return fetch(url);}
      export function alias(){const url='/api/items';const holder={url,nested:{options:{}}};const alias=holder.nested;alias.options.headers={};return fetch(url);}
      export function imported(){const holder={url:importedUrl,options:{}};holder.options.headers={};return fetch(importedUrl);}
      export function oneLevel(){const url='/api/items';const options={url,headers:{}};options.headers={};return fetch(url);}
      export function capturedMethod(){const method='POST';const options={method,headers:{}};options.headers={};return fetch('/api/items',{method});}
    `,
  });
  for(const name of ['direct','alias','oneLevel'])assert.deepEqual(routes(result,`Demo:siblings.ts#${name}`),['route:*:GET:/api/items']);
  assert.deepEqual(routes(result,'Demo:siblings.ts#imported'),['route:*:GET:/api/imported']);
  assert.deepEqual(routes(result,'Demo:siblings.ts#capturedMethod'),['route:*:POST:/api/items']);
});

test('nested literal and bound-object reads mask changed fields while retaining independent sibling paths', () => {
  const result=fixture({'bound-siblings.ts':`
    export function literal(){const holder={url:'/api/items',options:{method:'GET'}};holder.options.method='DELETE';fetch(holder.url,{method:'POST'});return fetch('/api/items',holder.options);}
    function bound(holder){holder.options.method='DELETE';fetch(holder.url,{method:'POST'});return fetch('/api/items',holder.options);}
    function request(options){options.headers={};return fetch(options.url,{method:'POST'});}
    function destructured({url,options}){const holder={url,options};holder.options.headers={};return fetch(url);}
    export function caller(){bound({url:'/api/items',options:{method:'GET'}});request({url:'/api/second',headers:{}});destructured({url:'/api/third',options:{}});}
    export function addedMethod(){const options={};options.method='DELETE';return fetch('/api/items',options);}
  `});
  assert.deepEqual(routes(result,'Demo:bound-siblings.ts#literal'),['route:*:POST:/api/items']);
  assert.deepEqual(routes(result,'Demo:bound-siblings.ts#caller'),['route:*:GET:/api/third','route:*:POST:/api/items','route:*:POST:/api/second']);
  assert.deepEqual(routes(result,'Demo:bound-siblings.ts#addedMethod'),[]);
});


test('Object.assign mutates the whole selected object argument rather than its parent member slot', () => {
  const result=fixture({'assign-targets.ts':`
    export function direct(){const options={method:'GET'};Object.assign(options,{method:'DELETE'});return fetch('/api',options);}
    export function nested(){const options={method:'GET'};const holder={options};Object.assign(holder.options,{method:'DELETE'});return fetch('/api',options);}
    export function computed(){const options={method:'GET'};const holder={options};Object.assign(holder['options'],{method:'DELETE'});return fetch('/api',options);}
    function bound(options){const holder={nested:{options}};Object.assign(holder.nested.options,{method:'DELETE'});return fetch('/api',options);}
    export function caller(){return bound({method:'GET'});}
    export function sibling(){const url='/api/items';const options={url,method:'POST',headers:{}};Object.assign(options.headers,{Authorization:'token'});return fetch(url,{method:options.method});}
  `});
  for(const name of ['direct','nested','computed','caller'])assert.deepEqual(routes(result,`Demo:assign-targets.ts#${name}`),[]);
  assert.deepEqual(routes(result,'Demo:assign-targets.ts#sibling'),['route:*:POST:/api/items']);
});
