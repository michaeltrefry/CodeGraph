import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Project, Node, ArrayLiteralExpression } from 'ts-morph';
import { analyzeAngular } from './angularAnalysis';
import { extractProject } from './extractor';
import { SemanticModel } from './semanticModel';
import { ExtractionWorkLimitError, type ExtractionWorkControl } from './extractionWork';
import type { ExtractProjectResponse, PendingEdgeDto, ResolvedImportDto } from './protocol';

const angular = `import {NgModule,Component,InjectionToken,inject} from '@angular/core';\n`;
function doubled(depth: number, field = 'imports') {
  return angular + `class Leaf {} const a0=[Leaf];\n` + Array.from({length:depth}, (_,i)=>`const a${i+1}=[...a${i},...a${i}];`).join('\n') +
    `\n@NgModule({${field}:a${depth}}) export class Module {}`;
}
function direct(source: string, control: ExtractionWorkControl = {}) {
  const project = new Project({useInMemoryFileSystem:true,skipFileDependencyResolution:true,compilerOptions:{noResolve:true,types:[],experimentalDecorators:true}});
  const root = fs.realpathSync(os.tmpdir());
  const file = project.createSourceFile(path.join(root,'app.ts'), source);
  const imports: ResolvedImportDto[] = [{fileQN:'Metadata:app.ts',filePath:'app.ts',importedNamespace:'@angular/core',importKind:'static',classification:'external_npm_package',externalPackageName:'@angular/core'}];
  const diagnostics: string[] = [];
  const model = new SemanticModel('Metadata',root,[file],imports,message=>diagnostics.push(message),control);
  const edges: PendingEdgeDto[]=[];
  return {file,model,edges,diagnostics,run:()=>analyzeAngular(model,[],edges,undefined,message=>diagnostics.push(message))};
}
function fixture(source: string) {
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'codegraph-metadata-expansion-'));
  fs.writeFileSync(path.join(root,'app.ts'),source);
  return {run:(control:ExtractionWorkControl={})=>extractProject('Metadata',root,path.join(root,'tsconfig.json'),()=>{},['app.ts'],undefined,control),
    dispose:()=>fs.rmSync(root,{recursive:true,force:true})};
}
function refused(result: ExtractProjectResponse, reason: string) {
  assert.equal(result.complete,false); assert.deepEqual(result.nodes,[]); assert.deepEqual(result.edges,[]);
  assert.deepEqual(result.processedFiles,[]); assert.deepEqual(result.resolvedImports,[]);
  assert.ok(result.diagnostics?.some(message=>message.includes(reason)),result.diagnostics?.join('\n'));
}
function canonical(result:ExtractProjectResponse) {
  assert.equal(result.complete,true,result.diagnostics?.join('\n'));
  return {nodes:result.nodes.map(value=>JSON.stringify(value)).sort(),edges:result.edges.map(value=>JSON.stringify(value)).sort(),processed:result.processedFiles};
}
function restore(target:object,key:PropertyKey,descriptor:PropertyDescriptor|undefined) {
  if(descriptor) Object.defineProperty(target,key,descriptor); else Reflect.deleteProperty(target,key);
}

for (const field of ['imports','providers']) test(`shared ${field} arrays reserve reference slots before copying an expanded child result`, context=>{
  const cap=256;
  const f=direct(doubled(12,field),{maxArrayReferences:cap,maxWork:Number.MAX_SAFE_INTEGER});
  const leaf=f.file.getClassOrThrow('Leaf');
  let copies=0,requests=0,largestRequest=0,largestAllocatedArray=0;
  const reserve=f.model.work.reserveArrayReferences.bind(f.model.work);
  f.model.work.reserveArrayReferences=(slots:number)=>{requests++;largestRequest=Math.max(largestRequest,slots);reserve(slots);};
  const ArrayConstructor=Array;
  const arrayDescriptor=Object.getOwnPropertyDescriptor(globalThis,'Array');
  Object.defineProperty(globalThis,'Array',{configurable:true,writable:true,value:new Proxy(ArrayConstructor,{construct(target,args,newTarget){
    if(requests&&args.length===1&&typeof args[0]==='number') largestAllocatedArray=Math.max(largestAllocatedArray,args[0]);
    return Reflect.construct(target,args,newTarget);
  }})});
  const push=Array.prototype.push,flatMap=Array.prototype.flatMap;
  const pushDescriptor=Object.getOwnPropertyDescriptor(Array.prototype,'push'), flatDescriptor=Object.getOwnPropertyDescriptor(Array.prototype,'flatMap');
  Object.defineProperty(Array.prototype,'push',{configurable:true,writable:true,value:function(this:unknown[],...items:unknown[]){
    for(const item of items) if(item===leaf) copies++;
    return push.apply(this,items);
  }});
  Object.defineProperty(Array.prototype,'flatMap',{configurable:true,writable:true,value:function(this:unknown[],...args:unknown[]){
    const result=(flatMap as (...args:unknown[])=>unknown[]).apply(this,args);
    for(const item of result) if(item===leaf) copies++;
    return result;
  }});
  try {
    assert.throws(f.run,(error:unknown)=>error instanceof ExtractionWorkLimitError&&error.reason==='retention_limit');
    assert.ok(requests>0,'expansion must use the shared preallocation guard');
    assert.ok(f.model.work.retainedArrayReferences<=cap);
    assert.ok(copies<=cap+16,`leaf references copied before refusal: ${copies}`);
    assert.ok(largestAllocatedArray<=cap,`array allocated before refusal: ${largestAllocatedArray}`);
    assert.throws(()=>f.model.work.charge(0),ExtractionWorkLimitError,'a refused expansion poisons this run');
    context.diagnostic(`${field} depth12/4096 leaves: ${copies} observed leaf copies, ${requests} reservations, largest request ${largestRequest}, largest allocation ${largestAllocatedArray}, retained ${f.model.work.retainedArrayReferences}/${cap}`);
  } finally {restore(ArrayConstructor.prototype,'push',pushDescriptor);restore(ArrayConstructor.prototype,'flatMap',flatDescriptor);restore(globalThis,'Array',arrayDescriptor);}
});

test('shared raw metadata expansions are cached without rescanning every spread for each consumer',context=>{
  const depth=8, consumers=10;
  const source=doubled(depth)+Array.from({length:consumers-1},(_,i)=>`\n@NgModule({imports:a${depth}}) class Other${i}{}`).join('');
  const f=direct(source);
  const arrays=new Set(f.file.getVariableDeclarations().filter(node=>/^a\d+$/.test(node.getName())).map(node=>node.getInitializer()).filter(Node.isArrayLiteralExpression));
  const descriptor=Object.getOwnPropertyDescriptor(ArrayLiteralExpression.prototype,'getElements');
  const getElements=ArrayLiteralExpression.prototype.getElements;
  let reads=0;
  Object.defineProperty(ArrayLiteralExpression.prototype,'getElements',{configurable:true,writable:true,value:function(this:ArrayLiteralExpression){if(arrays.has(this)) reads++;return getElements.call(this);}});
  try {
    assert.equal(f.run().complete,true,f.diagnostics.join('\n'));
    assert.ok(reads<=arrays.size*2,`${reads} array materializations for ${arrays.size} shared AST arrays`);
    assert.ok(f.model.work.retainedArrayReferences>=2**depth,'duplicates remain actual reference slots');
    context.diagnostic(`${consumers} consumers / ${arrays.size} arrays: ${reads} materializations, ${f.model.work.retainedArrayReferences} reserved references`);
  } finally {restore(ArrayLiteralExpression.prototype,'getElements',descriptor);}
});

test('provider flattening preserves duplicate multi entries and ordering instead of deduplicating shared arrays',()=>{
  const f=direct(angular+`interface Contract{run():void;} const TOKEN=new InjectionToken<Contract>('t');
    class First implements Contract{run(){}} class Second implements Contract{run(){}}
    const first={provide:TOKEN,useClass:First,multi:true};const second={provide:TOKEN,useClass:Second,multi:true};
    const shared=[first,second,first]; @NgModule({providers:[...shared,...shared]}) class Module{}`);
  const first=f.file.getVariableDeclarationOrThrow('first').getInitializerOrThrow();
  const second=f.file.getVariableDeclarationOrThrow('second').getInitializerOrThrow();
  const observed:string[]=[];const property=f.model.objectProperty.bind(f.model);
  f.model.objectProperty=(object,name)=>{if(name==='provide'&&(object===first||object===second)) observed.push(object===first?'first':'second');return property(object,name);};
  assert.equal(f.run().complete,true,f.diagnostics.join('\n'));
  assert.deepEqual(observed,['first','second','first','first','second','first']);
  assert.ok(f.edges.some(edge=>edge.type==='REGISTERS'&&edge.targetQN==='app.First'));
  assert.ok(f.edges.some(edge=>edge.type==='REGISTERS'&&edge.targetQN==='app.Second'));
});

test('raw shared provider values preserve ordered NgModule imports, declarations and isolated component overrides',()=>{
  const source=angular+`interface Contract{run():string;} const TOKEN=new InjectionToken<Contract>('t');
    class First implements Contract{run(){return 'first';}}class Second implements Contract{run(){return 'second';}}
    const shared=[{provide:TOKEN,useClass:First},{provide:TOKEN,useClass:Second}];
    @Component({selector:'feature',template:''}) class Feature{service=inject(TOKEN);run(){this.service.run();}}
    @Component({selector:'other',template:'',providers:[...shared,{provide:TOKEN,useClass:First}]}) class Other{service=inject(TOKEN);run(){this.service.run();}}
    const declared=[Feature,Other];@NgModule({declarations:[...declared],providers:shared}) class ChildModule{}
    const imported=[ChildModule];@NgModule({imports:[...imported],providers:[...shared,{provide:TOKEN,useClass:First}]}) class AppModule{}
    @Component({selector:'separate',template:''}) class Separate{service=inject(TOKEN);run(){this.service.run();}}
    @NgModule({declarations:[Separate],providers:shared}) class SeparateModule{}`;
  const f=fixture(source);
  try {
    const result=f.run();const expected=canonical(result);
    const calls=(owner:string)=>result.edges.filter(edge=>edge.type==='CALLS'&&edge.sourceQN===`app.${owner}.run`&&/app\.(First|Second)\.run$/.test(edge.targetQN)).map(edge=>edge.targetQN).sort();
    assert.deepEqual(calls('Feature'),['app.First.run']);assert.deepEqual(calls('Other'),['app.First.run']);assert.deepEqual(calls('Separate'),['app.Second.run']);
    assert.deepEqual(canonical(f.run()),expected);
  } finally {f.dispose();}
});

test('array reference refusal, cancellation and hard default admission discard whole extraction and recover freshly',()=>{
  const f=fixture(doubled(10));
  try {
    const healthy=f.run();const expected=canonical(healthy);
    refused(f.run({maxArrayReferences:128,maxWork:Number.MAX_SAFE_INTEGER}),'retention_limit');
    let cancelled=false;
    refused(f.run({shouldCancel:()=>cancelled,onWork:used=>{if(used>2000)cancelled=true;}}),'cancelled');
    assert.deepEqual(canonical(f.run()),expected);
  } finally {f.dispose();}
  const bounded=fixture(doubled(18));
  try {refused(bounded.run({maxArrayReferences:Number.MAX_SAFE_INTEGER,maxWork:Number.MAX_SAFE_INTEGER}),'retention_limit');}
  finally {bounded.dispose();}
});

test('metadata alias depth and cycles refuse safely and do not contaminate a fresh extraction',()=>{
  for(const source of [angular+`class Leaf{};const a0=[Leaf];`+Array.from({length:150},(_,i)=>`const a${i+1}=[...a${i}];`).join('')+`@NgModule({imports:a150}) class Module{}`,
    angular+`const a=[...b];const b=[...a];@NgModule({imports:a})class Module{}`]) {
    const f=fixture(source);
    try {
      const result=f.run();refused(result,'depth_limit');
      assert.ok(!result.diagnostics?.some(message=>/RangeError|maximum call stack/i.test(message)),result.diagnostics?.join('\n'));
    } finally {f.dispose();}
  }
  const healthy=fixture(doubled(2));try{canonical(healthy.run());}finally{healthy.dispose();}
});
