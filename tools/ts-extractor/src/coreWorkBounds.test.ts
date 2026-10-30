import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Project, ClassDeclaration, MethodDeclaration } from 'ts-morph';
import { extractProject } from './extractor';
import { MAX_PARSE_SOURCE_UNITS } from './extractionWork';
import type { ExtractionWorkControl } from './extractionWork';
import type { ExtractProjectResponse } from './protocol';

function fixture(files: Record<string, string>) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-core-reservation-'));
  for (const [file, text] of Object.entries(files)) { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); }
  const run = (control: ExtractionWorkControl = {}) => {
    let work = 0;
    const result = extractProject('Core', root, path.join(root, 'tsconfig.json'), () => {}, Object.keys(files), undefined,
      { ...control, onWork: value => { work = value; control.onWork?.(value); } });
    return { result, work };
  };
  return { root, run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
const corpus = (classes: number, methods: number) => Array.from({ length: classes }, (_, index) =>
  `export class C${index}{${Array.from({ length: methods }, (_, member) => `m${member}(value:number):number{return value;}`).join('')}}`).join('\n');
function empty(result: ExtractProjectResponse) {
  assert.equal(result.complete, false); assert.deepEqual(result.nodes, []); assert.deepEqual(result.edges, []);
  assert.deepEqual(result.processedFiles, []); assert.deepEqual(result.resolvedImports, []);
  assert.ok(result.diagnostics?.some(message => /work limit|retention|analysis work/i.test(message)), result.diagnostics?.join('\n'));
}
function canonical(result: ExtractProjectResponse) {
  assert.equal(result.complete, true, result.diagnostics?.join('\n'));
  return { nodes: result.nodes.map(node => JSON.stringify(node)).sort(), edges: result.edges.map(edge => JSON.stringify(edge)).sort(),
    processed: [...result.processedFiles].sort() };
}
/** Count the actual parser and legacy metadata entry points, rather than only checking the
 * empty failure response after an unrestricted compiler/DTO allocation has already happened. */
function observe<T>(root: string, action: () => T) {
  const counts = { parses: 0, classNames: 0, methodNames: 0 };
  const source = Project.prototype.createSourceFile;
  const className = ClassDeclaration.prototype.getName;
  const methodName = MethodDeclaration.prototype.getName;
  const sourceDescriptor = Object.getOwnPropertyDescriptor(Project.prototype, 'createSourceFile');
  const classDescriptor = Object.getOwnPropertyDescriptor(ClassDeclaration.prototype, 'getName');
  const methodDescriptor = Object.getOwnPropertyDescriptor(MethodDeclaration.prototype, 'getName');
  const own = (file: string) => file.startsWith(root + path.sep);
  Object.defineProperty(Project.prototype, 'createSourceFile', { configurable: true, writable: true, value: function(this: Project, ...args: unknown[]) {
    if (typeof args[0] === 'string' && own(path.resolve(args[0]))) counts.parses++;
    return (source as (...args: unknown[]) => unknown).apply(this, args);
  } });
  Object.defineProperty(ClassDeclaration.prototype, 'getName', { configurable: true, writable: true, value: function(this: ClassDeclaration) {
    if (own(this.getSourceFile().getFilePath())) counts.classNames++;
    return className.call(this);
  } });
  Object.defineProperty(MethodDeclaration.prototype, 'getName', { configurable: true, writable: true, value: function(this: MethodDeclaration) {
    if (own(this.getSourceFile().getFilePath())) counts.methodNames++;
    return methodName.call(this);
  } });
  try { return { value: action(), counts }; }
  finally {
    if (sourceDescriptor) Object.defineProperty(Project.prototype, 'createSourceFile', sourceDescriptor); else Reflect.deleteProperty(Project.prototype, 'createSourceFile');
    if (classDescriptor) Object.defineProperty(ClassDeclaration.prototype, 'getName', classDescriptor); else Reflect.deleteProperty(ClassDeclaration.prototype, 'getName');
    if (methodDescriptor) Object.defineProperty(MethodDeclaration.prototype, 'getName', methodDescriptor); else Reflect.deleteProperty(MethodDeclaration.prototype, 'getName');
  }
}

test('small class/method corpus reserves output before legacy metadata and edge DTO construction', context => {
  const classes = 40, methods = 8;
  const f = fixture({ 'core.ts': corpus(classes, methods) });
  try {
    const deniedClass = observe(f.root, () => f.run({ maxOutputItems: 0 })); empty(deniedClass.value.result);
    assert.equal(deniedClass.counts.classNames, 0, 'class reservation must precede expensive name/type/property metadata');
    assert.equal(deniedClass.counts.methodNames, 0);
    const deniedMethod = observe(f.root, () => f.run({ maxOutputItems: 2 })); empty(deniedMethod.value.result);
    assert.equal(deniedMethod.counts.classNames, 1, 'the first class may be emitted before the next method/edge reservation refuses');
    assert.equal(deniedMethod.counts.methodNames, 0, 'method and edge reservations must precede method DTO metadata');
    const healthy = f.run(); assert.equal(healthy.result.complete, true, healthy.result.diagnostics?.join('\n'));
    assert.equal(healthy.result.nodes.filter(node => node.label === 'Class').length, classes);
    assert.equal(healthy.result.nodes.filter(node => node.label === 'Method').length, classes * methods);
    assert.equal(healthy.result.edges.filter(edge => edge.type === 'DEFINES_METHOD').length, classes * methods);
    const method = healthy.result.nodes.find(node => node.qualifiedName === 'core.C0.m0'); assert.ok(method);
    assert.equal(method.properties.parameter_count, 1); assert.equal(method.properties.return_type, 'number');
    assert.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), canonical(healthy.result));
    context.diagnostic(`40 classes x 8 methods: ${healthy.work} work; refused metadata calls class=${deniedClass.counts.classNames}, method=${deniedMethod.counts.methodNames}`);
  } finally { f.dispose(); }
});

test('source and aggregate AST admission stop before retaining an entire many-file corpus', context => {
  const files = Object.fromEntries(Array.from({ length: 12 }, (_, index) => [`part${index}.ts`, corpus(2, 8)]));
  const f = fixture(files);
  try {
    const beforeParse = observe(f.root, () => f.run({ maxWork: 1 })); empty(beforeParse.value.result);
    assert.equal(beforeParse.counts.parses, 0, 'source retention must be reserved before createSourceFile');
    const retained = observe(f.root, () => f.run({ maxAstNodes: 10 })); empty(retained.value.result);
    assert.ok(retained.counts.parses > 0 && retained.counts.parses < Object.keys(files).length, 'count the parsed AST before admitting the next file');
    assert.equal(retained.counts.classNames, 0); assert.equal(retained.counts.methodNames, 0);
    const recovered = observe(f.root, () => f.run());
    assert.equal(recovered.value.result.complete, true, recovered.value.result.diagnostics?.join('\n'));
    assert.equal(recovered.counts.parses, Object.keys(files).length); assert.equal(recovered.value.result.processedFiles.length, Object.keys(files).length);
    context.diagnostic(`source refusal parsed ${beforeParse.counts.parses}; AST refusal parsed ${retained.counts.parses}; fresh success parsed ${recovered.counts.parses}`);
  } finally { f.dispose(); }
});

test('legacy core output work refusal and cancellation discard all partial facts and recover with fresh reservations', () => {
  const f = fixture({ 'core.ts': corpus(12, 6) });
  try {
    const healthy = f.run(); const expected = canonical(healthy.result); const threshold = Math.floor(healthy.work / 2);
    empty(f.run({ maxWork: threshold }).result);
    let cancelled = false;
    empty(f.run({ shouldCancel: () => cancelled, onWork: used => { if (used >= threshold) cancelled = true; } }).result);
    const recovered = f.run(); assert.deepEqual(canonical(recovered.result), expected); assert.equal(recovered.work, healthy.work);
  } finally { f.dispose(); }
});

test('normal generic/interface, Angular injection and React render facts remain canonical under core reservations', () => {
  const f = fixture({
    'contracts.ts': `export interface Contract<T>{run(value:T):T;} export class Base{ping():string{return 'ok';}} export enum State{Ready,Done}`,
    'service.ts': `import {Injectable} from '@angular/core'; import {Base,Contract} from './contracts';
      @Injectable() export class Service extends Base implements Contract<string>{
        run(value:string):string{return value;} async fetch(id:string):Promise<string>{return this.run(id);}
        arrow=(value:string)=>this.run(value);
      }`,
    'page.ts': `import {Component,Input,inject} from '@angular/core'; import {Service} from './service';
      @Component({selector:'app-page',template:'<span/>'}) export class Page{
        @Input() title:string=''; private service=inject(Service); constructor(private second:Service){}
        run(){return this.service.run(this.title);}
      }`,
    'app.tsx': `export function Panel(){return <section/>;} export function App(){return <Panel/>;}`,
  });
  try {
    const healthy = f.run(); const result = healthy.result;
    assert.equal(result.complete, true, result.diagnostics?.join('\n'));
    const node = (qn: string) => { const value = result.nodes.find(item => item.qualifiedName === qn); assert.ok(value, qn); return value; };
    assert.equal(node('contracts.Contract').label, 'Interface'); assert.equal(node('contracts.Contract').properties.is_generic, true);
    assert.equal(node('contracts.State').label, 'Enum'); assert.equal(node('service.Service').properties.is_service, true);
    assert.equal(node('page.Page').label, 'Component'); assert.equal(node('page.Page.title').properties.is_input, true);
    assert.equal(node('service.Service.fetch').properties.is_async, true); assert.equal(node('service.Service.run').properties.parameter_count, 1);
    assert.equal(node('service.Service.arrow').properties.is_arrow_property, true);
    for (const [source, target, type] of [
      ['service.Service', 'contracts.Base', 'INHERITS'], ['service.Service', 'contracts.Contract', 'IMPLEMENTS'],
      ['page.Page', 'service.Service', 'INJECTS'], ['Core:app.tsx#App', 'Core:app.tsx#Panel', 'RENDERS'],
    ]) assert.ok(result.edges.some(edge => edge.sourceQN === source && edge.targetQN === target && edge.type === type), `${source} ${type} ${target}`);
    const expected = canonical(result);
    assert.deepEqual(canonical(f.run({ maxWork: healthy.work + 1 }).result), expected);
    assert.deepEqual(canonical(f.run().result), expected);
  } finally { f.dispose(); }
});


test('large configured reader and work budgets cannot disable the independent pre-parser window', () => {
  const source = '/*' + 'x'.repeat(MAX_PARSE_SOURCE_UNITS) + '*/';
  assert.ok(source.length > MAX_PARSE_SOURCE_UNITS && source.length < 4 * 1024 * 1024);
  const f = fixture({ 'comment.ts': source });
  try {
    const refused = observe(f.root, () => extractProject('Core', f.root, path.join(f.root, 'tsconfig.json'), () => {}, ['comment.ts'],
      { maxFileSizeKb: 4096, maxAnalysisTextKb: 8192 }, { maxWork: Number.MAX_SAFE_INTEGER }));
    empty(refused.value);
    assert.equal(refused.counts.parses, 0, 'the independent temporary parse window must refuse before createSourceFile');
    assert.equal(refused.counts.classNames, 0); assert.equal(refused.counts.methodNames, 0);
  } finally { f.dispose(); }
});
