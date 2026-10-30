import { strict as assert } from 'assert';
import { test } from 'node:test';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Project } from 'ts-morph';
import { analyzeAngular } from './angularAnalysis';
import { extractProject } from './extractor';
import { SemanticModel } from './semanticModel';
import { analyzeWorkspaceImports } from './workspaceResolver';
import { DiscoveryScope } from './discoveryScope';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';

function fixture(sources: Record<string, string>, includedFiles = Object.keys(sources)) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-angular-'));
  for (const [file, contents] of Object.entries(sources)) { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), contents); }
  function extract(included = includedFiles) {
    const project = new Project({ compilerOptions: { noResolve: true, experimentalDecorators: true, types: [], skipLibCheck: true }, skipAddingFilesFromTsConfig: true });
    for (const file of included.filter(file => /\.tsx?$/.test(file))) project.addSourceFileAtPath(path.join(root, file));
    const discovery = new DiscoveryScope(root, included);
    const diagnostics: string[] = [];
    const imports = analyzeWorkspaceImports('Fixture', root, '', project.getSourceFiles(), message => diagnostics.push(message), discovery);
    const model = new SemanticModel('Fixture', root, project.getSourceFiles(), imports.resolvedImports, message => diagnostics.push(message));
    const nodes: GraphNodeDto[] = []; const edges: PendingEdgeDto[] = [];
    for (const [declaration, qualifiedName] of model.declarations) {
      if (!['ClassDeclaration', 'InterfaceDeclaration'].includes(declaration.getKindName())) continue;
      nodes.push({ label: declaration.getKindName() === 'ClassDeclaration' ? 'Class' : 'Interface', name: qualifiedName.split('.').pop()!, qualifiedName, filePath: model.relative(declaration), startLine: 1, endLine: 1, properties: {} });
    }
    model.extract(nodes, edges, []);
    const result = analyzeAngular(model, nodes, edges, discovery, message => diagnostics.push(message));
    return { ...result, nodes, edges, diagnostics, model };
  }
  return { root, extract, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function matching(edges: PendingEdgeDto[], type: string, source: string, target?: string) {
  return edges.filter(edge => edge.type === type && edge.sourceQN.endsWith(source) && (!target || edge.targetQN.endsWith(target)));
}

test('Angular template selectors, defer blocks and static outlets use actual components and scoped external HTML', () => {
  const f = fixture({
    'src/children.ts': `import { Component as C } from '@angular/core';
      @C({ selector: 'app-child', template: '' }) export class Child {}
      @C({ selector: 'button[app-action]:not([disabled])', template: '' }) export class Action {}
      @C({ selector: '[app-panel]', template: '' }) export class Panel {}
      @C({ selector: 'app-comment', template: '' }) export class Comment {}
      export class NotAComponent {}`,
    'src/app.ts': `import { Component } from '@angular/core'; import { Child as Selected, Action, Panel, Comment } from './children';
      @Component({selector:'app-root', imports: [Selected, Action, Panel, Comment], templateUrl: './app.html'}) export class App { panel = Selected; }
      function ComponentFake(x:any){return x} @ComponentFake({selector:'app-pretender',template:''}) export class Pretender {}`,
    'src/app.html': `<!-- <app-comment/> --><div title="<app-comment>"></div>@defer { <app-child/> }
      <button app-action></button><button app-action disabled></button><div [app-panel]="true"></div>
      <ng-container *ngComponentOutlet="panel"></ng-container><app-pretender/>`,
    'src/excluded.ts': `import {Component} from '@angular/core'; @Component({selector:'app-child',template:''}) export class Child {}`,
  }, ['src/app.ts', 'src/children.ts', 'src/app.html']);
  try {
    const result = f.extract();
    assert.equal(result.complete, true);
    assert.deepEqual(result.processedTemplates, ['src/app.html']);
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Child').length, 2);
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Action').length, 1);
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Panel').length, 1);
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Comment').length, 0);
    assert.ok(result.edges.filter(edge => edge.type === 'RENDERS').every(edge => edge.properties?.template_path === 'src/app.html'));
    assert.equal(result.nodes.find(node => node.name === 'Pretender')?.label, 'Class');
    const excluded = f.extract(['src/app.ts', 'src/children.ts']);
    assert.equal(excluded.complete, true);
    assert.equal(matching(excluded.edges, 'RENDERS', 'App').length, 0);
    assert.ok(excluded.diagnostics.some(message => message.startsWith('angularTemplateScope:')));
    fs.unlinkSync(path.join(f.root, 'src/app.html'));
    assert.equal(f.extract().complete, false, 'an unreadable admitted template is incomplete');
    assert.equal(f.extract(['src/app.ts', 'src/children.ts']).complete, true, 'a removed/excluded template permits recomputation');
  } finally { f.dispose(); }
});

test('Angular dynamic component APIs resolve import aliases, typed receivers and static class fields, excluding unrelated open/createComponent', () => {
  const f = fixture({ 'app.ts': `import { Component, createComponent as create, ViewContainerRef, inject } from '@angular/core';
    import {MatDialog} from '@angular/material/dialog'; import {MatSnackBar} from '@angular/material/snack-bar';
    @Component({selector:'app-dialog',template:''}) class Dialog {}
    @Component({selector:'app-other',template:''}) class Other {}
    class Entries { static dialog = Dialog; }
    @Component({selector:'app-root',template:''}) class App {
      dialog = inject(MatDialog); snackbar = inject(MatSnackBar);
      constructor(private container: ViewContainerRef) {}
      show(){ create(Dialog); this.container.createComponent(Dialog); this.dialog.open(Dialog); this.snackbar.openFromComponent(Dialog); create(Entries.dialog); }
      unrelated(){ const dialog={open(x:any){}}; dialog.open(Other); const own={createComponent(x:any){}}; own.createComponent(Other); }
    }` });
  try {
    const result = f.extract();
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Dialog').length, 4, JSON.stringify({edges:result.edges.filter(edge=>edge.type==='RENDERS'),diagnostics:result.diagnostics}));
    assert.equal(matching(result.edges, 'RENDERS', 'App', 'Other').length, 0);
    assert.equal(result.diagnostics.filter(message => message.startsWith('angularDynamicComponent:')).length, 0);
  } finally { f.dispose(); }
});

const contract = `import {InjectionToken} from '@angular/core';
  export interface Storage { data: string; load(): string; save: (value:string) => void; }
  export const PRIMARY = new InjectionToken<Storage>('primary'); export const SECONDARY = new InjectionToken<Storage>('secondary');
  export const ALIAS = new InjectionToken<Storage>('alias');
  export class Primary implements Storage {data='';load(){return this.data}save=(value:string)=>{this.data=value};}
  export class Secondary implements Storage {data='';load(){return this.data}save=(value:string)=>{this.data=value};}`;

test('token-specific implementations and aliases respect component overrides and isolated NgModule providers', () => {
  const f = fixture({
    'contract.ts': contract,
    'barrel.ts': `export {PRIMARY as TOKEN, SECONDARY, ALIAS, Storage, Primary, Secondary} from './contract';`,
    'app.ts': `import {Component, NgModule, Inject, inject, Injector} from '@angular/core';
      import {TOKEN as KEY, SECONDARY, ALIAS, Storage, Primary, Secondary} from './barrel';
      @Component({selector:'page-a',template:''}) export class PageA {
        tokenField=KEY; store=inject<Storage>(ALIAS); injector=inject(Injector);
        constructor(@Inject(KEY) private storage:Storage){}
        run(){ this.storage.load(); this.store.save('a'); const local=this.injector.get<Storage>(this.tokenField); local.load(); }
      }
      @Component({selector:'page-b',template:'',providers:[{provide:KEY,useClass:Secondary}]}) export class PageB {store=inject<Storage>(KEY);run(){this.store.load();}}
      @NgModule({declarations:[PageA,PageB], providers:[{provide:KEY,useClass:Primary}, {provide:ALIAS,useExisting:KEY}]}) export class MainModule {}
      @Component({selector:'other',template:''}) export class OtherPage { store=inject<Storage>(KEY);run(){this.store.load();} }
      @NgModule({declarations:[OtherPage],providers:[{provide:KEY,useClass:Secondary}]}) export class OtherModule {}`,
  });
  try {
    const { edges, nodes } = f.extract();
    assert.ok(matching(edges, 'REGISTERS', 'MainModule', 'Primary').length >= 1);
    assert.ok(matching(edges, 'REGISTERS', 'PageB', 'Secondary').length >= 1);
    assert.ok(matching(edges, 'INJECTS', 'PageA', 'Primary').every(edge => edge.properties?.confidence_band === 'high'));
    assert.equal(matching(edges, 'INJECTS', 'PageA', 'Secondary').length, 0);
    assert.equal(matching(edges, 'CALLS', 'PageA.run', 'Primary.load').length, 1);
    assert.equal(matching(edges, 'CALLS', 'PageA.run', 'Primary.save').length, 1);
    assert.equal(matching(edges, 'CALLS', 'PageA.run', 'Secondary.load').length, 0);
    assert.equal(matching(edges, 'CALLS', 'PageB.run', 'Secondary.load').length, 1);
    assert.equal(matching(edges, 'CALLS', 'OtherPage.run', 'Secondary.load').length, 1);
    assert.ok(nodes.some(node => node.qualifiedName.endsWith('Storage.save') && node.label === 'Method' && node.properties.is_function_property));
    assert.ok(!nodes.some(node => node.qualifiedName.endsWith('Storage.data')));
    assert.equal(matching(edges, 'CALLS', 'Storage.data').length, 0);
    assert.ok(matching(edges, 'CALLS', 'Storage.save', 'Primary.save').every(edge => edge.properties?.candidate === true));
  } finally { f.dispose(); }
});

test('runtime @Input token fields retain scoped medium candidates; explicit interfaces dispatch callable fields and locals only', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import { Component, NgModule, Input, InjectionToken, inject, Injector } from '@angular/core';
    import { Storage, PRIMARY, SECONDARY, Primary, Secondary } from './contract';
    @Component({selector:'runtime',template:''}) export class RuntimePage {
      @Input() token!:InjectionToken<Storage>; injector=inject(Injector); storage!:Storage;
      run(){this.injector.get<Storage>(this.token).load(); const local:Storage=this.storage; local.save('x'); this.storage.data.toString();}
    }
    @NgModule({declarations:[RuntimePage],providers:[{provide:PRIMARY,useClass:Primary},{provide:SECONDARY,useClass:Secondary}]}) class Module {}
    export class Unrelated { load(){} save(){} }
    export class Plain { run(){const x={load(){}};x.load();} }` });
  try {
    const { edges } = f.extract();
    assert.equal(matching(edges, 'INJECTS', 'RuntimePage').filter(edge => edge.properties?.runtime_token).length, 2);
    assert.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Primary.load').length, 1);
    assert.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Secondary.load').length, 1);
    assert.ok(matching(edges, 'CALLS', 'RuntimePage.run', 'Primary.load').every(edge => edge.properties?.confidence_band === 'medium' && edge.properties?.candidate));
    assert.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Storage.save').length, 1);
    assert.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Unrelated.load').length, 0);
    assert.equal(matching(edges, 'CALLS', 'Plain.run', 'Storage.load').length, 0);
  } finally { f.dispose(); }
});

test('standalone bootstrap providers are imported and file owned without leaking to a second bootstrap', () => {
  const f = fixture({ 'contract.ts': contract,
    'config.ts': `import {PRIMARY,Primary} from './contract'; export const appConfig={providers:[{provide:PRIMARY,useClass:Primary}]};`,
    'app.ts': `import {Component,inject} from '@angular/core';import {PRIMARY,Storage} from './contract';
      @Component({template:'',selector:'app-root'}) export class App {service=inject<Storage>(PRIMARY);run(){this.service.load();}}`,
    'main.ts': `import {bootstrapApplication} from '@angular/platform-browser';import {App} from './app';import {appConfig} from './config';bootstrapApplication(App,appConfig);`,
  });
  try {
    const { edges } = f.extract();
    assert.equal(matching(edges, 'REGISTERS', 'config.ts', 'Primary').length, 1, JSON.stringify(edges));
    assert.equal(matching(edges, 'CALLS', 'App.run', 'Primary.load').length, 1);
  } finally { f.dispose(); }
});

test('NgModule roots apply ordered imported providers and root overrides without crossing independent applications', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'feature',template:''}) class FeaturePage {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'other',template:''}) class OtherPage {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @NgModule({declarations:[FeaturePage],providers:[{provide:PRIMARY,useClass:Secondary}]}) class FeatureModule {}
    @NgModule({imports:[FeatureModule],providers:[{provide:PRIMARY,useClass:Primary}]}) class AppModule {}
    @NgModule({declarations:[OtherPage],providers:[{provide:PRIMARY,useClass:Secondary}]}) class OtherModule {}` });
  try {
    const {edges} = f.extract();
    assert.equal(matching(edges,'CALLS','FeaturePage.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','FeaturePage.run','Secondary.load').length,0);
    assert.equal(matching(edges,'CALLS','OtherPage.run','Secondary.load').length,1);
    assert.ok(matching(edges,'CALLS','FeaturePage.run','Primary.load').every(edge=>edge.properties?.confidence_band==='high'));
  } finally { f.dispose(); }
});

test('two same-file standalone application configs keep provider scopes separate and preserve provider-file ownership', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,inject} from '@angular/core';import {bootstrapApplication} from '@angular/platform-browser';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'one',template:''}) class One {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'two',template:''}) class Two {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    const oneConfig={providers:[{provide:PRIMARY,useClass:Primary}]};const twoConfig={providers:[{provide:PRIMARY,useClass:Secondary}]};
    bootstrapApplication(One,oneConfig);bootstrapApplication(Two,twoConfig);` });
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','One.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','One.run','Secondary.load').length,0);
    assert.equal(matching(edges,'CALLS','Two.run','Secondary.load').length,1);
    assert.ok(edges.filter(edge=>edge.type==='REGISTERS').every(edge=>edge.sourceQN==='Fixture:app.ts'));
  } finally { f.dispose(); }
});

test('rendered component overrides inherit injector context before injection, aliases retain their declaring injector', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'child',template:''}) class Child {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'parent',imports:[Child],providers:[{provide:PRIMARY,useClass:Secondary}],template:'<child/>'}) class Parent { alias=inject<Storage>(ALIAS);run(){this.alias.load();} }
    @NgModule({declarations:[Child,Parent],providers:[{provide:PRIMARY,useClass:Primary},{provide:ALIAS,useExisting:PRIMARY}]}) class Module {}` });
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','Child.run','Secondary.load').length,1);
    assert.equal(matching(edges,'CALLS','Child.run','Primary.load').length,0);
    assert.equal(matching(edges,'CALLS','Parent.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','Parent.run','Secondary.load').length,0);
  } finally { f.dispose(); }
});

test('conditional providers and uninitialized token fields preserve explicit medium candidates and ignore other app bindings', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject,Injector,InjectionToken} from '@angular/core';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'conditional',template:'',providers:[...(flag?[{provide:PRIMARY,useClass:Primary}]:[{provide:PRIMARY,useClass:Secondary}])]}) class Conditional {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'runtime',template:''}) class Runtime {token!:InjectionToken<Storage>;injector=inject(Injector);run(){this.injector.get(this.token).load();}}
    @NgModule({declarations:[Runtime],providers:[{provide:PRIMARY,useClass:Primary}]}) class OneModule {}
    @NgModule({providers:[{provide:PRIMARY,useClass:Secondary}]}) class OtherModule {}` });
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','Conditional.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','Conditional.run','Secondary.load').length,1);
    assert.ok(matching(edges,'CALLS','Conditional.run').every(edge=>edge.properties?.confidence_band==='medium'&&edge.properties?.candidate));
    assert.equal(matching(edges,'CALLS','Runtime.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','Runtime.run','Secondary.load').length,0);
  } finally { f.dispose(); }
});

test('CodeGraph-style standalone inject services, class-typed constructors and function callbacks reach repository implementations', () => {
  const f = fixture({
    'core/api.service.ts': `import {Injectable} from '@angular/core'; @Injectable({providedIn:'root'}) export class ApiService { getRepositories(){return [];} }`,
    'app.ts': `import {Component,inject,ErrorHandler} from '@angular/core';import {ApiService} from './core/api.service';
      @Component({selector:'app-root',template:''}) export class App {api=inject(ApiService);run(){this.api.getRepositories();}}
      export class ConstructorPage {constructor(private api:ApiService){}run(){this.api.getRepositories();}}
      export class ReportingErrorHandler {handleError(error:unknown){}}
      export const appConfig={providers:[{provide:ErrorHandler,useClass:ReportingErrorHandler}],initialize:()=>inject(ApiService).getRepositories()};`,
    'types.ts': `export interface Callable {call:()=>void; data:string;} export class Impl implements Callable {data='';call=()=>{}} export function run(service:Callable){service.call();service.data.toString();}`,
  });
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','App.run','ApiService.getRepositories').length,1);
    assert.equal(matching(edges,'CALLS','ConstructorPage.run','ApiService.getRepositories').length,1);
    assert.equal(matching(edges,'CALLS','initialize','ApiService.getRepositories').length,1);
    assert.equal(matching(edges,'REGISTERS','app.ts','ReportingErrorHandler').length,1);
    assert.equal(matching(edges,'CALLS','#run','Callable.call').length,1);
    assert.equal(matching(edges,'CALLS','Callable.data').length,0);
  } finally { f.dispose(); }
});

test('template-only edits and provider retargeting recompute derived links with no stale edges or rerun churn', () => {
  const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,inject} from '@angular/core';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'primary',template:''}) class PrimaryView {}
    @Component({selector:'secondary',template:''}) class SecondaryView {}
    @Component({selector:'app',templateUrl:'./app.html',providers:[{provide:PRIMARY,useClass:Primary}]}) class App {store=inject<Storage>(PRIMARY);run(){this.store.load();}}`,
    'app.html':'<primary/>' });
  try {
    assert.equal(matching(f.extract().edges,'RENDERS','App','PrimaryView').length,1);
    fs.writeFileSync(path.join(f.root,'app.html'),'<secondary/>');
    const second=f.extract();
    assert.equal(matching(second.edges,'RENDERS','App','PrimaryView').length,0);
    assert.equal(matching(second.edges,'RENDERS','App','SecondaryView').length,1);
    fs.writeFileSync(path.join(f.root,'app.ts'),fs.readFileSync(path.join(f.root,'app.ts'),'utf8').replace('useClass:Primary','useClass:Secondary'));
    const third=f.extract();
    assert.equal(matching(third.edges,'CALLS','App.run','Primary.load').length,0);
    assert.equal(matching(third.edges,'CALLS','App.run','Secondary.load').length,1);
    assert.deepEqual(f.extract().edges,third.edges);
  } finally { f.dispose(); }
});

test('template scope rejects outside-root and symlink escapes before reading content', () => {
  const f=fixture({'app.ts':`import {Component} from '@angular/core'; @Component({selector:'child',template:''}) class Child{} @Component({selector:'app',templateUrl:'./escape.html'}) class App{}`});
  const outside=fs.mkdtempSync(path.join(os.tmpdir(),'angular-outside-'));
  try {
    fs.writeFileSync(path.join(outside,'secret.html'),'<child/>');
    fs.symlinkSync(path.join(outside,'secret.html'),path.join(f.root,'escape.html'));
    const result=f.extract(['app.ts','escape.html']);
    assert.equal(result.complete,true);
    assert.equal(matching(result.edges,'RENDERS','App').length,0);
    assert.deepEqual(result.processedTemplates,[]);
    assert.ok(result.diagnostics.some(message=>message.startsWith('angularTemplateScope:')));
  } finally {f.dispose();fs.rmSync(outside,{recursive:true,force:true});}
});

test('NgModule aliases use the application injector when root providers override imported module tokens', () => {
  const f=fixture({'contract.ts':contract,'app.ts':`import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'page',template:''}) class Page {service=inject<Storage>(ALIAS);run(){this.service.load();}}
    @NgModule({declarations:[Page],providers:[{provide:PRIMARY,useClass:Secondary},{provide:ALIAS,useExisting:PRIMARY}]}) class Feature{}
    @NgModule({imports:[Feature],providers:[{provide:PRIMARY,useClass:Primary}]}) class Root{}`});
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','Page.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','Page.run','Secondary.load').length,0);
    assert.ok(matching(edges,'CALLS','Page.run','Primary.load').every(edge=>edge.properties?.confidence_band==='high'));
  } finally {f.dispose();}
});

test('extended interface callable properties and inherited token fields preserve declaring identities', () => {
  const f=fixture({'app.ts':`import {Component,InjectionToken,inject,Injector} from '@angular/core';
    interface Parent {save:(x:string)=>void;data:string;} interface Child extends Parent {load():void;}
    const TOKEN=new InjectionToken<Child>('child'); const save=(value:string)=>{};
    class Impl implements Child {data='';save=save;load(){}}
    class Base {protected token=TOKEN;protected injector=inject(Injector);}
    @Component({selector:'app',template:'',providers:[{provide:TOKEN,useClass:Impl}]}) class App extends Base {run(){this.injector.get<Child>(this.token).save('x');}}
    function consume(value:Child){value.save('x');}`});
  try {
    const {nodes,edges}=f.extract();
    assert.ok(nodes.some(node=>node.qualifiedName.endsWith('Parent.save')&&node.properties.is_function_property));
    assert.equal(matching(edges,'CALLS','App.run','#save').length,1);
    assert.equal(matching(edges,'CALLS','#consume','Parent.save').length,1);
    assert.equal(matching(edges,'DEFINES_METHOD','Parent','Parent.data').length,0);
  } finally {f.dispose();}
});

test('unresolved dynamic templates are incomplete instead of successfully empty', () => {
  const f=fixture({'app.ts':`import {Component} from '@angular/core'; @Component({selector:'app',templateUrl:unknownTemplate()}) class App{}`});
  try {const result=f.extract();assert.equal(result.complete,false);assert.ok(result.diagnostics.some(message=>message.startsWith('angularTemplateUnresolved:')));} finally {f.dispose();}
});

test('NgModule template visibility follows exports and standalone imports instead of exposing private imported components', () => {
  const f=fixture({'app.ts':`import {Component,NgModule} from '@angular/core';
    @Component({selector:'public-child',template:''}) class PublicChild{}
    @Component({selector:'private-child',template:''}) class PrivateChild{}
    @NgModule({declarations:[PublicChild,PrivateChild],exports:[PublicChild]}) class Library{}
    const metadata={selector:'app',imports:[Library],template:'<public-child/><private-child/>'}; @Component(metadata) class App{}`});
  try {const {edges}=f.extract();assert.equal(matching(edges,'RENDERS','App','PublicChild').length,1);assert.equal(matching(edges,'RENDERS','App','PrivateChild').length,0);} finally {f.dispose();}
});

test('known ModuleWithProviders and importProvidersFrom preserve module provider scope', () => {
  const f=fixture({'contract.ts':contract,'app.ts':`import {Component,NgModule,inject,importProvidersFrom} from '@angular/core';import {bootstrapApplication} from '@angular/platform-browser';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @NgModule({}) class Library { static forRoot(){return{ngModule:Library,providers:[{provide:PRIMARY,useClass:Primary}]};} }
    @Component({selector:'feature',template:''}) class Feature {service=inject<Storage>(PRIMARY);run(){this.service.load();}}
    @NgModule({imports:[Library.forRoot()],declarations:[Feature]}) class MainModule{}
    @NgModule({providers:[{provide:PRIMARY,useClass:Secondary}]}) class StandaloneProviders{}
    @Component({selector:'standalone',template:''}) class Standalone {service=inject<Storage>(PRIMARY);run(){this.service.load();}}
    bootstrapApplication(Standalone,{providers:[importProvidersFrom(StandaloneProviders)]});`});
  try {
    const {edges}=f.extract();
    assert.equal(matching(edges,'CALLS','Feature.run','Primary.load').length,1);
    assert.equal(matching(edges,'CALLS','Standalone.run','Secondary.load').length,1);
    assert.equal(matching(edges,'CALLS','Standalone.run','Primary.load').length,0);
  } finally {f.dispose();}
});


test('constructor class-token selection replaces provisional calls and injections after provider-only retargeting', () => {
  const f = fixture({
    'services.ts': `export class Original { load(){} } export class Replacement { load(){} } export class ReplacementTwo { load(){} }`,
    'providers.ts': `import {Original, Replacement, ReplacementTwo} from './services'; export const PROVIDERS = [{provide:Original,useClass:Replacement}];`,
    'page.ts': `import {Component} from '@angular/core'; import {Original} from './services'; import {PROVIDERS} from './providers';
      @Component({selector:'page',template:'',providers:PROVIDERS}) export class Page {constructor(private api:Original){} run(){this.api.load();}}`,
  });
  const files = ['services.ts', 'providers.ts', 'page.ts'];
  const extract = () => extractProject('Fixture', f.root, '', () => {}, files);
  function selected(result: ReturnType<typeof extract>, implementation: string): void {
    assert.equal(result.complete, true);
    for (const type of ['CALLS', 'INJECTS']) {
      const from = type === 'CALLS' ? 'Page.run' : 'Page';
      const dependencies = matching(result.edges, type, from);
      assert.equal(dependencies.length, 1, JSON.stringify(dependencies));
      assert.ok(dependencies[0].targetQN.endsWith(implementation + (type === 'CALLS' ? '.load' : '')));
      assert.equal(dependencies[0].properties?.confidence_band, 'high');
      assert.equal(dependencies[0].properties?.candidate, false);
    }
  }
  try {
    const page = fs.readFileSync(path.join(f.root, 'page.ts'), 'utf8');
    selected(extract(), 'Replacement');
    fs.writeFileSync(path.join(f.root, 'providers.ts'), fs.readFileSync(path.join(f.root, 'providers.ts'), 'utf8').replace('useClass:Replacement}', 'useClass:ReplacementTwo}'));
    const retargeted = extract(); selected(retargeted, 'ReplacementTwo');
    assert.deepEqual(extract().edges, retargeted.edges, 'an unchanged rerun has no derived-edge churn');
    assert.equal(fs.readFileSync(path.join(f.root, 'page.ts'), 'utf8'), page, 'only the imported provider file changed');
    fs.writeFileSync(path.join(f.root, 'providers.ts'), 'export const PROVIDERS = [];');
    selected(extract(), 'Original');
  } finally { f.dispose(); }
});

test('class-token dispatch preserves real direct and method-argument calls to the original implementation in the same owner', () => {
  const f = fixture({ 'app.ts': `import {Component} from '@angular/core';
    class Original {load(){}} class Replacement {load(){}}
    @Component({selector:'app',template:'',providers:[{provide:Original,useClass:Replacement}]}) class App {
      constructor(private api:Original){}
      onlyInjected(){this.api.load();}
      mixed(other:Original){this.api.load();other.load();}
      direct(){this.api.load();new Original().load();}
    }` });
  try {
    const result = extractProject('Fixture', f.root, '', () => {}, ['app.ts']);
    assert.equal(matching(result.edges, 'CALLS', 'App.onlyInjected', 'Original.load').length, 0);
    assert.equal(matching(result.edges, 'CALLS', 'App.onlyInjected', 'Replacement.load').length, 1);
    assert.ok(matching(result.edges, 'CALLS', 'App.mixed', 'Original.load').length > 0, 'the method argument still calls Original');
    assert.equal(matching(result.edges, 'CALLS', 'App.mixed', 'Replacement.load').length, 1);
    assert.ok(matching(result.edges, 'CALLS', 'App.direct', 'Original.load').length > 0, 'direct construction still calls Original');
    assert.equal(matching(result.edges, 'CALLS', 'App.direct', 'Replacement.load').length, 1);
  } finally { f.dispose(); }
});

test('conditional class-token providers emit only genuine constructor injection alternatives with candidate metadata', () => {
  const f = fixture({ 'app.ts': `import {Component} from '@angular/core';
    class Original {load(){}} class One {load(){}} class Two {load(){}}
    @Component({selector:'app',template:'',providers:[...(flag?[{provide:Original,useClass:One}]:[{provide:Original,useClass:Two}])]}) class App {
      constructor(private api:Original){} run(){this.api.load();}
    }` });
  try {
    const result = extractProject('Fixture', f.root, '', () => {}, ['app.ts']);
    for (const type of ['CALLS','INJECTS']) {
      const dependencies = matching(result.edges, type, type === 'CALLS' ? 'App.run' : 'App');
      assert.equal(dependencies.length, 2, JSON.stringify(dependencies));
      assert.ok(dependencies.every(edge => !edge.targetQN.includes('Original') && edge.properties?.candidate === true && edge.properties?.confidence_band === 'medium'));
    }
  } finally { f.dispose(); }
});


test('repeated injector.get class-token selections retain one dependency on the selected provider', () => {
  const f = fixture({'app.ts': `import {Component,Injector} from '@angular/core';
    class Original {load(){}} class Replacement {load(){}}
    @Component({selector:'app',template:'',providers:[{provide:Original,useClass:Replacement}]}) class App {
      constructor(private injector:Injector){}
      run(){this.injector.get<Original>(Original).load();this.injector.get<Original>(Original).load();}
    }`});
  try {
    const result = extractProject('Fixture', f.root, '', () => {}, ['app.ts']);
    assert.equal(matching(result.edges, 'INJECTS', 'App', 'Replacement').length, 1);
    assert.equal(matching(result.edges, 'INJECTS', 'App', 'Original').length, 0);
    assert.equal(matching(result.edges, 'CALLS', 'App.run', 'Replacement.load').length, 1);
    assert.equal(matching(result.edges, 'CALLS', 'App.run', 'Original.load').length, 0);
  } finally {f.dispose();}
});
