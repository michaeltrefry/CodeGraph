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
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const ts_morph_1 = require("ts-morph");
const angularAnalysis_1 = require("./angularAnalysis");
const extractor_1 = require("./extractor");
const extractionWork_1 = require("./extractionWork");
const semanticModel_1 = require("./semanticModel");
const workspaceResolver_1 = require("./workspaceResolver");
const discoveryScope_1 = require("./discoveryScope");
function fixture(sources, includedFiles = Object.keys(sources), control = {}, onAngularStart = () => { }) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-angular-'));
    for (const [file, contents] of Object.entries(sources)) {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.writeFileSync(path.join(root, file), contents);
    }
    function extract(included = includedFiles) {
        const project = new ts_morph_1.Project({ compilerOptions: { allowJs: true, jsx: 1, noResolve: true, experimentalDecorators: true, types: [], skipLibCheck: true }, skipAddingFilesFromTsConfig: true });
        for (const file of included.filter(file => /\.[jt]sx?$/.test(file)))
            project.addSourceFileAtPath(path.join(root, file));
        const discovery = new discoveryScope_1.DiscoveryScope(root, included);
        const diagnostics = [];
        const imports = (0, workspaceResolver_1.analyzeWorkspaceImports)('Fixture', root, '', project.getSourceFiles(), message => diagnostics.push(message), discovery);
        const model = new semanticModel_1.SemanticModel('Fixture', root, project.getSourceFiles(), imports.resolvedImports, message => diagnostics.push(message), control);
        const nodes = [];
        const edges = [];
        for (const [declaration, qualifiedName] of model.declarations) {
            if (!['ClassDeclaration', 'InterfaceDeclaration'].includes(declaration.getKindName()))
                continue;
            nodes.push({ label: declaration.getKindName() === 'ClassDeclaration' ? 'Class' : 'Interface', name: qualifiedName.split('.').pop(), qualifiedName, filePath: model.relative(declaration), startLine: 1, endLine: 1, properties: {} });
        }
        model.extract(nodes, edges, []);
        onAngularStart(model);
        const result = (0, angularAnalysis_1.analyzeAngular)(model, nodes, edges, discovery, message => diagnostics.push(message));
        return { ...result, nodes, edges, diagnostics, model };
    }
    return { root, extract, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function matching(edges, type, source, target) {
    return edges.filter(edge => edge.type === type && edge.sourceQN.endsWith(source) && (!target || edge.targetQN.endsWith(target)));
}
(0, node_test_1.test)('Angular template selectors, defer blocks and static outlets use actual components and scoped external HTML', () => {
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
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.deepEqual(result.processedTemplates, ['src/app.html']);
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Child').length, 2);
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Action').length, 1);
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Panel').length, 1);
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Comment').length, 0);
        assert_1.strict.ok(result.edges.filter(edge => edge.type === 'RENDERS').every(edge => edge.properties?.template_path === 'src/app.html'));
        assert_1.strict.equal(result.nodes.find(node => node.name === 'Pretender')?.label, 'Class');
        const excluded = f.extract(['src/app.ts', 'src/children.ts']);
        assert_1.strict.equal(excluded.complete, true);
        assert_1.strict.equal(matching(excluded.edges, 'RENDERS', 'App').length, 0);
        assert_1.strict.ok(excluded.diagnostics.some(message => message.startsWith('angularTemplateScope:')));
        fs.unlinkSync(path.join(f.root, 'src/app.html'));
        assert_1.strict.equal(f.extract().complete, false, 'an unreadable admitted template is incomplete');
        assert_1.strict.equal(f.extract(['src/app.ts', 'src/children.ts']).complete, true, 'a removed/excluded template permits recomputation');
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('Angular dynamic component APIs resolve import aliases, typed receivers and static class fields, excluding unrelated open/createComponent', () => {
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
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Dialog').length, 4, JSON.stringify({ edges: result.edges.filter(edge => edge.type === 'RENDERS'), diagnostics: result.diagnostics }));
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App', 'Other').length, 0);
        assert_1.strict.equal(result.diagnostics.filter(message => message.startsWith('angularDynamicComponent:')).length, 0);
    }
    finally {
        f.dispose();
    }
});
const contract = `import {InjectionToken} from '@angular/core';
  export interface Storage { data: string; load(): string; save: (value:string) => void; }
  export const PRIMARY = new InjectionToken<Storage>('primary'); export const SECONDARY = new InjectionToken<Storage>('secondary');
  export const ALIAS = new InjectionToken<Storage>('alias');
  export class Primary implements Storage {data='';load(){return this.data}save=(value:string)=>{this.data=value};}
  export class Secondary implements Storage {data='';load(){return this.data}save=(value:string)=>{this.data=value};}`;
(0, node_test_1.test)('token-specific implementations and aliases respect component overrides and isolated NgModule providers', () => {
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
        assert_1.strict.ok(matching(edges, 'REGISTERS', 'MainModule', 'Primary').length >= 1);
        assert_1.strict.ok(matching(edges, 'REGISTERS', 'PageB', 'Secondary').length >= 1);
        assert_1.strict.ok(matching(edges, 'INJECTS', 'PageA', 'Primary').every(edge => edge.properties?.confidence_band === 'high'));
        assert_1.strict.equal(matching(edges, 'INJECTS', 'PageA', 'Secondary').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'PageA.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'PageA.run', 'Primary.save').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'PageA.run', 'Secondary.load').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'PageB.run', 'Secondary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'OtherPage.run', 'Secondary.load').length, 1);
        assert_1.strict.ok(nodes.some(node => node.qualifiedName.endsWith('Storage.save') && node.label === 'Method' && node.properties.is_function_property));
        assert_1.strict.ok(!nodes.some(node => node.qualifiedName.endsWith('Storage.data')));
        assert_1.strict.equal(matching(edges, 'CALLS', 'Storage.data').length, 0);
        assert_1.strict.ok(matching(edges, 'CALLS', 'Storage.save', 'Primary.save').every(edge => edge.properties?.candidate === true));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('runtime @Input token fields retain scoped medium candidates; explicit interfaces dispatch callable fields and locals only', () => {
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
        assert_1.strict.equal(matching(edges, 'INJECTS', 'RuntimePage').filter(edge => edge.properties?.runtime_token).length, 2);
        assert_1.strict.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Secondary.load').length, 1);
        assert_1.strict.ok(matching(edges, 'CALLS', 'RuntimePage.run', 'Primary.load').every(edge => edge.properties?.confidence_band === 'medium' && edge.properties?.candidate));
        assert_1.strict.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Storage.save').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'RuntimePage.run', 'Unrelated.load').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Plain.run', 'Storage.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('standalone bootstrap providers are imported and file owned without leaking to a second bootstrap', () => {
    const f = fixture({ 'contract.ts': contract,
        'config.ts': `import {PRIMARY,Primary} from './contract'; export const appConfig={providers:[{provide:PRIMARY,useClass:Primary}]};`,
        'app.ts': `import {Component,inject} from '@angular/core';import {PRIMARY,Storage} from './contract';
      @Component({template:'',selector:'app-root'}) export class App {service=inject<Storage>(PRIMARY);run(){this.service.load();}}`,
        'main.ts': `import {bootstrapApplication} from '@angular/platform-browser';import {App} from './app';import {appConfig} from './config';bootstrapApplication(App,appConfig);`,
    });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'REGISTERS', 'config.ts', 'Primary').length, 1, JSON.stringify(edges));
        assert_1.strict.equal(matching(edges, 'CALLS', 'App.run', 'Primary.load').length, 1);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('NgModule roots apply ordered imported providers and root overrides without crossing independent applications', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'feature',template:''}) class FeaturePage {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'other',template:''}) class OtherPage {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @NgModule({declarations:[FeaturePage],providers:[{provide:PRIMARY,useClass:Secondary}]}) class FeatureModule {}
    @NgModule({imports:[FeatureModule],providers:[{provide:PRIMARY,useClass:Primary}]}) class AppModule {}
    @NgModule({declarations:[OtherPage],providers:[{provide:PRIMARY,useClass:Secondary}]}) class OtherModule {}` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'FeaturePage.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'FeaturePage.run', 'Secondary.load').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'OtherPage.run', 'Secondary.load').length, 1);
        assert_1.strict.ok(matching(edges, 'CALLS', 'FeaturePage.run', 'Primary.load').every(edge => edge.properties?.confidence_band === 'high'));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('two same-file standalone application configs keep provider scopes separate and preserve provider-file ownership', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,inject} from '@angular/core';import {bootstrapApplication} from '@angular/platform-browser';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'one',template:''}) class One {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'two',template:''}) class Two {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    const oneConfig={providers:[{provide:PRIMARY,useClass:Primary}]};const twoConfig={providers:[{provide:PRIMARY,useClass:Secondary}]};
    bootstrapApplication(One,oneConfig);bootstrapApplication(Two,twoConfig);` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'One.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'One.run', 'Secondary.load').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Two.run', 'Secondary.load').length, 1);
        assert_1.strict.ok(edges.filter(edge => edge.type === 'REGISTERS').every(edge => edge.sourceQN === 'Fixture:app.ts'));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('rendered component overrides inherit injector context before injection, aliases retain their declaring injector', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'child',template:''}) class Child {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'parent',imports:[Child],providers:[{provide:PRIMARY,useClass:Secondary}],template:'<child/>'}) class Parent { alias=inject<Storage>(ALIAS);run(){this.alias.load();} }
    @NgModule({declarations:[Child,Parent],providers:[{provide:PRIMARY,useClass:Primary},{provide:ALIAS,useExisting:PRIMARY}]}) class Module {}` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'Child.run', 'Secondary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Child.run', 'Primary.load').length, 0);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Parent.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Parent.run', 'Secondary.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('conditional providers and uninitialized token fields preserve explicit medium candidates and ignore other app bindings', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject,Injector,InjectionToken} from '@angular/core';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'conditional',template:'',providers:[...(flag?[{provide:PRIMARY,useClass:Primary}]:[{provide:PRIMARY,useClass:Secondary}])]}) class Conditional {store=inject<Storage>(PRIMARY);run(){this.store.load();}}
    @Component({selector:'runtime',template:''}) class Runtime {token!:InjectionToken<Storage>;injector=inject(Injector);run(){this.injector.get(this.token).load();}}
    @NgModule({declarations:[Runtime],providers:[{provide:PRIMARY,useClass:Primary}]}) class OneModule {}
    @NgModule({providers:[{provide:PRIMARY,useClass:Secondary}]}) class OtherModule {}` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'Conditional.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Conditional.run', 'Secondary.load').length, 1);
        assert_1.strict.ok(matching(edges, 'CALLS', 'Conditional.run').every(edge => edge.properties?.confidence_band === 'medium' && edge.properties?.candidate));
        assert_1.strict.equal(matching(edges, 'CALLS', 'Runtime.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Runtime.run', 'Secondary.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('CodeGraph-style standalone inject services, class-typed constructors and function callbacks reach repository implementations', () => {
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
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'App.run', 'ApiService.getRepositories').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'ConstructorPage.run', 'ApiService.getRepositories').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'initialize', 'ApiService.getRepositories').length, 1);
        assert_1.strict.equal(matching(edges, 'REGISTERS', 'app.ts', 'ReportingErrorHandler').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', '#run', 'Callable.call').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Callable.data').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('template-only edits and provider retargeting recompute derived links with no stale edges or rerun churn', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,inject} from '@angular/core';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @Component({selector:'primary',template:''}) class PrimaryView {}
    @Component({selector:'secondary',template:''}) class SecondaryView {}
    @Component({selector:'app',templateUrl:'./app.html',providers:[{provide:PRIMARY,useClass:Primary}]}) class App {store=inject<Storage>(PRIMARY);run(){this.store.load();}}`,
        'app.html': '<primary/>' });
    try {
        assert_1.strict.equal(matching(f.extract().edges, 'RENDERS', 'App', 'PrimaryView').length, 1);
        fs.writeFileSync(path.join(f.root, 'app.html'), '<secondary/>');
        const second = f.extract();
        assert_1.strict.equal(matching(second.edges, 'RENDERS', 'App', 'PrimaryView').length, 0);
        assert_1.strict.equal(matching(second.edges, 'RENDERS', 'App', 'SecondaryView').length, 1);
        fs.writeFileSync(path.join(f.root, 'app.ts'), fs.readFileSync(path.join(f.root, 'app.ts'), 'utf8').replace('useClass:Primary', 'useClass:Secondary'));
        const third = f.extract();
        assert_1.strict.equal(matching(third.edges, 'CALLS', 'App.run', 'Primary.load').length, 0);
        assert_1.strict.equal(matching(third.edges, 'CALLS', 'App.run', 'Secondary.load').length, 1);
        assert_1.strict.deepEqual(f.extract().edges, third.edges);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('template scope rejects outside-root and symlink escapes before reading content', () => {
    const f = fixture({ 'app.ts': `import {Component} from '@angular/core'; @Component({selector:'child',template:''}) class Child{} @Component({selector:'app',templateUrl:'./escape.html'}) class App{}` });
    const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'angular-outside-'));
    try {
        fs.writeFileSync(path.join(outside, 'secret.html'), '<child/>');
        fs.symlinkSync(path.join(outside, 'secret.html'), path.join(f.root, 'escape.html'));
        const result = f.extract(['app.ts', 'escape.html']);
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.equal(matching(result.edges, 'RENDERS', 'App').length, 0);
        assert_1.strict.deepEqual(result.processedTemplates, []);
        assert_1.strict.ok(result.diagnostics.some(message => message.startsWith('sourceAdmission:')));
    }
    finally {
        f.dispose();
        fs.rmSync(outside, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('NgModule aliases use the application injector when root providers override imported module tokens', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject} from '@angular/core';import {Storage,PRIMARY,ALIAS,Primary,Secondary} from './contract';
    @Component({selector:'page',template:''}) class Page {service=inject<Storage>(ALIAS);run(){this.service.load();}}
    @NgModule({declarations:[Page],providers:[{provide:PRIMARY,useClass:Secondary},{provide:ALIAS,useExisting:PRIMARY}]}) class Feature{}
    @NgModule({imports:[Feature],providers:[{provide:PRIMARY,useClass:Primary}]}) class Root{}` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'Page.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Page.run', 'Secondary.load').length, 0);
        assert_1.strict.ok(matching(edges, 'CALLS', 'Page.run', 'Primary.load').every(edge => edge.properties?.confidence_band === 'high'));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('extended interface callable properties and inherited token fields preserve declaring identities', () => {
    const f = fixture({ 'app.ts': `import {Component,InjectionToken,inject,Injector} from '@angular/core';
    interface Parent {save:(x:string)=>void;data:string;} interface Child extends Parent {load():void;}
    const TOKEN=new InjectionToken<Child>('child'); const save=(value:string)=>{};
    class Impl implements Child {data='';save=save;load(){}}
    class Base {protected token=TOKEN;protected injector=inject(Injector);}
    @Component({selector:'app',template:'',providers:[{provide:TOKEN,useClass:Impl}]}) class App extends Base {run(){this.injector.get<Child>(this.token).save('x');}}
    function consume(value:Child){value.save('x');}` });
    try {
        const { nodes, edges } = f.extract();
        assert_1.strict.ok(nodes.some(node => node.qualifiedName.endsWith('Parent.save') && node.properties.is_function_property));
        assert_1.strict.equal(matching(edges, 'CALLS', 'App.run', '#save').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', '#consume', 'Parent.save').length, 1);
        assert_1.strict.equal(matching(edges, 'DEFINES_METHOD', 'Parent', 'Parent.data').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('unresolved dynamic templates are incomplete instead of successfully empty', () => {
    const f = fixture({ 'app.ts': `import {Component} from '@angular/core'; @Component({selector:'app',templateUrl:unknownTemplate()}) class App{}` });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, false);
        assert_1.strict.ok(result.diagnostics.some(message => message.startsWith('angularTemplateUnresolved:')));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('NgModule template visibility follows exports and standalone imports instead of exposing private imported components', () => {
    const f = fixture({ 'app.ts': `import {Component,NgModule} from '@angular/core';
    @Component({selector:'public-child',template:''}) class PublicChild{}
    @Component({selector:'private-child',template:''}) class PrivateChild{}
    @NgModule({declarations:[PublicChild,PrivateChild],exports:[PublicChild]}) class Library{}
    const metadata={selector:'app',imports:[Library],template:'<public-child/><private-child/>'}; @Component(metadata) class App{}` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'RENDERS', 'App', 'PublicChild').length, 1);
        assert_1.strict.equal(matching(edges, 'RENDERS', 'App', 'PrivateChild').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('known ModuleWithProviders and importProvidersFrom preserve module provider scope', () => {
    const f = fixture({ 'contract.ts': contract, 'app.ts': `import {Component,NgModule,inject,importProvidersFrom} from '@angular/core';import {bootstrapApplication} from '@angular/platform-browser';import {Storage,PRIMARY,Primary,Secondary} from './contract';
    @NgModule({}) class Library { static forRoot(){return{ngModule:Library,providers:[{provide:PRIMARY,useClass:Primary}]};} }
    @Component({selector:'feature',template:''}) class Feature {service=inject<Storage>(PRIMARY);run(){this.service.load();}}
    @NgModule({imports:[Library.forRoot()],declarations:[Feature]}) class MainModule{}
    @NgModule({providers:[{provide:PRIMARY,useClass:Secondary}]}) class StandaloneProviders{}
    @Component({selector:'standalone',template:''}) class Standalone {service=inject<Storage>(PRIMARY);run(){this.service.load();}}
    bootstrapApplication(Standalone,{providers:[importProvidersFrom(StandaloneProviders)]});` });
    try {
        const { edges } = f.extract();
        assert_1.strict.equal(matching(edges, 'CALLS', 'Feature.run', 'Primary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Standalone.run', 'Secondary.load').length, 1);
        assert_1.strict.equal(matching(edges, 'CALLS', 'Standalone.run', 'Primary.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('constructor class-token selection replaces provisional calls and injections after provider-only retargeting', () => {
    const f = fixture({
        'services.ts': `export class Original { load(){} } export class Replacement { load(){} } export class ReplacementTwo { load(){} }`,
        'providers.ts': `import {Original, Replacement, ReplacementTwo} from './services'; export const PROVIDERS = [{provide:Original,useClass:Replacement}];`,
        'page.ts': `import {Component} from '@angular/core'; import {Original} from './services'; import {PROVIDERS} from './providers';
      @Component({selector:'page',template:'',providers:PROVIDERS}) export class Page {constructor(private api:Original){} run(){this.api.load();}}`,
    });
    const files = ['services.ts', 'providers.ts', 'page.ts'];
    const extract = () => (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, files);
    function selected(result, implementation) {
        assert_1.strict.equal(result.complete, true);
        for (const type of ['CALLS', 'INJECTS']) {
            const from = type === 'CALLS' ? 'Page.run' : 'Page';
            const dependencies = matching(result.edges, type, from);
            assert_1.strict.equal(dependencies.length, 1, JSON.stringify(dependencies));
            assert_1.strict.ok(dependencies[0].targetQN.endsWith(implementation + (type === 'CALLS' ? '.load' : '')));
            assert_1.strict.equal(dependencies[0].properties?.confidence_band, 'high');
            assert_1.strict.equal(dependencies[0].properties?.candidate, false);
        }
    }
    try {
        const page = fs.readFileSync(path.join(f.root, 'page.ts'), 'utf8');
        selected(extract(), 'Replacement');
        fs.writeFileSync(path.join(f.root, 'providers.ts'), fs.readFileSync(path.join(f.root, 'providers.ts'), 'utf8').replace('useClass:Replacement}', 'useClass:ReplacementTwo}'));
        const retargeted = extract();
        selected(retargeted, 'ReplacementTwo');
        assert_1.strict.deepEqual(extract().edges, retargeted.edges, 'an unchanged rerun has no derived-edge churn');
        assert_1.strict.equal(fs.readFileSync(path.join(f.root, 'page.ts'), 'utf8'), page, 'only the imported provider file changed');
        fs.writeFileSync(path.join(f.root, 'providers.ts'), 'export const PROVIDERS = [];');
        selected(extract(), 'Original');
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('class-token dispatch preserves real direct and method-argument calls to the original implementation in the same owner', () => {
    const f = fixture({ 'app.ts': `import {Component} from '@angular/core';
    class Original {load(){}} class Replacement {load(){}}
    @Component({selector:'app',template:'',providers:[{provide:Original,useClass:Replacement}]}) class App {
      constructor(private api:Original){}
      onlyInjected(){this.api.load();}
      mixed(other:Original){this.api.load();other.load();}
      direct(){this.api.load();new Original().load();}
    }` });
    try {
        const result = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts']);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.onlyInjected', 'Original.load').length, 0);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.onlyInjected', 'Replacement.load').length, 1);
        assert_1.strict.ok(matching(result.edges, 'CALLS', 'App.mixed', 'Original.load').length > 0, 'the method argument still calls Original');
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.mixed', 'Replacement.load').length, 1);
        assert_1.strict.ok(matching(result.edges, 'CALLS', 'App.direct', 'Original.load').length > 0, 'direct construction still calls Original');
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.direct', 'Replacement.load').length, 1);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('conditional class-token providers emit only genuine constructor injection alternatives with candidate metadata', () => {
    const f = fixture({ 'app.ts': `import {Component} from '@angular/core';
    class Original {load(){}} class One {load(){}} class Two {load(){}}
    @Component({selector:'app',template:'',providers:[...(flag?[{provide:Original,useClass:One}]:[{provide:Original,useClass:Two}])]}) class App {
      constructor(private api:Original){} run(){this.api.load();}
    }` });
    try {
        const result = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts']);
        for (const type of ['CALLS', 'INJECTS']) {
            const dependencies = matching(result.edges, type, type === 'CALLS' ? 'App.run' : 'App');
            assert_1.strict.equal(dependencies.length, 2, JSON.stringify(dependencies));
            assert_1.strict.ok(dependencies.every(edge => !edge.targetQN.includes('Original') && edge.properties?.candidate === true && edge.properties?.confidence_band === 'medium'));
        }
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('repeated injector.get class-token selections retain one dependency on the selected provider', () => {
    const f = fixture({ 'app.ts': `import {Component,Injector} from '@angular/core';
    class Original {load(){}} class Replacement {load(){}}
    @Component({selector:'app',template:'',providers:[{provide:Original,useClass:Replacement}]}) class App {
      constructor(private injector:Injector){}
      run(){this.injector.get<Original>(Original).load();this.injector.get<Original>(Original).load();}
    }` });
    try {
        const result = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts']);
        assert_1.strict.equal(matching(result.edges, 'INJECTS', 'App', 'Replacement').length, 1);
        assert_1.strict.equal(matching(result.edges, 'INJECTS', 'App', 'Original').length, 0);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.run', 'Replacement.load').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.run', 'Original.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
function selectorCrowd(count, template, eachTemplate = '') {
    return `import {Component as C} from '@angular/core';` +
        Array.from({ length: count }, (_, index) => `@C({selector:'x',template:${JSON.stringify(eachTemplate)}})class C${index}{}`).join('') +
        `@C({selector:'host',template:${JSON.stringify(template)}})class Host{}`;
}
(0, node_test_1.test)('the 10k selector by 10k repeated-tag shape has bounded growth and retains every ambiguous component target', t => {
    let importViews = 0;
    let semanticWork = 0;
    const f = fixture({ 'app.ts': selectorCrowd(10000, '<x/>'.repeat(10000)) }, ['app.ts'], {}, model => {
        semanticWork = model.work.used;
        const file = model.files[0];
        const original = file.getImportDeclarations.bind(file);
        file.getImportDeclarations = () => { importViews++; return original(); };
    });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, true);
        const renders = matching(result.edges, 'RENDERS', 'Host');
        assert_1.strict.equal(renders.length, 10000);
        assert_1.strict.ok(renders.every(edge => edge.properties?.candidate === true && edge.properties?.confidence_band === 'medium'));
        // Includes independent hard reservations for the 10k component and render DTOs.
        assert_1.strict.ok(result.model.work.used <= 30000000, `work=${result.model.work.used}`);
        assert_1.strict.equal(importViews, 1, 'the source import list is materialized once, not once per component/API check');
        t.diagnostic(JSON.stringify({ semanticWork, angularWork: result.model.work.used - semanticWork, totalWork: result.model.work.used, importViews }));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('doubling both selectors and repeated tags grows charged work linearly rather than multiplying matching/render attempts', t => {
    const one = fixture({ 'app.ts': selectorCrowd(120, '<x/>'.repeat(120)) });
    const two = fixture({ 'app.ts': selectorCrowd(240, '<x/>'.repeat(240)) });
    try {
        const first = one.extract();
        const second = two.extract();
        assert_1.strict.equal(matching(first.edges, 'RENDERS', 'Host').length, 120);
        assert_1.strict.equal(matching(second.edges, 'RENDERS', 'Host').length, 240);
        assert_1.strict.ok(second.model.work.used < first.model.work.used * 3, `${first.model.work.used} -> ${second.model.work.used}`);
        t.diagnostic(JSON.stringify({ firstWork: first.model.work.used, secondWork: second.model.work.used }));
    }
    finally {
        one.dispose();
        two.dispose();
    }
});
(0, node_test_1.test)('unique selector/template output exhausts a shared allowance before graph or processed-file output can escape', () => {
    const f = fixture({ 'app.ts': selectorCrowd(80, '', '<x/>') });
    try {
        let lastWork = 0;
        let angularStarted = false;
        const direct = fixture({ 'app.ts': selectorCrowd(80, '', '<x/>') }, ['app.ts'], { maxWork: 300000 }, () => { angularStarted = true; });
        try {
            assert_1.strict.throws(() => direct.extract(), error => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'work_limit');
            assert_1.strict.equal(angularStarted, true, 'the refusal covers Angular expansion after semantic extraction');
        }
        finally {
            direct.dispose();
        }
        const refused = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts'], {}, { maxWork: 300000, onWork: work => { lastWork = work; } });
        assert_1.strict.equal(refused.complete, false);
        assert_1.strict.deepEqual(refused.nodes, []);
        assert_1.strict.deepEqual(refused.edges, []);
        assert_1.strict.deepEqual(refused.processedFiles, []);
        assert_1.strict.ok(lastWork > 0 && lastWork <= 300000);
        assert_1.strict.ok(refused.diagnostics?.some(message => message.includes('semantic analysis work limit or cancellation')));
        const normal = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts']);
        assert_1.strict.equal(normal.complete, true, 'a fresh independent allowance can retain the complete graph');
        assert_1.strict.equal(normal.edges.filter(edge => edge.type === 'RENDERS').length, 80 * 80);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('Angular synchronously polls cancellation, poisons the refused run, and does not poison a fresh extraction', () => {
    let active = false;
    let enabled = true;
    let polls = 0;
    let lastWork = 0;
    const f = fixture({ 'app.ts': selectorCrowd(80, '<x/>'.repeat(80)) }, ['app.ts'], {
        shouldCancel: () => active && enabled && ++polls > 100,
        onWork: work => { lastWork = work; },
    }, () => { active = true; });
    try {
        assert_1.strict.throws(() => f.extract(), error => error instanceof extractionWork_1.ExtractionWorkLimitError && error.reason === 'cancelled' && error.work === lastWork);
        enabled = false;
        active = false;
        polls = 0;
        assert_1.strict.equal(f.extract().complete, true);
        const refused = (0, extractor_1.extractProject)('Fixture', f.root, '', () => { }, ['app.ts'], {}, { shouldCancel: () => true });
        assert_1.strict.equal(refused.complete, false);
        assert_1.strict.deepEqual(refused.edges, []);
        assert_1.strict.deepEqual(refused.processedFiles, []);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('malformed repeated HTML prefixes scan linearly while comments and raw-text content remain ignored', () => {
    const normal = fixture({ 'app.ts': `import {Component} from '@angular/core';
    @Component({selector:'child',template:''}) class Child{}
    @Component({selector:'app',template:'<!-- <child/> --><script><child/></script><style><child/></style><div title="<child/>"></div>@defer {<child/>}'}) class App{}` });
    const openComments = fixture({ 'app.ts': selectorCrowd(1, '<!--'.repeat(20000) + '<x/>') });
    const openScripts = fixture({ 'app.ts': selectorCrowd(1, '<script>'.repeat(20000) + '<x/>') });
    const openTags = fixture({ 'app.ts': selectorCrowd(1, '<div '.repeat(20000) + '<x/>') });
    try {
        assert_1.strict.equal(matching(normal.extract().edges, 'RENDERS', 'App', 'Child').length, 1);
        assert_1.strict.equal(matching(openComments.extract().edges, 'RENDERS', 'Host').length, 0);
        assert_1.strict.equal(matching(openScripts.extract().edges, 'RENDERS', 'Host').length, 0);
        assert_1.strict.equal(matching(openTags.extract().edges, 'RENDERS', 'Host', 'C0').length, 1);
    }
    finally {
        normal.dispose();
        openComments.dispose();
        openScripts.dispose();
        openTags.dispose();
    }
});
(0, node_test_1.test)('React import volume does not multiply unrelated Angular API/injection scans', t => {
    let semanticWork = 0;
    let importedWidths = 0;
    const imports = Array.from({ length: 240 }, (_, index) => `import {helper as helper${index}} from '@react-library/long-package-${index}';`).join('');
    const calls = Array.from({ length: 1800 }, (_, index) => `helper${index % 240}(${index});`).join('');
    const f = fixture({ 'app.jsx': `${imports} export function Panel(){${calls}return <div/>;}` }, ['app.jsx'], { maxWork: 4000000 }, model => {
        semanticWork = model.work.used;
        for (const imp of model.files[0].getImportDeclarations()) {
            const original = imp.getFullWidth.bind(imp);
            imp.getFullWidth = () => { importedWidths++; return original(); };
        }
    });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.ok(result.nodes.some(node => node.name === 'Panel' && node.label === 'Component'));
        assert_1.strict.equal(importedWidths, 240, 'each unrelated import is indexed once, without rescanning on every call');
        assert_1.strict.ok(result.model.work.used - semanticWork < 300000);
        t.diagnostic(JSON.stringify({ semanticWork, angularWork: result.model.work.used - semanticWork, totalWork: result.model.work.used, importedWidths }));
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('non-Angular JS callers retain interface/class dispatch from imported typed declarations', () => {
    const f = fixture({
        'typed.ts': `export interface Worker{run():void;save:()=>void;data:string;}export class Impl implements Worker{data='';run(){}save=()=>{}}
      export const worker:Worker=new Impl();export const concrete:Impl=new Impl();`,
        'app.js': `import {worker,concrete} from './typed';export function run(){worker.run();worker.save();concrete.run();}`,
    });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.ok(result.nodes.some(node => node.qualifiedName.endsWith('Worker.save') && node.properties.is_interface_member));
        assert_1.strict.equal(matching(result.edges, 'CALLS', '#run', 'Worker.run').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', '#run', 'Worker.save').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', '#run', 'Impl.run').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'Worker.data').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('explicit Angular bootstrap initializes a plain imported provider config exactly once', () => {
    const f = fixture({
        'services.ts': `export class Original{load(){}}export class Replacement{load(){}}`,
        'config.ts': `import {Original,Replacement} from './services';export const config={providers:[{provide:Original,useClass:Replacement}]};`,
        'app.ts': `import {Component,inject} from '@angular/core';import {Original} from './services';@Component({selector:'app',template:''})export class App{service=inject(Original);run(){this.service.load();}}`,
        'main.ts': `import {bootstrapApplication} from '@angular/platform-browser';import {App} from './app';import {config} from './config';bootstrapApplication(App,config);`,
    });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.equal(matching(result.edges, 'REGISTERS', 'config.ts', 'Replacement').length, 1);
        assert_1.strict.equal(matching(result.edges, 'INJECTS', 'App', 'Replacement').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.run', 'Replacement.load').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.run', 'Original.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
(0, node_test_1.test)('framework evidence and API aliases follow admitted barrels while shadowed names remain unrelated', () => {
    const f = fixture({
        'framework.ts': `export {Component as Page,inject as resolve,InjectionToken,Input,Injector} from '@angular/core';`,
        'barrel.ts': `export * from './framework';`,
        'services.ts': `export class Original{load(){}}export class Replacement{load(){}}`,
        'app.ts': `import * as ng from './barrel';import {resolve} from './barrel';import {Original,Replacement} from './services';const get=resolve;
      @ng.Page({selector:'app',template:'',providers:[{provide:Original,useClass:Replacement}]})export class App{service=get(Original);run(){this.service.load();}}
      export function unrelated(resolve:any){resolve(Original).load();}`,
    });
    try {
        const result = f.extract();
        assert_1.strict.equal(result.complete, true);
        assert_1.strict.equal(result.nodes.find(node => node.name === 'App')?.label, 'Component');
        assert_1.strict.equal(matching(result.edges, 'INJECTS', 'App', 'Replacement').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', 'App.run', 'Replacement.load').length, 1);
        assert_1.strict.equal(matching(result.edges, 'CALLS', '#unrelated', 'Replacement.load').length, 0);
    }
    finally {
        f.dispose();
    }
});
