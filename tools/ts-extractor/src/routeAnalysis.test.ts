import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { Project, ScriptTarget, ModuleKind, ts } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { DiscoveryScope } from './discoveryScope';
import { analyzeWorkspaceImports } from './workspaceResolver';
import { analyzeRoutes } from './routeAnalysis';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';

function fixture(files: Record<string, string>, excluded: string[] = []) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-routes-'));
  for (const [name, content] of Object.entries({ 'tsconfig.json': '{"compilerOptions":{"allowJs":true,"jsx":"preserve","experimentalDecorators":true}}', ...files })) {
    fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true }); fs.writeFileSync(path.join(root, name), content);
  }
  const analyze = () => {
    const inputs = ['tsconfig.json', ...Object.keys(files)].filter((name, index, all) => all.indexOf(name) === index && !excluded.includes(name) && fs.existsSync(path.join(root, name)));
    const scope = new DiscoveryScope(root, inputs);
    const project = new Project({ skipAddingFilesFromTsConfig: true, skipFileDependencyResolution: true,
      compilerOptions: { allowJs: true, target: ScriptTarget.ES2020, module: ModuleKind.ESNext, jsx: ts.JsxEmit.Preserve, experimentalDecorators: true, noResolve: true } });
    for (const name of inputs) if (/\.(?:tsx?|jsx?)$/.test(name)) project.createSourceFile(path.join(root, name), fs.readFileSync(path.join(root, name), 'utf8'), { overwrite: true });
    const sources = project.getSourceFiles();
    const resolution = analyzeWorkspaceImports('Fixture', root, path.join(root, 'tsconfig.json'), sources, () => {}, scope);
    const diagnostics: string[] = [];
    const model = new SemanticModel('Fixture', root, sources, resolution.resolvedImports, message => diagnostics.push(message));
    const nodes: GraphNodeDto[] = [], edges: PendingEdgeDto[] = [];
    model.extract(nodes, edges, []);
    const status = analyzeRoutes(model, nodes, edges, scope, message => diagnostics.push(message));
    return { nodes, edges, model, diagnostics, status, resolution, routes: nodes.filter(node => node.properties.route_kind === 'ui') };
  };
  return { root, analyze, dispose: () => fs.rmSync(root, { recursive: true, force: true }),
    write: (name: string, content: string) => { files[name] = content; fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true }); fs.writeFileSync(path.join(root, name), content); } };
}
function route(result: ReturnType<ReturnType<typeof fixture>['analyze']>, fullPath: string, framework?: string) {
  const value = result.routes.find(node => node.properties.full_path === fullPath && (!framework || node.properties.framework === framework));
  assert.ok(value, `Expected ${framework ?? ''} ${fullPath}; got ${result.routes.map(node => node.qualifiedName)}; ${result.diagnostics}`); return value;
}

test('CodeGraph/CodeFlow Angular lazy trees reuse aliases, barrels, named/default exports and routing NgModules', () => {
  const f = fixture({
    'CodeGraphWeb/package.json': '{}',
    'CodeGraphWeb/tsconfig.json': '{"compilerOptions":{"baseUrl":".","paths":{"@pages/*":["src/pages/*"]}}}',
    'CodeGraphWeb/src/app.routes.ts': `import { provideRouter as setup, Routes } from '@angular/router';
      import { Shell } from './shell'; import { details } from './children';
      const routes: Routes = [{path: '', component: Shell, children: [
        {path: 'repositories', loadChildren: () => import('@pages/repositories').then(m => m.RepositoryModule)},
        {path: 'ask', loadComponent: () => import('./ask')}, ...details,
        {path: 'old', redirectTo: 'ask'}, {path: '**', redirectTo: 'ask'} ]}]; setup(routes);`,
    'CodeGraphWeb/src/shell.ts': 'export class Shell {}',
    'CodeGraphWeb/src/ask.ts': 'export default class Ask {}',
    'CodeGraphWeb/src/children.ts': `import { Detail } from './detail'; export const details = [{path:'details/:id', component: Detail}];`,
    'CodeGraphWeb/src/detail.ts': 'export class Detail {}',
    'CodeGraphWeb/src/pages/repositories.ts': `import { NgModule } from '@angular/core'; import { RepositoryRouting } from './routing';
      @NgModule({imports: [RepositoryRouting]}) export class RepositoryModule {}`,
    'CodeGraphWeb/src/pages/routing.ts': `import { NgModule } from '@angular/core'; import { RouterModule } from '@angular/router'; import { List } from './list';
      const leaf = [{path:'list',component:List}]; const routes = [{path:'',children:[...leaf]}];
      @NgModule({imports:[RouterModule.forChild(routes)]}) export class RepositoryRouting {}`,
    'CodeGraphWeb/src/pages/list.ts': 'export class List {}',
  });
  try {
    const result = f.analyze(); assert.equal(result.status.complete, true, result.diagnostics.join('\n'));
    const list = route(result, '/repositories/list', 'angular');
    assert.equal(list.filePath, 'CodeGraphWeb/src/pages/routing.ts');
    assert.equal(list.properties.app_root, 'CodeGraphWeb');
    assert.ok(!result.routes.some(node => node.properties.full_path === '/list'));
    assert.ok(result.edges.some(edge => edge.sourceQN === list.qualifiedName && edge.type === 'HANDLES' && edge.targetQN.endsWith('pages.list.List')));
    assert.ok(result.edges.some(edge => edge.sourceQN.endsWith('shell.Shell') && edge.type === 'RENDERS' && edge.targetQN.endsWith('pages.list.List')));
    assert.ok(result.edges.some(edge => edge.sourceQN === route(result, '/repositories').qualifiedName && edge.properties?.lazy === true && edge.targetQN.endsWith('repositories.RepositoryModule')));
    assert.ok(result.edges.some(edge => edge.sourceQN === route(result, '/ask').qualifiedName && edge.targetQN.endsWith('ask.Ask')));
    assert.equal(route(result, '/old').properties.redirect_to, 'ask'); route(result, '/**'); route(result, '/details/:id');
    for (const node of result.routes) { assert.equal(node.properties.http_method, undefined); assert.equal(node.properties.extraction_source_file, node.filePath); }
  } finally { f.dispose(); }
});

test('Angular forRoot, literal children, default lazy arrays, legacy strings and imported module cycles are bounded', () => {
  const f = fixture({
    'codeflow-ui/package.json': '{}',
    'codeflow-ui/src/root.ts': `import {RouterModule} from '@angular/router'; const ignored = [{path:'fake'}];
      RouterModule.forRoot([{path:'legacy',loadChildren:'./legacy#LegacyModule'}, {path:'modern',loadChildren:()=>import('./modern')}, {path:'outside',loadChildren:()=>import('./excluded')}]);`,
    'codeflow-ui/src/legacy.ts': `import {NgModule} from '@angular/core'; import {RouterModule} from '@angular/router'; import {Cycle} from './cycle'; import {Page} from './page';
      @NgModule({imports:[Cycle, RouterModule.forChild([{path:'child',component:Page}])]}) export class LegacyModule {}`,
    'codeflow-ui/src/cycle.ts': `import {NgModule} from '@angular/core'; import {LegacyModule} from './legacy'; @NgModule({imports:[LegacyModule]}) export class Cycle {}`,
    'codeflow-ui/src/page.ts': 'export class Page {}',
    'codeflow-ui/src/modern.ts': `import {Page} from './page'; export default [{path:'child',component:Page}];`,
    'codeflow-ui/src/excluded.ts': `export default [{path:'secret'}];`,
  }, ['codeflow-ui/src/excluded.ts']);
  try {
    const result = f.analyze(); route(result, '/legacy/child'); route(result, '/modern/child');
    assert.ok(!result.routes.some(node => ['/child', '/fake', '/outside/secret'].includes(String(node.properties.full_path))));
    assert.equal(result.status.complete, false);
    assert.ok(result.resolution.resolvedImports.some(item => item.importedNamespace === './legacy' && item.classification === 'local_file'));
    assert.ok(!result.model.byPath.has('codeflow-ui/src/excluded.ts'));
  } finally { f.dispose(); }
});

test('twitter-clone imported React Router JSX handles nested/index/pathless layouts, basename, redirects and duplicate app paths', () => {
  const f = fixture({
    'apps/twitter/package.json': '{}', 'apps/admin/package.json': '{}',
    'apps/twitter/src/pages.tsx': 'export function Feed(){return <main/>} export function Profile(){return <main/>} export function Layout(){return <main/>}',
    'apps/twitter/src/App.tsx': `import {BrowserRouter as Browser, Routes, Route as PageRoute, Navigate} from 'react-router-dom'; import {Feed, Profile, Layout} from './pages';
      export function App(){return <Browser basename='/ui'><Routes><PageRoute element={<Layout/>}>
        <PageRoute index element={<Feed/>}/><PageRoute path='/users/:id' element={<Profile/>}/>
        <PageRoute path='settings'><PageRoute path='*' element={<Feed/>}/></PageRoute>
        <PageRoute path='old' element={<Navigate to='/users/me'/>}/></PageRoute></Routes></Browser>}`,
    'apps/admin/src/App.tsx': `import {Route,Routes} from 'react-router-dom'; function Admin(){return <main/>} export function App(){return <Routes><Route path='/ui/users/:id' element={<Admin/>}/></Routes>}`,
    'apps/twitter/src/fake.tsx': `function Route(props:any){return null}; export function Fake(){return <Route path='fake'/>}`,
    'apps/twitter/src/screens.tsx': `export function Screens({activeView}){return activeView === 'settings' ? <section/> : <main/>}`,
  });
  try {
    const result = f.analyze(); assert.equal(result.status.complete, true);
    route(result, '/ui'); route(result, '/ui/settings/*'); assert.equal(route(result, '/ui/old').properties.redirect_to, '/users/me');
    const profiles = result.routes.filter(node => node.properties.full_path === '/ui/users/:id'); assert.equal(profiles.length, 2);
    assert.notEqual(profiles[0].qualifiedName, profiles[1].qualifiedName);
    assert.ok(result.edges.some(edge => edge.type === 'RENDERS' && edge.sourceQN.endsWith('#Layout') && edge.targetQN.endsWith('#Profile')));
    assert.ok(!result.routes.some(node => /fake|activeView/.test(node.qualifiedName)));
  } finally { f.dispose(); }
});

test('React Router object declarations compose children/spreads and Component handlers through imported APIs', () => {
  const f = fixture({
    'src/App.tsx': `import {createBrowserRouter} from 'react-router-dom'; import {Page,Layout} from './page';
      const children = [{path:'items/:id',Component: Page}, {index:true,element:<Page/>}];
      const routes = [{path:'',element:<Layout/>,children:[...children]}]; const router = createBrowserRouter(routes,{basename:'/console'});`,
    'src/page.tsx': `export function Page(){return <p/>} export function Layout(){return <main/>}`,
  });
  try { const result = f.analyze(); route(result, '/console/items/:id'); assert.equal(route(result, '/console').properties.index, true); }
  finally { f.dispose(); }
});

test('router Next App Router reads static /ui config and joins groups, dynamic segments, catchalls and nested layouts', () => {
  const f = fixture({
    'frontend/package.json': '{"dependencies":{"next":"15"}}',
    'frontend/next.config.ts': `const isDev=process.env.NODE_ENV==='development'; const config = {...(isDev ? {rewrites:async()=>process.env.DYNAMIC} : {output:'export'}),basePath:'/ui'}; export default config;`,
    'frontend/src/app/layout.tsx': 'export default function Root({children}){return <html>{children}</html>}',
    'frontend/src/app/(dashboard)/layout.tsx': 'export default ({children}) => <main>{children}</main>;',
    'frontend/src/app/(dashboard)/users/[id]/page.tsx': 'export default function Page(){return <p/>}',
    'frontend/src/app/docs/[...slug]/page.jsx': 'export default function Docs(){return <article/>}',
    'frontend/src/app/search/[[...query]]/page.tsx': 'export default () => <article/>;',
    'frontend/src/app/api/users/route.ts': 'export function GET(){ return Response.json([]); }',
  });
  try {
    const result = f.analyze(); assert.equal(result.status.complete, true, result.diagnostics.join('\n'));
    const page = route(result, '/ui/users/[id]', 'nextjs'); route(result, '/ui/docs/[...slug]'); route(result, '/ui/search/[[...query]]');
    assert.ok(!result.routes.some(node => String(node.properties.full_path).includes('/api/')));
    assert.equal(page.filePath, 'frontend/src/app/(dashboard)/users/[id]/page.tsx');
    const layoutEdges = result.edges.filter(edge => edge.sourceQN === page.qualifiedName && edge.type === 'RENDERS'); assert.equal(layoutEdges.length, 2);
    assert.ok(result.edges.some(edge => edge.type === 'RENDERS' && edge.sourceQN.endsWith('layout.tsx#default') && edge.targetQN.endsWith('page.tsx#Page')));
  } finally { f.dispose(); }
});

test('Next dynamic configuration is unresolved, scoped configurations cannot be read, and static known basePath survives dynamic unrelated fields', () => {
  const f = fixture({
    'frontend/package.json': '{"dependencies":{"next":"15"}}',
    'frontend/next.config.ts': `export default {basePath:process.env.BASE};`,
    'frontend/src/app/page.tsx': 'export default function Page(){return <p/>}',
    'other/next.config.ts': `throw new Error('must not execute'); export default {basePath:'/forbidden'};`,
    'other/src/app/page.tsx': 'export default function Hidden(){return <p/>}',
  }, ['other/next.config.ts', 'other/src/app/page.tsx']);
  try {
    let result = f.analyze(); assert.equal(result.status.complete, false); assert.equal(result.routes.length, 0); assert.ok(result.diagnostics.some(message => message.includes('basePath unknown')));
    f.write('frontend/next.config.ts', `export default {basePath:'/ui',...dynamicOtherConfig};`);
    result = f.analyze(); assert.equal(result.status.complete, true); route(result, '/ui'); assert.ok(!result.diagnostics.some(message => message.includes('other/')));
  } finally { f.dispose(); }
});

test('parent route and layout-only edits recompute source-owned dependent facts; page default export identity stays stable', () => {
  const f = fixture({
    'frontend/package.json': '{"dependencies":{"next":"15"}}',
    'frontend/next.config.ts': `export default {basePath:'/ui'};`,
    'frontend/src/app/layout.tsx': 'export default function Layout({children}){return <main>{children}</main>}',
    'frontend/src/app/page.tsx': 'export default () => <p/>;',
    'angular/package.json': '{}',
    'angular/root.ts': `import {provideRouter} from '@angular/router'; import {routes} from './child'; provideRouter([{path:'before',children:routes}]);`,
    'angular/child.ts': `import {Page} from './page'; export const routes=[{path:'child',component:Page}];`,
    'angular/page.ts': 'export class Page {}',
  });
  try {
    const before = f.analyze(); const old = route(before, '/before/child'); const page = route(before, '/ui');
    const defaultQN = before.edges.find(edge => edge.sourceQN === page.qualifiedName && edge.type === 'HANDLES')!.targetQN;
    f.write('angular/root.ts', `import {provideRouter} from '@angular/router'; import {routes} from './child'; provideRouter([{path:'after',children:routes}]);`);
    f.write('frontend/src/app/layout.tsx', 'export default function NewLayout({children}){return <main>{children}</main>}');
    const after = f.analyze(); const child = route(after, '/after/child'); assert.equal(child.filePath, old.filePath);
    assert.ok(!after.routes.some(node => node.qualifiedName === old.qualifiedName));
    assert.equal(after.edges.find(edge => edge.sourceQN === page.qualifiedName && edge.type === 'HANDLES')!.targetQN, defaultQN);
    assert.ok(after.edges.some(edge => edge.sourceQN === page.qualifiedName && edge.targetQN.endsWith('#NewLayout')));
    assert.ok(!after.edges.some(edge => edge.sourceQN === page.qualifiedName && edge.targetQN.endsWith('#Layout')));
    assert.deepEqual(after.routes, f.analyze().routes);
  } finally { f.dispose(); }
});

test('lazy identifier/async loaders and JSX router factories retain static exports and basename', () => {
  const f = fixture({
    'angular/root.ts': `import {provideRouter} from '@angular/router';
      const loader = async () => (await import('./children')).routes;
      provideRouter([{path:'async',loadChildren:loader},{path:'destructured',loadChildren:()=>import('./children').then(({routes: children})=>children)}]);`,
    'angular/children.ts': `import {Page} from './page'; export const routes=[{path:'child',component:Page}];`,
    'angular/page.ts': 'export class Page {}',
    'react/App.tsx': `import {createBrowserRouter, createRoutesFromElements, Route} from 'react-router-dom';
      function Page(){return <main/>}; const router=createBrowserRouter(createRoutesFromElements(<Route path='/users/:id' element={<Page/>}/>),{basename:'/ui'});`,
  });
  try {
    const result = f.analyze(); assert.equal(result.status.complete, true, result.diagnostics.join('\n'));
    route(result, '/async/child'); route(result, '/destructured/child'); route(result, '/ui/users/:id');
  } finally { f.dispose(); }
});

test('dynamic arrays and cyclic spreads report incomplete enumeration instead of successful empty routes', () => {
  const f = fixture({
    'src/routes.ts': `import {provideRouter} from '@angular/router'; const shared=[...shared]; provideRouter([{path:'known',children:[...shared]}]);`,
    'src/App.tsx': `import {createBrowserRouter} from 'react-router-dom'; const value=unknownRoutes(); createBrowserRouter(value);`,
  });
  try { const result = f.analyze(); assert.equal(result.status.complete, false); route(result, '/known'); assert.ok(result.diagnostics.some(message => /dynamic|cyclic/.test(message))); }
  finally { f.dispose(); }
});
